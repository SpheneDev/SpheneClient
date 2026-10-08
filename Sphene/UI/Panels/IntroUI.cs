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
    private int _currentLanguage;
    private bool _readFirstPage;

    private string _secretKey = string.Empty;
    private string _timeoutLabel = string.Empty;
    private Task? _timeoutTask;
    private string[]? _tosParagraphs;
    private bool _useLegacyLogin = false;

    // ShrinkU integration dependencies
    

    // Page navigation flag
    private bool _showShrinkUPage = false;
    private readonly ConfigBackupService _configBackupService;
    private readonly FileDialogManager _fileDialogManager;
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

    private const float BUTTON_HEIGHT = 32.0f;
    private const float HEADER_HEIGHT = 32.0f;
    private const float BUTTON_AREA_HEIGHT = 80.0f; // Increased to accommodate info boxes and multiple elements

    public IntroUi(ILogger<IntroUi> logger, UiSharedService uiShared, SpheneConfigService configService,
        CacheMonitor fileCacheManager, ServerConfigurationManager serverConfigurationManager, SpheneMediator spheneMediator,
        PerformanceCollectorService performanceCollectorService, DalamudUtilService dalamudUtilService,
        ShrinkUHostService shrinkuHostService, ShrinkUConfigService shrinkuConfig,
        ConversionUI shrinkuConversion, FirstRunSetupUI shrinkuFirstRun,
        ConfigBackupService configBackupService, FileDialogManager fileDialogManager) : base(logger, spheneMediator, "Sphene Setup", performanceCollectorService)
    {
        _uiShared = uiShared;
        _configService = configService;
        _cacheMonitor = fileCacheManager;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;
        _ = shrinkuHostService;
        _ = shrinkuConfig;
        _ = shrinkuConversion;
        _ = shrinkuFirstRun;
        _configBackupService = configBackupService;
        _fileDialogManager = fileDialogManager;
        _shrinkUConfigService = shrinkuConfig;
        IsOpen = false;
        ShowCloseButton = false;
        RespectCloseHotkey = false;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(700, 560),
            MaximumSize = new Vector2(700, 600),
        };

        GetToSLocalization();

        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) =>
        {
            _configService.Current.UseCompactor = !dalamudUtilService.IsWine;
            IsOpen = true;
        });
    }

    private int _prevIdx = -1;

    protected override void DrawInternal()
    {
        if (_uiShared.IsInGpose) return;

        SpheneUI.Text.PageHeader("Sphene Network Setup");

        // Calculate available content height (window height minus header and button area)
        var windowHeight = ImGui.GetWindowSize().Y;
        var headerHeight = HEADER_HEIGHT + 10; // Header + spacing
        var contentHeight = windowHeight - headerHeight - BUTTON_AREA_HEIGHT - 20; // Extra margin

        var contentBegun = SpheneUI.Layout.BeginScrollArea("ContentArea", new Vector2(0, contentHeight));
        if (contentBegun)
        {
            if (_showShrinkUPage)
            {
                DrawShrinkUPageContent();
            }
            else if (!_configService.Current.AcceptedAgreement && !_readFirstPage)
            {
                DrawWelcomePageContent();
            }
            else if (!_configService.Current.AcceptedAgreement && _readFirstPage)
            {
                DrawAgreementPageContent();
            }
            else if (_configService.Current.AcceptedAgreement
                     && (string.IsNullOrEmpty(_configService.Current.CacheFolder)
                         || !_configService.Current.InitialScanComplete
                         || !Directory.Exists(_configService.Current.CacheFolder)))
            {
                DrawCacheSetupPageContent();
            }
            else if (_configService.Current.AcceptedAgreement
                     && !string.IsNullOrEmpty(_configService.Current.CacheFolder)
                     && _configService.Current.InitialScanComplete
                     && Directory.Exists(_configService.Current.CacheFolder)
                     && !_configService.Current.HasSeenSyncshellSettings)
            {
                _showShrinkUPage = true;
                DrawShrinkUPageContent();
            }
            else if (!_uiShared.ApiController.ServerAlive)
            {
                DrawNetworkAuthenticationPageContent();
            }
            else
            {
                Mediator.Publish(new SwitchToMainUiMessage());
                IsOpen = false;
            }
        }
        SpheneUI.Layout.EndScrollArea();

        // Sticky button area at bottom
        SpheneUI.Layout.Separator();
        ImGui.Spacing();
        DrawStickyButtons();

        DrawImportBackupModal();
    }

    private void DrawWelcomePageContent()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.SectionHeader("Welcome to Sphene Network", icon: Dalamud.Interface.FontAwesomeIcon.Star);

            SpheneUI.Text.Wrapped("Experience seamless character synchronization across the realm through the power of Living Memory. " +
                          "Share your complete appearance and glamour with trusted companions in real-time.");

            ImGui.Spacing();
            SpheneUI.Layout.Callout("You'll need Penumbra and Glamourer installed to access the Network's full capabilities.",
                Dalamud.Interface.FontAwesomeIcon.InfoCircle, ImGuiColors.HealerGreen, "Prerequisites");

            ImGui.Spacing();
            SpheneUI.Layout.Callout("Only modifications channeled through Penumbra can be transmitted. " +
                                 "Ensure all your customizations flow through Penumbra's systems for perfect synchronization.",
                Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow, "Important");
        });

        _uiShared.DrawOtherPluginState();

        ImGui.Spacing();
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.SectionHeader("Restore from Backup", icon: Dalamud.Interface.FontAwesomeIcon.FileImport);
            SpheneUI.Text.ColoredWrapped("Have a previous Sphene backup? Restore all your settings, pairs, and secret keys instantly.", ImGuiColors.DalamudGrey);
            ImGui.Spacing();
            if (SpheneUI.Controls.HeroButton("intro-import-backup", "Import Config Backup",
                    Dalamud.Interface.FontAwesomeIcon.FileImport, new Vector2(200f, BUTTON_HEIGHT),
                    new Vector4(0.35f, 0.55f, 0.85f, 1.0f)))
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
        });
    }

    private static void DrawShrinkUPageContent()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.SectionHeader("ShrinkU", icon: Dalamud.Interface.FontAwesomeIcon.Tools);
            SpheneUI.Text.Wrapped("Manage texture backups and convert formats to keep your Penumbra-based mods healthy. ShrinkU is bundled with Sphene and integrates with the same Window System.");

            ImGui.Spacing();
            SpheneUI.Layout.Callout("Backups are stored under Sphene's cache in 'texture_backups'. First run will configure this automatically.",
                Dalamud.Interface.FontAwesomeIcon.InfoCircle, ImGuiColors.HealerGreen, "Backup Location");

            ImGui.Spacing();
            SpheneUI.Layout.Callout("You can enable or disable ShrinkU integration anytime in Settings → Appearance.",
                Dalamud.Interface.FontAwesomeIcon.InfoCircle, ImGuiColors.DalamudGrey3, "Integration Toggle");

            ImGui.Spacing();
        });
    }

    private void DrawAgreementPageContent()
    {
        
            // Language selector in top right
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - 120);
            SpheneUI.Text.Plain(Strings.ToS.LanguageLabel);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            if (ImGui.Combo("##language", ref _currentLanguage, _languages.Keys.ToArray(), _languages.Count))
            {
                GetToSLocalization(_currentLanguage);
            }
            
            ImGui.SetCursorPosX(0);
            SpheneUI.Text.SectionHeader(Strings.ToS.AgreementLabel);
            
            // Prominent warning
            ImGui.Spacing();
            var warningText = Strings.ToS.ReadLabel;
            var warningSize = ImGui.CalcTextSize(warningText);
            ImGui.SetCursorPosX((ImGui.GetWindowSize().X - warningSize.X) * 0.5f);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
            {
                ImGui.SetWindowFontScale(1.3f);
                SpheneUI.Text.Plain(warningText);
                ImGui.SetWindowFontScale(1.0f);
            }
            
            SpheneUI.Layout.Separator();
            ImGui.Spacing();
            
            // Terms content area without scrollbars
            using (var child = ImRaii.Child("TermsContent", new Vector2(0, 300), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (child)
                {
                    foreach (var paragraph in _tosParagraphs!)
                    {
                        SpheneUI.Text.Wrapped(paragraph);
                        ImGui.Spacing();
                    }
                }
            }
    }

    private void DrawCacheSetupPageContent()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.SectionHeader("Memory Archive Configuration", icon: Dalamud.Interface.FontAwesomeIcon.Database);

            ImGui.Spacing();
            if (!_uiShared.HasValidPenumbraModPath)
            {
                SpheneUI.Layout.Callout("Please configure a valid Penumbra mod directory path before proceeding.",
                    Dalamud.Interface.FontAwesomeIcon.ExclamationCircle, ImGuiColors.DalamudRed, "Penumbra Path Required");
            }
            else
            {
                SpheneUI.Text.Wrapped("The Network requires a local archive to store synchronized character data and must catalog your existing modifications for optimal transmission efficiency.");

                ImGui.Spacing();
                SpheneUI.Layout.Callout(
                    "• Initial cataloging may take time depending on your modification library\n" +
                    "• Do not remove FileCache.csv from your Dalamud Plugin Configurations\n" +
                    "• Ensure your Penumbra directory structure is properly configured",
                    Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow, "Important Notes");

                ImGui.Spacing();
                _uiShared.DrawCacheDirectorySetting();
            }
        });

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
            ImGui.Spacing();
            SpheneUI.Layout.SectionCard(() =>
            {
                var useFileCompactor = _configService.Current.UseCompactor;
                if (SpheneUI.Controls.Toggle("Enable File Compactor", ref useFileCompactor))
                {
                    _configService.Current.UseCompactor = useFileCompactor;
                    _configService.Save();
                }
                SpheneUI.AttachTooltip("Saves significant disk space for downloads with minor CPU overhead. Recommended to keep enabled.");
                
                ImGui.Spacing();
                SpheneUI.Text.ColoredWrapped("The File Compactor reduces storage requirements for downloaded content while providing faster loading times. " +
                    "This setting can be changed later in Sphene settings.", ImGuiColors.DalamudGrey3);
            });
        }
    }

    

    private void DrawNetworkAuthenticationPageContent()
    {
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.SectionHeader("Network Authentication", icon: Dalamud.Interface.FontAwesomeIcon.Shield);

            ImGui.Spacing();
            SpheneUI.Text.Wrapped("Establish your identity within the Sphene Network to access synchronization services.");

            ImGui.Spacing();
            SpheneUI.Layout.Callout(
                $"For the primary Network node \"{WebAPI.ApiController.MainServer}\", authentication is managed through our Discord community. " +
                "Join the Discord and follow the authentication protocols in #sphene-registration.",
                Dalamud.Interface.FontAwesomeIcon.InfoCircle, ImGuiColors.HealerGreen, "Main Server Authentication");
        });

        ImGui.Spacing();
        if (SpheneUI.Controls.HeroButton("intro-join-discord", "Join Discord Community",
                Dalamud.Interface.FontAwesomeIcon.Users, new Vector2(200f, 50f),
                new Vector4(0.44f, 0.47f, 0.78f, 1.0f), textScale: 0.5f))
        {
            Util.OpenLink("https://discord.gg/GbnwsP2XsF");
        }

        ImGui.Spacing();

        // Network Configuration
        SpheneUI.Layout.SectionCard(() =>
        {
            SpheneUI.Text.SectionHeader("Network Configuration", icon: Dalamud.Interface.FontAwesomeIcon.Cog);
            
            int serverIdx = 0;

            serverIdx = _uiShared.DrawServiceSelection(selectOnChange: true, showConnect: false);
            if (serverIdx != _prevIdx)
            {
                _uiShared.ResetOAuthTasksState();
                _prevIdx = serverIdx;
            }

            var selectedServer = _serverConfigurationManager.GetServerByIndex(serverIdx);
            
            // Force legacy authentication mode by default
            _useLegacyLogin = true;
            selectedServer.UseOAuth2 = false;
            _serverConfigurationManager.Save();

            if (_dalamudUtilService.IsWine)
            {
                ImGui.Spacing();
                ConnectivityOptionBlock.DrawWineForceWebSocketsOption(selectedServer, _serverConfigurationManager, _uiShared, "IntroWineForceWebSockets");
            }
        });

        ImGui.Spacing();
        
        // Authentication Key Input
        if (_useLegacyLogin)
        {
            SpheneUI.Layout.SectionCard(() =>
            {
                SpheneUI.Text.SectionHeader("Authentication Key", icon: Dalamud.Interface.FontAwesomeIcon.Key);

                ImGui.SetNextItemWidth(-1);
                SpheneUI.Controls.InputTextWithHint("##secretKey", "Enter your 64-character authentication key...", ref _secretKey, 64);

                if (_secretKey.Length > 0 && _secretKey.Length != 64)
                {
                    ImGui.Spacing();
                    SpheneUI.Layout.Callout("Authentication key must be exactly 64 characters long. Do not use Lodestone credentials.",
                        Dalamud.Interface.FontAwesomeIcon.ExclamationCircle, ImGuiColors.DalamudRed, "Invalid Length");
                }
                else if (_secretKey.Length == 64 && !Base32Regex().IsMatch(_secretKey))
                {
                    ImGui.Spacing();
                    SpheneUI.Layout.Callout("Authentication key may only contain letters A-Z and numbers 2-7.",
                        Dalamud.Interface.FontAwesomeIcon.ExclamationCircle, ImGuiColors.DalamudRed, "Invalid Format");
                }
            });
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
                        
                        if (SpheneUI.Icons.TextButton(FontAwesomeIcon.UserPlus, "Add Current Character"))
                        {
                            _serverConfigurationManager.AddCurrentCharacterToServer();
                        }
                    }
                }
            });
        }
    }

    private void DrawStickyButtons()
    {
        var successColor = new Vector4(0.25f, 0.70f, 0.35f, 1.0f);
        if (!_configService.Current.AcceptedAgreement && !_readFirstPage)
        {
            // Welcome page button
            if (SpheneUI.Controls.HeroButton("intro-begin-setup", "Begin Setup"))
            {
                _readFirstPage = true;
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
        }
        else if (!_configService.Current.AcceptedAgreement && _readFirstPage)
        {
            // Agreement page button
            if (_timeoutTask?.IsCompleted ?? true)
            {
                if (SpheneUI.Controls.HeroButton("intro-agree", Strings.ToS.AgreeLabel, color: successColor))
                {
                    _configService.Current.AcceptedAgreement = true;
                    _configService.Save();
                }
            }
            else
            {
                SpheneUI.Controls.HeroButton("intro-agree-wait", $"Please Wait ({_timeoutLabel})",
                    color: ImGuiColors.DalamudYellow, enabled: false);
            }
        }
        else if (_configService.Current.AcceptedAgreement
                 && (string.IsNullOrEmpty(_configService.Current.CacheFolder)
                     || !_configService.Current.InitialScanComplete
                     || !Directory.Exists(_configService.Current.CacheFolder)))
        {
            // Cache setup page button
            if (!_cacheMonitor.IsScanRunning && !string.IsNullOrEmpty(_configService.Current.CacheFolder)
                && _uiShared.HasValidPenumbraModPath && Directory.Exists(_configService.Current.CacheFolder)
                && SpheneUI.Controls.HeroButton("intro-start-scan", "Start Archive Scan", color: successColor))
            {
                _cacheMonitor.InvokeScan();
            }
        }
        else if (_configService.Current.AcceptedAgreement
                 && !string.IsNullOrEmpty(_configService.Current.CacheFolder)
                 && _configService.Current.InitialScanComplete
                 && Directory.Exists(_configService.Current.CacheFolder)
                 && !_configService.Current.HasSeenSyncshellSettings)
        {
            // ShrinkU step: continue to authentication
            if (SpheneUI.Controls.HeroButton("intro-continue-auth", "Continue to Authentication", color: successColor))
            {
                _configService.Current.HasSeenSyncshellSettings = true;
                _configService.Save();
                _showShrinkUPage = false;
            }
        }
        else if (!_uiShared.ApiController.ServerAlive && _useLegacyLogin && _secretKey.Length == 64 && Base32Regex().IsMatch(_secretKey)
                 && SpheneUI.Controls.HeroButton("intro-save-key", "Save Authentication Key", color: successColor))
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
                ImGuiHelpers.ScaledDummy(5);
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
                ImGuiHelpers.ScaledDummy(5);

                SpheneUI.Text.ColoredWrapped("If the backup is password-protected, enter the password below. Otherwise leave blank.", ImGuiColors.DalamudGrey);
                SpheneUI.Text.Plain("Password (if encrypted):");
                SpheneUI.Controls.InputText("##introImportPassword", ref _importPassword, 64, ImGuiInputTextFlags.Password);
                ImGuiHelpers.ScaledDummy(10);

                SpheneUI.Layout.Separator();
                ImGuiHelpers.ScaledDummy(5);
                SpheneUI.Text.SectionHeader("Path Options", "Choose whether to restore folder paths from the backup or keep current paths.");
                ImGuiHelpers.ScaledDummy(5);

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

                ImGuiHelpers.ScaledDummy(5);

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

                ImGuiHelpers.ScaledDummy(10);

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
                ImGuiHelpers.ScaledDummy(5);
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

        _tosParagraphs = [Strings.ToS.Paragraph1, Strings.ToS.Paragraph2, Strings.ToS.Paragraph3, Strings.ToS.Paragraph4, Strings.ToS.Paragraph5, Strings.ToS.Paragraph6];
    }

    [GeneratedRegex("^[A-Z0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Base32Regex();
}
