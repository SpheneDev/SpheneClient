using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.User;
using Sphene.PlayerData.Pairs;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Configurations;
using Sphene.WebAPI;
using System.Numerics;

namespace Sphene.Services;

public sealed class SupporterNameplateService : IDisposable, IHostedService
{
    private const float IconSize = 20f;
    private const float IconGap = 4f;
    private const int MaxNamePlateObjects = 50;
    private const float OcclusionProbeHeight = 2.2f;
    private const float OcclusionProbeMargin = 0.6f;
    private const int OccGridCols = 4;
    private const int OccGridRows = 2;
    private const int FullOccMask = (1 << (OccGridCols * OccGridRows)) - 1;

    private readonly record struct IconDrawPosition(Vector2 Anchor, Vector3 TargetWorldPos, Vector3 CharPos, int PlateIndex, nint Owner, string IconLabel, uint IconLabelColor);

    private readonly record struct ResolvedStyle(
        bool IndicatorsEnabled,
        bool SymbolEnabled, string Symbol,
        bool TextEnabled, string LabelText,
        SupporterLabelOrder LabelOrder, SupporterSymbolPosition Position,
        ushort SymbolColorKey, ushort LabelColorKey,
        bool IconEnabled, bool IconLabelEnabled, string IconLabelText, uint IconLabelColor);

    private readonly INamePlateGui _namePlateGui;
    private readonly IObjectTable _objectTable;
    private readonly IGameGui _gameGui;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ITextureProvider _textureProvider;
    private readonly ApiController _apiController;
    private readonly SpheneConfigService _configService;
    private readonly PairManager _pairManager;
    private readonly ILogger<SupporterNameplateService> _logger;
    private ISharedImmediateTexture? _iconTexture;
    private IReadOnlyList<IconDrawPosition> _iconPositions = [];
    private int _namePlateLayer = -1;
    private ushort _namePlateDrawOrder;
    private string? _occDebug;
    private bool _disposed;
    private HashSet<string> _supporterNameCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SupporterStyleDto> _supporterStyleMap = new(StringComparer.OrdinalIgnoreCase);
    private string _supporterStyleFingerprint = string.Empty;
    private string? _lastPublishedStyleKey;
    private readonly Dictionary<int, string> _appliedPlateNames = new();
    private DateTime _lastCacheRefresh = DateTime.MinValue;
    private DateTime _lastTraceLog = DateTime.MinValue;
    private string? _lastSupporterSettingsKey;
    private readonly TimeSpan _cacheRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TraceInterval = TimeSpan.FromSeconds(5);

    public SupporterNameplateService(INamePlateGui namePlateGui,
        IObjectTable objectTable,
        IGameGui gameGui,
        IDalamudPluginInterface pluginInterface,
        ITextureProvider textureProvider,
        ApiController apiController,
        SpheneConfigService configService,
        PairManager pairManager,
        ILogger<SupporterNameplateService> logger)
    {
        _namePlateGui = namePlateGui;
        _objectTable = objectTable;
        _gameGui = gameGui;
        _pluginInterface = pluginInterface;
        _textureProvider = textureProvider;
        _apiController = apiController;
        _configService = configService;
        _pairManager = pairManager;
        _logger = logger;
        _namePlateGui.OnDataUpdate += OnDataUpdate;
        _namePlateGui.OnNamePlateUpdate += OnNamePlateTextUpdate;
        _pluginInterface.UiBuilder.Draw += DrawSupporterIcons;
        _iconTexture = LoadIconTexture();
        _logger.LogDebug("[Nameplate] Service started, subscribed to OnDataUpdate + OnNamePlateUpdate");
    }

    private ISharedImmediateTexture? LoadIconTexture()
    {
        try
        {
            var assembly = GetType().Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("images.icon.png", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
            {
                _logger.LogDebug("[Nameplate] Embedded supporter icon resource not found");
                return null;
            }
            return _textureProvider.GetFromManifestResource(assembly, resourceName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Nameplate] Failed to load supporter icon");
            return null;
        }
    }

    private bool ShouldLogTrace()
    {
        var now = DateTime.UtcNow;
        if (now - _lastTraceLog < TraceInterval)
            return false;
        _lastTraceLog = now;
        return true;
    }

    private void RefreshSupporterNameCache()
    {
        var now = DateTime.UtcNow;
        if (now - _lastCacheRefresh < _cacheRefreshInterval)
            return;

        _lastCacheRefresh = now;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var styles = new Dictionary<string, SupporterStyleDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _pairManager.PairsWithGroups.Keys)
        {
            if (pair.UserData == null || string.IsNullOrEmpty(pair.UserData.UID))
                continue;
            if (!pair.UserData.IsSupporter)
                continue;
            var playerName = pair.PlayerName;
            if (string.IsNullOrEmpty(playerName))
                continue;
            names.Add(playerName);
            if (pair.UserPair?.OtherSupporterStyle != null)
                styles[playerName] = pair.UserPair.OtherSupporterStyle;
        }

        if (_apiController.IsSupporter)
        {
            var localName = _objectTable.LocalPlayer?.Name.TextValue;
            if (!string.IsNullOrEmpty(localName))
                names.Add(localName);
        }

        _supporterNameCache = names;
        _supporterStyleMap = styles;
        _supporterStyleFingerprint = styles.Count == 0
            ? string.Empty
            : string.Join(";", styles.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .Select(k => $"{k.Key}~{k.Value.IndicatorsEnabled}~{k.Value.SymbolChar}~{k.Value.SymbolPosition}~{k.Value.TextEnabled}~{k.Value.LabelText}~{k.Value.LabelOrder}~{k.Value.SymbolColorKey}~{k.Value.LabelColorKey}~{k.Value.IconEnabled}~{k.Value.IconLabelEnabled}~{k.Value.IconLabelText}~{k.Value.IconLabelColor}"));
        _logger.LogDebug("[Nameplate] Cache refreshed: {count} supporter names [{names}], own IsSupporter={isSupporter}, localName={localName}, pairs={pairs}",
            names.Count, string.Join(", ", names), _apiController.IsSupporter,
            _objectTable.LocalPlayer?.Name.TextValue ?? "null", _pairManager.PairsWithGroups.Count);
    }

    private bool IsSupporter(string characterName)
    {
        if (string.IsNullOrEmpty(characterName))
            return false;
        return _supporterNameCache.Contains(characterName);
    }

    private ResolvedStyle ResolveStyle(string name, bool isLocalPlayer)
    {
        if (!isLocalPlayer && _supporterStyleMap.TryGetValue(name, out var synced))
            return FromDto(synced);
        return FromConfig();
    }

    private ResolvedStyle FromConfig()
    {
        var c = _configService.Current;
        return new ResolvedStyle(
            c.ShowSupporterNameplate,
            !string.IsNullOrEmpty(c.SupporterSymbolChar), c.SupporterSymbolChar ?? string.Empty,
            c.SupporterTextEnabled && !string.IsNullOrWhiteSpace(c.SupporterLabelText), c.SupporterLabelText ?? string.Empty,
            c.SupporterLabelOrder, c.SupporterSymbolPosition,
            c.SupporterColorKey, c.SupporterLabelColorKey,
            c.SupporterIconEnabled, c.SupporterIconLabelEnabled, c.SupporterIconLabelText ?? string.Empty, c.SupporterIconLabelColor);
    }

    private static ResolvedStyle FromDto(SupporterStyleDto s)
    {
        var position = Enum.IsDefined(typeof(SupporterSymbolPosition), s.SymbolPosition)
            ? (SupporterSymbolPosition)s.SymbolPosition : SupporterSymbolPosition.Left;
        var order = Enum.IsDefined(typeof(SupporterLabelOrder), s.LabelOrder)
            ? (SupporterLabelOrder)s.LabelOrder : SupporterLabelOrder.SymbolThenText;
        return new ResolvedStyle(
            s.IndicatorsEnabled,
            !string.IsNullOrEmpty(s.SymbolChar), s.SymbolChar ?? string.Empty,
            s.TextEnabled && !string.IsNullOrWhiteSpace(s.LabelText), s.LabelText ?? string.Empty,
            order, position,
            (ushort)Math.Clamp(s.SymbolColorKey, 0, ushort.MaxValue), (ushort)Math.Clamp(s.LabelColorKey, 0, ushort.MaxValue),
            s.IconEnabled, s.IconLabelEnabled, s.IconLabelText ?? string.Empty, s.IconLabelColor);
    }

    private SupporterStyleDto BuildOwnStyle()
    {
        var c = _configService.Current;
        return new SupporterStyleDto
        {
            IndicatorsEnabled = c.ShowSupporterNameplate,
            SymbolChar = c.SupporterSymbolChar ?? string.Empty,
            SymbolPosition = (int)c.SupporterSymbolPosition,
            TextEnabled = c.SupporterTextEnabled,
            LabelText = c.SupporterLabelText ?? string.Empty,
            LabelOrder = (int)c.SupporterLabelOrder,
            SymbolColorKey = c.SupporterColorKey,
            LabelColorKey = c.SupporterLabelColorKey,
            IconEnabled = c.SupporterIconEnabled,
            IconLabelEnabled = c.SupporterIconLabelEnabled,
            IconLabelText = c.SupporterIconLabelText ?? string.Empty,
            IconLabelColor = c.SupporterIconLabelColor
        };
    }

    private void OnDataUpdate(INamePlateUpdateContext ctx, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        RequestRedrawIfSettingsChanged();

        if (!_configService.Current.ShowSupporterNameplate || !_apiController.SupporterFeaturesEnabled)
        {
            _iconPositions = [];
            _appliedPlateNames.Clear();
            if (ShouldLogTrace())
                _logger.LogDebug("[Nameplate] Skipped: ShowSupporterNameplate is disabled");
            return;
        }

        var logThisTick = ShouldLogTrace();
        var localAddr = _objectTable.LocalPlayer?.Address ?? IntPtr.Zero;

        RefreshSupporterNameCache();
        if (_supporterNameCache.Count == 0)
        {
            _iconPositions = [];
            _appliedPlateNames.Clear();
            if (logThisTick)
                _logger.LogDebug("[Nameplate] Skipped: supporter cache is empty (handlers={handlers})", handlers.Count);
            return;
        }

        var localSymbolEnabled = !string.IsNullOrEmpty(_configService.Current.SupporterSymbolChar);
        var localTextEnabled = _configService.Current.SupporterTextEnabled && !string.IsNullOrWhiteSpace(_configService.Current.SupporterLabelText);
        var localIconEnabled = _configService.Current.SupporterIconEnabled;
        var needsRedraw = false;
        var playerCount = 0;
        var selfPlatePresent = false;
        List<IconDrawPosition>? iconPos = null;
        string? iconDbg = null;
        List<string>? matchedNames = logThisTick ? new List<string>() : null;
        List<string>? seenNames = logThisTick ? new List<string>() : null;

        for (int i = 0; i < handlers.Count; i++)
        {
            var h = handlers[i];
            var obj = h.GameObject;
            if (obj is not IPlayerCharacter pc)
                continue;

            playerCount++;
            var isSelf = pc.Address == localAddr;
            if (isSelf)
                selfPlatePresent = true;

            var name = pc.Name.TextValue;
            seenNames?.Add(name);
            if (!IsSupporter(name))
                continue;

            var style = ResolveStyle(name, isSelf);
            if (!style.IndicatorsEnabled)
                continue;

            matchedNames?.Add(name);

            if ((style.SymbolEnabled || style.TextEnabled)
                && (!_appliedPlateNames.TryGetValue(h.NamePlateIndex, out var appliedFor)
                    || !string.Equals(appliedFor, name, StringComparison.OrdinalIgnoreCase)))
            {
                needsRedraw = true;
            }

            if (style.IconEnabled)
            {
                var platePos = GetNameplateIconPosition(h);
                if (platePos.HasValue)
                {
                    var charPos = pc.Position;
                    iconPos ??= new List<IconDrawPosition>(8);
                    iconPos.Add(new IconDrawPosition(platePos.Value,
                        charPos + Vector3.UnitY * OcclusionProbeHeight, charPos,
                        h.NamePlateIndex, pc.Address,
                        style.IconLabelEnabled ? style.IconLabelText : string.Empty,
                        style.IconLabelColor));
                }
                if (logThisTick && iconDbg == null)
                {
                    iconDbg = (platePos.HasValue ? "ok " : "fail ") + DescribeNameplateIcon(h);
                }
            }
        }

        _iconPositions = iconPos is { Count: > 0 } ? (IReadOnlyList<IconDrawPosition>)iconPos.ToArray() : [];

        if (needsRedraw)
            RequestRedraw();

        if (logThisTick)
        {
            _logger.LogDebug("[Nameplate] Tick: handlers={handlers}, players={players}, selfPlate={selfPlate}, matched={matched} [{matchedNames}], icons={icons}, ind=[sym={sym},txt={txt},icon={icon}], iconDbg={iconDbg}, occ={occDbg}, seen=[{seenNames}]",
                handlers.Count, playerCount, selfPlatePresent, matchedNames?.Count ?? 0,
                string.Join(", ", matchedNames ?? Enumerable.Empty<string>()),
                _iconPositions.Count,
                localSymbolEnabled, localTextEnabled, localIconEnabled,
                iconDbg ?? "-",
                _occDebug ?? "-",
                string.Join(", ", seenNames ?? Enumerable.Empty<string>()));
        }
    }

    private void OnNamePlateTextUpdate(INamePlateUpdateContext ctx, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (!_configService.Current.ShowSupporterNameplate || !_apiController.SupporterFeaturesEnabled)
            return;

        RefreshSupporterNameCache();
        if (_supporterNameCache.Count == 0)
            return;

        var localAddr = _objectTable.LocalPlayer?.Address ?? IntPtr.Zero;

        for (int i = 0; i < handlers.Count; i++)
        {
            var h = handlers[i];
            if (h.GameObject is not IPlayerCharacter pc)
                continue;

            var name = pc.Name.TextValue;
            if (!IsSupporter(name))
                continue;

            var style = ResolveStyle(name, pc.Address == localAddr);
            if (!style.IndicatorsEnabled || (!style.SymbolEnabled && !style.TextEnabled))
                continue;

            var symbol = style.Symbol;
            var textEnabled = style.TextEnabled;
            var symbolEnabled = style.SymbolEnabled;
            var labelText = style.LabelText;
            var position = style.Position;
            var order = style.LabelOrder;
            var symbolColorKey = style.SymbolColorKey;
            var labelColorKey = style.LabelColorKey;

            var original = h.InfoView.Name;

            SeStringBuilder AppendSupporterParts(SeStringBuilder builder)
            {
                var first = true;
                void AddPart(string? partText, ushort partColorKey)
                {
                    if (string.IsNullOrEmpty(partText)) return;
                    if (!first) builder.AddText(" ");
                    builder.AddUiForeground(partText, partColorKey);
                    first = false;
                }

                if (order == SupporterLabelOrder.SymbolThenText)
                {
                    if (symbolEnabled) AddPart(symbol, symbolColorKey);
                    if (textEnabled) AddPart(labelText, labelColorKey);
                }
                else
                {
                    if (textEnabled) AddPart(labelText, labelColorKey);
                    if (symbolEnabled) AddPart(symbol, symbolColorKey);
                }
                return builder;
            }

            var left = new SeStringBuilder().Build();
            var right = new SeStringBuilder().Build();
            if (position == SupporterSymbolPosition.Left)
            {
                left = AppendSupporterParts(new SeStringBuilder()).AddText(" ").Build();
            }
            else if (position == SupporterSymbolPosition.Right)
            {
                right = AppendSupporterParts(new SeStringBuilder().AddText(" ")).Build();
            }
            else
            {
                left = AppendSupporterParts(new SeStringBuilder()).AddText(" ").Build();
                if (symbolEnabled)
                {
                    right = new SeStringBuilder().AddText(" ").AddUiForeground(symbol, symbolColorKey).Build();
                }
            }

            h.NameParts.Text = original;
            h.NameParts.TextWrap = (left, right);
            if (h.IsUpdating)
            {
                _appliedPlateNames[h.NamePlateIndex] = name;
            }
        }
    }

    private void RequestRedrawIfSettingsChanged()
    {
        var current = _configService.Current;
        var key = $"{current.ShowSupporterNameplate}|{current.SupporterSymbolChar}|{current.SupporterTextEnabled}|{current.SupporterLabelText}|{current.SupporterLabelOrder}|{current.SupporterSymbolPosition}|{current.SupporterColorKey}|{current.SupporterLabelColorKey}|{current.SupporterIconEnabled}|{current.SupporterIconLabelEnabled}|{current.SupporterIconLabelText}|{current.SupporterIconLabelColor}|{_apiController.SupporterFeaturesEnabled}|{string.Join(",", _supporterNameCache.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}|{_supporterStyleFingerprint}";

        var publishKey = _apiController.IsConnected && _apiController.IsSupporter && _apiController.SupporterFeaturesEnabled
            ? key
            : "unavailable";
        if (!string.Equals(publishKey, _lastPublishedStyleKey, StringComparison.Ordinal))
        {
            _lastPublishedStyleKey = publishKey;
            _ = _apiController.UserUpdateSupporterStyle(
                _apiController.IsConnected && _apiController.IsSupporter && _apiController.SupporterFeaturesEnabled
                    ? BuildOwnStyle()
                    : null);
        }
        if (string.Equals(key, _lastSupporterSettingsKey, StringComparison.Ordinal))
            return;

        var hadPreviousKey = _lastSupporterSettingsKey != null;
        _lastSupporterSettingsKey = key;
        if (hadPreviousKey)
        {
            _appliedPlateNames.Clear();
            RequestRedraw();
        }
    }

    private unsafe Vector2? GetNameplateIconPosition(INamePlateUpdateHandler handler)
    {
        if (handler.NamePlateIndex is < 0 or >= MaxNamePlateObjects)
            return null;
        var addonPtr = _gameGui.GetAddonByName("NamePlate", 1);
        if (addonPtr.IsNull)
            return null;
        var addon = (AddonNamePlate*)addonPtr.Address;
        if (addon->NamePlateObjectArray == null)
            return null;

        var plate = addon->NamePlateObjectArray + handler.NamePlateIndex;
        var rootComponent = plate->RootComponentNode;
        if (rootComponent == null || !rootComponent->AtkResNode.IsVisible() || rootComponent->AtkResNode.Alpha_2 == 0)
            return null;

        var textNode = (AtkResNode*)plate->NameText;
        if (textNode == null || plate->TextW <= 0)
            return null;

        var collision = (AtkResNode*)plate->NameplateCollision;
        var text = plate->NameText;
        float endX;
        float centerY;
        if (collision != null && collision->Width > 0)
        {
            endX = collision->ScreenX + collision->Width * collision->GetScaleX();
            var collisionH = collision->Height * collision->GetScaleY();
            var nameLineH = text->FontSize > 0
                ? text->FontSize * textNode->GetScaleY()
                : collisionH * 0.5f;
            nameLineH = MathF.Min(nameLineH, collisionH);
            centerY = handler.IsPrefixTitle
                ? collision->ScreenY + collisionH - nameLineH * 0.5f
                : collision->ScreenY + nameLineH * 0.5f;
        }
        else
        {
            var textW = MathF.Min(textNode->Width, plate->TextW);
            endX = textNode->ScreenX + (textNode->Width + textW) * textNode->GetScaleX() * 0.5f;
            centerY = textNode->ScreenY + textNode->Height * textNode->GetScaleY() * 0.5f;
        }
        return new Vector2(endX, centerY);
    }

    private unsafe void RefreshNamePlateLayer()
    {
        _namePlateLayer = -1;
        var stage = AtkStage.Instance();
        var addonPtr = _gameGui.GetAddonByName("NamePlate", 1);
        if (stage == null || addonPtr.IsNull)
            return;

        var namePlate = (AtkUnitBase*)addonPtr.Address;
        _namePlateDrawOrder = namePlate->DrawOrderIndex;
        var lists = &stage->RaptureAtkUnitManager->AtkUnitManager.DepthLayerOneList;
        for (var l = 0; l < 13; l++)
        {
            var list = lists + l;
            for (var i = 0; i < list->Count; i++)
            {
                if (list->Entries[i] == namePlate)
                {
                    _namePlateLayer = l;
                    return;
                }
            }
        }
    }

    private unsafe bool IsIconOccludedByUi(float x1, float y1, float x2, float y2, out string? addonName)
    {
        addonName = null;
        var stage = AtkStage.Instance();
        if (stage == null || _namePlateLayer < 0)
            return false;

        var lists = &stage->RaptureAtkUnitManager->AtkUnitManager.DepthLayerOneList;
        for (var l = _namePlateLayer; l < 13; l++)
        {
            var list = lists + l;
            for (var i = 0; i < list->Count; i++)
            {
                var addon = list->Entries[i].Value;
                if (addon == null || !addon->IsVisible || addon->Alpha == 0)
                    continue;
                if (l == _namePlateLayer && addon->DrawOrderIndex <= _namePlateDrawOrder)
                    continue;
                if (addon->CollisionNodeListCount == 0 || IsIgnoredAddon(addon))
                    continue;

                var root = addon->RootNode;
                if (root == null || !root->IsVisible() || root->Alpha_2 == 0 || !HasVisibleContent(root))
                    continue;
                var w = root->Width * root->GetScaleX();
                var h = root->Height * root->GetScaleY();
                if (w <= 0 || h <= 0)
                    continue;
                if (x1 < root->ScreenX + w && x2 > root->ScreenX
                    && y1 < root->ScreenY + h && y2 > root->ScreenY)
                {
                    addonName = addon->NameString;
                    if (ShouldLogTrace())
                        _logger.LogDebug("[Nameplate] Icon occluded by addon {name} layer={layer} order={order} rect=({x},{y} {w}x{h})",
                            addonName, l, addon->DrawOrderIndex, root->ScreenX, root->ScreenY, w, h);
                    return true;
                }
            }
        }
        return false;
    }

    private static unsafe bool IsIgnoredAddon(AtkUnitBase* addon)
    {
        var name = addon->NameString;
        return name.StartsWith("KTK_Overlay", StringComparison.Ordinal)
            || name.StartsWith("_ActionDoubleCross", StringComparison.Ordinal);
    }

    private static unsafe bool HasVisibleContent(AtkResNode* node)
    {
        var child = node->ChildNode;
        if (child == null)
            return true;
        while (child != null)
        {
            if (child->IsVisible() && child->Alpha_2 != 0)
                return true;
            child = child->NextSiblingNode;
        }
        return false;
    }

    private unsafe int ComputeOcclusionCells(IconDrawPosition entry, Vector2 rectMin, Vector2 rectMax)
    {
        var camera = CameraManager.Instance()->GetActiveCamera();
        if (camera == null)
        {
            _occDebug = "noCam";
            return 0;
        }
        var camPos = camera->SceneCamera.Position;
        var origin = new Vector3(camPos.X, camPos.Y, camPos.Z);
        var target = entry.TargetWorldPos;
        var cellW = (rectMax.X - rectMin.X) / OccGridCols;
        var cellH = (rectMax.Y - rectMin.Y) / OccGridRows;
        var mask = 0;
        var cell = 0;
        var firstInfo = string.Empty;
        for (var row = 0; row < OccGridRows; row++)
        {
            for (var col = 0; col < OccGridCols; col++, cell++)
            {
                var center = new Vector2(rectMin.X + cellW * (col + 0.5f), rectMin.Y + cellH * (row + 0.5f));
                var world = GetIconWorldPos(origin, target, center);
                if (!world.HasValue)
                    continue;
                string info;
                var occluded = ProbeOccluded(origin, world.Value, out _, out info);
                if (!occluded)
                {
                    occluded = IsIconCoveredByObject(origin, center, (world.Value - origin).Length(),
                        entry.Owner, out info, out var anyPick);
                    if (!occluded && !anyPick)
                        occluded = IsOccludedByCharacter(origin, world.Value, entry.Owner, out info);
                }
                if (!occluded)
                    continue;
                mask |= 1 << cell;
                if (firstInfo.Length == 0)
                    firstInfo = info;
            }
        }
        _occDebug = $"cells={Convert.ToString(mask, 2).PadLeft(OccGridCols * OccGridRows, '0')} {firstInfo}";
        return mask;
    }

    private Vector3? GetIconWorldPos(Vector3 origin, Vector3 plateWorld, Vector2 iconCenterScreen)
    {
        if (!_gameGui.WorldToScreen(plateWorld, out var plateScreen))
            return null;
        var forward = Vector3.Normalize(plateWorld - origin);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        if (!_gameGui.WorldToScreen(plateWorld + right, out var offsetScreen))
            return null;
        var pxPerMeter = offsetScreen.X - plateScreen.X;
        if (MathF.Abs(pxPerMeter) < 1f)
            return null;
        var up = Vector3.Cross(right, forward);
        var metersX = (iconCenterScreen.X - plateScreen.X) / pxPerMeter;
        var metersY = (iconCenterScreen.Y - plateScreen.Y) / pxPerMeter;
        return plateWorld + right * metersX - up * metersY;
    }

    private static unsafe bool ProbeOccluded(Vector3 origin, Vector3 target, out string debug, out string hitInfo)
    {
        hitInfo = string.Empty;
        var toTarget = target - origin;
        var distance = toTarget.Length();
        if (distance < 0.01f)
        {
            debug = "close";
            return false;
        }
        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        var module = framework != null ? framework->BGCollisionModule : null;
        if (module == null)
        {
            debug = "noModule";
            return false;
        }
        var direction = toTarget / distance;
        var flags = stackalloc int[] { -1, -1, 0, 0 };
        var skipped = 0;
        var rayOrigin = origin;
        var remaining = distance;
        while (true)
        {
            RaycastHit hit;
            if (!module->RaycastMaterialFilter(&hit, &rayOrigin, &direction, remaining, -1, flags))
            {
                debug = $"miss d={distance:F1}{(skipped > 0 ? $" skip{skipped}" : string.Empty)}";
                return false;
            }
            var colType = hit.Object != null ? hit.Object->GetColliderType() : (ColliderType)0;
            var layer = hit.Object != null ? hit.Object->LayerMask : 0UL;
            var layoutId = hit.Object != null ? hit.Object->LayoutObjectId : 0UL;
            hitInfo = $"t{(int)colType} l{layer:X} m{hit.Material:X} id{layoutId:X8}";
            if (IsExcludedCollider(colType) && skipped < 4)
            {
                skipped++;
                rayOrigin = hit.Point + direction * 0.05f;
                remaining = distance - (rayOrigin - origin).Length();
                if (remaining > 0.01f)
                    continue;
                debug = $"miss d={distance:F1} skip{skipped}";
                return false;
            }
            var travelled = (hit.Point - origin).Length();
            var occluded = travelled < distance - OcclusionProbeMargin;
            debug = $"{(occluded ? "OCC" : "pass")} h={travelled:F1}/d={distance:F1} {hitInfo}{(skipped > 0 ? $" skip{skipped}" : string.Empty)}";
            return occluded;
        }
    }

    private static bool IsExcludedCollider(ColliderType type)
        => type is ColliderType.Box or ColliderType.Cylinder or ColliderType.Sphere
            or ColliderType.Plane or ColliderType.PlaneTwoSided;

    private static unsafe bool IsIconCoveredByObject(Vector3 origin, Vector2 iconCenterScreen, float iconDist, nint owner, out string hitInfo, out bool hitAnything)
    {
        hitInfo = string.Empty;
        hitAnything = false;
        var ts = TargetSystem.Instance();
        if (ts == null)
            return false;
        var obj = ts->GetMouseOverObject((int)iconCenterScreen.X, (int)iconCenterScreen.Y);
        if (obj == null)
            return false;
        hitAnything = true;
        if ((nint)obj == owner)
            return false;
        var objDist = (new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z) - origin).Length();
        var inFront = objDist < iconDist - 0.3f;
        hitInfo = $" pick:{obj->NameString} d{objDist:F1}/i{iconDist:F1}{(inFront ? string.Empty : " bg")}";
        return inFront;
    }

    private unsafe bool IsOccludedByCharacter(Vector3 origin, Vector3 target, nint owner, out string hitInfo)
    {
        hitInfo = string.Empty;
        var segment = target - origin;
        var segLen = segment.Length();
        if (segLen < 0.01f)
            return false;
        var dir = segment / segLen;
        for (var i = 0; i < _objectTable.Length; i++)
        {
            var obj = _objectTable[i];
            if (obj == null || obj.Address == owner || obj.IsDead)
                continue;
            if (obj.ObjectKind is not (ObjectKind.Pc or ObjectKind.BattleNpc
                    or ObjectKind.EventNpc or ObjectKind.Companion or ObjectKind.Retainer
                    or ObjectKind.Mount))
                continue;
            var radius = obj.HitboxRadius;
            if (radius < 0.05f)
                continue;
            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
            var height = go != null && go->Height > 0.1f ? go->Height : 1.8f;
            var pos = obj.Position;
            var t = Vector3.Dot(pos - origin, dir);
            if (t < -radius || t > segLen + radius)
                continue;
            var closest = origin + dir * Math.Clamp(t, 0, segLen);
            var dx = closest.X - pos.X;
            var dz = closest.Z - pos.Z;
            if (dx * dx + dz * dz > radius * radius)
                continue;
            if (closest.Y < pos.Y - 0.3f || closest.Y > pos.Y + height + 0.5f)
                continue;
            hitInfo = $" char:{obj.Name.TextValue} r{radius:F1} h{height:F1}";
            return true;
        }
        return false;
    }

    private unsafe string DescribeNameplateIcon(INamePlateUpdateHandler handler)
    {
        if (handler.NamePlateIndex is < 0 or >= MaxNamePlateObjects)
            return $"badIdx={handler.NamePlateIndex}";
        var addonPtr = _gameGui.GetAddonByName("NamePlate", 1);
        if (addonPtr.IsNull)
            return "noAddon";
        var addon = (AddonNamePlate*)addonPtr.Address;
        if (addon->NamePlateObjectArray == null)
            return "noObjArray";
        var plate = addon->NamePlateObjectArray + handler.NamePlateIndex;
        if (plate->NameText == null)
            return "noTextNode";
        var node = (AtkResNode*)plate->NameText;
        var container = plate->NameContainer;
        var collision = (AtkResNode*)plate->NameplateCollision;
        var containerInfo = container != null
            ? $" container=({container->ScreenX:F0},{container->ScreenY:F0} {container->Width}x{container->Height} scale={container->GetScaleX():F2})"
            : string.Empty;
        var collisionInfo = collision != null
            ? $" collision=({collision->ScreenX:F0},{collision->ScreenY:F0} {collision->Width}x{collision->Height} scale={collision->GetScaleX():F2})"
            : " noCollision";
        var text = plate->NameText;
        var root = plate->RootComponentNode;
        var rootInfo = root != null
            ? $" rootVis={root->AtkResNode.IsVisible()} rootAlpha={root->AtkResNode.Alpha_2}"
            : " noRoot";
        return $"idx={handler.NamePlateIndex} prefix={handler.IsPrefixTitle} vis={handler.VisibilityFlags:X} draw={handler.DrawFlags:X} textW={plate->TextW} textH={plate->TextH} font={text->FontSize} ls={text->LineSpacing}{rootInfo} node=({node->ScreenX:F0},{node->ScreenY:F0} {node->Width}x{node->Height} scale={node->GetScaleX():F2}){containerInfo}{collisionInfo}";
    }

    private void DrawSupporterIcons()
    {
        var positions = _iconPositions;
        if (positions.Count == 0)
            return;
        var icon = _iconTexture;
        if (icon == null || !icon.TryGetWrap(out var tex, out _) || tex == null)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(IconSize * scale);
        var drawList = ImGui.GetBackgroundDrawList();
        RefreshNamePlateLayer();
        for (int i = 0; i < positions.Count; i++)
        {
            var entry = positions[i];
            var label = entry.IconLabel;
            var labelColor = entry.IconLabelColor;
            var labelSize = label.Length > 0 ? ImGui.CalcTextSize(label) : Vector2.Zero;
            var min = new Vector2(entry.Anchor.X + IconGap * scale, entry.Anchor.Y - size.Y * 0.5f);
            var right = min.X + size.X;
            if (labelSize != Vector2.Zero)
                right += IconGap * scale + labelSize.X;
            var occMask = ComputeOcclusionCells(entry, min, new Vector2(right, min.Y + size.Y));
            if (occMask == FullOccMask)
                continue;
            if (IsIconOccludedByUi(min.X, min.Y, right, min.Y + size.Y, out _))
                continue;
            if (occMask == 0)
            {
                DrawIconAndLabel(drawList, tex, min, size, label, labelColor, labelSize, entry.Anchor.Y, scale);
                continue;
            }
            var cellW = (right - min.X) / OccGridCols;
            var cellH = size.Y / OccGridRows;
            var cell = 0;
            for (var row = 0; row < OccGridRows; row++)
            {
                for (var col = 0; col < OccGridCols; col++, cell++)
                {
                    if ((occMask & (1 << cell)) != 0)
                        continue;
                    var cellMin = new Vector2(min.X + cellW * col, min.Y + cellH * row);
                    drawList.PushClipRect(cellMin, cellMin + new Vector2(cellW, cellH), true);
                    DrawIconAndLabel(drawList, tex, min, size, label, labelColor, labelSize, entry.Anchor.Y, scale);
                    drawList.PopClipRect();
                }
            }
        }
    }

    private static void DrawIconAndLabel(ImDrawListPtr drawList, IDalamudTextureWrap tex,
        Vector2 min, Vector2 size, string label, uint labelColor, Vector2 labelSize, float anchorY, float scale)
    {
        drawList.AddImage(tex.Handle, min, min + size);
        if (labelSize != Vector2.Zero)
        {
            drawList.AddText(
                new Vector2(min.X + size.X + IconGap * scale, anchorY - labelSize.Y * 0.5f),
                labelColor,
                label);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void RequestRedraw()
    {
        if (_disposed)
            return;
        try { _namePlateGui.RequestRedraw(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to request nameplate redraw"); }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _iconPositions = [];
        try { _pluginInterface.UiBuilder.Draw -= DrawSupporterIcons; } catch (Exception ex) { _logger.LogDebug(ex, "Failed to unsubscribe from UiBuilder.Draw"); }
        try { _namePlateGui.OnDataUpdate -= OnDataUpdate; } catch (Exception ex) { _logger.LogDebug(ex, "Failed to unsubscribe from data update"); }
        try { _namePlateGui.OnNamePlateUpdate -= OnNamePlateTextUpdate; } catch (Exception ex) { _logger.LogDebug(ex, "Failed to unsubscribe from nameplate update"); }
        try { _namePlateGui.RequestRedraw(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to request nameplate redraw on dispose"); }
        _iconTexture = null;
    }
}
