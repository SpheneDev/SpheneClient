using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using System.Numerics;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sphene.UI.Panels;

public class ReleaseChangelogUi : WindowMediatorSubscriberBase
{
    private readonly UiSharedService _uiShared;
    private readonly SpheneConfigService _configService;
    private readonly ChangelogService _changelogService;
    private string _version = string.Empty;
    private volatile bool _loading;
    private List<ReleaseChangelogViewEntry> _entries = new();
    private bool _showAll;
    private bool _firstRenderAfterOpen;
    private string _defaultExpandedVersion = string.Empty;

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
            _showAll = false;
            _firstRenderAfterOpen = true;
            _defaultExpandedVersion = string.Empty;
            IsOpen = true;

            Task.Run(async () =>
            {
                try
                {
                    var list = await _changelogService.GetChangelogEntriesAsync().ConfigureAwait(false);
                    var currentVersion = ParseVersionSafe(_version);
                    _entries = list
                        .Where(e => ParseVersionSafe(e.Version) <= currentVersion)
                        .OrderByDescending(e => ParseVersionSafe(e.Version))
                        .ToList();
                    _defaultExpandedVersion = _entries.FirstOrDefault()?.Version ?? string.Empty;
                }
                catch { }
                finally { _loading = false; }
            });
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
    }

    protected override void DrawInternal()
    {
        if (_uiShared.IsInGpose)
            return;

        // Header
        _uiShared.BigText("What’s New in Sphene");
        ImGui.Separator();

        using (var table = ImRaii.Table("ReleaseInfo", 2, ImGuiTableFlags.None))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text("Version:");
                ImGui.TableNextColumn();
                ImGui.TextColored(ImGuiColors.HealerGreen, string.IsNullOrEmpty(_version) ? "Unknown" : _version);
            }
        }

        ImGui.Spacing();

        // Controls
        ImGui.Spacing();
        if (_uiShared.IconTextButton(_showAll ? FontAwesomeIcon.List : FontAwesomeIcon.ListOl, _showAll ? "Show last 5" : "Show all"))
        {
            _showAll = !_showAll;
        }
        ImGui.SameLine();
        UiSharedService.AttachToolTip("Toggle between last five releases and all releases.");
        ImGui.Spacing();

        using (var child = ImRaii.Child("ChangelogPane", new Vector2(-1, 350), true, ImGuiWindowFlags.NoNav))
        {
            if (child)
            {
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f))
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(4, 3)))
                using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4)))
                {
                    if (_loading)
                    {
                        UiSharedService.TextWrapped("Loading release notes...");
                    }
                    else if (_entries == null || _entries.Count == 0)
                    {
                        UiSharedService.TextWrapped("No release notes available.");
                    }
                    else
                    {
                        var list = _showAll ? _entries : _entries.Take(5);
                        foreach (var e in list)
                        {
                            var flags = ImGuiTreeNodeFlags.None;
                            if (!string.IsNullOrEmpty(_defaultExpandedVersion) && e.Version == _defaultExpandedVersion)
                            {
                                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
                                flags |= ImGuiTreeNodeFlags.DefaultOpen;
                            }

                            var headerLabel = $"{e.Version} - {e.Title}###ch_{e.Version}";
                            var opened = ImGui.CollapsingHeader(headerLabel, flags);
                            if (opened)
                            {
                                    ImGui.Dummy(new Vector2(0, 2));

                                if (!string.IsNullOrEmpty(e.Description))
                                    UiSharedService.TextWrapped(e.Description);

                                ImGuiHelpers.ScaledDummy(2f);

                                using (ImRaii.PushIndent(10f))
                                {
                                    foreach (var change in e.Changes)
                                    {
                                        if (change == null)
                                            continue;

                                        var trimmedMain = (change.Text ?? string.Empty).Trim();
                                        if (trimmedMain.StartsWith("- ")) trimmedMain = trimmedMain.Substring(2);
                                        if (trimmedMain.StartsWith("• ")) trimmedMain = trimmedMain.Substring(2);

                                        ImGui.Bullet();
                                        float bulletGap = ImGui.GetStyle().ItemInnerSpacing.X + ImGuiHelpers.GlobalScale * 8f;
                                        ImGui.SameLine(0, bulletGap);
                                        if (change.Sub is { Count: > 0 })
                                        {
                                            var baseColor = ImGuiColors.ParsedBlue;
                                            var textColor = new Vector4(baseColor.X, baseColor.Y, baseColor.Z, 0.90f);
                                            ImGui.PushStyleColor(ImGuiCol.Text, textColor);
                                            UiSharedService.TextWrapped(trimmedMain);
                                            ImGui.PopStyleColor();
                                        }
                                        else
                                        {
                                            UiSharedService.TextWrapped(trimmedMain);
                                        }

                                        if (change.Sub is { Count: > 0 })
                                        {
                                            ImGui.Indent(ImGuiHelpers.GlobalScale * 18f);
                                            foreach (var sub in change.Sub)
                                            {
                                                if (string.IsNullOrWhiteSpace(sub))
                                                    continue;

                                                var trimmedSub = sub.Trim();
                                                if (trimmedSub.StartsWith("- ")) trimmedSub = trimmedSub.Substring(2);
                                                if (trimmedSub.StartsWith("• ")) trimmedSub = trimmedSub.Substring(2);

                                                ImGui.Bullet();
                                                ImGui.SameLine(0, bulletGap);
                                                UiSharedService.TextWrapped(trimmedSub);
                                            }
                                            ImGui.Unindent(ImGuiHelpers.GlobalScale * 18f);
                                            // add gap only after structured entries with sub-items
                                            ImGuiHelpers.ScaledDummy(3f);
                                        }
                                    }
                                }
                            }
                            ImGui.Spacing();
                        }

                        _firstRenderAfterOpen = false;
                    }
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();

        // Footer actions
        if (_uiShared.IconTextButton(FontAwesomeIcon.Check, "Okay close!"))
        {
            // Close will persist the last seen version
            IsOpen = false;
        }
    }

    private static Version ParseVersionSafe(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return new Version(0,0,0,0);
        try
        {
            return Version.Parse(v);
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
}