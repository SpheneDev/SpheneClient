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
    private string _lastSeenVersionBeforeUpdate = string.Empty;
    private volatile bool _loading;
    private List<ReleaseChangelogViewEntry> _entries = new();
    private bool _showAll;
    private bool _showTestBuildReleases;
    private string _pendingExpandVersion = string.Empty;
    private bool _embeddedInitialized;
    private bool _entriesRequested;

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
            _lastSeenVersionBeforeUpdate = string.IsNullOrWhiteSpace(msg.LastSeenVersionBeforeUpdate)
                ? _configService.Current.LastSeenVersionChangelog ?? string.Empty
                : msg.LastSeenVersionBeforeUpdate;
            _version = msg.CurrentVersion;
            WindowName = $"Sphene Release Notes - {_version}";
            _loading = true;
            _entries = new List<ReleaseChangelogViewEntry>();
            _showAll = false;
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

        SpheneUI.Text.PageHeader("Release Notes");

        SpheneUI.Layout.SectionCard(() =>
        {
            DrawReleaseSummary();
            DrawControls();
        }, animatedBackdrop: true);

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + (ImGui.GetStyle().ItemSpacing.Y * 2f) + (ImGuiHelpers.GlobalScale * 6f);
        var changelogPaneHeight = SpheneUI.Layout.FillToViewBottomHeight(footerHeight);
        using (ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.GetColorU32(opaqueChildBg)))
        {
            var scrollBegun = SpheneUI.Layout.BeginScrollArea("ChangelogPane", new Vector2(-1, changelogPaneHeight), border: true,
                windowFlags: ImGuiWindowFlags.NoNav);
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
        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Check, "Okay close!"))
        {
            // Close will persist the last seen version
            IsOpen = false;
        }
    }

    internal void DrawEmbedded()
    {
        EnsureEmbeddedDefaults();
        if (!_entriesRequested)
        {
            StartLoadingEntries();
        }

        SpheneUI.Layout.SectionCard(() =>
        {
            DrawReleaseSummary();
            DrawControls();
            SpheneUI.Layout.DistanceSeparator();
            if (SpheneUI.Layout.BeginFillToBottomScrollArea("changelog_embedded_list"))
            {
                DrawReleaseList();
            }
            SpheneUI.Layout.EndScrollArea();
        }, title: "Regulator Update History", animatedBackdrop: true);
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
        _lastSeenVersionBeforeUpdate = _configService.Current.LastSeenVersionChangelog ?? string.Empty;
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

    private static readonly string[] RangeTabs = ["Latest 5", "All releases"];

    private void DrawReleaseSummary()
    {
        var newCount = _loading ? 0 : CountNewEntries();
        var sb = new StringBuilder("Installed ");
        sb.Append(string.IsNullOrEmpty(_version) ? "unknown" : $"v{_version}");
        if (!_loading && _entries.Count > 0)
        {
            sb.Append("   \u00b7   ").Append(_entries.Count).Append(" releases");
            if (newCount > 0)
            {
                sb.Append("   \u00b7   ").Append(newCount).Append(" new");
            }
        }
        SpheneUI.Text.Muted(sb.ToString());
        if (newCount > 0 && !string.IsNullOrWhiteSpace(_lastSeenVersionBeforeUpdate))
        {
            SpheneUI.AttachTooltip($"{newCount} release(s) since last seen version {_lastSeenVersionBeforeUpdate}.");
        }
    }

    private void DrawControls()
    {
        var selected = SpheneUI.Layout.TabStrip("##rel_range", RangeTabs, _showAll ? 1 : 0);
        _showAll = selected == 1;
        SpheneUI.AttachTooltip("Limit the list to the five newest releases or show the full history.");

#if !IS_TEST_BUILD
        ImGui.SameLine();
        var showTest = _showTestBuildReleases;
        if (SpheneUI.Controls.Toggle("Testbuilds##rel_testbuild", ref showTest))
        {
            _showTestBuildReleases = showTest;
            _configService.Current.ShowTestBuildChangelogs = showTest;
            _configService.Save();
            StartLoadingEntries();
        }
        SpheneUI.AttachTooltip("Toggle whether prerelease/testbuild releases are visible.");
#endif
        SpheneUI.Layout.VerticalSpace();
    }

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
            SpheneUI.Text.Muted("No release notes available.");
            return;
        }

        var list = _showAll ? _entries : _entries.Take(5);
        var first = true;
        foreach (var e in list)
        {
            if (!first)
            {
                SpheneUI.Layout.DistanceSeparator();
            }
            first = false;
            DrawReleaseEntry(e, IsNewEntry(e));
        }
    }

    private void ApplyPendingExpansion()
    {
        if (string.IsNullOrEmpty(_pendingExpandVersion))
        {
            return;
        }
        SpheneUI.Layout.SetExpanderOpen($"changelog_rel_{_pendingExpandVersion}", true);
        _pendingExpandVersion = string.Empty;
    }

    private bool IsNewEntry(ReleaseChangelogViewEntry e)
    {
        var lastSeen = ParseVersionSafe(_lastSeenVersionBeforeUpdate);
        if (lastSeen == new Version(0, 0, 0, 0))
        {
            return false;
        }
        return ParseVersionSafe(e.Version) > lastSeen;
    }

    private int CountNewEntries()
    {
        var count = 0;
        foreach (var e in _entries)
        {
            if (IsNewEntry(e))
            {
                count++;
            }
        }
        return count;
    }

    private static void DrawReleaseEntry(ReleaseChangelogViewEntry e, bool isNew)
    {
        var id = $"changelog_rel_{e.Version}";
        var scale = ImGuiHelpers.GlobalScale;
        var open = SpheneUI.Layout.IsExpanderOpen(id);
        var drawList = ImGui.GetWindowDrawList();
        var accent = SpheneUI.Theme.Current.AccentCyan;
        var availW = MathF.Max(60f * scale, ImGui.GetContentRegionAvail().X);
        var padX = 8f * scale;
        var rowH = ImGui.GetFrameHeight() + 8f * scale;
        var rowMin = ImGui.GetCursorScreenPos();
        var rowMax = rowMin + new Vector2(availW, rowH);

        var clicked = ImGui.InvisibleButton($"##row_{id}", new Vector2(availW, rowH));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            var hoverAccent = e.IsPrerelease ? SpheneUI.Colors.Warning : accent;
            drawList.AddRectFilled(rowMin, rowMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(hoverAccent, 0.07f)), 6f * scale);
        }
        if (clicked)
        {
            SpheneUI.Layout.ToggleExpander(id);
        }
        if (hovered)
        {
            SpheneUI.AttachTooltip(open ? "Collapse release notes." : "Expand release notes.");
        }

        var centerY = rowMin.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f;
        var chipH = SpheneUI.Layout.BadgeHeight;
        var rightX = rowMax.X - padX;

        var chevron = open ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight;
        var iconSize = SpheneUI.Icons.Size(chevron);
        rightX -= iconSize.X;
        ImGui.SetCursorScreenPos(new Vector2(rightX, rowMin.Y + (rowH - iconSize.Y) * 0.5f));
        SpheneUI.Icons.Icon(chevron, SpheneUI.Colors.TextMuted);
        rightX -= 8f * scale;

        if (!string.IsNullOrWhiteSpace(e.Date))
        {
            rightX -= ImGui.CalcTextSize(e.Date).X;
            ImGui.SetCursorScreenPos(new Vector2(rightX, centerY));
            SpheneUI.Text.Muted(e.Date);
            rightX -= 8f * scale;
        }
        if (e.IsPrerelease)
        {
            rightX -= SpheneUI.Layout.BadgeWidth("TEST");
            ImGui.SetCursorScreenPos(new Vector2(rightX, rowMin.Y + (rowH - chipH) * 0.5f));
            SpheneUI.Layout.Badge("TEST", SpheneUI.Colors.Warning);
            rightX -= 6f * scale;
        }
        if (isNew)
        {
            rightX -= SpheneUI.Layout.BadgeWidth("NEW");
            ImGui.SetCursorScreenPos(new Vector2(rightX, rowMin.Y + (rowH - chipH) * 0.5f));
            SpheneUI.Layout.Badge("NEW", accent);
            rightX -= 6f * scale;
        }

        ImGui.SetCursorScreenPos(new Vector2(rowMin.X + padX, centerY));
        var versionColor = e.IsPrerelease ? SpheneUI.Colors.Warning
            : (isNew ? accent : SpheneUI.Colors.TextPrimary);
        SpheneUI.Text.Colored($"v{e.Version}", versionColor);
        ImGui.SameLine();
        var headerTitle = BuildHeaderTitle(e);
        if (!string.IsNullOrWhiteSpace(headerTitle))
        {
            var titleMaxW = rightX - 8f * scale - ImGui.GetCursorScreenPos().X;
            SpheneUI.Text.Colored(
                SpheneUI.Text.Ellipsize(headerTitle, MathF.Max(24f * scale, titleMaxW)),
                SpheneUI.Colors.TextMuted);
        }

        ImGui.SetCursorScreenPos(new Vector2(rowMin.X, rowMax.Y));

        if (SpheneUI.Layout.BeginExpander(id))
        {
            DrawReleaseBody(e);
            SpheneUI.Layout.EndExpander();
        }
    }

    private static void DrawReleaseBody(ReleaseChangelogViewEntry e)
    {
        var drewContent = false;
        if (!string.IsNullOrWhiteSpace(e.Version))
        {
            SpheneUI.Text.ColoredWrapped($"~/sphene/releases/v{e.Version}/",
                SpheneUI.Colors.TextMuted);
            drewContent = true;
        }

        if (!string.IsNullOrWhiteSpace(e.Description))
        {
            var normalizedDescription = NormalizeDescription(e.Description);
            if (!TryDrawImportantCallout(normalizedDescription))
            {
                if (drewContent)
                {
                    SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                }
                SpheneUI.Text.MutedWrapped(normalizedDescription);
            }
            drewContent = true;
        }

        var firstGroup = true;
        foreach (var change in e.Changes)
        {
            if (change == null)
            {
                continue;
            }

            var main = TrimBulletPrefix((change.Text ?? string.Empty).Trim());
            var hasSubs = change.Sub is { Count: > 0 };
            if (!hasSubs && string.IsNullOrWhiteSpace(main))
            {
                continue;
            }

            if (!firstGroup)
            {
                SpheneUI.Layout.Space(SpheneUI.Spacing.X10);
            }
            else if (drewContent)
            {
                SpheneUI.Layout.Space(SpheneUI.Spacing.X8);
            }
            firstGroup = false;
            drewContent = true;

            var isCategory = hasSubs && !string.IsNullOrWhiteSpace(main);
            if (isCategory)
            {
                var color = ChangeCategoryColor(main);
                var railX = DrawChangeCategory(main, color);
                DrawChangeSections(change.Sub!, color, railX);
            }
            else
            {
                if (hasSubs)
                {
                    DrawChangeSections(change.Sub!, SpheneUI.Colors.Accent, null);
                }
                else
                {
                    DrawChangeFileItem(main, 0f, null, SpheneUI.Colors.TextMuted);
                }
            }
        }

        if (!drewContent)
        {
            SpheneUI.Text.Muted("No detailed changes listed.");
        }
    }

    private static float DrawChangeCategory(string label, Vector4 color)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var iconSize = SpheneUI.Icons.Size(FontAwesomeIcon.FolderOpen);
        var labelHeight = MathF.Max(ImGui.GetTextLineHeight(), iconSize.Y);
        var markerX = ImGui.GetCursorScreenPos().X;
        var markerY = ImGui.GetCursorScreenPos().Y;
        var iconCenter = new Vector2(markerX + iconSize.X * 0.5f,
            markerY + labelHeight * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(
            iconCenter.X - iconSize.X * 0.5f,
            iconCenter.Y - iconSize.Y * 0.5f));
        SpheneUI.Icons.Icon(FontAwesomeIcon.FolderOpen, color);
        ImGui.SetCursorScreenPos(new Vector2(markerX + iconSize.X + 8f * scale, markerY));
        SpheneUI.Text.Colored($"{label.TrimEnd('/')}/", color);
        return iconCenter.X;
    }

    private static void DrawChangeSections(List<string> sub, Vector4 color, float? railX)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var sections = GroupSubItems(sub);
        if (sections.Count == 0)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var railCol = SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(color, 0.30f));
        var railW = MathF.Max(1f, 1.5f * scale);
        var indent1 = 22f * scale;
        var indent2 = 20f * scale;
        var halfLine = ImGui.GetTextLineHeight() * 0.5f;
        var folderIconSize = SpheneUI.Icons.Size(FontAwesomeIcon.FolderOpen);
        var folderColor = SpheneUI.Palette.SpheneGold;

        var railTop = ImGui.GetCursorScreenPos().Y + 3f * scale;
        SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

        var railBottom = -1f;
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (i > 0)
            {
                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
            }

            var hasHeading = !string.IsNullOrWhiteSpace(section.Heading);
            var itemRailX = railX;
            var itemRailTop = railTop;
            if (hasHeading)
            {
                var rowMin = ImGui.GetCursorScreenPos();
                var rowMidY = rowMin.Y + halfLine;
                var iconX = rowMin.X + indent1;
                var iconCenterX = iconX + folderIconSize.X * 0.5f;
                if (railX is { } rx)
                {
                    drawList.AddLine(new Vector2(rx, rowMidY),
                        new Vector2(iconX - 2f * scale, rowMidY), railCol, railW);
                }
                ImGui.SetCursorScreenPos(new Vector2(iconX,
                    rowMidY - folderIconSize.Y * 0.5f));
                SpheneUI.Icons.Icon(FontAwesomeIcon.FolderOpen, folderColor);
                ImGui.SetCursorScreenPos(new Vector2(
                    iconX + folderIconSize.X + 6f * scale, rowMin.Y));
                SpheneUI.Text.Colored($"{section.Heading!.TrimEnd('/')}/",
                    SpheneUI.Colors.TextSecondary);
                railBottom = rowMidY;
                itemRailX = iconCenterX;
                itemRailTop = ImGui.GetCursorScreenPos().Y + 2f * scale;
            }

            var itemIndent = hasHeading ? indent1 + indent2 : indent1;
            var lastItemRowY = -1f;
            var firstItem = true;
            foreach (var subItem in section.Items)
            {
                if (string.IsNullOrWhiteSpace(subItem))
                {
                    continue;
                }
                if (!firstItem)
                {
                    SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                }
                firstItem = false;
                var trimmed = TrimBulletPrefix(subItem.Trim());
                lastItemRowY = DrawChangeFileItem(trimmed, itemIndent, itemRailX, color);
            }

            if (hasHeading && itemRailX is { } irx2 && lastItemRowY > itemRailTop)
            {
                drawList.AddLine(new Vector2(irx2, itemRailTop),
                    new Vector2(irx2, lastItemRowY), railCol, railW);
            }
            else if (!hasHeading && lastItemRowY > railBottom)
            {
                railBottom = lastItemRowY;
            }
        }

        if (railX is { } rail0 && railBottom > railTop)
        {
            drawList.AddLine(new Vector2(rail0, railTop),
                new Vector2(rail0, railBottom), railCol, railW);
        }
    }

    private static float DrawChangeFileItem(string text, float indent,
        float? railX, Vector4 accent)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var padX = 8f * scale;
        var padY = 5f * scale;
        var railW = MathF.Max(1f, 1.5f * scale);
        var rowMin = ImGui.GetCursorScreenPos();
        var itemX = rowMin.X + indent;
        var cardW = MathF.Max(60f * scale, ImGui.GetContentRegionAvail().X - indent);
        var cardMin = new Vector2(itemX, rowMin.Y);
        var textW = MathF.Max(24f * scale, cardW - padX * 2f);
        var textH = ImGui.CalcTextSize(text, false, textW).Y;
        var cardMax = new Vector2(cardMin.X + cardW, cardMin.Y + textH + padY * 2f);

        var hovered = ImGui.IsMouseHoveringRect(cardMin, cardMax);
        var rounding = 5f * scale;
        drawList.AddRectFilled(cardMin, cardMax, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(Vector4.One, hovered ? 0.075f : 0.045f)), rounding);
        drawList.AddRect(cardMin, cardMax, SpheneUI.ColorUtils.ToU32(
            SpheneUI.ColorUtils.WithAlpha(accent, hovered ? 0.24f : 0.10f)), rounding);

        var midY = cardMin.Y + (cardMax.Y - cardMin.Y) * 0.5f;
        if (railX is { } rx)
        {
            drawList.AddLine(new Vector2(rx, midY), new Vector2(cardMin.X - 1f, midY),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.30f)), railW);
        }

        var wrapLocal = cardMax.X - padX - ImGui.GetWindowPos().X;
        ImGui.SetCursorScreenPos(new Vector2(cardMin.X + padX, cardMin.Y + padY));
        SpheneUI.Text.Wrapped(text, wrapLocal);
        ImGui.SetCursorScreenPos(new Vector2(rowMin.X, cardMax.Y));
        return midY;
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
            var logoWrap = SpheneUI.Media.Texture("sphene:logo",
                () => System.Convert.FromBase64String(SpheneImages.SpheneLogoBase64));
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

    private static string BuildHeaderTitle(ReleaseChangelogViewEntry entry)
        => string.IsNullOrWhiteSpace(entry.Title) ? string.Empty : entry.Title.Trim();

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
