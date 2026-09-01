using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Sphene.PlayerData.Pairs;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Configurations;

namespace Sphene.Services;

public sealed class SupporterNameplateService : IDisposable
{
    private readonly INamePlateGui _namePlateGui;
    private readonly SpheneConfigService _configService;
    private readonly PairManager _pairManager;
    private readonly ILogger<SupporterNameplateService> _logger;
    private bool _disposed;
    private HashSet<string> _supporterNameCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCacheRefresh = DateTime.MinValue;
    private readonly TimeSpan _cacheRefreshInterval = TimeSpan.FromSeconds(5);

    public SupporterNameplateService(INamePlateGui namePlateGui,
        SpheneConfigService configService,
        PairManager pairManager,
        ILogger<SupporterNameplateService> logger)
    {
        _namePlateGui = namePlateGui;
        _configService = configService;
        _pairManager = pairManager;
        _logger = logger;
        _namePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;
    }

    private void RefreshSupporterNameCache()
    {
        var now = DateTime.UtcNow;
        if (now - _lastCacheRefresh < _cacheRefreshInterval)
            return;

        _lastCacheRefresh = now;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _pairManager.DirectPairs)
        {
            if (pair.UserData == null || string.IsNullOrEmpty(pair.UserData.UID))
                continue;
            if (!pair.UserData.IsSupporter)
                continue;
            var playerName = pair.PlayerName;
            if (!string.IsNullOrEmpty(playerName))
                names.Add(playerName);
        }

        _supporterNameCache = names;
    }

    private bool IsSupporter(string characterName)
    {
        if (string.IsNullOrEmpty(characterName))
            return false;
        return _supporterNameCache.Contains(characterName);
    }

    private void OnNamePlateUpdate(INamePlateUpdateContext ctx, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (!_configService.Current.ShowSupporterNameplate)
            return;

        RefreshSupporterNameCache();
        if (_supporterNameCache.Count == 0)
            return;

        var symbol = _configService.Current.SupporterSymbolChar ?? string.Empty;
        var textEnabled = _configService.Current.SupporterTextEnabled && !string.IsNullOrWhiteSpace(_configService.Current.SupporterLabelText);
        var symbolEnabled = !string.IsNullOrEmpty(symbol);
        if (!textEnabled && !symbolEnabled)
            return;

        var labelText = _configService.Current.SupporterLabelText ?? string.Empty;
        var position = _configService.Current.SupporterSymbolPosition;
        var order = _configService.Current.SupporterLabelOrder;
        var colorKey = _configService.Current.SupporterColorKey;

        for (int i = 0; i < handlers.Count; i++)
        {
            var h = handlers[i];
            var obj = h.GameObject;
            if (obj is not IPlayerCharacter pc)
                continue;

            var name = pc.Name.TextValue;
            if (!IsSupporter(name))
                continue;

            var original = h.InfoView.Name;

            string ComposeGroup(bool sym, bool txt)
            {
                if (txt && sym)
                {
                    return order == SupporterLabelOrder.SymbolThenText
                        ? (symbol + " " + labelText)
                        : (labelText + " " + symbol);
                }
                if (sym) return symbol;
                if (txt) return labelText;
                return string.Empty;
            }

            var leftGroup = string.Empty;
            var rightGroup = string.Empty;
            if (position == SupporterSymbolPosition.Left)
            {
                leftGroup = ComposeGroup(symbolEnabled, textEnabled);
            }
            else if (position == SupporterSymbolPosition.Right)
            {
                rightGroup = ComposeGroup(symbolEnabled, textEnabled);
            }
            else
            {
                leftGroup = ComposeGroup(symbolEnabled, textEnabled);
                rightGroup = symbolEnabled ? symbol : string.Empty;
            }

            var left = new SeStringBuilder().Build();
            var right = new SeStringBuilder().Build();
            if (!string.IsNullOrEmpty(leftGroup))
            {
                left = new SeStringBuilder().AddUiForeground(leftGroup + " ", colorKey).Build();
            }
            if (!string.IsNullOrEmpty(rightGroup))
            {
                right = new SeStringBuilder().AddUiForeground(" " + rightGroup, colorKey).Build();
            }

            h.NameParts.Text = original;
            h.NameParts.TextWrap = (left, right);
        }
    }

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
        try { _namePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate; } catch (Exception ex) { _logger.LogDebug(ex, "Failed to unsubscribe from nameplate update"); }
        try { _namePlateGui.RequestRedraw(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to request nameplate redraw on dispose"); }
    }
}
