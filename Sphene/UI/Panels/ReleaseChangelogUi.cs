using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Sphene.UI.Components;
using System.Numerics;
using System.Linq;
using System.Text;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public class ReleaseChangelogUi : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiShared;
    private readonly SpheneConfigService _configService;
    private readonly ChangelogService _changelogService;
    private string _version = string.Empty;
    private volatile bool _loading;
    private List<ReleaseChangelogViewEntry> _entries = new();
    private bool _showTestBuildReleases;
    private string _pendingExpandVersion = string.Empty;
    private bool _embeddedInitialized;
    private bool _entriesRequested;
    private DateTime _entriesFetchedUtc;

    public ReleaseChangelogUi(
        ILogger<ReleaseChangelogUi> logger,
        UiSharedService uiShared,
        SpheneConfigService configService,
        SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        ChangelogService changelogService)
        : base(logger, mediator, "Sphene Release Notes", performanceCollectorService)
    {
        _uiShared = uiShared;
        _configService = configService;
        _changelogService = changelogService;
        RespectCloseHotkey = true;
        ShowCloseButton = true;
        IsOpen = false;

        SizeConstraints = new()
        {
            MinimumSize = new Vector2(600, 507),
            MaximumSize = new Vector2(600, 507),
        };
        Flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;

        Mediator.Subscribe<ShowReleaseChangelogMessage>(this, (msg) =>
        {
            _version = msg.CurrentVersion;
            WindowName = $"Sphene Release Notes - {_version}";
            _loading = true;
            _entries = new List<ReleaseChangelogViewEntry>();
            _pendingExpandVersion = string.Empty;
            IsOpen = true;

#if IS_TEST_BUILD
            _showTestBuildReleases = true;
#else
            _showTestBuildReleases = _configService.Current.ShowTestBuildChangelogs;
#endif

            StartLoadingEntries();
        });
    }

    /// <summary>Programmatically selects a release (news-card deep links,
    /// history restore). Applied to the row list on next draw.</summary>
    public void SelectRelease(string version)
    {
        _selectedVersion = version;
        _pendingExpandVersion = version;
    }

    private void StartLoadingEntries()
    {
        _entriesRequested = true;
        _loading = true;
        _entries = new List<ReleaseChangelogViewEntry>();
        _pendingExpandVersion = string.Empty;

        _ = Task.Run(async () =>
        {
            try
            {
                var list = await _changelogService.GetChangelogEntriesAsync().ConfigureAwait(false);
                var currentVersion = ParseVersionSafe(_version);
                var filtered = list.Where(e => ParseVersionSafe(e.Version) <= currentVersion);
                if (!_showTestBuildReleases)
                {
                    filtered = filtered.Where(e => !e.IsPrerelease);
                }
                _entriesFetchedUtc = DateTime.UtcNow;
                _entries = filtered
                    .OrderByDescending(e => ParseVersionSafe(e.Version))
                    .ToList();
                _pendingExpandVersion = _entries.FirstOrDefault()?.Version ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load release changelog entries");
            }
            finally
            {
                _loading = false;
            }
        });
    }

    public override void OnClose()
    {
        // Persist last seen version when user closes the window
        if (!string.IsNullOrEmpty(_version))
        {
            _configService.Current.LastSeenVersionChangelog = _version;
            _configService.Save();
        }
        Mediator.Publish(new ReleaseChangelogClosedMessage());
    }

    protected override void DrawInternal()
    {
        if (_uiShared.IsInGpose)
            return;

        var childBg = ImGui.GetStyle().Colors[(int)ImGuiCol.ChildBg];
        var opaqueChildBg = new Vector4(childBg.X, childBg.Y, childBg.Z, 1f);

        SpheneUI.Layout.PageHeader("Release Notes", "changelog", () =>
        {
            SpheneUI.Text.MutedWrapped("What changed in this release - fixes, features and things worth knowing.", ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
        });

        DrawControls();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + (ImGui.GetStyle().ItemSpacing.Y * 2f) + (ImGuiHelpers.GlobalScale * 6f);
        var changelogPaneHeight = SpheneUI.Layout.FillToViewBottomHeight(footerHeight);
        using (ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.GetColorU32(opaqueChildBg)))
        {
            var scrollBegun = SpheneUI.Layout.BeginScrollArea("ChangelogPane", new Vector2(-1, changelogPaneHeight), border: true,
                windowFlags: ImGuiWindowFlags.NoNav
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse);
            if (scrollBegun)
            {
                var childWindowPos = ImGui.GetWindowPos();
                var childWindowSize = ImGui.GetWindowSize();
                DrawSpheneWatermark(ImGui.GetWindowDrawList(), childWindowPos, childWindowPos + childWindowSize, 0.09f, -0.06f);
                DrawReleaseList();
            }
            SpheneUI.Layout.EndScrollArea();
        }

        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
        if (SpheneUI.Controls.PrimaryButton("Okay close!", FontAwesomeIcon.Check))
        {
            // Close will persist the last seen version
            IsOpen = false;
        }
    }

    internal void DrawEmbedded()
    {
        EnsureEmbeddedDefaults();
        if (!_entriesRequested
            || DateTime.UtcNow - _entriesFetchedUtc > TimeSpan.FromMinutes(5))
        {
            StartLoadingEntries();
        }

        DrawControls();
        var paneH = SpheneUI.Layout.FillToViewBottomHeight(4f);
        if (SpheneUI.Layout.BeginScrollArea("changelog_embedded_pane",
                new Vector2(-1, paneH), border: false,
                windowFlags: ImGuiWindowFlags.NoNav
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawReleaseList();
            SpheneUI.Layout.EndScrollArea();
        }
    }

    private void EnsureEmbeddedDefaults()
    {
        if (_embeddedInitialized)
            return;
        _embeddedInitialized = true;

        if (!string.IsNullOrEmpty(_version))
            return;

        var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        _version = assemblyVersion != null
            ? (assemblyVersion.Revision > 0
                ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}.{assemblyVersion.Revision}"
                : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}")
            : string.Empty;
#if IS_TEST_BUILD
        _showTestBuildReleases = true;
#else
        _showTestBuildReleases = _configService.Current.ShowTestBuildChangelogs;
#endif

        if (!string.IsNullOrEmpty(_version)
            && !string.Equals(_configService.Current.LastSeenVersionChangelog, _version, StringComparison.Ordinal))
        {
            _configService.Current.LastSeenVersionChangelog = _version;
            _configService.Save();
        }
    }


    private void DrawControls()
    {
#if !IS_TEST_BUILD
        var showTest = _showTestBuildReleases;
        if (SpheneUI.Controls.Toggle("Testbuilds##rel_testbuild", ref showTest))
        {
            _showTestBuildReleases = showTest;
            _configService.Current.ShowTestBuildChangelogs = showTest;
            _configService.Save();
            StartLoadingEntries();
        }
        SpheneUI.AttachTooltip("Toggle whether prerelease/testbuild releases are visible.");
        SpheneUI.Layout.VerticalSpace();
#endif
    }

    private string? _selectedVersion;
    private float _colScrollH;
    private float _colScrollWinH;
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);

    private void DrawReleaseList()
    {
        ApplyPendingExpansion();
        if (_loading)
        {
            SpheneUI.Text.Muted("Loading release notes...");
            return;
        }
        if (_entries == null || _entries.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Newspaper, "No release notes",
                "Changelog entries appear here once a release ships.");
            return;
        }

        var list = _entries.ToList();
        var sel = list.FirstOrDefault(e =>
            string.Equals(e.Version, _selectedVersion, StringComparison.Ordinal));
        sel ??= list[0];
        _selectedVersion = sel.Version;

        // no outer scroll, no table - two sibling scroll areas positioned
        // per column (DESIGN.md column rule: SetCursorScreenPos +
        // ContentBounds, never nested scroll children inside scroll cells).
        var scale2 = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        var gap = SpheneUI.Spacing.CardGap.Scaled;
        var colW0 = availW * 0.36f - gap * 0.5f;
        var colW1 = availW * 0.64f - gap * 0.5f;

        // fixed scroll height from the stable window size - never from
        // per-frame avail (that shifts while scrolling → flicker).
        var winH = ImGui.GetWindowSize().Y;
        if (_colScrollH <= 0f || MathF.Abs(_colScrollWinH - winH) > 1f)
        {
            _colScrollWinH = winH;
            _colScrollH = MathF.Max(80f * scale2,
                winH - (origin.Y - ImGui.GetWindowPos().Y) - 8f * scale2);
        }

        // left column · header + scrollable version rail
        var c0min = origin.X;
        SpheneUI.ContentBounds.Push(new Vector2(c0min, c0min + colW0));
        SpheneUI.Text.Colored("Release Notes", SpheneUI.Colors.TextPrimary);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X2);
        SpheneUI.Text.MutedWrapped("All versions and changelogs in one place.",
            c0min + colW0);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        var verH = MathF.Max(60f * scale2,
            _colScrollH - (ImGui.GetCursorScreenPos().Y - origin.Y));
        if (SpheneUI.Layout.BeginScrollArea("rel-ver-scroll",
                new Vector2(colW0, verH)))
        {
            for (var i = 0; i < list.Count; i++)
            {
                DrawReleaseRowItem(list[i],
                    ReferenceEquals(list[i], sel), i == 0);
            }
            SpheneUI.Layout.EndScrollArea();
        }
        SpheneUI.ContentBounds.Pop();

        // right column · detail scroll aligned to the same top edge
        var c1min = c0min + colW0 + gap;
        ImGui.SetCursorScreenPos(new Vector2(c1min, origin.Y));
        SpheneUI.ContentBounds.Push(new Vector2(c1min, c1min + colW1));
        if (SpheneUI.Layout.BeginScrollArea("rel-det-scroll",
                new Vector2(colW1, _colScrollH)))
        {
            DrawReleaseDetail(sel);
            SpheneUI.Layout.EndScrollArea();
        }
        SpheneUI.ContentBounds.Pop();

        // land the cursor below both columns
        ImGui.SetCursorScreenPos(new Vector2(origin.X,
            origin.Y + _colScrollH));
    }

    private void DrawReleaseRowItem(ReleaseChangelogViewEntry e,
        bool selected, bool isLatest)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var accent = SpheneUI.Theme.Current.AccentCyan;
        var pad = 8f * scale;
        var w = ImGui.GetContentRegionAvail().X;
        var lineH = ImGui.GetTextLineHeight();
        var h = lineH * 2.3f + pad * 2f;
        var clicked = ImGui.InvisibleButton($"##relv_{e.Version}",
            new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();

        if (selected)
        {
            dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(accent, 0.16f)), 8f * scale);
            dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(accent, 0.5f)), 8f * scale);
        }
        else
        {
            dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
                hovered ? SpheneUI.Layout.NavRailBandColor()
                        : SpheneUI.Colors.Surface), 8f * scale);
            dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.3f)),
                8f * scale);
        }

        // line 1 · version + Latest badge + right-aligned date
        var tx = min.X + pad;
        var ty = min.Y + pad;
        dl.AddText(new Vector2(tx, ty), SpheneUI.ColorUtils.ToU32(
            e.IsPrerelease ? SpheneUI.Colors.Warning
                           : SpheneUI.Colors.TextPrimary),
            $"v{e.Version}");
        tx += ImGui.CalcTextSize($"v{e.Version}").X + 8f * scale;
        if (isLatest)
        {
            var badge = "Latest";
            var bw = ImGui.CalcTextSize(badge).X + 12f * scale;
            var bh = lineH * 1.15f;
            dl.AddRectFilled(new Vector2(tx, ty - 1f * scale),
                new Vector2(tx + bw, ty - 1f * scale + bh),
                SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(accent, 0.28f)), bh * 0.5f);
            dl.AddText(new Vector2(tx + 6f * scale, ty),
                SpheneUI.ColorUtils.ToU32(accent), badge);
        }
        var chev = FontAwesomeIcon.ChevronRight.ToIconString();
        float chevW;
        using (SpheneUI.Icons.PushFont())
        {
            chevW = ImGui.CalcTextSize(chev).X;
        }
        if (!string.IsNullOrWhiteSpace(e.Date))
        {
            var dateText = FormatReleaseDate(e.Date);
            var dw = ImGui.CalcTextSize(dateText).X;
            dl.AddText(new Vector2(max.X - pad - chevW - 10f * scale - dw, ty),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), dateText);
        }

        // line 2 · summary (title or description, ellipsized)
        var sub = !string.IsNullOrWhiteSpace(e.Title) ? e.Title
            : !string.IsNullOrWhiteSpace(e.Description) ? e.Description
            : string.Empty;
        if (sub.Length > 0)
        {
            dl.PushClipRect(new Vector2(min.X + pad, ty + lineH * 1.25f),
                new Vector2(max.X - pad - chevW - 8f * scale, max.Y), true);
            dl.AddText(new Vector2(min.X + pad, ty + lineH * 1.25f),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
                SpheneUI.Text.Ellipsize(sub,
                    max.X - min.X - pad * 2 - chevW - 8f * scale));
            dl.PopClipRect();
        }

        // chevron centered on the right edge
        using (SpheneUI.Icons.PushFont())
        {
            dl.AddText(new Vector2(max.X - pad - chevW,
                    min.Y + (h - lineH) * 0.5f),
                SpheneUI.ColorUtils.ToU32(
                    selected ? accent : SpheneUI.Colors.TextMuted), chev);
        }
        if (clicked)
        {
            _selectedVersion = e.Version;
        }
        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
    }

    private void DrawReleaseDetail(ReleaseChangelogViewEntry e)
    {
        var accent = SpheneUI.Theme.Current.AccentCyan;
        var scale = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();
        // card 1 · edge-to-edge banner hero with the release info overlaid
        // (mockup) - falls back to a plain text card when no banner exists.
        var bannerBytes = _changelogService.GetCachedImage(e.Banner);
        var wrap = bannerBytes != null
            ? SpheneUI.Media.Texture($"clb:{e.Banner}", () => bannerBytes)
            : null;
        var heroText = !string.IsNullOrWhiteSpace(e.Description)
            ? e.Description
            : !string.IsNullOrWhiteSpace(e.Title) ? e.Title : string.Empty;
        var hasDesc = !string.IsNullOrWhiteSpace(heroText);
        var lineH = ImGui.GetTextLineHeight();

        if (wrap != null)
        {
            var bw = ImGui.GetContentRegionAvail().X;
            var bh = MathF.Min(bw * 0.30f, 190f * scale);
            ImGui.Dummy(new Vector2(bw, bh));
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var rnd = 10f * scale;
            SpheneUI.Media.ImageInRect(dl, wrap, min, max, ImageFit.Cover,
                rounding: rnd);

            // mockup fade · left ~40% nearly solid, image emerges on the
            // right half - same strength as the reference banner.
            var darkL = new Vector4(0f, 0f, 0f, 0.88f);
            var darkL32 = SpheneUI.ColorUtils.ToU32(darkL);
            var darkB = new Vector4(0f, 0f, 0f, 0.55f);
            var clear = new Vector4(0f, 0f, 0f, 0f);
            SpheneUI.Media.FillRoundedGradient(dl, min,
                new Vector2(min.X + bw * 0.34f, max.Y), darkL, darkL, darkL, darkL,
                rnd, ImDrawFlags.RoundCornersLeft);
            dl.AddRectFilledMultiColor(
                new Vector2(min.X + bw * 0.34f, min.Y),
                new Vector2(min.X + bw * 0.78f, max.Y),
                darkL32, 0u, 0u, darkL32);
            SpheneUI.Media.FillRoundedGradient(dl,
                new Vector2(min.X, min.Y + bh * 0.6f), max,
                clear, clear, darkB, darkB, rnd, ImDrawFlags.RoundCornersBottom);
            dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
                SpheneUI.ColorUtils.WithAlpha(accent, 0.35f)), rnd);

            // text block vertically centered (mockup): big version headline,
            // badge beside it, date, then description with a real gap.
            var pad2 = 16f * scale;
            var white = SpheneUI.ColorUtils.ToU32(Vector4.One);
            var subWhite = SpheneUI.ColorUtils.ToU32(
                new Vector4(1f, 1f, 1f, 0.82f));
            var font = ImGui.GetFont();
            var verSize = lineH * 1.9f;
            var blockH = verSize + lineH * 0.6f + lineH
                + (hasDesc ? lineH * 1.9f : 0f);
            var ty = min.Y + (bh - blockH) * 0.56f;
            var tx = min.X + pad2;
            var descW = bw * 0.55f - pad2;
            var verText = $"v{e.Version}";
            var verW = ImGui.CalcTextSize(verText).X * (verSize / lineH);

            dl.AddText(font, verSize, new Vector2(tx, ty), white, verText);

            var badge = "Latest Release";
            var bw2 = ImGui.CalcTextSize(badge).X + 16f * scale;
            var bh2 = lineH * 1.35f;
            var badgeX = tx + verW + 10f * scale;
            var badgeY = ty + (verSize - bh2) * 0.55f;
            dl.AddRectFilled(new Vector2(badgeX, badgeY),
                new Vector2(badgeX + bw2, badgeY + bh2),
                SpheneUI.ColorUtils.ToU32(
                    SpheneUI.ColorUtils.WithAlpha(accent, 0.85f)),
                bh2 * 0.5f);
            dl.AddText(new Vector2(badgeX + 8f * scale,
                    badgeY + (bh2 - lineH) * 0.5f),
                SpheneUI.ColorUtils.ToU32(new Vector4(1f, 1f, 1f, 1f)), badge);

            var dateY = ty + verSize + lineH * 0.55f;
            if (!string.IsNullOrWhiteSpace(e.Date))
            {
                dl.AddText(new Vector2(tx, dateY), subWhite,
                    FormatReleaseDate(e.Date));
            }
            if (hasDesc)
            {
                var nd = NormalizeDescription(heroText);
                var lines = WrapText(nd, descW, 2);
                var dy = dateY + lineH * 1.35f;
                for (var i = 0; i < lines.Count; i++)
                {
                    dl.AddText(new Vector2(tx, dy + i * lineH), subWhite,
                        lines[i]);
                }
            }
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X8);
        }
        else if (hasDesc)
        {
            SpheneUI.Layout.SectionCard(() =>
            {
                var nd = NormalizeDescription(e.Description);
                if (!TryDrawImportantCallout(nd))
                {
                    SpheneUI.Text.MutedWrapped(nd);
                }
            }, title: "Description", icon: FontAwesomeIcon.AlignLeft);
        }

        // card 2 · highlights (only when the data actually carries them)
        if (e.Highlights.Count > 0)
        {
            SpheneUI.Layout.SectionCard(() =>
            {
                SpheneUI.Layout.CardGrid(e.Highlights, 200f * scale,
                    (hl, i) => DrawHighlightCard(hl, accent));
            }, title: "Highlights", icon: FontAwesomeIcon.Star);
        }

        // card 3 · changelog groups, collapsible with counts
        SpheneUI.Layout.SectionCard(() =>
        {
            if (e.Changes.Count == 0)
            {
                SpheneUI.Text.Muted(
                    "No changelog entries available for this version.");
                return;
            }
            var first = true;
            foreach (var change in e.Changes)
            {
                if (change == null)
                {
                    continue;
                }
                var main = TrimBulletPrefix(
                    (change.Text ?? string.Empty).Trim());
                var items = FlattenChangeItems(change);
                if (items.Count == 0)
                {
                    continue;
                }
                if (!first)
                {
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                }
                first = false;
                var color = ChangeCategoryColor(main);
                DrawChangelogGroup(e, main, items, color);
            }
        }, title: "Changelog", icon: FontAwesomeIcon.Scroll);
    }

    /// <summary>Flattens a change group into plain item lines - headings are
    /// dropped so the changelog reads as a flat list, not a folder tree.</summary>
    private static List<string> FlattenChangeItems(ReleaseChangeView change)
    {
        var items = new List<string>();
        var main = TrimBulletPrefix((change.Text ?? string.Empty).Trim());
        if (change.Sub is { Count: > 0 })
        {
            foreach (var s in GroupSubItems(change.Sub))
            {
                foreach (var it in s.Items)
                {
                    var v = TrimBulletPrefix((it ?? string.Empty).Trim());
                    if (v.Length > 0)
                    {
                        items.Add(v);
                    }
                }
            }
        }
        else if (main.Length > 0)
        {
            items.Add(main);
        }
        return items;
    }

    private void DrawChangelogGroup(ReleaseChangelogViewEntry e,
        string category, List<string> items, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var gid = $"clgrp_{e.Version}_{category}_{items.Count}";
        var open = SpheneUI.Layout.IsExpanderOpen(gid);
        var dl = ImGui.GetWindowDrawList();
        var pad = 10f * scale;
        var w = ImGui.GetContentRegionAvail().X;
        var lineH = ImGui.GetTextLineHeight();
        var headerH = lineH + pad * 1.6f;
        var full = _expandedGroups.Contains(gid);
        var shown = open ? (full ? items.Count : Math.Min(5, items.Count)) : 0;
        var showMore = open && !full && items.Count > 5;
        var rowPad = pad * 0.9f;
        var textIndent = pad + 18f * scale;

        // measure the seamless body height up front - the card is ONE rect
        // that grows when expanded; no second container under the header.
        float bodyH = 0;
        var itemHeights = open ? new float[shown] : null;
        if (open)
        {
            var textW = w - textIndent - pad;
            for (var i = 0; i < shown; i++)
            {
                itemHeights![i] = ImGui.CalcTextSize(items[i], false, textW).Y
                    + rowPad;
                bodyH += itemHeights[i];
            }
            if (showMore)
            {
                bodyH += lineH + rowPad;
            }
            bodyH += pad * 0.4f;
        }

        var totalH = headerH + bodyH;
        var clicked = ImGui.InvisibleButton($"##{gid}", new Vector2(w, headerH));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = new Vector2(min.X + w, min.Y + totalH);

        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(Vector4.One,
                hovered ? 0.05f : 0.025f)), 8f * scale);
        dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(color,
                hovered ? 0.35f : 0.18f)), 8f * scale);

        // header row inside the same card
        var icon = ChangeCategoryIcon(category);
        var iconX = min.X + pad;
        using (SpheneUI.Icons.PushFont())
        {
            var isz = ImGui.CalcTextSize(icon.ToIconString());
            dl.AddText(new Vector2(iconX, min.Y + (headerH - isz.Y) * 0.5f),
                SpheneUI.ColorUtils.ToU32(color), icon.ToIconString());
            iconX += isz.X + 8f * scale;
        }
        var label = $"{category} ({items.Count})";
        dl.AddText(new Vector2(iconX, min.Y + (headerH - lineH) * 0.5f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary), label);
        using (SpheneUI.Icons.PushFont())
        {
            var chev = (open ? FontAwesomeIcon.ChevronDown
                : FontAwesomeIcon.ChevronRight).ToIconString();
            var cw = ImGui.CalcTextSize(chev).X;
            dl.AddText(new Vector2(max.X - pad - cw,
                    min.Y + (headerH - lineH) * 0.5f),
                SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted), chev);
        }
        if (clicked)
        {
            SpheneUI.Layout.ToggleExpander(gid);
        }

        // items inside the same card - seamless, no extra container
        if (open)
        {
            var iy = min.Y + headerH;
            for (var i = 0; i < shown; i++)
            {
                dl.AddText(new Vector2(min.X + pad, iy + rowPad * 0.3f),
                    SpheneUI.ColorUtils.ToU32(color), "+");
                var wrapLocal = min.X + w - pad - ImGui.GetWindowPos().X;
                ImGui.SetCursorScreenPos(new Vector2(min.X + textIndent,
                    iy + rowPad * 0.3f));
                dl.PushClipRect(new Vector2(min.X + textIndent, iy),
                    new Vector2(max.X - pad, iy + itemHeights![i]), true);
                SpheneUI.Text.Wrapped(items[i], wrapLocal);
                dl.PopClipRect();
                iy += itemHeights[i];
            }
            if (showMore)
            {
                var moreY = iy + rowPad * 0.3f;
                dl.AddText(new Vector2(min.X + textIndent, moreY),
                    SpheneUI.ColorUtils.ToU32(color),
                    $"Show {items.Count - 5} more...");
                var moreRect = new Vector2(min.X + textIndent, moreY);
                var moreSz = ImGui.CalcTextSize(
                    $"Show {items.Count - 5} more...");
                if (ImGui.IsMouseHoveringRect(moreRect, moreRect + moreSz)
                    && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    _expandedGroups.Add(gid);
                }
            }
        }
        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
    }

    private static string FormatReleaseDate(string date)
        => SpheneUI.Text.FormatDate(date);

    private static FontAwesomeIcon ChangeCategoryIcon(string category)
    {
        var lower = category.Trim().ToLowerInvariant();
        if (lower.Contains("add") || lower.Contains("new")
            || lower.Contains("feature"))
        {
            return FontAwesomeIcon.Plus;
        }
        if (lower.Contains("fix") || lower.Contains("bug"))
        {
            return FontAwesomeIcon.Wrench;
        }
        if (lower.Contains("remov"))
        {
            return FontAwesomeIcon.Minus;
        }
        return FontAwesomeIcon.Sync;
    }

    private static void DrawHighlightCard(ReleaseHighlightView hl,
        Vector4 accent)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var pad = 10f * scale;
        var lineH = ImGui.GetTextLineHeight();
        var chip = 26f * scale;
        var h = chip + lineH * 2.6f + pad * 2.2f;
        var dl = ImGui.GetWindowDrawList();
        var cellW = ImGui.GetContentRegionAvail().X;
        ImGui.Dummy(new Vector2(cellW, h));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        dl.AddRectFilled(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.Colors.Surface), 8f * scale);
        dl.AddRect(min, max, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(SpheneUI.Colors.Border, 0.5f)),
            8f * scale);

        var chipMin = min + new Vector2(pad, pad);
        var icon = HighlightIcon(hl.Icon);
        SpheneUI.Layout.SlotIcon(dl, chipMin, chip, icon, accent);
        var titleX = chipMin.X + chip + 8f * scale;
        dl.AddText(new Vector2(titleX,
                chipMin.Y + (chip - lineH) * 0.5f),
            SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextPrimary),
            SpheneUI.Text.Ellipsize(hl.Title, max.X - titleX - pad));

        if (!string.IsNullOrWhiteSpace(hl.Text))
        {
            var textY = chipMin.Y + chip + 6f * scale;
            var lines = WrapText(hl.Text, max.X - min.X - pad * 2f, 2);
            for (var i = 0; i < lines.Count; i++)
            {
                dl.AddText(new Vector2(min.X + pad, textY + i * lineH),
                    SpheneUI.ColorUtils.ToU32(SpheneUI.Colors.TextMuted),
                    lines[i]);
            }
        }
        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
    }

    private static List<string> WrapText(string text, float maxW, int maxLines)
        => SpheneUI.Text.WrapLines(text, maxW, maxLines);

    private static FontAwesomeIcon HighlightIcon(string icon)
        => icon switch
        {
            "users" => FontAwesomeIcon.Users,
            "comment" => FontAwesomeIcon.Comment,
            "shield" => FontAwesomeIcon.ShieldAlt,
            "wand-magic-sparkles" => FontAwesomeIcon.Magic,
            "image" or "images" => FontAwesomeIcon.Images,
            "star" => FontAwesomeIcon.Star,
            _ => FontAwesomeIcon.Gem,
        };

    private void ApplyPendingExpansion()
    {
        if (string.IsNullOrEmpty(_pendingExpandVersion))
        {
            return;
        }
        SpheneUI.Layout.SetExpanderOpen($"changelog_rel_{_pendingExpandVersion}", true);
        _pendingExpandVersion = string.Empty;
    }

    private static Vector4 ChangeCategoryColor(string label)
    {
        var lower = label.Trim().ToLowerInvariant();
        if (lower.Contains("add") || lower.Contains("new") || lower.Contains("feature"))
        {
            return SpheneUI.Colors.Success;
        }
        if (lower.Contains("fix") || lower.Contains("improv") || lower.Contains("bug"))
        {
            return ImGuiColors.ParsedBlue;
        }
        if (lower.Contains("remov") || lower.Contains("delet") || lower.Contains("drop"))
        {
            return SpheneUI.Colors.Danger;
        }
        if (lower.Contains("secur") || lower.Contains("patch"))
        {
            return ImGuiColors.DalamudViolet;
        }
        if (lower.Contains("chang") || lower.Contains("updat")
            || lower.Contains("refactor") || lower.Contains("tweak"))
        {
            return SpheneUI.Colors.Warning;
        }
        return SpheneUI.Colors.Accent;
    }

    private static bool TryDrawImportantCallout(string description)
    {
        const string token = "!!IMPORTANT!!";
        if (string.IsNullOrWhiteSpace(description)
            || !description.Contains(token, StringComparison.Ordinal))
        {
            return false;
        }

        var text = description.Replace(token, string.Empty, StringComparison.Ordinal).Trim();
        SpheneUI.Layout.Callout(text, FontAwesomeIcon.ExclamationTriangle,
            SpheneUI.Colors.Warning, "Important");
        return true;
    }

    private void DrawSpheneWatermark(ImDrawListPtr drawList, Vector2 rectMin, Vector2 rectMax, float alpha, float verticalOffsetFactor)
    {
        try
        {
            var logoWrap = SpheneBrandIcons.SpheneLogo();
            if (logoWrap == null)
                return;

            var regionSize = rectMax - rectMin;
            if (regionSize.X <= 1f || regionSize.Y <= 1f)
                return;

            var side = MathF.Min(regionSize.X, regionSize.Y) * 0.9f;
            var center = rectMin + (regionSize * 0.5f) + new Vector2(0f, regionSize.Y * verticalOffsetFactor);
            var half = new Vector2(side * 0.5f, side * 0.5f);
            var pMin = center - half;
            var pMax = center + half;

            var tint = new Vector4(1f, 1f, 1f, Math.Clamp(alpha, 0f, 1f));
            SpheneUI.Media.ImageInRect(drawList, logoWrap, pMin, pMax, ImageFit.Stretch, tint);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to draw Sphene watermark");
        }
    }

    private static Version ParseVersionSafe(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return new Version(0,0,0,0);
        try
        {
            var core = v.Trim();
            var dashIndex = core.IndexOf('-', StringComparison.Ordinal);
            if (dashIndex >= 0)
            {
                core = core[..dashIndex];
            }
            core = core.Trim();
            if (core.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                core = core[1..].Trim();
            }
            var parsed = Version.Parse(core);
            var major = parsed.Major < 0 ? 0 : parsed.Major;
            var minor = parsed.Minor < 0 ? 0 : parsed.Minor;
            var build = parsed.Build < 0 ? 0 : parsed.Build;
            var revision = parsed.Revision < 0 ? 0 : parsed.Revision;
            return new Version(major, minor, build, revision);
        }
        catch
        {
            var parts = v!.Split('.', StringSplitOptions.RemoveEmptyEntries);
            int[] nums = parts.Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            while (nums.Length < 4)
            {
                nums = nums.Concat(new[] { 0 }).ToArray();
            }
            return new Version(nums[0], nums[1], nums[2], nums[3]);
        }
    }

    private static string NormalizeDescription(string description)
    {
        var normalized = description.Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0)
            return string.Empty;

        var sb = new StringBuilder(normalized.Length);
        var newlineRun = 0;
        foreach (var ch in normalized)
        {
            if (ch == '\n')
            {
                newlineRun++;
                if (newlineRun <= 2)
                    sb.Append('\n');
                continue;
            }

            newlineRun = 0;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string TrimBulletPrefix(string value)
    {
        if (value.StartsWith("- ", StringComparison.Ordinal))
            return value[2..];
        if (value.StartsWith("* ", StringComparison.Ordinal))
            return value[2..];
        if (value.StartsWith("• ", StringComparison.Ordinal))
            return value[2..];
        return value;
    }

    private static List<SubItemSection> GroupSubItems(List<string> subItems)
    {
        var sections = new List<SubItemSection>();
        SubItemSection? activeSection = null;

        foreach (var rawSub in subItems)
        {
            if (string.IsNullOrWhiteSpace(rawSub))
                continue;

            var normalized = TrimBulletPrefix(rawSub.Trim());
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (TryExtractSectionHeader(normalized, out var heading, out var content))
            {
                activeSection = new SubItemSection(heading);
                sections.Add(activeSection);

                if (!string.IsNullOrWhiteSpace(content))
                    activeSection.Items.Add(content);

                continue;
            }

            if (activeSection == null)
            {
                activeSection = new SubItemSection(string.Empty);
                sections.Add(activeSection);
            }

            activeSection.Items.Add(normalized);
        }

        return sections;
    }

    private static bool TryExtractSectionHeader(string value, out string heading, out string content)
    {
        heading = string.Empty;
        content = string.Empty;

        var separatorIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0)
            return false;

        var parsedHeading = value[..separatorIndex].Trim();
        if (string.IsNullOrWhiteSpace(parsedHeading))
            return false;

        heading = parsedHeading;
        content = value[(separatorIndex + 1)..].Trim();
        return true;
    }

    private sealed class SubItemSection
    {
        public SubItemSection(string heading)
        {
            Heading = heading;
        }

        public string Heading { get; }
        public List<string> Items { get; } = new();
    }
}
