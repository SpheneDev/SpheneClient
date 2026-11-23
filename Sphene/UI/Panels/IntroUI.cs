using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using Sphene.FileCache;
using Sphene.Localization;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI.Components;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShrinkU.Configuration;
using ShrinkU.UI;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public partial class IntroUi : WindowMediatorSubscriberBase
{
    private readonly SpheneConfigService _configService;
    private readonly CacheMonitor _cacheMonitor;
    private readonly Dictionary<string, string> _languages = new(StringComparer.Ordinal) { { "English", "en" }, { "Deutsch", "de" }, { "Français", "fr" } };
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly UiSharedService _uiShared;
    private readonly PluginInstallerService _pluginInstaller;
    private int _currentLanguage;
    private readonly string[] _languageItems = ["English", "Deutsch", "Français"];
    private bool _readFirstPage;

    private string _secretKey = string.Empty;
    private string _timeoutLabel = string.Empty;
    private Task? _timeoutTask;
    private string[]? _tosParagraphs;
    private bool _useLegacyLogin = false;
    private readonly SupporterNameplateStudioUi _supporterStudio;
    // ShrinkU integration dependencies
    

    // Page navigation flag
    private bool _showShrinkUPage = false;
    private int? _viewPage;
    private int _lastPage = -1;
    private bool _resetPageScroll;
    private bool _resetTosScroll;
    private readonly ConfigBackupService _configBackupService;
    private readonly FileDialogManager _fileDialogManager;
    private readonly ShrinkUHostService _shrinkuHostService;
    private readonly ShrinkUConfigService _shrinkUConfigService;
    private bool _showImportModal = false;
    private string _importPassword = string.Empty;
    private string _importFilePath = string.Empty;
    private bool _importRestoreCachePath = true;
    private bool _importRestoreShrinkUPath = true;
    private string _backupCachePath = string.Empty;
    private string _backupShrinkUPath = string.Empty;
    private string? _importResultMessage = null;
    private bool _importResultIsError = false;

    private const float BUTTON_AREA_RESERVE = 64.0f;

    public IntroUi(ILogger<IntroUi> logger, UiSharedService uiShared, PluginInstallerService pluginInstaller, SpheneConfigService configService,
        CacheMonitor fileCacheManager, ServerConfigurationManager serverConfigurationManager, SpheneMediator spheneMediator,
        PerformanceCollectorService performanceCollectorService, DalamudUtilService dalamudUtilService,
        ShrinkUHostService shrinkuHostService, ShrinkUConfigService shrinkuConfig,
        ConversionUI shrinkuConversion, FirstRunSetupUI shrinkuFirstRun,
        ConfigBackupService configBackupService, FileDialogManager fileDialogManager,
        SupporterNameplateStudioUi supporterStudio) : base(logger, spheneMediator, "Sphene Setup", performanceCollectorService)
    {
        _uiShared = uiShared;
        _pluginInstaller = pluginInstaller;
        _configService = configService;
        _cacheMonitor = fileCacheManager;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;
        _shrinkuHostService = shrinkuHostService;
        _ = shrinkuConfig;
        _ = shrinkuConversion;
        _ = shrinkuFirstRun;
        _configBackupService = configBackupService;
        _fileDialogManager = fileDialogManager;
        _shrinkUConfigService = shrinkuConfig;
        _supporterStudio = supporterStudio;
        IsOpen = false;
        ShowCloseButton = false;
        RespectCloseHotkey = false;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(720, 640),
            MaximumSize = new Vector2(720, 640),
        };

        GetToSLocalization();

        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) =>
        {
            _configService.Current.UseCompactor = !dalamudUtilService.IsWine;
            IsOpen = true;
        });
    }

    /// <summary>Setup back/forward nav lives in the custom title bar.</summary>
    protected override IReadOnlyList<SpheneUI.Chrome.TitleBarButton>? ChromeRightButtons =>
    [
        new SpheneUI.Chrome.TitleBarButton(FontAwesomeIcon.ChevronLeft, "Previous step", () =>
        {
            var page = CurrentIntroPage();
            if (page > 0) _viewPage = page - 1;
        }),
        new SpheneUI.Chrome.TitleBarButton(FontAwesomeIcon.ChevronRight, "Next step", () =>
        {
            var page = CurrentIntroPage();
            var statePage = StatePage();
            if (page < statePage) _viewPage = page + 1 < statePage ? page + 1 : null;
        }),
    ];

    protected override void DrawInternal()
    {
        if (_uiShared.IsInGpose) return;

        var page = CurrentIntroPage();
        if (page != _lastPage)
        {
            _lastPage = page;
            _resetPageScroll = true;
            _resetTosScroll = true;
        }
        SpheneUI.Layout.PageHeader("Sphene Network Setup", "intro", () =>
        {
            SpheneUI.Text.MutedWrapped("First-run setup - walks you through the essentials before you start syncing.", ImGui.GetCursorPosX() + 320f * SpheneUI.Scale);
        });

        DrawSetupSteps(page);
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);

        // Content fills down to the sticky button strip · reserve covers
        // separator + spacing + the 38-scaled button + bottom padding
        var contentHeight = SpheneUI.Layout.FillToViewBottomHeight(BUTTON_AREA_RESERVE * SpheneUI.Scale);

        var contentBegun = SpheneUI.Layout.BeginScrollArea("ContentArea", new Vector2(0, contentHeight));
        if (contentBegun)
        {
            if (_resetPageScroll)
            {
                ImGui.SetScrollY(0);
                _resetPageScroll = false;
            }
            switch (page)
            {
                case 0:
                    DrawWelcomePageContent();
                    break;
                case 1:
                    DrawAgreementPageContent();
                    break;
                case 2:
                    DrawCacheSetupPageContent();
                    break;
                case 3:
                    _showShrinkUPage = true;
                    DrawShrinkUPageContent();
                    break;
                case 4:
                    DrawNetworkAuthenticationPageContent();
                    break;
                default:
                    Mediator.Publish(new SwitchToMainUiMessage());
                    IsOpen = false;
                    break;
            }
        }
        SpheneUI.Layout.EndScrollArea();

        // Sticky button area at bottom
        SpheneUI.Layout.Separator();
        SpheneUI.Layout.VerticalSpace();
        DrawStickyButtons(page);

        DrawImportBackupModal();
        _supporterStudio.DrawUnlockWindow();
    }

    /// <summary>The displayed page - an explicit back-navigation view wins over the state-driven one.</summary>
    private int CurrentIntroPage() => _viewPage ?? StatePage();

    /// <summary>Wizard page index derived from config state - the furthest point reached.</summary>
    private int StatePage()
    {
        if (_showShrinkUPage) return 3;
        if (!_configService.Current.AcceptedAgreement) return _readFirstPage ? 1 : 0;
        if (string.IsNullOrEmpty(_configService.Current.CacheFolder)
            || !_configService.Current.InitialScanComplete
            || !Directory.Exists(_configService.Current.CacheFolder)) return 2;
        if (!_configService.Current.HasSeenSyncshellSettings) return 3;
        if (!_uiShared.ApiController.ServerAlive) return 4;
        return 5;
    }

    /// <summary>Forward CTA on a viewed page: step the view forward, resume state flow at the frontier.</summary>
    private void AdvanceOrResume(int page)
    {
        _viewPage = page + 1 < StatePage() ? page + 1 : null;
    }

    /// <summary>Compact progress row: dots connected by a hairline, pulsing ring on the active step.</summary>
    private static void DrawSetupSteps(int current)
    {
        var scale = SpheneUI.Scale;
        var dl = ImGui.GetWindowDrawList();
        var labels = new[] { "Welcome", "Agreement", "Archive", "Extras", "Connect" };
        var pos = ImGui.GetCursorScreenPos();
        var avail = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var seg = avail / labels.Length;
        var dotR = 4.5f * scale;
        var cy = pos.Y + dotR + 2f * scale;
        var accent = SpheneUI.Palette.SpheneGold;
        var muted = SpheneUI.Colors.TextMuted;
        var border = SpheneUI.Colors.Border;
        var x0 = pos.X + seg * 0.5f;
        var x1 = pos.X + seg * (labels.Length - 0.5f);

        dl.AddLine(new Vector2(x0, cy), new Vector2(x1, cy),
            SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(muted, 0.30f)), 1.5f * scale);
        if (current > 0)
        {
            dl.AddLine(new Vector2(x0, cy), new Vector2(pos.X + seg * current + seg * 0.5f, cy),
                SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.80f)), 1.5f * scale);
        }

        var pulse = 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 3f);
        for (var i = 0; i < labels.Length; i++)
        {
            var cx = pos.X + seg * i + seg * 0.5f;
            var done = i < current;
            var cur = i == current;
            var fill = done || cur ? accent : SpheneUI.Colors.FrameBg;
            dl.AddCircleFilled(new Vector2(cx, cy), dotR, SpheneUI.ColorUtils.ToU32(fill));
            if (cur)
            {
                dl.AddCircle(new Vector2(cx, cy), dotR + 3f * scale,
                    SpheneUI.ColorUtils.ToU32(SpheneUI.ColorUtils.WithAlpha(accent, 0.35f + 0.40f * pulse)),
                    0, 1.5f * scale);
            }
            else
            {
                dl.AddCircle(new Vector2(cx, cy), dotR,
                    SpheneUI.ColorUtils.ToU32(done ? accent : border), 0, 1.2f * scale);
            }
            var tw = ImGui.CalcTextSize(labels[i]).X;
            dl.AddText(new Vector2(cx - tw * 0.5f, cy + dotR + 4f * scale),
                SpheneUI.ColorUtils.ToU32(cur ? accent : muted), labels[i]);
        }
        ImGui.Dummy(new Vector2(0f, dotR * 2f + ImGui.GetTextLineHeight() + 10f * scale));
    }

    private void DrawWelcomePageContent()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.Wrapped("Experience seamless character synchronization across the realm through the power of Living Memory. " +
                          "Share your complete appearance and glamour with trusted companions in real-time.");

            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Layout.Callout("You'll need Penumbra and Glamourer installed to access the Network's full capabilities.",
                Dalamud.Interface.FontAwesomeIcon.InfoCircle, ImGuiColors.HealerGreen, "Prerequisites");

            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Layout.Callout("Only modifications channeled through Penumbra can be transmitted. " +
                                 "Ensure all your customizations flow through Penumbra's systems for perfect synchronization.",
                Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow, "Important");
        }, title: "Welcome to Sphene Network", icon: FontAwesomeIcon.Star);

        _pluginInstaller.DrawOtherPluginState();

        SpheneUI.Layout.VerticalSpace();
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.ColoredWrapped("Have a previous Sphene backup? Restore all your settings, pairs, and secret keys instantly.", ImGuiColors.DalamudGrey);
            SpheneUI.Layout.VerticalSpace();
            if (SpheneUI.Controls.PrimaryButton("Import Config Backup",
                    Dalamud.Interface.FontAwesomeIcon.FileImport))
            {
                _showImportModal = true;
                _importPassword = string.Empty;
                _importFilePath = string.Empty;
                _importRestoreCachePath = true;
                _importRestoreShrinkUPath = true;
                _backupCachePath = string.Empty;
                _backupShrinkUPath = string.Empty;
                _importResultMessage = null;
            }
        }, title: "Restore from Backup", icon: FontAwesomeIcon.FileImport);
    }

    private void DrawShrinkUPageContent()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.Wrapped("Manage texture backups and convert formats to keep your Penumbra-based mods healthy. ShrinkU is bundled with Sphene and integrates with the same Window System.");

            SpheneUI.Layout.VerticalSpace();
            var shrinkuEnabled = _configService.Current.EnableShrinkUIntegration;
            if (SpheneUI.Controls.Toggle("Enable ShrinkU integration", ref shrinkuEnabled))
            {
                _configService.Current.EnableShrinkUIntegration = shrinkuEnabled;
                _configService.Save();
                try { _shrinkuHostService.ApplyIntegrationEnabled(shrinkuEnabled, openWindow: false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to apply ShrinkU integration setting"); }
            }
            SpheneUI.Text.ColoredWrapped(
                shrinkuEnabled
                    ? "ShrinkU windows and automatic texture optimization are active."
                    : "ShrinkU windows stay hidden. You can still compress textures from the conversion button in the main panel.",
                ImGuiColors.DalamudGrey3);

            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Layout.Callout("Backups are stored under Sphene's cache in 'texture_backups'. First run will configure this automatically.",
                Dalamud.Interface.FontAwesomeIcon.InfoCircle, ImGuiColors.HealerGreen, "Backup Location");

            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Layout.Callout("You can change this anytime later in Settings → Plugins.",
                Dalamud.Interface.FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudGrey3, "Integration Toggle");

            SpheneUI.Layout.VerticalSpace();
        }, title: "ShrinkU", icon: FontAwesomeIcon.Tools);
    }

    private void DrawAgreementPageContent()
    {
        
            var headerMin = ImGui.GetCursorScreenPos();
            SpheneUI.Text.SectionHeader(Strings.ToS.AgreementLabel);
            var afterHeader = ImGui.GetCursorScreenPos();

            // language combo overlaid right into the header's own line - the
            // control is self-explanatory, no "Language" label needed
            var comboW = 140f * SpheneUI.Scale;
            var rightX = afterHeader.X + ImGui.GetContentRegionAvail().X;
            var comboY = MathF.Max(headerMin.Y, headerMin.Y + (ImGui.GetTextLineHeight() - ImGui.GetFrameHeight()) * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(rightX - comboW, comboY));
            ImGui.SetNextItemWidth(comboW);
            if (SpheneUI.Controls.LabeledCombo("##intro-lang",
                    ref _currentLanguage, _languageItems, _languageItems.Length))
            {
                GetToSLocalization(_currentLanguage);
            }
            ImGui.SetCursorScreenPos(afterHeader);

            // Prominent warning
            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Layout.Callout(Strings.ToS.ReadLabel,
                Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle,
                ImGuiColors.DalamudRed);

            SpheneUI.Layout.Separator();
            SpheneUI.Layout.VerticalSpace();
            
            // Terms fill the remaining content height · scrollable, padded,
            // numbered paragraphs so the document reads structured
            var tosH = SpheneUI.Layout.FillToViewBottomHeight();
            if (SpheneUI.Layout.BeginScrollArea("TermsContent", new Vector2(0, tosH),
                    border: true,
                    contentPadding: new Vector2(14f, 10f) * SpheneUI.Scale))
            {
                if (_resetTosScroll)
                {
                    ImGui.SetScrollY(0);
                    _resetTosScroll = false;
                }
                var accent = SpheneUI.Palette.SpheneGold;
                for (var i = 0; i < _tosParagraphs!.Length; i++)
                {
                    SpheneUI.Text.Colored($"{i + 1}.", accent);
                    ImGui.SameLine(0f, 8f * SpheneUI.Scale);
                    SpheneUI.Text.Wrapped(_tosParagraphs[i]);
                    if (i < _tosParagraphs.Length - 1)
                    {
                        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                    }
                }
            }
            SpheneUI.Layout.EndScrollArea();

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Text.ColoredWrapped(Strings.ToS.LastUpdated, ImGuiColors.DalamudGrey3);
    }

    private void DrawCacheSetupPageContent()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            if (!_uiShared.HasValidPenumbraModPath)
            {
                SpheneUI.Layout.Callout("Please configure a valid Penumbra mod directory path before proceeding.",
                    Dalamud.Interface.FontAwesomeIcon.ExclamationCircle, ImGuiColors.DalamudRed, "Penumbra Path Required");
            }
            else
            {
                SpheneUI.Text.Wrapped("The Network requires a local archive to store synchronized character data and must catalog your existing modifications for optimal transmission efficiency.");

                SpheneUI.Layout.VerticalSpace();
                SpheneUI.Layout.Callout(
                    "• Initial cataloging may take time depending on your modification library\n" +
                    "• Do not remove FileCache.csv from your Dalamud Plugin Configurations\n" +
                    "• Ensure your Penumbra directory structure is properly configured",
                    Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow, "Important Notes");

                SpheneUI.Layout.VerticalSpace();
                _uiShared.DrawCacheDirectorySetting();
            }
        }, title: "Memory Archive Configuration", icon: FontAwesomeIcon.Database);

        if (!_cacheMonitor.IsScanRunning && !string.IsNullOrEmpty(_configService.Current.CacheFolder) && _uiShared.HasValidPenumbraModPath && Directory.Exists(_configService.Current.CacheFolder))
        {
            // Scan button will be in sticky area
        }
        else
        {
            _uiShared.DrawFileScanState();
        }
        
        if (!_dalamudUtilService.IsWine)
        {
            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Layout.SectionCard(() =>
            {
                var useFileCompactor = _configService.Current.UseCompactor;
                if (SpheneUI.Controls.Toggle("Enable File Compactor", ref useFileCompactor))
                {
                    _configService.Current.UseCompactor = useFileCompactor;
                    _configService.Save();
                }
                SpheneUI.AttachTooltip("Saves significant disk space for downloads with minor CPU overhead. Recommended to keep enabled.");
                
                SpheneUI.Layout.VerticalSpace();
                SpheneUI.Text.ColoredWrapped("The File Compactor reduces storage requirements for downloaded content while providing faster loading times. " +
                    "This setting can be changed later in Sphene settings.", ImGuiColors.DalamudGrey3);
            }, title: "File Compactor", icon: FontAwesomeIcon.FileArchive);
        }
    }

    

    private void DrawNetworkAuthenticationPageContent()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.Wrapped("Establish your identity within the Sphene Network to access synchronization services.");

            SpheneUI.Layout.VerticalSpace();
            SpheneUI.Layout.Callout(
                $"For the primary Network node \"{WebAPI.ApiController.MainServer}\", authentication is managed through our Discord community. " +
                "Join the Discord and follow the authentication protocols in #sphene-registration.",
                Dalamud.Interface.FontAwesomeIcon.InfoCircle, ImGuiColors.HealerGreen, "Main Server Authentication");
        }, title: "Network Authentication", icon: FontAwesomeIcon.Shield);

        SpheneUI.Layout.VerticalSpace();

        SpheneUI.Layout.SectionCard(() =>
        {

            // brand links fill the card width in thirds · taller for bigger
            // glyphs · Ko-fi carries the perks half as a joined split button
            var brandH = 36f * SpheneUI.Scale;
            var thirdW = (ImGui.GetContentRegionAvail().X - 16f * SpheneUI.Scale) / 3f;
            if (SpheneBrandIcons.IconTextButton(SpheneBrandIcons.Brand.Discord, "Discord",
                    "intro-discord", width: thirdW, tooltip: "Open the Sphene Discord community",
                    height: brandH))
            {
                Util.OpenLink("https://discord.gg/GbnwsP2XsF");
            }
            ImGui.SameLine(0f, 8f * SpheneUI.Scale);

            var split = SpheneBrandIcons.KofiPerksSplit(thirdW, brandH);
            if (split.kofi)
            {
                Util.OpenLink("https://ko-fi.com/sphenedev");
            }
            if (split.perks)
            {
                _supporterStudio.OpenUnlockWindow();
            }

            ImGui.SameLine(0f, 8f * SpheneUI.Scale);
            if (SpheneBrandIcons.IconTextButton(SpheneBrandIcons.Brand.Sphene, "Sphene",
                    "intro-sphene", width: thirdW, tooltip: "Open sphene.online",
                    height: brandH))
            {
                Util.OpenLink("https://sphene.online");
            }

            SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.X4);
            SpheneUI.Text.ColoredWrapped(
                "Ko-fi donations unlock nameplate perks - tap the perks half for the live preview.",
                ImGuiColors.DalamudGrey3);
        }, title: "Community & Support", icon: FontAwesomeIcon.Users);

        SpheneUI.Layout.VerticalSpace();

        // Single canonical server - no selection UI needed; still pin legacy key auth
        _useLegacyLogin = true;
        var selectedServer = _serverConfigurationManager.GetServerByIndex(0);
        selectedServer.UseOAuth2 = false;
        _serverConfigurationManager.Save();

        SpheneUI.Layout.VerticalSpace();
        
        // Authentication Key Input
        if (_useLegacyLogin)
        {
            SpheneUI.Layout.SectionCard(() =>
            {
                ImGui.SetNextItemWidth(-1);
                SpheneUI.Controls.InputTextWithHint("##secretKey", "Enter your 64-character authentication key...", ref _secretKey, 64);

                if (_secretKey.Length > 0 && _secretKey.Length != 64)
                {
                    SpheneUI.Layout.VerticalSpace();
                    SpheneUI.Layout.Callout("Authentication key must be exactly 64 characters long. Do not use Lodestone credentials.",
                        Dalamud.Interface.FontAwesomeIcon.ExclamationCircle, ImGuiColors.DalamudRed, "Invalid Length");
                }
                else if (_secretKey.Length == 64 && !Base32Regex().IsMatch(_secretKey))
                {
                    SpheneUI.Layout.VerticalSpace();
                    SpheneUI.Layout.Callout("Authentication key may only contain letters A-Z and numbers 2-7.",
                        Dalamud.Interface.FontAwesomeIcon.ExclamationCircle, ImGuiColors.DalamudRed, "Invalid Format");
                }

                if (_dalamudUtilService.IsWine)
                {
                    SpheneUI.Layout.VerticalSpace();
                    ConnectivityOptionBlock.DrawWineForceWebSocketsOption(selectedServer, _serverConfigurationManager, _uiShared, "IntroWineForceWebSockets");
                }
            }, title: "Authentication Key", icon: FontAwesomeIcon.Key);
        }
        else
        {
            // OAuth2 flow (kept for compatibility but not used by default)
            SpheneUI.Layout.SectionCard(() =>
            {
                var selectedServer = _serverConfigurationManager.GetServerByIndex(0);
                if (string.IsNullOrEmpty(selectedServer.OAuthToken))
                {
                    SpheneUI.Text.Wrapped("Press the button below to verify Network OAuth2 compatibility and authenticate through Discord.");
                    _uiShared.DrawOAuth(selectedServer);
                }
                else
                {
                    SpheneUI.Text.ColoredWrapped($"Network authentication established. Connected as: Discord User {_serverConfigurationManager.GetDiscordUserFromToken(selectedServer)}", ImGuiColors.HealerGreen);
                    SpheneUI.Text.Wrapped("Retrieve your Network identifiers to complete the connection process.");
                    _uiShared.DrawUpdateOAuthUIDsButton(selectedServer);
                    
                    var playerName = _dalamudUtilService.GetPlayerName();
                    var playerWorld = _dalamudUtilService.GetHomeWorldId();
                    if (!string.IsNullOrEmpty(playerName) && playerWorld != 0)
                    {
                        var worldName = _dalamudUtilService.WorldData.Value.TryGetValue((ushort)playerWorld, out var world) ? world : "Unknown";
                        SpheneUI.Text.Wrapped($"Current Character: {playerName}@{worldName}");
                        
                        SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.UserPlus, "Add Current Character"))
                        {
                            _serverConfigurationManager.AddCurrentCharacterToServer();
                        }
                    }
                }
            }, title: "Discord Authentication", icon: FontAwesomeIcon.SignInAlt);
        }
    }

    private void DrawStickyButtons(int page)
    {
        var fullW = new Vector2(-1f, 38f * SpheneUI.Scale);
        var successColor = new Vector4(0.25f, 0.70f, 0.35f, 1.0f);
        switch (page)
        {
            case 0:
                if (SpheneUI.Controls.PrimaryButton("Begin Setup",
                        Dalamud.Interface.FontAwesomeIcon.Rocket, fullW))
                {
                    _readFirstPage = true;
                    AdvanceOrResume(page);
#if !DEBUG
                    _timeoutTask = Task.Run(async () =>
                    {
                        for (int i = 20; i > 0; i--)
                        {
                            _timeoutLabel = $"{Strings.ToS.ButtonWillBeAvailableIn} {i}s";
                            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                        }
                    });
#else
                    _timeoutTask = Task.CompletedTask;
#endif
                }
                break;
            case 1:
                if (_timeoutTask?.IsCompleted ?? true)
                {
                    if (SpheneUI.Controls.PrimaryButton(Strings.ToS.AgreeLabel,
                            Dalamud.Interface.FontAwesomeIcon.Check, fullW, successColor))
                    {
                        _configService.Current.AcceptedAgreement = true;
                        _configService.Save();
                        AdvanceOrResume(page);
                    }
                }
                else
                {
                    SpheneUI.Controls.PrimaryButton($"Please Wait ({_timeoutLabel})",
                        Dalamud.Interface.FontAwesomeIcon.Clock, fullW,
                        ImGuiColors.DalamudYellow, enabled: false);
                }
                break;
            case 2:
                if (!_cacheMonitor.IsScanRunning && !string.IsNullOrEmpty(_configService.Current.CacheFolder)
                    && _uiShared.HasValidPenumbraModPath && Directory.Exists(_configService.Current.CacheFolder)
                    && SpheneUI.Controls.PrimaryButton("Start Archive Scan",
                        Dalamud.Interface.FontAwesomeIcon.Sync, fullW, successColor))
                {
                    _cacheMonitor.InvokeScan();
                }
                break;
            case 3:
                if (SpheneUI.Controls.PrimaryButton("Continue to Authentication",
                        Dalamud.Interface.FontAwesomeIcon.ArrowRight, fullW, successColor))
                {
                    _configService.Current.HasSeenSyncshellSettings = true;
                    _configService.Save();
                    _showShrinkUPage = false;
                    _viewPage = null;
                }
                break;
            case 4:
                if (_useLegacyLogin && _secretKey.Length == 64 && Base32Regex().IsMatch(_secretKey)
                    && SpheneUI.Controls.PrimaryButton("Save Authentication Key",
                        Dalamud.Interface.FontAwesomeIcon.Key, fullW, successColor))
                {
                if (_serverConfigurationManager.CurrentServer == null) _serverConfigurationManager.SelectServer(0);
                if (!_serverConfigurationManager.CurrentServer!.SecretKeys.Any())
                {
                    _serverConfigurationManager.CurrentServer!.SecretKeys.Add(_serverConfigurationManager.CurrentServer.SecretKeys.Select(k => k.Key).LastOrDefault() + 1, new SecretKey()
                    {
                        FriendlyName = $"Authentication Key added on Setup ({DateTime.UtcNow:yyyy-MM-dd})",
                        Key = _secretKey,
                    });
                    _serverConfigurationManager.AddCurrentCharacterToServer();
                }
                else
                {
                    _serverConfigurationManager.CurrentServer!.SecretKeys[0] = new SecretKey()
                    {
                        FriendlyName = $"Authentication Key added on Setup ({DateTime.UtcNow:yyyy-MM-dd})",
                        Key = _secretKey,
                    };
                }
                _secretKey = string.Empty;
                _ = Task.Run(() => _uiShared.ApiController.CreateConnectionsAsync());
                }
                break;
        }
    }

    private void DrawImportBackupModal()
    {
        if (!_showImportModal) return;

        var center = (ImGui.GetMainViewport().Size / 2) + ImGui.GetMainViewport().Pos;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(450, 250), new Vector2(700, 500));

        if (ImGui.Begin("Import Config Backup", ref _showImportModal, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            if (string.IsNullOrEmpty(_importFilePath))
            {
                SpheneUI.Text.ColoredWrapped("Select a backup file to restore all your Sphene settings.", ImGuiColors.DalamudGrey);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                if (SpheneUI.Controls.Button("Select Backup File", new Vector2(0, 0)))
                {
                    _fileDialogManager.OpenFileDialog("Import Config Backup", ".json", (success, path) =>
                    {
                        if (success && !string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            _importFilePath = path;
                            try
                            {
                                var json = File.ReadAllText(path);
                                var dto = JsonSerializer.Deserialize<SpheneBackupDto>(json);
                                if (dto != null)
                                {
                                    _backupCachePath = ConfigBackupService.GetBackupCacheFolderPath(dto);
                                    _backupShrinkUPath = ConfigBackupService.GetBackupShrinkUPath(dto);
                                }
                            }
                            catch { /* ignore */ }
                        }
                    });
                }
            }
            else
            {
                SpheneUI.Text.ColoredWrapped($"Selected: {Path.GetFileName(_importFilePath)}", ImGuiColors.ParsedGreen);
                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                SpheneUI.Text.ColoredWrapped("If the backup is password-protected, enter the password below. Otherwise leave blank.", ImGuiColors.DalamudGrey);
                SpheneUI.Text.Plain("Password (if encrypted):");
                SpheneUI.Controls.InputText("##introImportPassword", ref _importPassword, 64, ImGuiInputTextFlags.Password);
                SpheneUI.Layout.Space(SpheneUI.Spacing.X10);

                SpheneUI.Layout.Separator();
                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                SpheneUI.Text.SectionHeader("Path Options", "Choose whether to restore folder paths from the backup or keep current paths.");
                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                if (!string.IsNullOrEmpty(_backupCachePath))
                {
                    SpheneUI.Controls.Toggle($"Restore Cache Folder from backup: {_backupCachePath}", ref _importRestoreCachePath);
                    if (!_importRestoreCachePath)
                    {
                        ImGui.Indent();
                        SpheneUI.Text.ColoredWrapped($"Current cache folder will be kept: {_configService.Current.CacheFolder}", ImGuiColors.DalamudGrey);
                        ImGui.Unindent();
                    }
                }
                else
                {
                    _importRestoreCachePath = false;
                    SpheneUI.Text.ColoredWrapped("No cache folder path in backup.", ImGuiColors.DalamudGrey);
                }

                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);

                var currentShrinkU = _shrinkUConfigService?.Current?.BackupFolderPath ?? "Not configured";
                if (!string.IsNullOrEmpty(_backupShrinkUPath))
                {
                    SpheneUI.Controls.Toggle($"Restore ShrinkU Backup Folder from backup: {_backupShrinkUPath}", ref _importRestoreShrinkUPath);
                    if (!_importRestoreShrinkUPath)
                    {
                        ImGui.Indent();
                        SpheneUI.Text.ColoredWrapped($"Current ShrinkU folder will be kept: {currentShrinkU}", ImGuiColors.DalamudGrey);
                        ImGui.Unindent();
                    }
                }
                else
                {
                    _importRestoreShrinkUPath = false;
                    SpheneUI.Text.ColoredWrapped("No ShrinkU backup folder path in backup.", ImGuiColors.DalamudGrey);
                }

                SpheneUI.Layout.Space(SpheneUI.Spacing.X10);

                var buttonSize = (ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X -
                                  ImGui.GetStyle().ItemSpacing.X) / 2;

                if (SpheneUI.Controls.Button("Import", new Vector2(buttonSize, 0)))
                {
                    try
                    {
                        var password = string.IsNullOrWhiteSpace(_importPassword) ? null : _importPassword;
                        var result = _configBackupService.ImportFromFile(_importFilePath, password, _importRestoreCachePath, _importRestoreShrinkUPath, _shrinkUConfigService);
                        if (result.Success)
                        {
                            _importResultMessage = $"Import completed: {result.RestoredCount} config(s) restored.";
                            _importResultIsError = false;
                            _showImportModal = false;
                            _configService.Current.AcceptedAgreement = true;
                            _configService.Save();
                        }
                        else
                        {
                            _importResultMessage = result.ErrorMessage ?? "Import failed.";
                            _importResultIsError = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _importResultMessage = "Failed to import backup: " + ex.Message;
                        _importResultIsError = true;
                    }
                }

                ImGui.SameLine();
                if (SpheneUI.Controls.Button("Cancel", new Vector2(buttonSize, 0)))
                {
                    _showImportModal = false;
                }
            }

            if (!string.IsNullOrEmpty(_importResultMessage))
            {
                SpheneUI.Layout.Space(SpheneUI.Spacing.X4);
                var color = _importResultIsError ? ImGuiColors.DalamudRed : ImGuiColors.ParsedGreen;
                SpheneUI.Text.Colored(_importResultMessage, color);
            }

            ImGui.End();
        }
    }

    private void GetToSLocalization(int changeLanguageTo = -1)
    {
        if (changeLanguageTo != -1)
        {
            _uiShared.LoadLocalization(_languages.ElementAt(changeLanguageTo).Value);
        }

        _tosParagraphs = [Strings.ToS.Paragraph1, Strings.ToS.Paragraph2, Strings.ToS.Paragraph3, Strings.ToS.Paragraph4, Strings.ToS.Paragraph5, Strings.ToS.Paragraph6, Strings.ToS.Paragraph7];
    }

    [GeneratedRegex("^[A-Z0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Base32Regex();
}
