using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Sphene.FileCache;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration;
using Microsoft.Extensions.Logging;
using Sphene.UI.Styling;
using Sphene.Services;
using System.Numerics;

namespace Sphene.UI.Panels;

public sealed class OneTimeUpdateOptionsSummaryUi : WindowMediatorSubscriberBase
{
    private readonly SpheneConfigService _configService;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly TransientResourceManager _transientResourceManager;
    private readonly UiSharedService _uiShared;
    private string _promptKey = string.Empty;
    private int _selectedTempCollectionEntry = -1;
    private string _uidToAddForTempCollection = string.Empty;

    public OneTimeUpdateOptionsSummaryUi(
        ILogger<OneTimeUpdateOptionsSummaryUi> logger,
        SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        UiSharedService uiShared,
        SpheneConfigService configService,
        PlayerPerformanceConfigService playerPerformanceConfigService,
        TransientResourceManager transientResourceManager)
        : base(logger, mediator, "Sphene - New Options Summary", performanceCollectorService)
    {
        _uiShared = uiShared;
        _configService = configService;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _transientResourceManager = transientResourceManager;
        SizeConstraints = new()
        {
            MinimumSize = new Vector2(760, 540),
            MaximumSize = new Vector2(1200, 1200),
        };
        Flags |= ImGuiWindowFlags.NoCollapse;
        IsOpen = false;

        Mediator.Subscribe<ShowOneTimeUpdateOptionsSummaryMessage>(this, msg =>
        {
            _promptKey = msg.PromptKey;
            if (string.IsNullOrWhiteSpace(_promptKey)) return;
            IsOpen = true;
        });
    }

    public override void OnClose()
    {
        MarkPromptSeen();
    }

    protected override void DrawInternal()
    {
        UiSharedService.TextWrapped("This window summarizes new options added in this update.");
        UiSharedService.TextWrapped("Review and adjust these settings now. It will not be shown again.");
        ImGui.Separator();

        using (var child = ImRaii.Child("##one_time_options_summary_content", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() - 8f * ImGuiHelpers.GlobalScale), true))
        {
            if (child)
            {
                DrawSyncSettingsSection();
                ImGuiHelpers.ScaledDummy(8f);
                DrawCharacterDataSection();
                ImGuiHelpers.ScaledDummy(8f);
                DrawTemporaryCollectionWhitelistSection();
            }
        }

        if (_uiShared.IconTextButton(FontAwesomeIcon.Check, "Done"))
        {
            MarkPromptSeen();
            IsOpen = false;
        }
    }

    private void DrawSyncSettingsSection()
    {
        _uiShared.BigText("Sync Settings");
        UiSharedService.ColorTextWrapped("These options control when sync applies data and which incoming files are filtered.", ImGuiColors.DalamudGrey);

        var disableSyncPause = _configService.Current.DisableSyncPauseDuringDutyOrCombat;
        if (ImGui.Checkbox("Do not pause sync during duties/combat", ref disableSyncPause))
        {
            _configService.Current.DisableSyncPauseDuringDutyOrCombat = disableSyncPause;
            _configService.Save();
        }
        UiSharedService.AttachToolTip("Keep synchronization active during duties/combat. May increase CPU usage in heavy encounters.");

        var filterCharacterLegacy = _configService.Current.FilterCharacterLegacyShpk;

        var disableAutoRedraw = _configService.Current.DisableAutomaticRedrawOnEquipmentOrWeaponChanges;
        if (ImGui.Checkbox("Disable automatic redraw after equipment or weapon changes", ref disableAutoRedraw))
        {
            _configService.Current.DisableAutomaticRedrawOnEquipmentOrWeaponChanges = disableAutoRedraw;
            _configService.Save();
        }
        UiSharedService.AttachToolTip("Prevents redraw triggers for gear/weapon-only changes. Manual redraw may be needed.");

        var redrawSpecialOnly = _configService.Current.RedrawPairsOnlyForSpecialEmotesFirstApply;
        if (ImGui.Checkbox("Redraw pairs only for sit/ground sit/doze on first apply", ref redrawSpecialOnly))
        {
            _configService.Current.RedrawPairsOnlyForSpecialEmotesFirstApply = redrawSpecialOnly;
            _configService.Save();
        }
        UiSharedService.AttachToolTip("Suppresses initial redraw unless the first payload includes sit/ground sit/doze states.");

        var skipPostZoneUnchanged = _configService.Current.SkipPostZoneReapplyWhenUnchanged;
        if (ImGui.Checkbox("Skip post-zone reapply when data is unchanged", ref skipPostZoneUnchanged))
        {
            _configService.Current.SkipPostZoneReapplyWhenUnchanged = skipPostZoneUnchanged;
            _configService.Save();
        }
        UiSharedService.AttachToolTip("After zoning, skip reapply when received character data hash is unchanged.");

        var skipPostZoneEquipmentOnly = _configService.Current.SkipPostZoneReapplyForEquipmentOrWeaponOnlyChanges;
        using (ImRaii.PushIndent(20f * ImGuiHelpers.GlobalScale))
        {
            using (ImRaii.Disabled(!skipPostZoneUnchanged))
            {
                if (ImGui.Checkbox("Skip post-zone reapply for equipment or weapon-only changes", ref skipPostZoneEquipmentOnly))
                {
                    _configService.Current.SkipPostZoneReapplyForEquipmentOrWeaponOnlyChanges = skipPostZoneEquipmentOnly;
                    _configService.Save();
                }
                UiSharedService.AttachToolTip("Dependent option. Skips reapply if only equipment/weapon replacements changed.");
            }
        }

        var preloadPairCollection = _configService.Current.PreloadPairCollectionFromLastReceivedData;
        if (ImGui.Checkbox("Preload pair collection from last received data", ref preloadPairCollection))
        {
            _configService.Current.PreloadPairCollectionFromLastReceivedData = preloadPairCollection;
            _configService.Save();
        }
        UiSharedService.AttachToolTip("Prepares temporary collections in advance using last data to reduce visual pop-in when players appear.");

        var disableTemporaryCollectionsAfterInactivity = _configService.Current.DisableTemporaryCollectionsAfterInactivity;
        if (ImGui.Checkbox("Disable temporary collections after inactivity", ref disableTemporaryCollectionsAfterInactivity))
        {
            _configService.Current.DisableTemporaryCollectionsAfterInactivity = disableTemporaryCollectionsAfterInactivity;
            _configService.Save();
        }
        UiSharedService.AttachToolTip("Removes inactive temporary collections to reduce background overhead and memory usage.");

        var temporaryCollectionTimeoutMinutes = Math.Max(1, _configService.Current.TemporaryCollectionInactivityTimeoutMinutes);
        using (ImRaii.PushIndent(20f * ImGuiHelpers.GlobalScale))
        {
            using (ImRaii.Disabled(!disableTemporaryCollectionsAfterInactivity))
            {
                ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
                if (ImGui.SliderInt("Temporary collection inactivity timeout (minutes)", ref temporaryCollectionTimeoutMinutes, 1, 40))
                {
                    _configService.Current.TemporaryCollectionInactivityTimeoutMinutes = Math.Max(1, temporaryCollectionTimeoutMinutes);
                    _configService.Save();
                }
            }
            UiSharedService.AttachToolTip("How long a pair can stay inactive before its temporary collection is removed.");
        }

        ImGuiHelpers.ScaledDummy(3f);
        ImGui.Separator();

        if (ImGui.Checkbox("Filter characterlegacy.shpk on receive", ref filterCharacterLegacy))
        {
            _configService.Current.FilterCharacterLegacyShpk = filterCharacterLegacy;
            _configService.Save();
        }
        UiSharedService.AttachToolTip("Filters legacy character shader data to avoid known visual/shader issues on some setups.");

        var filterCharacterShpk = _configService.Current.FilterCharacterShpk;
        if (ImGui.Checkbox("Filter character.shpk on receive", ref filterCharacterShpk))
        {
            _configService.Current.FilterCharacterShpk = filterCharacterShpk;
            _configService.Save();
        }
        UiSharedService.AttachToolTip("Filters character shader data if you see shader-related artifacts or instability.");
        UiSharedService.ColorTextWrapped(
            "Experimental: shader file filters are fallback workarounds based on observed issues. Enable them only if you encounter related problems. These options may change or be removed in future updates.",
            ImGuiColors.DalamudYellow);
        ImGuiHelpers.ScaledDummy(3f);

        ImGui.Separator();
    }

    private void DrawCharacterDataSection()
    {
        _uiShared.BigText("Character Data");
        UiSharedService.ColorTextWrapped("These options define local character-data persistence and learning behavior.", ImGuiColors.DalamudGrey);
        var enableModLearning = _configService.Current.EnableModLearning;
        if (ImGui.Checkbox("Enable mod learning", ref enableModLearning))
        {
            SetEnableModLearning(enableModLearning, clearTransientOnEnable: true);
        }
        UiSharedService.AttachToolTip("Learns mod-specific resources for options/emotes. Enabling here clears transient files once to start clean.");

        var persistCharacterData = _configService.Current.PersistReceivedCharacterData;
        if (ImGui.Checkbox("Persist received character data", ref persistCharacterData))
        {
            _configService.Current.PersistReceivedCharacterData = persistCharacterData;
            _configService.Save();
        }
        UiSharedService.AttachToolTip("Stores received data across restarts for faster comparisons and less re-sync work.");
    }

    private void DrawTemporaryCollectionWhitelistSection()
    {
        _uiShared.BigText("Temporary Collection Whitelist");
        UiSharedService.ColorTextWrapped("Only users in this list will use preloaded temporary collections when preload is enabled.", ImGuiColors.DalamudGrey);
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##one_time_temp_uid", "UID or Vanity ID", ref _uidToAddForTempCollection, 20);
        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_uidToAddForTempCollection)))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Plus, "Add"))
            {
                if (!_playerPerformanceConfigService.Current.TemporaryCollectionWhitelist.Contains(_uidToAddForTempCollection, StringComparer.Ordinal))
                {
                    _playerPerformanceConfigService.Current.TemporaryCollectionWhitelist.Add(_uidToAddForTempCollection);
                    _playerPerformanceConfigService.Save();
                }
                _uidToAddForTempCollection = string.Empty;
            }
        }
        UiSharedService.AttachToolTip("Use exact UID/Vanity ID. You can also manage this from the pair user context menu.");

        var tempCollectionList = _playerPerformanceConfigService.Current.TemporaryCollectionWhitelist;
        using (var lb = ImRaii.ListBox("##one_time_temp_whitelist", new Vector2(-1, 120f * ImGuiHelpers.GlobalScale)))
        {
            if (lb)
            {
                for (var i = 0; i < tempCollectionList.Count; i++)
                {
                    if (ImGui.Selectable($"{tempCollectionList[i]}##one_time_temp{i}", _selectedTempCollectionEntry == i))
                    {
                        _selectedTempCollectionEntry = i;
                    }
                }
            }
        }
        using (ImRaii.Disabled(_selectedTempCollectionEntry < 0 || _selectedTempCollectionEntry >= tempCollectionList.Count))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Remove selected"))
            {
                tempCollectionList.RemoveAt(_selectedTempCollectionEntry);
                _selectedTempCollectionEntry = -1;
                _playerPerformanceConfigService.Save();
            }
        }
        UiSharedService.AttachToolTip("Removes the selected user from preload eligibility.");
    }

    private void SetEnableModLearning(bool enableModLearning, bool clearTransientOnEnable)
    {
        var wasEnabled = _configService.Current.EnableModLearning;
        _configService.Current.EnableModLearning = enableModLearning;
        _configService.Save();
        if (!wasEnabled && enableModLearning && clearTransientOnEnable)
        {
            ClearTransientFilesAfterModLearningEnable();
        }
    }

    private void ClearTransientFilesAfterModLearningEnable()
    {
        try
        {
            _transientResourceManager.ClearCurrentCharacterPersistentTransientData();
            _logger.LogInformation("Cleared transient file settings for current character after enabling mod learning from one-time options summary");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear transient file settings after enabling mod learning");
        }
    }

    private void MarkPromptSeen()
    {
        if (string.IsNullOrWhiteSpace(_promptKey)) return;
        if (_configService.Current.SeenOneTimeOptionSummaryPrompts.Contains(_promptKey)) return;
        _configService.Current.SeenOneTimeOptionSummaryPrompts.Add(_promptKey);
        _configService.Save();
    }
}
