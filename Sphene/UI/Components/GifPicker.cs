using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.Services.Gifs;
using Sphene.SpheneConfiguration;
using Sphene.UI.Design;

namespace Sphene.UI.Components;

/// <summary>
/// Private-chat GIF picker: a themed flyout with search, trending results,
/// incremental loading and live animated previews. All provider access goes
/// through <see cref="IGifProvider"/> (consent, allowlist, bounds,
/// attribution) so the picker stays provider-agnostic. Selecting a GIF
/// reports the provider item to the host - sending stays the chat
/// transport's job.
/// </summary>
public sealed class GifPicker : IDisposable
{
    private const int MaxSearchLength = 64;
    private const float PanelWidthLogical = 336f;
    private const float GridHeightLogical = 232f;
    private const float PreviewCellLogical = 78f;
    private const int SearchDebounceMs = 350;
    private const int MaxConcurrentPreviewFetches = 4;

    private readonly IGifProvider _provider;
    private readonly SpheneConfigService _config;
    private readonly List<GifItem> _items = new();
    private readonly HashSet<string> _itemIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _previewRequested = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _previewSlots = new(MaxConcurrentPreviewFetches);
    private string _search = string.Empty;
    private string _activeQuery = string.Empty;
    private int _nextPage;
    private bool _hasMore;
    private bool _loading;
    private string? _error;
    private CancellationTokenSource? _searchCts;
    private DateTime _searchEditedAt = DateTime.MinValue;
    private bool _wantSearchFocus;
    private Action<GifItem>? _onPick;
    private bool _wasOpen;
    private string _activeFlyoutId = string.Empty;

    public GifPicker(IGifProvider provider, SpheneConfigService config)
    {
        _provider = provider;
        _config = config;
    }

    /// <summary>Small "GIF" trigger for the chat composer.</summary>
    public void TriggerButton(string flyoutId, Action<GifItem> onPick, bool enabled = true)
    {
        using var disabled = ImRaii.Disabled(!enabled);
        if (SpheneUI.Controls.Button("GIF"))
        {
            _onPick = onPick;
            if (!SpheneUI.Popups.IsFlyoutOpen(flyoutId))
            {
                _wantSearchFocus = true;
                if (_items.Count == 0 && _error == null)
                {
                    QueueSearch(reset: true);
                }
            }
            SpheneUI.Popups.ToggleFlyout(flyoutId, preferAbove: true);
        }
        SpheneUI.AttachTooltip(enabled
            ? "Send a GIF"
            : "Send a GIF (unavailable - provider not configured)");
    }

    /// <summary>Renders the flyout while open; a no-op otherwise.</summary>
    public void Draw(string flyoutId)
    {
        var open = SpheneUI.Popups.IsFlyoutOpen(flyoutId);
        if (!open && _wasOpen)
        {
            ReleasePreviews();
            CancelPending();
        }
        _wasOpen = open;
        if (!SpheneUI.Popups.BeginFlyout(flyoutId))
        {
            return;
        }
        _activeFlyoutId = flyoutId;
        var scale = SpheneUI.Scale;
        var panelW = PanelWidthLogical * scale;
        DrawBody(panelW);
        SpheneUI.Popups.EndFlyout();
    }

    private void DrawBody(float panelW)
    {
        if (!_provider.IsConfigured)
        {
            SpheneUI.Text.MutedWrapped(
                "GIF search is not available for this account yet. The server grants "
                + _provider.DisplayName + " access gradually while the feature is in testing.",
                ImGui.GetCursorPosX() + panelW);
            ImGui.Dummy(new Vector2(panelW, 1f));
            return;
        }
        if (!_provider.ConsentGiven)
        {
            SpheneUI.Text.Wrapped(
                "GIFs are provided by " + _provider.DisplayName + ". Searching and loading GIFs contacts "
                + _provider.DisplayName + "'s servers with your search text and the GIF ids you view. "
                + "Message contents stay end-to-end encrypted - only the search "
                + "and image requests leave this device.", ImGui.GetCursorPosX() + panelW);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            if (SpheneUI.Controls.Button("Enable external GIFs"))
            {
                _config.Current.ChatExternalMediaEnabled = true;
                _config.Save();
                QueueSearch(reset: true);
            }
            SpheneUI.AttachTooltip("Can be changed any time in Settings");
            ImGui.Dummy(new Vector2(panelW, 1f));
            return;
        }

        if (_wantSearchFocus)
        {
            ImGui.SetKeyboardFocusHere();
            _wantSearchFocus = false;
        }
        if (SpheneUI.Controls.InputTextWithHint("##gif-search", _provider.SearchPlaceholder + "…",
            ref _search, MaxSearchLength, preferredWidth: panelW))
        {
            _searchEditedAt = DateTime.UtcNow;
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        // debounce live search
        if (_searchEditedAt != DateTime.MinValue
            && (DateTime.UtcNow - _searchEditedAt).TotalMilliseconds >= SearchDebounceMs)
        {
            _searchEditedAt = DateTime.MinValue;
            if (!string.Equals(_activeQuery, _search.Trim(), StringComparison.Ordinal))
            {
                QueueSearch(reset: true);
            }
        }

        DrawResults(panelW);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
        var attr = _provider.AttributionText;
        var attrW = ImGui.CalcTextSize(attr).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + panelW - attrW);
        SpheneUI.Text.Colored(attr, SpheneUI.Colors.TextMuted with { W = 0.6f });
    }

    private void DrawResults(float panelW)
    {
        var scale = SpheneUI.Scale;
        var gridH = GridHeightLogical * scale;
        if (_loading && _items.Count == 0)
        {
            SpheneUI.Layout.BeginScrollArea("##gif-grid", new Vector2(panelW, gridH), border: true);
            SpheneUI.Text.MutedWrapped(_provider.RateLimited
                ? _provider.DisplayName + " is rate limiting requests - retry in a moment."
                : "Loading GIFs…");
            SpheneUI.Layout.EndScrollArea();
            return;
        }
        if (_error != null && _items.Count == 0)
        {
            SpheneUI.Layout.BeginScrollArea("##gif-grid", new Vector2(panelW, gridH), border: true);
            SpheneUI.Text.MutedWrapped(_error);
            SpheneUI.Layout.EndScrollArea();
            return;
        }
        if (!SpheneUI.Layout.BeginScrollArea("##gif-grid", new Vector2(panelW, gridH), border: true))
        {
            return;
        }
        if (_items.Count == 0)
        {
            SpheneUI.Text.MutedWrapped("No GIFs found.");
        }
        else
        {
            var cell = PreviewCellLogical * scale;
            var gap = 3f * scale;
            var cols = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + gap) / (cell + gap)));
            var rows = (_items.Count + cols - 1) / cols;
            var rowH = cell + gap;
            var scrollY = ImGui.GetScrollY();
            var firstRow = Math.Max(0, (int)(scrollY / rowH) - 1);
            var lastRow = Math.Min(rows - 1, (int)((scrollY + ImGui.GetWindowHeight()) / rowH) + 1);
            if (firstRow > 0)
            {
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + firstRow * rowH);
            }
            for (var r = firstRow; r <= lastRow; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    var idx = r * cols + c;
                    if (c > 0)
                    {
                        ImGui.SameLine(0f, gap);
                    }
                    if (idx >= _items.Count)
                    {
                        break;
                    }
                    DrawPreviewCell(_items[idx], cell);
                }
                if (r == lastRow && rows > lastRow + 1)
                {
                    ImGui.Dummy(new Vector2(1f, (rows - lastRow - 1) * rowH));
                }
            }
            // incremental page loading
            if (_hasMore && !_loading
                && SpheneUI.Controls.SmallButton("Load more"))
            {
                QueueSearch(reset: false);
            }
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawPreviewCell(GifItem item, float cell)
    {
        var scale = SpheneUI.Scale;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##gif" + item.Id, new Vector2(cell, cell));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new Vector2(cell),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.FrameBgHover), 4f * scale);
            if (!string.IsNullOrEmpty(item.Title))
            {
                SpheneUI.AttachTooltip(item.Title);
            }
        }
        EnsurePreview(item);
        var anim = _previewBytes.TryGetValue(item.Id, out var bytes)
            ? SpheneUI.Media.Animated(PreviewKey(item.Id), bytes)
            : null;
        if (anim != null)
        {
            var still = !_config.Current.ChatGifAnimation;
            SpheneUI.Media.AnimatedInRect(ImGui.GetWindowDrawList(), anim,
                pos + new Vector2(1.5f * scale), pos + new Vector2(cell - 1.5f * scale),
                ImageFit.Cover, rounding: 4f * scale, time: still ? 0 : null);
        }
        else
        {
            SpheneUI.Media.PlaceholderInRect(ImGui.GetWindowDrawList(),
                pos + new Vector2(1.5f * scale), pos + new Vector2(cell - 1.5f * scale),
                4f * scale, border: false, icon: FontAwesomeIcon.Images);
        }
        if (hovered)
        {
            ImGui.GetWindowDrawList().AddRect(pos, pos + new Vector2(cell),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Accent), 4f * scale,
                ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            var cb = _onPick;
            if (_activeFlyoutId.Length > 0)
            {
                SpheneUI.Popups.CloseFlyout(_activeFlyoutId);
            }
            cb?.Invoke(item);
        }
    }

    private static string PreviewKey(string id) => "gifprev:" + id;

    private readonly Dictionary<string, byte[]> _previewBytes = new(StringComparer.Ordinal);
    private readonly System.Threading.Lock _previewBytesLock = new();

    private void EnsurePreview(GifItem item)
    {
        bool already;
        lock (_previewBytesLock)
        {
            already = _previewBytes.ContainsKey(item.Id);
        }
        if (already || !_previewRequested.Add(item.Id))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            await _previewSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                var bytes = await _provider.FetchPreviewBytesAsync(item, CancellationToken.None)
                    .ConfigureAwait(false);
                if (bytes != null)
                {
                    lock (_previewBytesLock)
                    {
                        _previewBytes[item.Id] = bytes;
                    }
                }
            }
            catch
            {
                // fetch failures stay as placeholder cells
            }
            finally
            {
                _previewSlots.Release();
            }
        });
    }

    private void QueueSearch(bool reset)
    {
        CancelPending();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var page = reset ? 0 : _nextPage;
        var query = _search.Trim();
        _loading = true;
        if (reset)
        {
            _error = null;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var result = string.IsNullOrEmpty(query)
                    ? await _provider.TrendingAsync(page, ct).ConfigureAwait(false)
                    : await _provider.SearchAsync(query, page, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                _activeQuery = query;
                _hasMore = result.HasMore;
                if (result.RateLimited)
                {
                    _error = _provider.DisplayName + " is rate limiting requests - retry in a moment.";
                }
                else if (result.Error != null)
                {
                    _error = string.Equals(result.Error, "no-key", StringComparison.Ordinal)
                        ? "GIF search is not configured for this server."
                        : "Could not reach " + _provider.DisplayName + " - check your connection.";
                }
                else
                {
                    _error = null;
                    _nextPage = page + 1;
                }
                if (reset)
                {
                    _items.Clear();
                    _itemIds.Clear();
                    ReleasePreviews();
                }
                foreach (var item in result.Items)
                {
                    if (_itemIds.Add(item.Id))
                    {
                        _items.Add(item);
                    }
                }
            }
            finally
            {
                _loading = false;
            }
        }, ct);
    }

    private void CancelPending()
    {
        try
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // CTS raced with Dispose - nothing left to cancel
        }
        _searchCts = null;
        _loading = false;
    }

    private void ReleasePreviews()
    {
        List<string> ids;
        lock (_previewBytesLock)
        {
            ids = new List<string>(_previewRequested);
            _previewRequested.Clear();
            _previewBytes.Clear();
        }
        foreach (var id in ids)
        {
            SpheneUI.Media.Evict(PreviewKey(id));
        }
    }

    public void Dispose()
    {
        CancelPending();
        ReleasePreviews();
        _previewSlots.Dispose();
    }
}
