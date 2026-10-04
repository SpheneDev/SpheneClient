using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.PlayerData.Pairs;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Configurations;
using Sphene.WebAPI;

namespace Sphene.Services;

public sealed class SupporterNameplateService : IDisposable, IHostedService
{
    private readonly INamePlateGui _namePlateGui;
    private readonly IObjectTable _objectTable;
    private readonly ApiController _apiController;
    private readonly SpheneConfigService _configService;
    private readonly PairManager _pairManager;
    private readonly ILogger<SupporterNameplateService> _logger;
    private bool _disposed;
    private HashSet<string> _supporterNameCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCacheRefresh = DateTime.MinValue;
    private DateTime _lastTraceLog = DateTime.MinValue;
    private readonly TimeSpan _cacheRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TraceInterval = TimeSpan.FromSeconds(5);

    public SupporterNameplateService(INamePlateGui namePlateGui,
        IObjectTable objectTable,
        ApiController apiController,
        SpheneConfigService configService,
        PairManager pairManager,
        ILogger<SupporterNameplateService> logger)
    {
        _namePlateGui = namePlateGui;
        _objectTable = objectTable;
        _apiController = apiController;
        _configService = configService;
        _pairManager = pairManager;
        _logger = logger;
        _namePlateGui.OnDataUpdate += OnNamePlateUpdate;
        _logger.LogDebug("[Nameplate] Service started, subscribed to OnDataUpdate");
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

        if (_apiController.IsSupporter)
        {
            var localName = _objectTable.LocalPlayer?.Name.TextValue;
            if (!string.IsNullOrEmpty(localName))
                names.Add(localName);
        }

        _supporterNameCache = names;
        _logger.LogDebug("[Nameplate] Cache refreshed: {count} supporter names [{names}], own IsSupporter={isSupporter}, localName={localName}, directPairs={pairs}",
            names.Count, string.Join(", ", names), _apiController.IsSupporter,
            _objectTable.LocalPlayer?.Name.TextValue ?? "null", _pairManager.DirectPairs.Count);
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
        {
            if (ShouldLogTrace())
                _logger.LogDebug("[Nameplate] Skipped: ShowSupporterNameplate is disabled");
            return;
        }

        var logThisTick = ShouldLogTrace();
        var localAddr = _objectTable.LocalPlayer?.Address ?? IntPtr.Zero;

        RefreshSupporterNameCache();
        if (_supporterNameCache.Count == 0)
        {
            if (logThisTick)
                _logger.LogDebug("[Nameplate] Skipped: supporter cache is empty (handlers={handlers})", handlers.Count);
            return;
        }

        var symbol = _configService.Current.SupporterSymbolChar ?? string.Empty;
        var textEnabled = _configService.Current.SupporterTextEnabled && !string.IsNullOrWhiteSpace(_configService.Current.SupporterLabelText);
        var symbolEnabled = !string.IsNullOrEmpty(symbol);
        if (!textEnabled && !symbolEnabled)
        {
            if (logThisTick)
                _logger.LogDebug("[Nameplate] Skipped: neither symbol nor text label configured");
            return;
        }

        var labelText = _configService.Current.SupporterLabelText ?? string.Empty;
        var position = _configService.Current.SupporterSymbolPosition;
        var order = _configService.Current.SupporterLabelOrder;
        var colorKey = _configService.Current.SupporterColorKey;

        var playerCount = 0;
        var selfPlatePresent = false;
        List<string>? matchedNames = logThisTick ? new List<string>() : null;
        List<string>? seenNames = logThisTick ? new List<string>() : null;

        for (int i = 0; i < handlers.Count; i++)
        {
            var h = handlers[i];
            var obj = h.GameObject;
            if (obj is not IPlayerCharacter pc)
                continue;

            playerCount++;
            if (pc.Address == localAddr)
                selfPlatePresent = true;

            var name = pc.Name.TextValue;
            seenNames?.Add(name);
            if (!IsSupporter(name))
                continue;

            matchedNames?.Add(name);

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

        if (logThisTick)
        {
            _logger.LogDebug("[Nameplate] Tick: handlers={handlers}, players={players}, selfPlate={selfPlate}, matched={matched} [{matchedNames}], seen=[{seenNames}]",
                handlers.Count, playerCount, selfPlatePresent, matchedNames?.Count ?? 0,
                string.Join(", ", matchedNames ?? Enumerable.Empty<string>()),
                string.Join(", ", seenNames ?? Enumerable.Empty<string>()));
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
        try { _namePlateGui.OnDataUpdate -= OnNamePlateUpdate; } catch (Exception ex) { _logger.LogDebug(ex, "Failed to unsubscribe from nameplate update"); }
        try { _namePlateGui.RequestRedraw(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to request nameplate redraw on dispose"); }
    }
}
