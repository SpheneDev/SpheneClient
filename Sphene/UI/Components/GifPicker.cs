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
/// incremental loading, favorites and a full-size detail preview before
/// sending. All provider access goes through <see cref="IGifProvider"/>
/// (consent, allowlist, bounds, attribution) so the picker stays
/// provider-agnostic. Selecting a GIF reports the provider item to the
/// host - sending stays the chat transport's job.
/// </summary>
public sealed class GifPicker : IDisposable
{
    private const int MaxSearchLength = 64;
    private const float PanelWidthLogical = 400f;
    private const float GridHeightLogical = 296f;
    private const int SearchDebounceMs = 350;
    private const int MaxConcurrentPreviewFetches = 4;

    private const int TabBrowse = 0;
    private const int TabFavorites = 1;
    private static readonly string[] PickerTabs = ["Browse", "Favorites"];

    private readonly IGifProvider _provider;
    private readonly SpheneConfigService _config;
    private readonly bool _stickers;
    // _items/_itemIds are swapped atomically, never mutated in place - the
    // result handler runs on a pool thread while Draw iterates the list.
    private List<GifItem> _items = new();
    private HashSet<string> _itemIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _previewRequested = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _previewSlots = new(MaxConcurrentPreviewFetches);
    private string _search = string.Empty;
    private string _issuedQuery = string.Empty;
    private int _nextPage;
    private bool _hasMore;
    private bool _loading;
    private string? _error;
    private CancellationTokenSource? _searchCts;
    private int _searchSeq;
    private DateTime _searchEditedAt = DateTime.MinValue;
    private bool _wantSearchFocus;
    private Action<GifItem>? _onPick;
    private bool _wasOpen;
    private string _activeFlyoutId = string.Empty;
    private string? _savedGridScrollId;
    private float _savedGridScrollY;
    private int _savedGridScrollTries;
    private int _scrollTopFrames;

    private int _tab;
    private GifItem? _detail;
    private bool _favItemsDirty = true;
    private readonly List<GifItem> _favItems = new();
    private readonly Dictionary<string, byte[]> _mediaBytes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mediaRequested = new(StringComparer.Ordinal);

    public GifPicker(IGifProvider provider, SpheneConfigService config,
        bool stickers = false)
    {
        _provider = provider;
        _config = config;
        _stickers = stickers;
    }

    /// <summary>Icon trigger for the chat composer - same control language
    /// as the emoji button. <paramref name="ghost"/> renders the
    /// borderless in-field style.</summary>
    public void TriggerButton(string flyoutId, Action<GifItem> onPick, bool enabled = true,
        bool ghost = false)
    {
        using var disabled = ImRaii.Disabled(!enabled);
        var clicked = _stickers
            ? (ghost ? SpheneUI.Icons.GhostButton(FontAwesomeIcon.StickyNote)
                : SpheneUI.Icons.Button(FontAwesomeIcon.StickyNote))
            : (ghost ? SpheneUI.Icons.GhostButton("GIF")
                : SpheneUI.Icons.Button("GIF"));
        if (clicked)
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
            ? (_stickers ? "Send a sticker" : "Send a GIF")
            : ((_stickers ? "Send a sticker" : "Send a GIF")
                + " (unavailable - provider not configured)"));
    }

    /// <summary>Renders the flyout while open; a no-op otherwise.</summary>
    public void Draw(string flyoutId)
    {
        var open = SpheneUI.Popups.IsFlyoutOpen(flyoutId);
        if (!open && _wasOpen)
        {
            ReleasePreviews();
            CancelPending();
            _detail = null;
            _savedGridScrollId = null;
        }
        else if (open && !_wasOpen)
        {
            _favItemsDirty = true;
            // fresh open starts at the top - the grid child keeps its
            // scroll across close/open by ImGui window state, so pin it
            // for the first frames. The detail->back restore below is a
            // different mechanism and stays unaffected.
            _scrollTopFrames = 3;
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
                "GIFs and stickers are provided by " + _provider.DisplayName + ". Searching and loading media contacts "
                + _provider.DisplayName + "'s servers with your search text and the ids you view. "
                + "Message contents stay end-to-end encrypted - only the search "
                + "and image requests leave this device.", ImGui.GetCursorPosX() + panelW);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            if (SpheneUI.Controls.Button("Enable external media"))
            {
                _config.Current.ChatExternalMediaEnabled = true;
                _config.Save();
                QueueSearch(reset: true);
            }
            SpheneUI.AttachTooltip("Can be changed any time in Settings");
            ImGui.Dummy(new Vector2(panelW, 1f));
            return;
        }

        if (_detail != null)
        {
            DrawDetail(_detail, panelW);
            return;
        }

        if (_tab == TabBrowse)
        {
            if (_wantSearchFocus)
            {
                ImGui.SetKeyboardFocusHere();
                _wantSearchFocus = false;
            }
            if (SpheneUI.Controls.InputTextWithHint("##gif-search",
                _provider.SearchPlaceholder + (_stickers ? " stickers" : ""),
                ref _search, MaxSearchLength, preferredWidth: panelW))
            {
                _searchEditedAt = DateTime.UtcNow;
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);

            // debounce live search
            if (_searchEditedAt != DateTime.MinValue
                && (DateTime.UtcNow - _searchEditedAt).TotalMilliseconds >= SearchDebounceMs)
            {
                _searchEditedAt = DateTime.MinValue;
                if (!string.Equals(_issuedQuery, _search.Trim(), StringComparison.Ordinal))
                {
                    QueueSearch(reset: true);
                }
            }
        }

        SpheneUI.Controls.Segmented("gif-tabs", ref _tab, PickerTabs, panelW);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);

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
        if (_tab == TabFavorites)
        {
            RefreshFavorites();
            DrawItemGrid(panelW, gridH, _favItems, "gif-grid-fav" + (_stickers ? "-s" : ""),
                _stickers
                    ? "No favorites yet - star any sticker to pin it here."
                    : "No favorites yet - star any GIF to pin it here.");
            return;
        }
        if (_loading && _items.Count == 0)
        {
            SpheneUI.Layout.BeginScrollArea("##gif-grid", new Vector2(panelW, gridH), border: true);
            SpheneUI.Text.MutedWrapped(_provider.RateLimited
                ? _provider.DisplayName + " is rate limiting requests - retry in a moment."
                : (_stickers ? "Loading stickers..." : "Loading GIFs..."));
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
        DrawItemGrid(panelW, gridH, _items, _stickers ? "sticker-grid" : "gif-grid",
            _stickers ? "No stickers found." : "No GIFs found.");
    }

    /// <summary>Shared square-cell grid for browse results and favorites.
    /// Scroll-windowed: only visible rows submit cells.</summary>
    private void DrawItemGrid(float panelW, float gridH, List<GifItem> items,
        string scrollId, string emptyText)
    {
        if (!SpheneUI.Layout.BeginScrollArea("##" + scrollId,
            new Vector2(panelW, gridH), border: true))
        {
            return;
        }
        // Re-apply the scroll position the user left at before a detail
        // preview - the child skips frames in detail mode, and ImGui can
        // lose its scroll state while it is away. ScrollMax only becomes
        // reliable once content has laid out again, so retry briefly.
        // A fresh flyout open pins to the top instead.
        if (_scrollTopFrames > 0)
        {
            ImGui.SetScrollY(0f);
            _scrollTopFrames--;
        }
        else if (string.Equals(_savedGridScrollId, scrollId, StringComparison.Ordinal))
        {
            var maxY = ImGui.GetScrollMaxY();
            if (maxY > 0.5f)
            {
                ImGui.SetScrollY(MathF.Min(_savedGridScrollY, maxY));
                _savedGridScrollId = null;
            }
            else if (--_savedGridScrollTries <= 0)
            {
                _savedGridScrollId = null;
            }
        }
        if (items.Count == 0)
        {
            SpheneUI.Text.MutedWrapped(emptyText);
        }
        else
        {
            var scale = SpheneUI.Scale;
            // Discord-style 2-up rows: a row's cells share one height and
            // split the width by aspect ratio, so every media shows whole
            // instead of cropped to a square. Spacing: gap between medias,
            // half that as the grid's outer edge.
            var gap = 8f * scale;
            var edge = gap * 0.5f;
            const int cols = 2;
            // only a max clamp: it leaves the row underfilled (cells stay
            // whole), while a min clamp could overflow the right edge
            var maxRowH = 240f * scale;
            var x0 = ImGui.GetCursorPosX();
            var availW = ImGui.GetContentRegionAvail().X;
            var rowW = availW - edge * 2f;
            var baseY = ImGui.GetCursorPosY();
            var rows = (items.Count + cols - 1) / cols;
            // variable row heights need a prefix pass for the scroll window
            var rowTop = new List<float>(rows + 1);
            var rowH = new List<float>(rows);
            var acc = 0f;
            for (var r = 0; r < rows; r++)
            {
                var paired = r * cols + 1 < items.Count;
                var a = Aspect(items[r * cols])
                    + (paired ? Aspect(items[r * cols + 1]) : 0f);
                var h = MathF.Min((rowW - (paired ? gap : 0f))
                    / MathF.Max(a, 0.01f), maxRowH);
                rowTop.Add(acc);
                rowH.Add(h);
                acc += h + gap;
            }
            rowTop.Add(acc - gap); // bottom of the last row - no trailing gap
            var scrollY = ImGui.GetScrollY();
            var winH = ImGui.GetWindowHeight();
            var firstRow = 0;
            while (firstRow < rows - 1
                && rowTop[firstRow] + rowH[firstRow] + gap <= scrollY)
            {
                firstRow++;
            }
            var lastRow = rows - 1;
            while (lastRow > firstRow && rowTop[lastRow] > scrollY + winH)
            {
                lastRow--;
            }
            ImGui.SetCursorPosY(baseY + edge + rowTop[firstRow]);
            for (var r = firstRow; r <= lastRow; r++)
            {
                ImGui.SetCursorPos(new Vector2(x0 + edge, baseY + edge + rowTop[r]));
                var c0 = r * cols;
                var c1 = Math.Min(c0 + cols - 1, items.Count - 1);
                for (var c = c0; c <= c1; c++)
                {
                    if (c > c0)
                    {
                        ImGui.SameLine(0f, gap);
                    }
                    var item = items[c];
                    DrawPreviewCell(item, rowH[r] * Aspect(item), rowH[r], scrollId);
                }
            }
            // land the cursor on the content bottom exactly - SetCursorPosY
            // extends CursorMaxPos, so the child's scroll range stays
            // truthful without reserving dummies for skipped rows
            ImGui.SetCursorPosY(baseY + edge + rowTop[rows]);
            ImGui.Dummy(new Vector2(1f, edge));
            // incremental page loading - browse tab only, right-docked
            // on the grid's right edge.
            if (_tab == TabBrowse && _hasMore && !_loading)
            {
                var btnW = SpheneUI.Controls.SmallButtonWidth("Load more");
                ImGui.SetCursorPosX(x0 + availW - edge - btnW);
                if (SpheneUI.Controls.SmallButton("Load more"))
                {
                    QueueSearch(reset: false);
                }
            }
        }
        SpheneUI.Layout.EndScrollArea();
    }

    private void DrawPreviewCell(GifItem item, float w, float h, string scrollId)
    {
        var scale = SpheneUI.Scale;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##gif" + item.Id, new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        // the media fills the cell - breathing room comes from the grid's
        // gap/edge spacing; hover feedback hugs the media rect.
        var mediaMin = pos;
        var mediaMax = pos + new Vector2(w, h);
        if (hovered)
        {
            dl.AddRectFilled(mediaMin, mediaMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.FrameBgHover), 4f * scale);
            if (!string.IsNullOrEmpty(item.Title))
            {
                SpheneUI.AttachTooltip(item.Title + " - click to preview");
            }
        }
        EnsurePreview(item);
        var anim = _previewBytes.TryGetValue(item.Id, out var bytes)
            ? SpheneUI.Media.Animated(PreviewKey(item.Id), bytes)
            : null;
        if (anim != null)
        {
            var still = !_config.Current.ChatGifAnimation;
            SpheneUI.Media.AnimatedInRect(dl, anim, mediaMin, mediaMax,
                ImageFit.Fit, rounding: 4f * scale, time: still ? 0 : null);
        }
        else
        {
            SpheneUI.Media.PlaceholderInRect(dl, mediaMin, mediaMax,
                4f * scale, border: false, icon: FontAwesomeIcon.Images);
        }
        if (hovered)
        {
            dl.AddRect(mediaMin, mediaMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Accent), 4f * scale,
                ImDrawFlags.RoundCornersAll, MathF.Max(1f, scale));
        }
        var starHot = DrawCellStar(item, pos, new Vector2(w, h), hovered, dl);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !starHot)
        {
            _detail = item;
            _savedGridScrollId = scrollId;
            _savedGridScrollY = ImGui.GetScrollY();
            _savedGridScrollTries = 3;
        }
    }

    /// <summary>Favorite star glyph in the cell's top-right corner - icon
    /// only, no plate (per owner feedback), just a 1px drop shadow so it
    /// survives bright frames. Hit-testing is manual (no extra ImGui item)
    /// so the grid's SameLine flow is untouched; a hit suppresses the
    /// cell's preview-open click.</summary>
    private bool DrawCellStar(GifItem item, Vector2 pos, Vector2 size,
        bool cellHovered, ImDrawListPtr dl)
    {
        var scale = SpheneUI.Scale;
        var hit = 18f * scale;
        var hitMin = pos + new Vector2(size.X - hit, 0f);
        var pinned = IsFavorite(item);
        var starHot = cellHovered
            && ImGui.IsMouseHoveringRect(hitMin, hitMin + new Vector2(hit));
        if (!pinned && !cellHovered)
        {
            return false;
        }
        var glyph = FontAwesomeIcon.Star.ToIconString();
        using (SpheneUI.IconFont.Push())
        {
            var gs = ImGui.CalcTextSize(glyph);
            var glyphMin = hitMin + (new Vector2(hit) - gs) * 0.5f;
            var shadow = ImGui.GetColorU32(new Vector4(0f, 0f, 0f,
                pinned || starHot ? 0.75f : 0.5f));
            var col = pinned
                ? SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.Accent)
                : ImGui.GetColorU32(new Vector4(1f, 1f, 1f, starHot ? 0.95f : 0.6f));
            dl.AddText(glyphMin + new Vector2(scale), shadow, glyph);
            dl.AddText(glyphMin, col, glyph);
        }
        if (starHot)
        {
            SpheneUI.AttachTooltip(pinned ? "Remove from favorites" : "Add to favorites");
        }
        if (starHot && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            ToggleFavorite(item);
        }
        return starHot;
    }

    private static float Aspect(GifItem item)
        => item is { Width: > 0, Height: > 0 }
            ? Math.Clamp((float)item.Width / item.Height, 0.3f, 3f)
            : 1f;

    /// <summary>Detail view: the media preview with title, favorite toggle
    /// and the Send action. The flyout is AlwaysAutoResize, so the body is
    /// held at the browse body's exact height - the window rect stays
    /// pinned across the grid&lt;-&gt;detail swap and its border never
    /// overhangs for a frame.</summary>
    private void DrawDetail(GifItem item, float panelW)
    {
        var scale = SpheneUI.Scale;
        var frameH = ImGui.GetFrameHeight();
        var x2 = SpheneUI.Spacing.X2.Scaled;
        var x4 = SpheneUI.Spacing.X4.Scaled;
        var lineH = ImGui.GetTextLineHeight();
        // browse body = search row + tabs row + grid + attribution row:
        var bodyH = frameH * 2f + x2 * 3f + GridHeightLogical * scale + lineH;
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowLeft, "Back"))
        {
            _detail = null;
            return;
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        EnsureMedia(item);
        EnsurePreview(item);
        var dl = ImGui.GetWindowDrawList();
        // Hold the cell preview until the full-media anim has decoded -
        // the media bytes arriving mid-frame used to blank the preview
        // for the whole decode pass (~seconds on big files).
        var media = MediaBytes(item.Id);
        var anim = media != null
            ? SpheneUI.Media.Animated(DetailKey(item.Id), media)
            : null;
        if (anim == null && _previewBytes.TryGetValue(item.Id, out var pb))
        {
            anim = SpheneUI.Media.Animated(PreviewKey(item.Id), pb);
        }
        var availW = MathF.Min(panelW, ImGui.GetContentRegionAvail().X);
        var titleBlock = string.IsNullOrEmpty(item.Title) ? 0f : lineH + x2;
        // the media viewport consumes whatever the chrome leaves over, so
        // the stacked content ends exactly at bodyH
        var mediaH = bodyH - (frameH + x4 + x2 + titleBlock + frameH + x2 + lineH);
        var size = Helpers.Emoji.GifFit.Fit(item.Width, item.Height, availW, scale);
        if (size.Y > mediaH)
        {
            size *= mediaH / size.Y;
        }
        var mediaPos = ImGui.GetCursorScreenPos()
            + new Vector2((availW - size.X) * 0.5f,
                MathF.Max(0f, mediaH - size.Y) * 0.5f);
        ImGui.Dummy(new Vector2(availW, mediaH));
        if (anim != null)
        {
            var still = !_config.Current.ChatGifAnimation;
            SpheneUI.Media.AnimatedInRect(dl, anim, mediaPos, mediaPos + size,
                ImageFit.Fit, rounding: 4f * scale, time: still ? 0 : null);
        }
        else
        {
            SpheneUI.Media.PlaceholderInRect(dl, mediaPos, mediaPos + size,
                4f * scale, border: false, icon: FontAwesomeIcon.Images);
        }
        if (titleBlock > 0f)
        {
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
            SpheneUI.Text.Muted(TrimToWidth(item.Title, availW));
            SpheneUI.AttachTooltip(item.Title);
        }
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
        var rowStartX = ImGui.GetCursorPosX();
        var pinned = IsFavorite(item);
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Star,
            pinned ? "Favorited" : "Favorite"))
        {
            ToggleFavorite(item);
        }
        var sendW = SpheneUI.Controls.SmallButtonWidth("Send");
        ImGui.SameLine(rowStartX + panelW - sendW);
        if (SpheneUI.Controls.Button("Send"))
        {
            var cb = _onPick;
            if (_activeFlyoutId.Length > 0)
            {
                SpheneUI.Popups.CloseFlyout(_activeFlyoutId);
            }
            cb?.Invoke(item);
        }
        SpheneUI.AttachTooltip(_stickers
            ? "Send this sticker to the chat"
            : "Send this GIF to the chat");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
        var attr = _provider.AttributionText;
        var attrW = ImGui.CalcTextSize(attr).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + panelW - attrW);
        SpheneUI.Text.Colored(attr, SpheneUI.Colors.TextMuted with { W = 0.6f });
    }

    private static string TrimToWidth(string text, float maxW)
    {
        if (ImGui.CalcTextSize(text).X <= maxW)
        {
            return text;
        }
        var n = text.Length;
        while (n > 1 && ImGui.CalcTextSize(text[..n] + "...").X > maxW)
        {
            n--;
        }
        return n <= 1 ? text : text[..n].TrimEnd() + "...";
    }

    // keys live in the global Media cache - the catalog prefix keeps a
    // gif id and a sticker id from aliasing the same cached anim
    private string PreviewKey(string id) => (_stickers ? "stkprev:" : "gifprev:") + id;
    private string DetailKey(string id) => (_stickers ? "stkd:" : "gifd:") + id;

    private readonly Dictionary<string, byte[]> _previewBytes = new(StringComparer.Ordinal);
    private readonly System.Threading.Lock _previewBytesLock = new();

    private bool IsFavorite(GifItem item)
        => GifFavorites.IsPinned(_config.Current.GifFavorites,
            _provider.ProviderId, item.Id);

    private void ToggleFavorite(GifItem item)
    {
        GifFavorites.Toggle(_config.Current.GifFavorites,
            _provider.ProviderId, item);
        _favItemsDirty = true;
        _config.Save();
    }

    /// <summary>Materializes the favorites tab's items from config, filtered
    /// to the active provider. Rebuilt only on toggle/open.</summary>
    private void RefreshFavorites()
    {
        if (!_favItemsDirty)
        {
            return;
        }
        _favItemsDirty = false;
        _favItems.Clear();
        var favs = _config.Current.GifFavorites;
        for (var i = 0; i < favs.Count; i++)
        {
            if (string.Equals(favs[i].Provider, _provider.ProviderId,
                StringComparison.Ordinal)
                && favs[i].IsSticker == _stickers)
            {
                _favItems.Add(GifFavorites.ToItem(favs[i]));
            }
        }
    }

    /// <summary>Full-media fetch for the detail preview - same rendition the
    /// chat bubble will render, so the preview is WYSIWYG.</summary>
    private void EnsureMedia(GifItem item)
    {
        lock (_previewBytesLock)
        {
            if (_mediaBytes.ContainsKey(item.Id) || !_mediaRequested.Add(item.Id))
            {
                return;
            }
        }
        _ = Task.Run(async () =>
        {
            await _previewSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                var bytes = await _provider.FetchGifBytesAsync(item, CancellationToken.None)
                    .ConfigureAwait(false);
                if (bytes != null)
                {
                    lock (_previewBytesLock)
                    {
                        _mediaBytes[item.Id] = bytes;
                    }
                }
            }
            catch
            {
                // media fetch failures keep the preview-bytes fallback
            }
            finally
            {
                _previewSlots.Release();
            }
        });
    }

    private byte[]? MediaBytes(string id)
    {
        lock (_previewBytesLock)
        {
            return _mediaBytes.GetValueOrDefault(id);
        }
    }

    private void EnsurePreview(GifItem item)
    {
        lock (_previewBytesLock)
        {
            if (_previewBytes.ContainsKey(item.Id) || !_previewRequested.Add(item.Id))
            {
                return;
            }
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
        _issuedQuery = query;
        var seq = ++_searchSeq;
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
                    ? (_stickers
                        ? await _provider.TrendingStickersAsync(page, ct).ConfigureAwait(false)
                        : await _provider.TrendingAsync(page, ct).ConfigureAwait(false))
                    : (_stickers
                        ? await _provider.SearchStickersAsync(query, page, ct).ConfigureAwait(false)
                        : await _provider.SearchAsync(query, page, ct).ConfigureAwait(false));
                // latest-wins: a cancelled or superseded request must never
                // touch _items - an out-of-order clear is what used to dump
                // loaded pages back to the first trending page.
                if (ct.IsCancellationRequested
                    || seq != Volatile.Read(ref _searchSeq))
                {
                    return;
                }
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
                var next = reset ? new List<GifItem>(result.Items.Count) : new List<GifItem>(_items);
                var nextIds = reset ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(_itemIds, StringComparer.Ordinal);
                foreach (var item in result.Items)
                {
                    if (nextIds.Add(item.Id))
                    {
                        next.Add(item);
                    }
                }
                if (reset)
                {
                    ReleasePreviews();
                }
                _itemIds = nextIds;
                _items = next;
            }
            finally
            {
                if (seq == Volatile.Read(ref _searchSeq))
                {
                    _loading = false;
                }
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
        List<string> mediaIds;
        lock (_previewBytesLock)
        {
            ids = new List<string>(_previewRequested);
            _previewRequested.Clear();
            _previewBytes.Clear();
            mediaIds = new List<string>(_mediaRequested);
            _mediaRequested.Clear();
            _mediaBytes.Clear();
        }
        foreach (var id in ids)
        {
            SpheneUI.Media.Evict(PreviewKey(id));
        }
        foreach (var id in mediaIds)
        {
            SpheneUI.Media.Evict(DetailKey(id));
        }
    }

    public void Dispose()
    {
        CancelPending();
        ReleasePreviews();
        _previewSlots.Dispose();
    }
}
