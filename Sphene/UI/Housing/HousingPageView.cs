using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.CharaData;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Components;
using Sphene.UI.Design;
using Sphene.WebAPI;
using Sphene.Utils;

namespace Sphene.UI.Housing;

/// <summary>
/// Property-centric housing page. The list shows every detected/verified
/// property with its region artwork · opening a property gets its own page
/// where owned syncshells are assigned to its bound areas (outdoor, floors,
/// interior) via GroupSetAreaBinding.
/// </summary>
public sealed class HousingPageView
{
    private readonly ILogger<HousingPageView> _logger;
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly SpheneMediator _mediator;
    private readonly HousingOwnershipService _housingOwnershipService;
    private readonly DalamudUtilService _dalamudUtilService;

    private List<HousingOwnershipService.DetectedHousingProperty> _detected = [];
    private List<AreaBoundSyncshellDto> _boundShells = [];
    private Task? _refreshTask;
    private LocationInfo? _selected;
    /// <summary>Key of the open property, or null on the list · used by the
    /// hub to snapshot the detail location into its back/forward history.</summary>
    public string? SelectedKey => _selected is { } s ? PropertyKey(s) : null;
    /// <summary>When set, property open/close goes through this callback
    /// (the hub's navigation history) instead of mutating
    /// <see cref="_selected"/> directly.</summary>
    public Action<LocationInfo?>? OnNavigate { get; set; }
    /// <summary>Stable key for a property location · shared with the hub's
    /// history entries.</summary>
    public static string KeyOf(LocationInfo l) => PropertyKey(l);
    /// <summary>Restores the open property from a history key; null closes
    /// the detail and lands back on the list.</summary>
    public void SelectProperty(string? key)
    {
        if (key == null)
        {
            _selected = null;
            return;
        }
        foreach (var e in CombinedEntries())
        {
            if (string.Equals(PropertyKey(e.Location), key, StringComparison.Ordinal))
            {
                _selected = e.Location;
                return;
            }
        }
        _selected = null;
    }
    private readonly Dictionary<string, List<AreaBoundLocationDto>> _working = new(StringComparer.Ordinal);
    private readonly HashSet<string> _saving = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settingsOpen = new(StringComparer.Ordinal);

    public HousingPageView(ILogger<HousingPageView> logger, ApiController apiController,
        PairManager pairManager, SpheneMediator mediator,
        HousingOwnershipService housingOwnershipService, DalamudUtilService dalamudUtilService)
    {
        _logger = logger;
        _apiController = apiController;
        _pairManager = pairManager;
        _mediator = mediator;
        _housingOwnershipService = housingOwnershipService;
        _dalamudUtilService = dalamudUtilService;
    }

    public void DrawContent()
    {
        if (_refreshTask == null)
        {
            Refresh("initial");
        }
        if (_selected == null)
        {
            DrawList();
        }
        else
        {
            DrawDetail(_selected.Value);
        }
    }

    // ---------------------------------------------------------------- list

    /// <summary>The Housing "?" flyout · shared by the hub rail's nav help
    /// dock and the embedded page registration.</summary>
    internal static void HousingHelpContent()
    {
        SpheneUI.Text.MutedWrapped(
            "Your detected housing properties live here. Open one to assign your " +
            "syncshells to its bound areas - floors, outdoor space or interiors.",
            ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
    }

    private void DrawList()
    {
        SpheneUI.Layout.RegisterPageHelp("housing", HousingHelpContent);

        var entries = CombinedEntries();

        SpheneUI.Layout.SectionCard(() =>
        {
            if (_refreshTask != null && !_refreshTask.IsCompleted)
            {
                SpheneUI.Text.Colored("Detecting properties...", SpheneUI.Colors.TextMuted);
                return;
            }
            if (entries.Count == 0)
            {
                SpheneUI.Layout.Callout(
                    "No housing properties detected yet. Stand near a house, apartment or FC room you own and hit Refresh.",
                    FontAwesomeIcon.Home, title: "Nothing detected");
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Redo, "Refresh"))
                {
                    Refresh("manual refresh");
                }
                return;
            }

            var first = true;
            foreach (var e in entries)
            {
                if (!first)
                {
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                }
                first = false;
                DrawPropertyRow(e);
            }
        }, "Your Properties",
            description: "Everything the game says you own - select one to manage its bound syncshells.");
    }

    private void DrawPropertyRow((string Type, LocationInfo Location, bool IsActive) e)
    {
        var loc = e.Location;
        var key = PropertyKey(loc);
        using var idScope = ImRaii.PushId("hp_" + key);
        var territoryName = AreaArt.TerritoryName(_dalamudUtilService.TerritoryData.Value, loc.TerritoryId);

        // Fixed-height card: art thumb left, meta right, chevron at the edge.
        var min = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var w = SpheneUI.Controls.BoundedAvailX();
        var density = SpheneUI.Density.Factor;
        var thumbW = 128f * scale * density;
        var cardH = 62f * scale * density;
        var max = new Vector2(min.X + w, min.Y + cardH);
        var art = AreaArt.Wrap(AreaArt.LocationSlug(territoryName, loc.WardId, loc.HouseId));

        var hovered = ImGui.IsMouseHoveringRect(min, max);
        dl.AddRectFilled(min, max,
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                SpheneUI.Colors.Surface, hovered ? 0.85f : 0.7f)),
            8f * scale);
        if (art != null)
        {
            var tMax = new Vector2(min.X + thumbW, max.Y);
            SpheneUI.Media.ImageInRect(dl, art, min, tMax, ImageFit.Cover,
                rounding: 8f * scale, corners: ImDrawFlags.RoundCornersLeft);
        }

        // Meta text column (drawn after layout; absolute so it centers on the card).
        var boundCount = 0;
        foreach (var shell in _boundShells)
        {
            if (shell.BoundAreas.Any(b => BelongsToProperty(b, loc)))
            {
                boundCount++;
            }
        }

        var tx = min.X + (art != null ? thumbW + 12f * scale : 12f * scale);
        var lineH = ImGui.GetTextLineHeight();
        var cy = min.Y + (cardH - (lineH * 2f + 4f * scale)) * 0.5f;
        var typeColor = e.IsActive ? ImGuiColors.ParsedGreen : SpheneUI.Colors.TextMuted;
        dl.AddText(new Vector2(tx, cy), SpheneUI.ColorUtils.ToU32(typeColor),
            $"{e.Type} · {WorldName(loc.ServerId)}");
        var wardText = loc.WardId > 0 ? $"Ward {loc.WardId}" : "Ward -";
        var plotText = loc.HouseId > 0 ? (loc.HouseId == 100 ? "Apt" : $"Plot {loc.HouseId}") : "Plot -";
        var roomText = loc.RoomId > 0 ? $" • Room {loc.RoomId}" : string.Empty;
        dl.AddText(new Vector2(tx, cy + lineH + 4f * scale),
            ImGui.GetColorU32(ImGuiColors.DalamudGrey),
            $"{territoryName} · {wardText} • {plotText}{roomText}" +
            (boundCount > 0 ? $"  ·  {boundCount} bound" : ""));

        // Chevron at the right edge.
        using (SpheneUI.Icons.PushFont())
        {
            var chev = FontAwesomeIcon.ChevronRight.ToIconString();
            var cw = ImGui.CalcTextSize(chev).X;
            dl.AddText(new Vector2(max.X - cw - 14f * scale, min.Y + (cardH - lineH) * 0.5f),
                ImGui.GetColorU32(ImGuiColors.DalamudGrey), chev);
        }

        ImGui.Dummy(new Vector2(w, cardH));
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && hovered)
        {
            if (OnNavigate != null)
            {
                OnNavigate(loc);
            }
            else
            {
                _selected = loc;
            }
        }
    }

    // -------------------------------------------------------------- detail

    private void DrawDetail(LocationInfo loc)
    {
        var territoryName = AreaArt.TerritoryName(_dalamudUtilService.TerritoryData.Value, loc.TerritoryId);
        var scale = ImGuiHelpers.GlobalScale;

        // Back lives in the hub's title-bar nav cluster (browser-style),
        // no in-page back button here.

        // Hero: region art cover or accent gradient · district name on scrim.
        var min = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var heroW = SpheneUI.Controls.BoundedAvailX();
        var heroH = 132f * scale * SpheneUI.Density.Factor;
        var max = new Vector2(min.X + heroW, min.Y + heroH);
        var art = AreaArt.Wrap(AreaArt.LocationSlug(territoryName, loc.WardId, loc.HouseId));
        if (art != null)
        {
            SpheneUI.Media.ImageInRect(dl, art, min, max, ImageFit.Cover,
                rounding: 10f * scale, corners: ImDrawFlags.RoundCornersAll);
        }
        else
        {
            var (hDeep, hHi) = SpheneUI.Colors.AccentRamp(SpheneUI.Colors.Accent);
            var top = SpheneUI.ColorUtils.LerpColor(hHi, new Vector4(0f, 0f, 0f, 1f), 0.45f);
            var bot = SpheneUI.ColorUtils.LerpColor(hDeep, new Vector4(0f, 0f, 0f, 1f), 0.55f);
            SpheneUI.Media.FillRoundedGradient(dl, min, max, top, top, bot, bot,
                10f * scale);
            var logoClipInset = new Vector2(4f * scale, 4f * scale);
            dl.PushClipRect(min + logoClipInset, max - logoClipInset, true);
            var logo = SpheneBrandIcons.SpheneLogo();
            if (logo != null)
            {
                var lh = heroH * 1.3f;
                var lw = lh * ((float)logo.Width / logo.Height);
                var c = new Vector2(min.X + heroW * 0.8f, min.Y + heroH * 0.55f);
                SpheneUI.Media.ImageInRect(dl, logo, c - new Vector2(lw, lh) * 0.5f,
                    c + new Vector2(lw, lh) * 0.5f, ImageFit.Fit,
                    tint: new Vector4(1f, 1f, 1f, 0.14f));
            }
            dl.PopClipRect();
        }
        SyncshellCard.BottomScrim(dl, min, max, 0.6f, 10f * scale);
        var wardText = loc.WardId > 0 ? $"Ward {loc.WardId}" : "Ward -";
        var plotText = loc.HouseId > 0 ? (loc.HouseId == 100 ? "Apt" : $"Plot {loc.HouseId}") : "Plot -";
        var roomText = loc.RoomId > 0 ? $" · Room {loc.RoomId}" : string.Empty;
        var pad = 12f * scale;
        var lineH = ImGui.GetTextLineHeight();
        dl.AddText(min + new Vector2(pad, max.Y - min.Y - pad - lineH * 2f - 2f),
            ImGui.GetColorU32(Vector4.One), territoryName);
        dl.AddText(min + new Vector2(pad, max.Y - min.Y - pad - lineH),
            ImGui.GetColorU32(ImGuiColors.DalamudGrey),
            $"{WorldName(loc.ServerId)} · {wardText} • {plotText}{roomText}");
        ImGui.Dummy(new Vector2(heroW, heroH));

        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Section);

        var owned = OwnedShells();
        SpheneUI.Text.SectionHeader("Syncshells on this property");
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        if (owned.Count == 0)
        {
            SpheneUI.Layout.Callout(
                "You don't own a syncshell yet. Create one, then bind it to this property here.",
                FontAwesomeIcon.Link, SpheneUI.Palette.CrystalBlue,
                title: "No syncshell to bind");
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            if (SpheneUI.Controls.PrimaryButton("Create Syncshell"))
            {
                _mediator.Publish(new OpenSpheneHubMessage(
                    SpheneHubPage.Syncshells, SyncshellSub: SyncshellHubSubPage.Create));
            }
            return;
        }

        foreach (var group in owned)
        {
            DrawShellCard(group, loc);
            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        }
    }

    /// <summary>One shell per card: identity + master toggle in the header,
    /// fixed-size zone segments, and a collapsible settings block.</summary>
    private void DrawShellCard(GroupFullInfoDto group, LocationInfo loc)
    {
        var gid = group.Group!.GID;
        using var sid = ImRaii.PushId(gid);
        var areas = WorkingAreas(gid);
        var saving = _saving.Contains(gid);
        var existing = _boundShells.FirstOrDefault(
            s => string.Equals(s.Group?.GID, gid, StringComparison.Ordinal));
        var boundHere = areas.Any(b => BelongsToProperty(b, loc));
        var active = existing?.IsAreaBound ?? boundHere;

        SpheneUI.Layout.SectionCard(() =>
        {
            var memberCount = _pairManager.GroupPairs.TryGetValue(group, out var pairs)
                ? pairs.Count : 0;
            SpheneUI.Icons.Icon(boundHere ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink,
                boundHere ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudGrey);
            ImGui.SameLine();
            SpheneUI.Text.Colored($"{memberCount} members", SpheneUI.Colors.TextMuted);
            if (saving)
            {
                ImGui.SameLine();
                SpheneUI.Text.Colored("saving...", SpheneUI.Colors.TextMuted);
            }
            ImGui.SameLine();
            var expanded = _settingsOpen.Contains(gid);
            if (SpheneUI.Icons.Button(FontAwesomeIcon.Cog))
            {
                if (expanded) _settingsOpen.Remove(gid); else _settingsOpen.Add(gid);
            }
            SpheneUI.AttachTooltip(expanded ? "Hide binding settings" : "Binding settings");
            ImGui.SameLine();
            var activeToggle = active;
            using (ImRaii.Disabled(saving || (!active && areas.Count == 0)))
            {
                if (SpheneUI.Controls.Toggle("Active", ref activeToggle))
                {
                    ToggleActive(gid, group, areas, existing, activeToggle);
                }
            }
            SpheneUI.AttachTooltip("Enable or disable this syncshell's area binding - areas are kept while off");

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

            using (ImRaii.Disabled(saving || !active))
            {
                DrawZoneSegments(areas, loc, group);
            }

            if (expanded)
            {
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                SpheneUI.Layout.Separator();
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
                DrawBindingSettings(gid, existing, saving);
            }
        }, group.GroupAliasOrGID);
    }

    /// <summary>Fixed-size zone segments · they never stretch with the window
    /// and icon + label sit centered inside each card.</summary>
    private void DrawZoneSegments(List<AreaBoundLocationDto> areas, LocationInfo loc,
        GroupFullInfoDto group)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = _dalamudUtilService.GetHousingPlotSizeLabel(loc);
        var showSecond = size is "Medium" or "Large";
        var outdoorCapable = loc.RoomId == 0 && loc.HouseId != 100;

        var zones = new List<(string Label, FontAwesomeIcon Icon, AreaMatchingMode Mode, uint MapId, bool Indoor, bool On)>();
        if (outdoorCapable)
        {
            if (showSecond)
            {
                zones.Add(("2nd Floor", FontAwesomeIcon.LevelUpAlt, AreaMatchingMode.HousingPlotIndoor,
                    HousingInteriorRelativeMapIds.Second, true,
                    HasArea(areas, loc, AreaMatchingMode.HousingPlotIndoor, HousingInteriorRelativeMapIds.Second)));
            }
            zones.Add(("Ground", FontAwesomeIcon.DoorOpen, AreaMatchingMode.HousingPlotIndoor,
                HousingInteriorRelativeMapIds.Ground, true,
                HasArea(areas, loc, AreaMatchingMode.HousingPlotIndoor, HousingInteriorRelativeMapIds.Ground)));
            zones.Add(("Basement", FontAwesomeIcon.LevelDownAlt, AreaMatchingMode.HousingPlotIndoor,
                HousingInteriorRelativeMapIds.Basement, true,
                HasArea(areas, loc, AreaMatchingMode.HousingPlotIndoor, HousingInteriorRelativeMapIds.Basement)));
            zones.Add(("Garden", FontAwesomeIcon.Tree, AreaMatchingMode.HousingPlotOutdoor, 0, false,
                HasArea(areas, loc, AreaMatchingMode.HousingPlotOutdoor, 0)));
        }
        else
        {
            zones.Add((loc.HouseId == 100 ? "Interior" : $"Room {loc.RoomId}", FontAwesomeIcon.DoorClosed,
                AreaMatchingMode.HousingPlotIndoor, 0, true,
                areas.Any(b => b.MatchingMode == AreaMatchingMode.HousingPlotIndoor
                    && BelongsToProperty(b, loc) && b.Location.RoomId == loc.RoomId)));
        }

        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var segW = 110f * scale;
        var segH = 64f * scale * SpheneUI.Density.Factor;
        var gap = 6f * scale;
        var round = 8f * scale;
        var accent = SpheneUI.Palette.CrystalBlue;
        var surface = SpheneUI.Colors.Surface;
        var lineH = ImGui.GetTextLineHeight();
        var pending = -1;

        for (var i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            var zMin = new Vector2(min.X + i * (segW + gap), min.Y);
            var zMax = new Vector2(zMin.X + segW, zMin.Y + segH);
            var hov = ImGui.IsMouseHoveringRect(zMin, zMax);

            var topCol = z.On ? SpheneUI.ColorUtils.WithAlpha(accent, hov ? 0.44f : 0.34f)
                : SpheneUI.ColorUtils.WithAlpha(surface, hov ? 0.9f : 0.7f);
            var botCol = z.On ? SpheneUI.ColorUtils.WithAlpha(accent, hov ? 0.30f : 0.20f)
                : SpheneUI.ColorUtils.WithAlpha(surface, hov ? 0.72f : 0.55f);
            SpheneUI.Media.FillRoundedGradient(dl, zMin, zMax, topCol, topCol,
                botCol, botCol, round);
            dl.AddRect(zMin, zMax,
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(
                    z.On ? accent : SpheneUI.Colors.TextMuted, z.On ? 0.9f : (hov ? 0.6f : 0.3f))),
                round);

            var ic = z.Icon.ToIconString();
            var iw = ImGui.CalcTextSize(ic).X;
            var lw = ImGui.CalcTextSize(z.Label).X;
            var stackH = lineH * 2f + 2f * scale;
            var iy = zMin.Y + (segH - stackH) * 0.5f;
            using (SpheneUI.Icons.PushFont())
            {
                dl.AddText(new Vector2(zMin.X + (segW - iw) * 0.5f, iy),
                    z.On ? ImGui.GetColorU32(accent) : ImGui.GetColorU32(ImGuiColors.DalamudGrey), ic);
            }
            dl.AddText(new Vector2(zMin.X + (segW - lw) * 0.5f, iy + lineH + 2f * scale),
                z.On ? ImGui.GetColorU32(Vector4.One) : ImGui.GetColorU32(ImGuiColors.DalamudGrey),
                z.Label);

            if (hov)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                SpheneUI.AttachTooltip(z.On ? $"{z.Label} is bound - click to unbind"
                    : $"Click to bind {z.Label}");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    pending = i;
                }
            }
        }

        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(new Vector2(zones.Count * (segW + gap), segH));

        if (pending < 0)
        {
            return;
        }
        var z_ = zones[pending];
        var gid = group.Group!.GID;
        if (!outdoorCapable)
        {
            var wasOn = areas.Any(b => b.MatchingMode == AreaMatchingMode.HousingPlotIndoor
                && BelongsToProperty(b, loc) && b.Location.RoomId == loc.RoomId);
            SetRoom(areas, loc, !wasOn);
            Save(gid, group, areas);
            return;
        }
        var was = HasArea(areas, loc, z_.Mode, z_.MapId);
        areas.RemoveAll(b => b.MatchingMode == z_.Mode && BelongsToProperty(b, loc)
            && (z_.Mode == AreaMatchingMode.HousingPlotOutdoor
                || b.Location.MapId == z_.MapId
                || (z_.MapId == HousingInteriorRelativeMapIds.Ground && b.Location.MapId == 0)));
        if (!was)
        {
            areas.Add(new AreaBoundLocationDto
            {
                Id = 0,
                Location = new LocationInfo
                {
                    ServerId = loc.ServerId,
                    TerritoryId = loc.TerritoryId,
                    WardId = loc.WardId,
                    HouseId = loc.HouseId,
                    RoomId = 0,
                    MapId = z_.MapId,
                    IsIndoor = z_.Indoor,
                },
                MatchingMode = z_.Mode,
                LocationName = z_.Label,
                CreatedAt = DateTime.UtcNow,
            });
        }
        Save(gid, group, areas);
    }

    /// <summary>Master toggle · flips the shell's IsAreaBound flag while
    /// keeping its bound areas intact.</summary>
    private void ToggleActive(string gid, GroupFullInfoDto group,
        List<AreaBoundLocationDto> areas, AreaBoundSyncshellDto? existing, bool on)
    {
        var snapshot = new List<AreaBoundLocationDto>(areas);
        _ = Task.Run(async () =>
        {
            var dto = new AreaBoundSyncshellDto(group.Group!, snapshot)
            {
                IsAreaBound = on,
            };
            if (existing != null)
            {
                dto.Settings = existing.Settings;
                dto.IsLocked = existing.IsLocked;
            }
            try
            {
                await _apiController.GroupSetAreaBinding(dto).ConfigureAwait(false);
                var idx = _boundShells.FindIndex(
                    s => string.Equals(s.Group?.GID, gid, StringComparison.Ordinal));
                if (idx >= 0) { _boundShells[idx] = dto; } else { _boundShells.Add(dto); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to toggle area binding for {GID}", gid);
            }
        });
    }

    /// <summary>Per-shell binding settings inside the card's collapse ·
    /// writes the whole AreaBoundSettings block on change.</summary>
    private void DrawBindingSettings(string gid, AreaBoundSyncshellDto? existing, bool saving)
    {
        var settings = existing?.Settings ?? new AreaBoundSettings();
        var dirty = false;

        var auto = settings.AutoBroadcastEnabled;
        if (SpheneUI.Controls.Toggle("Auto broadcast", ref auto))
        {
            settings.AutoBroadcastEnabled = auto; dirty = true;
        }
        SpheneUI.Controls.HelpText("Broadcast this syncshell automatically when you enter a bound area.");

        var owner = settings.RequireOwnerPresence;
        if (SpheneUI.Controls.Toggle("Require owner presence", ref owner))
        {
            settings.RequireOwnerPresence = owner; dirty = true;
        }

        var maxJoin = settings.MaxAutoJoinUsers;
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Max auto-join users", ref maxJoin, 1, 10))
        {
            settings.MaxAutoJoinUsers = Math.Clamp(maxJoin, 1, 500); dirty = true;
        }

        var enter = settings.NotifyOnUserEnter;
        if (SpheneUI.Controls.Toggle("Notify on join", ref enter))
        {
            settings.NotifyOnUserEnter = enter; dirty = true;
        }
        ImGui.SameLine();
        var leave = settings.NotifyOnUserLeave;
        if (SpheneUI.Controls.Toggle("Notify on leave", ref leave))
        {
            settings.NotifyOnUserLeave = leave; dirty = true;
        }

        var rules = settings.RequireRulesAcceptance;
        if (SpheneUI.Controls.Toggle("Require rules acceptance", ref rules))
        {
            settings.RequireRulesAcceptance = rules; dirty = true;
        }

        var msg = settings.CustomJoinMessage ?? "";
        ImGui.SetNextItemWidth(-1f);
        if (SpheneUI.Controls.InputTextWithHint("##joinmsg_" + gid, "Optional welcome message", ref msg, 255))
        {
            settings.CustomJoinMessage = string.IsNullOrWhiteSpace(msg) ? null : msg;
            dirty = true;
        }

        if (dirty && existing != null && !saving)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var dto = new AreaBoundSyncshellDto(existing.Group!, existing.BoundAreas)
                    {
                        IsAreaBound = existing.IsAreaBound,
                        IsLocked = existing.IsLocked,
                        Settings = settings,
                    };
                    await _apiController.GroupSetAreaBinding(dto).ConfigureAwait(false);
                    var idx = _boundShells.FindIndex(
                        s => string.Equals(s.Group?.GID, gid, StringComparison.Ordinal));
                    if (idx >= 0) { _boundShells[idx] = dto; }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save binding settings for {GID}", gid);
                }
            });
        }
    }

    private static bool HasArea(List<AreaBoundLocationDto> areas, LocationInfo loc,
        AreaMatchingMode mode, uint mapId)
    {
        foreach (var b in areas)
        {
            if (b.MatchingMode != mode || !BelongsToProperty(b, loc))
            {
                continue;
            }
            if (mode == AreaMatchingMode.HousingPlotOutdoor
                || b.Location.MapId == mapId
                || (mapId == HousingInteriorRelativeMapIds.Ground && b.Location.MapId == 0))
            {
                return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- data

    private List<GroupFullInfoDto> OwnedShells()
    {
        var owned = new List<GroupFullInfoDto>();
        foreach (var kv in _pairManager.GroupPairs)
        {
            if (kv.Key.Group != null
                && string.Equals(kv.Key.OwnerUID, _apiController.UID, StringComparison.Ordinal))
            {
                owned.Add(kv.Key);
            }
        }
        owned.Sort((a, b) => string.Compare(a.GroupAliasOrGID, b.GroupAliasOrGID,
            StringComparison.OrdinalIgnoreCase));
        return owned;
    }

    private List<AreaBoundLocationDto> WorkingAreas(string gid)
    {
        if (!_working.TryGetValue(gid, out var areas))
        {
            var existing = _boundShells.FirstOrDefault(
                s => string.Equals(s.Group?.GID, gid, StringComparison.Ordinal));
            areas = existing?.BoundAreas != null
                ? new List<AreaBoundLocationDto>(existing.BoundAreas)
                : new List<AreaBoundLocationDto>();
            _working[gid] = areas;
        }
        return areas;
    }

    private void SetRoom(List<AreaBoundLocationDto> areas, LocationInfo loc, bool enabled)
    {
        areas.RemoveAll(b => b.MatchingMode == AreaMatchingMode.HousingPlotIndoor
            && BelongsToProperty(b, loc) && b.Location.RoomId == loc.RoomId);
        if (enabled)
        {
            areas.Add(new AreaBoundLocationDto
            {
                Id = 0,
                Location = new LocationInfo
                {
                    ServerId = loc.ServerId,
                    TerritoryId = loc.TerritoryId,
                    WardId = loc.WardId,
                    HouseId = loc.HouseId,
                    RoomId = loc.RoomId,
                    MapId = 0,
                    IsIndoor = true,
                },
                MatchingMode = AreaMatchingMode.HousingPlotIndoor,
                LocationName = loc.HouseId == 100 ? "Apartment" : $"Room {loc.RoomId}",
                CreatedAt = DateTime.UtcNow,
            });
        }
    }

    private void Save(string gid, GroupFullInfoDto group, List<AreaBoundLocationDto> areas)
    {
        if (!_saving.Add(gid))
        {
            return;
        }
        var existing = _boundShells.FirstOrDefault(
            s => string.Equals(s.Group?.GID, gid, StringComparison.Ordinal));
        var snapshot = new List<AreaBoundLocationDto>(areas);
        _ = Task.Run(async () =>
        {
            try
            {
                if (snapshot.Count == 0)
                {
                    if (existing != null)
                    {
                        await _apiController.GroupRemoveAreaBinding(new GroupDto(group.Group!))
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    var dto = new AreaBoundSyncshellDto(group.Group!, snapshot);
                    if (existing != null)
                    {
                        dto.Settings = existing.Settings;
                        dto.IsLocked = existing.IsLocked;
                    }
                    await _apiController.GroupSetAreaBinding(dto).ConfigureAwait(false);
                }
                // Optimistic local update · without it the refresh refetches
                // the pre-write state and the toggle visually snaps back.
                var idx = _boundShells.FindIndex(
                    s => string.Equals(s.Group?.GID, gid, StringComparison.Ordinal));
                if (snapshot.Count == 0)
                {
                    if (idx >= 0)
                    {
                        _boundShells.RemoveAt(idx);
                    }
                }
                else
                {
                    var dto = new AreaBoundSyncshellDto(group.Group!, snapshot);
                    if (existing != null)
                    {
                        dto.Settings = existing.Settings;
                        dto.IsLocked = existing.IsLocked;
                    }
                    if (idx >= 0) { _boundShells[idx] = dto; } else { _boundShells.Add(dto); }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save area binding for {GID}", gid);
            }
            finally
            {
                _saving.Remove(gid);
            }
        });
    }

    private void Refresh(string reason)
    {
        _refreshTask = Task.Run(async () =>
        {
            try
            {
                _detected = await _housingOwnershipService
                    .GetDetectedOwnedHousingPropertiesAsync().ConfigureAwait(false);
                _boundShells = await _apiController.GroupGetAreaBoundSyncshells()
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh housing data ({Reason})", reason);
            }
        });
    }

    private List<(string Type, LocationInfo Location, bool IsActive)> CombinedEntries()
    {
        var combined = new Dictionary<string, (string Type, LocationInfo Location, bool IsActive)>(
            StringComparer.Ordinal);
        foreach (var d in _detected)
        {
            var loc = d.Property.Location;
            var src = d.Source;
            if (string.Equals(src, "FreeCompanyEstate", StringComparison.OrdinalIgnoreCase)) src = "Free Company";
            else if (string.Equals(src, "PersonalChambers", StringComparison.OrdinalIgnoreCase)) src = loc.RoomId > 0 ? $"Room {loc.RoomId}" : "Room";
            else if (string.Equals(src, "ApartmentRoom", StringComparison.OrdinalIgnoreCase)) src = "Apartment";
            var type = loc.HouseId == 100 ? "Apartment" : loc.RoomId > 0 ? "Room" : src;
            combined[PropertyKey(loc)] = (type, loc, false);
        }
        foreach (var v in _housingOwnershipService.GetVerifiedOwnedProperties())
        {
            var loc = v.Location;
            var type = loc.HouseId == 100 ? "Apartment" : loc.RoomId > 0 ? "Room" : "House";
            if (!combined.TryAdd(PropertyKey(loc), (type, loc, true)))
            {
                var existing = combined[PropertyKey(loc)];
                combined[PropertyKey(loc)] = (existing.Type, loc, true);
            }
        }
        var list = new List<(string Type, LocationInfo Location, bool IsActive)>(combined.Values);
        list.Sort((a, b) =>
        {
            var c = a.Location.ServerId.CompareTo(b.Location.ServerId);
            if (c != 0) return c;
            c = a.Location.TerritoryId.CompareTo(b.Location.TerritoryId);
            if (c != 0) return c;
            c = a.Location.WardId.CompareTo(b.Location.WardId);
            if (c != 0) return c;
            c = a.Location.HouseId.CompareTo(b.Location.HouseId);
            return c != 0 ? c : a.Location.RoomId.CompareTo(b.Location.RoomId);
        });
        return list;
    }

    private string WorldName(uint serverId) =>
        _dalamudUtilService.WorldData.Value.TryGetValue((ushort)serverId, out var n)
            ? n
            : $"Server {serverId}";

    private static string PropertyKey(LocationInfo l)
        => $"{l.ServerId}_{l.TerritoryId}_{l.WardId}_{l.HouseId}_{l.RoomId}";

    private static bool BelongsToProperty(AreaBoundLocationDto boundArea, LocationInfo property)
    {
        if (property.WardId <= 0 || property.HouseId <= 0)
        {
            return false;
        }
        var loc = boundArea.Location;
        if (loc.WardId != 0 && loc.WardId != property.WardId) return false;
        if (loc.HouseId != 0 && loc.HouseId != property.HouseId) return false;
        if (loc.ServerId != 0 && loc.ServerId != property.ServerId) return false;
        if (loc.TerritoryId != 0 && loc.TerritoryId != property.TerritoryId) return false;
        if (property.RoomId == 0)
        {
            if (loc.RoomId != 0) return false;
        }
        else if (loc.RoomId != 0 && loc.RoomId != property.RoomId) return false;
        return true;
    }
}
