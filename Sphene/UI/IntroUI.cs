using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Sphene.FileCache;
using Sphene.Localization;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Sphene.UI;

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

    // Modern UI constants - Optimized for reduced scrolling
    private const float SECTION_SPACING = 12.0f;
    private const float CARD_PADDING = 12.0f;
    private const float BUTTON_HEIGHT = 32.0f;
    private const float HEADER_HEIGHT = 32.0f;
    private const float BUTTON_AREA_HEIGHT = 80.0f; // Increased to accommodate info boxes and multiple elements

    public IntroUi(ILogger<IntroUi> logger, UiSharedService uiShared, SpheneConfigService configService,
        CacheMonitor fileCacheManager, ServerConfigurationManager serverConfigurationManager, SpheneMediator spheneMediator,
        PerformanceCollectorService performanceCollectorService, DalamudUtilService dalamudUtilService) : base(logger, spheneMediator, "Sphene Setup", performanceCollectorService)
    {
        _uiShared = uiShared;
        _configService = configService;
        _cacheMonitor = fileCacheManager;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;
        IsOpen = false;
        ShowCloseButton = false;
        RespectCloseHotkey = false;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(700, 500),
            MaximumSize = new Vector2(700, 2000),
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

        // Modern header styling
        DrawModernHeader();

        // Calculate available content height (window height minus header and button area)
        var windowHeight = ImGui.GetWindowSize().Y;
        var headerHeight = HEADER_HEIGHT + 10; // Header + spacing
        var contentHeight = windowHeight - headerHeight - BUTTON_AREA_HEIGHT - 20; // Extra margin

        // Content area with scrolling
        using (var contentChild = ImRaii.Child("ContentArea", new Vector2(0, contentHeight), false))
        {
            if (contentChild)
            {
                if (!_configService.Current.AcceptedAgreement && !_readFirstPage)
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
                    DrawSyncshellSettingsPageContent();
                }
                else if (!_uiShared.ApiController.ServerAlive)
                {
                    DrawNetworkAuthenticationPageContent();
                }
                else
                {
                    Mediator.Publish(new SwitchToMainUiMessage());
                    IsOpen = false;
                    return;
                }
            }
        }

        // Sticky button area at bottom
        ImGui.Separator();
        ImGui.Spacing();
        DrawStickyButtons();
    }

    private void DrawModernHeader()
    {
        var windowWidth = ImGui.GetWindowSize().X;
        var headerHeight = HEADER_HEIGHT;
        
        // Background for header
        var drawList = ImGui.GetWindowDrawList();
        var headerStart = ImGui.GetCursorScreenPos();
        var headerEnd = new Vector2(headerStart.X + windowWidth, headerStart.Y + headerHeight);
        
        drawList.AddRectFilled(headerStart, headerEnd, ImGui.GetColorU32(ImGuiCol.FrameBg), 8.0f);
        
        // Center the title - reduced padding
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4);
        using (_uiShared.UidFont.Push())
        {
            var titleText = "Sphene Network Setup";
            var titleSize = ImGui.CalcTextSize(titleText);
            ImGui.SetCursorPosX((windowWidth - titleSize.X) * 0.5f);
            UiSharedService.ColorText(titleText, ImGuiColors.TankBlue);
        }
        
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4);
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawWelcomePageContent()
    {
        DrawModernCard(() =>
        {
            DrawSectionHeader("Welcome to Sphene Network", Dalamud.Interface.FontAwesomeIcon.Star);
            
            UiSharedService.TextWrapped("Experience seamless character synchronization across the realm through the power of Living Memory. " +
                          "Share your complete appearance and glamour with trusted companions in real-time.");
            
            ImGui.Spacing();
            DrawInfoBox("Prerequisites", "You'll need Penumbra and Glamourer installed to access the Network's full capabilities.", ImGuiColors.HealerGreen);
            
            ImGui.Spacing();
            DrawWarningBox("Important", "Only modifications channeled through Penumbra can be transmitted. " +
                                 "Ensure all your customizations flow through Penumbra's systems for perfect synchronization.", ImGuiColors.DalamudYellow);
        });

        if (!_uiShared.DrawOtherPluginState()) return;
    }

    private void DrawAgreementPageContent()
    {
        DrawModernCard(() =>
        {
            // Language selector in top right
            var languageSize = ImGui.CalcTextSize(Strings.ToS.LanguageLabel);
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - 120);
            ImGui.TextUnformatted(Strings.ToS.LanguageLabel);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            if (ImGui.Combo("##language", ref _currentLanguage, _languages.Keys.ToArray(), _languages.Count))
            {
                GetToSLocalization(_currentLanguage);
            }
            
            ImGui.SetCursorPosX(0);
            DrawSectionHeader(Strings.ToS.AgreementLabel); // Removed icon parameter
            
            // Prominent warning
            ImGui.Spacing();
            var warningText = Strings.ToS.ReadLabel;
            var warningSize = ImGui.CalcTextSize(warningText);
            ImGui.SetCursorPosX((ImGui.GetWindowSize().X - warningSize.X) * 0.5f);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
            {
                ImGui.SetWindowFontScale(1.3f);
                ImGui.TextUnformatted(warningText);
                ImGui.SetWindowFontScale(1.0f);
            }
            
            ImGui.Separator();
            ImGui.Spacing();
            
            // Terms content in scrollable area
            using (var child = ImRaii.Child("TermsContent", new Vector2(0, 300), true))
            {
                if (child)
                {
                    foreach (var paragraph in _tosParagraphs!)
                    {
                        UiSharedService.TextWrapped(paragraph);
                        ImGui.Spacing();
                    }
                }
            }
        });
    }

    private void DrawCacheSetupPageContent()
    {
        DrawModernCard(() =>
        {
            DrawSectionHeader("Memory Archive Configuration", Dalamud.Interface.FontAwesomeIcon.Database);
            
            ImGui.Spacing();
            if (!_uiShared.HasValidPenumbraModPath)
            {
                DrawErrorBox("Penumbra Path Required", "Please configure a valid Penumbra mod directory path before proceeding.", ImGuiColors.DalamudRed);
            }
            else
            {
                UiSharedService.TextWrapped("The Network requires a local archive to store synchronized character data and must catalog your existing modifications for optimal transmission efficiency.");
                
                ImGui.Spacing();
                DrawWarningBox("Important Notes", 
                    "• Initial cataloging may take time depending on your modification library\n" +
                    "• Do not remove FileCache.csv from your Dalamud Plugin Configurations\n" +
                    "• Ensure your Penumbra directory structure is properly configured", ImGuiColors.DalamudYellow);
                
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
            DrawModernCard(() =>
            {
                var useFileCompactor = _configService.Current.UseCompactor;
                if (ImGui.Checkbox("Enable File Compactor", ref useFileCompactor))
                {
                    _configService.Current.UseCompactor = useFileCompactor;
                    _configService.Save();
                }
                UiSharedService.AttachToolTip("Saves significant disk space for downloads with minor CPU overhead. Recommended to keep enabled.");
                
                ImGui.Spacing();
                UiSharedService.ColorTextWrapped("The File Compactor reduces storage requirements for downloaded content while providing faster loading times. " +
                    "This setting can be changed later in Sphene settings.", ImGuiColors.DalamudGrey3);
            });
        }
    }

    private void DrawSyncshellSettingsPageContent()
    {
        DrawModernCard(() =>
        {
            DrawSectionHeader("Notification Preferences", Dalamud.Interface.FontAwesomeIcon.Bell);
            
            ImGui.Spacing();
            UiSharedService.TextWrapped("Configure how you want to be notified about syncshell activities before connecting to the Network.");
        });

        ImGui.Spacing();
        
        // Area Bound Syncshells
        DrawModernCard(() =>
        {
            DrawSubsectionHeader("Area-Bound Syncshells", Dalamud.Interface.FontAwesomeIcon.MapMarkerAlt);
            
            var showAreaBoundNotifications = _configService.Current.ShowAreaBoundSyncshellNotifications;
            if (ImGui.Checkbox("Show area-bound syncshell notifications", ref showAreaBoundNotifications))
            {
                _configService.Current.ShowAreaBoundSyncshellNotifications = showAreaBoundNotifications;
                _configService.Save();
            }
            UiSharedService.AttachToolTip("Receive notifications when area-bound syncshells become available in your current location");
            
            if (_configService.Current.ShowAreaBoundSyncshellNotifications)
            {
                ImGui.Indent();
                
                var notificationLocation = _configService.Current.AreaBoundSyncshellNotification;
                var notificationOptions = new[] { "Nowhere", "Chat", "Toast", "Both" };
                var currentIndex = (int)notificationLocation;
                
                if (ImGui.Combo("Notification Location", ref currentIndex, notificationOptions, notificationOptions.Length))
                {
                    _configService.Current.AreaBoundSyncshellNotification = (NotificationLocation)currentIndex;
                    _configService.Save();
                }
                UiSharedService.AttachToolTip("Choose where area-bound syncshell notifications should appear");
                
                ImGui.Unindent();
            }
            
            var showAreaBoundWelcome = _configService.Current.ShowAreaBoundSyncshellWelcomeMessages;
            if (ImGui.Checkbox("Show welcome messages", ref showAreaBoundWelcome))
            {
                _configService.Current.ShowAreaBoundSyncshellWelcomeMessages = showAreaBoundWelcome;
                _configService.Save();
            }
            UiSharedService.AttachToolTip("Display welcome messages when joining area-bound syncshells");
            
            var autoShowConsent = _configService.Current.AutoShowAreaBoundSyncshellConsent;
            if (ImGui.Checkbox("Automatically show consent dialogs", ref autoShowConsent))
            {
                _configService.Current.AutoShowAreaBoundSyncshellConsent = autoShowConsent;
                _configService.Save();
            }
            UiSharedService.AttachToolTip("When enabled, consent dialogs for area-bound syncshells will appear automatically when entering areas. When disabled, you can manually trigger consent using the button in the Compact UI. This setting also controls city syncshell join requests.");
        });

        ImGui.Spacing();
        
    }

    private void DrawNetworkAuthenticationPageContent()
    {
        DrawModernCard(() =>
        {
            DrawSectionHeader("Network Authentication", Dalamud.Interface.FontAwesomeIcon.Shield);
            
            ImGui.Spacing();
            UiSharedService.TextWrapped("Establish your identity within the Sphene Network to access synchronization services.");
            
            ImGui.Spacing();
            DrawInfoBox("Main Server Authentication", 
                $"For the primary Network node \"{WebAPI.ApiController.MainServer}\", authentication is managed through our Discord community. " +
                "Join the Discord and follow the authentication protocols in #sphene-registration.", ImGuiColors.HealerGreen);
        });

        ImGui.Spacing();
        DrawModernButton("Join Discord Community", Dalamud.Interface.FontAwesomeIcon.Users, () =>
        {
            Util.OpenLink("https://discord.gg/GbnwsP2XsF");
        }, new Vector4(0.44f, 0.47f, 0.78f, 1.0f));

        ImGui.Spacing();
        
        // Network Configuration
        DrawModernCard(() =>
        {
            DrawSectionHeader("Network Configuration", Dalamud.Interface.FontAwesomeIcon.Cog);
            
            int serverIdx = 0;
            var selectedServer = _serverConfigurationManager.GetServerByIndex(serverIdx);

            serverIdx = _uiShared.DrawServiceSelection(selectOnChange: true, showConnect: false);
            if (serverIdx != _prevIdx)
            {
                _uiShared.ResetOAuthTasksState();
                _prevIdx = serverIdx;
            }

            selectedServer = _serverConfigurationManager.GetServerByIndex(serverIdx);
            
            // Force legacy authentication mode by default
            _useLegacyLogin = true;
            selectedServer.UseOAuth2 = false;
            _serverConfigurationManager.Save();
        });

        ImGui.Spacing();
        
        // Authentication Key Input
        if (_useLegacyLogin)
        {
            DrawModernCard(() =>
            {
                DrawSubsectionHeader("Authentication Key", Dalamud.Interface.FontAwesomeIcon.Key);
                
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##secretKey", "Enter your 64-character authentication key...", ref _secretKey, 64);
                
                if (_secretKey.Length > 0 && _secretKey.Length != 64)
                {
                    ImGui.Spacing();
                    DrawErrorBox("Invalid Length", "Authentication key must be exactly 64 characters long. Do not use Lodestone credentials.", ImGuiColors.DalamudRed);
                }
                else if (_secretKey.Length == 64 && !Base32Regex().IsMatch(_secretKey))
                {
                    ImGui.Spacing();
                    DrawErrorBox("Invalid Format", "Authentication key may only contain letters A-Z and numbers 2-7.", ImGuiColors.DalamudRed);
                }
            });
        }
        else
        {
            // OAuth2 flow (kept for compatibility but not used by default)
            DrawModernCard(() =>
            {
                var selectedServer = _serverConfigurationManager.GetServerByIndex(0);
                if (string.IsNullOrEmpty(selectedServer.OAuthToken))
                {
                    UiSharedService.TextWrapped("Press the button below to verify Network OAuth2 compatibility and authenticate through Discord.");
                    _uiShared.DrawOAuth(selectedServer);
                }
                else
                {
                    UiSharedService.ColorTextWrapped($"Network authentication established. Connected as: Discord User {_serverConfigurationManager.GetDiscordUserFromToken(selectedServer)}", ImGuiColors.HealerGreen);
                    UiSharedService.TextWrapped("Retrieve your Network identifiers to complete the connection process.");
                    _uiShared.DrawUpdateOAuthUIDsButton(selectedServer);
                    
                    var playerName = _dalamudUtilService.GetPlayerName();
                    var playerWorld = _dalamudUtilService.GetHomeWorldId();
                    if (!string.IsNullOrEmpty(playerName) && playerWorld != 0)
                    {
                        var worldName = _dalamudUtilService.WorldData.Value.TryGetValue((ushort)playerWorld, out var world) ? world : "Unknown";
                        UiSharedService.TextWrapped($"Current Character: {playerName}@{worldName}");
                        
                        if (_uiShared.IconTextButton(FontAwesomeIcon.UserPlus, "Add Current Character"))
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
        if (!_configService.Current.AcceptedAgreement && !_readFirstPage)
        {
            // Welcome page button
            DrawModernButton("Begin Setup", Dalamud.Interface.FontAwesomeIcon.ArrowRight, () =>
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
            });
        }
        else if (!_configService.Current.AcceptedAgreement && _readFirstPage)
        {
            // Agreement page button
            if (_timeoutTask?.IsCompleted ?? true)
            {
                DrawModernButton(Strings.ToS.AgreeLabel, Dalamud.Interface.FontAwesomeIcon.Check, () =>
                {
                    _configService.Current.AcceptedAgreement = true;
                    _configService.Save();
                }, ImGuiColors.HealerGreen);
            }
            else
            {
                DrawInfoBox("Please Wait", _timeoutLabel, ImGuiColors.DalamudYellow);
            }
        }
        else if (_configService.Current.AcceptedAgreement
                 && (string.IsNullOrEmpty(_configService.Current.CacheFolder)
                     || !_configService.Current.InitialScanComplete
                     || !Directory.Exists(_configService.Current.CacheFolder)))
        {
            // Cache setup page button
            if (!_cacheMonitor.IsScanRunning && !string.IsNullOrEmpty(_configService.Current.CacheFolder) && _uiShared.HasValidPenumbraModPath && Directory.Exists(_configService.Current.CacheFolder))
            {
                DrawModernButton("Start Archive Scan", Dalamud.Interface.FontAwesomeIcon.Play, () =>
                {
                    _cacheMonitor.InvokeScan();
                }, ImGuiColors.HealerGreen);
            }
        }
        else if (_configService.Current.AcceptedAgreement 
                 && !string.IsNullOrEmpty(_configService.Current.CacheFolder)
                 && _configService.Current.InitialScanComplete
                 && Directory.Exists(_configService.Current.CacheFolder)
                 && !_configService.Current.HasSeenSyncshellSettings)
        {
            // Syncshell settings page button
            DrawModernButton("Continue to Authentication", Dalamud.Interface.FontAwesomeIcon.ArrowRight, () =>
            {
                _configService.Current.HasSeenSyncshellSettings = true;
                _configService.Save();
            });
        }
        else if (!_uiShared.ApiController.ServerAlive)
        {
            // Network authentication page button
            if (_useLegacyLogin && _secretKey.Length == 64 && Base32Regex().IsMatch(_secretKey))
            {
                DrawModernButton("Save Authentication Key", Dalamud.Interface.FontAwesomeIcon.Save, () =>
                {
                    if (_serverConfigurationManager.CurrentServer == null) _serverConfigurationManager.SelectServer(0);
                    if (!_serverConfigurationManager.CurrentServer!.SecretKeys.Any())
                    {
                        _serverConfigurationManager.CurrentServer!.SecretKeys.Add(_serverConfigurationManager.CurrentServer.SecretKeys.Select(k => k.Key).LastOrDefault() + 1, new SecretKey()
                        {
                            FriendlyName = $"Authentication Key added on Setup ({DateTime.Now:yyyy-MM-dd})",
                            Key = _secretKey,
                        });
                        _serverConfigurationManager.AddCurrentCharacterToServer();
                    }
                    else
                    {
                        _serverConfigurationManager.CurrentServer!.SecretKeys[0] = new SecretKey()
                        {
                            FriendlyName = $"Authentication Key added on Setup ({DateTime.Now:yyyy-MM-dd})",
                            Key = _secretKey,
                        };
                    }
                    _secretKey = string.Empty;
                    _ = Task.Run(() => _uiShared.ApiController.CreateConnectionsAsync());
                }, ImGuiColors.HealerGreen);
            }
        }
    }

    // Modern UI Helper Methods
    private void DrawModernCard(Action content)
    {
        var drawList = ImGui.GetWindowDrawList();
        var cardStart = ImGui.GetCursorScreenPos();
        
        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(CARD_PADDING, CARD_PADDING * 0.5f));
        ImGui.Indent(CARD_PADDING);
        
        content();
        
        ImGui.Unindent(CARD_PADDING);
        ImGui.Dummy(new Vector2(CARD_PADDING, CARD_PADDING * 0.5f));
        ImGui.EndGroup();
        
        var cardEnd = ImGui.GetItemRectMax();
        drawList.AddRectFilled(cardStart, cardEnd, ImGui.GetColorU32(ImGuiCol.ChildBg), 8.0f);
        drawList.AddRect(cardStart, cardEnd, ImGui.GetColorU32(ImGuiCol.Border), 8.0f, ImDrawFlags.None, 1.0f);
    }

    private void DrawSectionHeader(string title, Dalamud.Interface.FontAwesomeIcon? icon = null)
    {
        if (icon.HasValue)
        {
            _uiShared.IconText(icon.Value);
            ImGui.SameLine();
        }
        ImGui.Text(title);
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawSubsectionHeader(string title, Dalamud.Interface.FontAwesomeIcon icon)
    {
        _uiShared.IconText(icon);
        ImGui.SameLine();
        ImGui.Text(title);
        ImGui.Spacing();
    }

    private void DrawModernButton(string text, Dalamud.Interface.FontAwesomeIcon icon, Action onClick, Vector4? color = null)
    {
        var buttonWidth = ImGui.GetContentRegionAvail().X;
        var actualColor = color ?? new Vector4(0.2f, 0.6f, 0.9f, 1.0f);
        
        using (ImRaii.PushColor(ImGuiCol.Button, actualColor))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, actualColor * 1.1f))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, actualColor * 0.9f))
        {
            if (_uiShared.IconTextButton(icon, text, buttonWidth))
            {
                onClick();
            }
        }
    }

    private void DrawInfoBox(string title, string content, Vector4 color)
    {
        var drawList = ImGui.GetWindowDrawList();
        var boxStart = ImGui.GetCursorScreenPos();
        
        // Reserve consistent height for info boxes
        var boxHeight = BUTTON_HEIGHT + 12; // Button height + padding
        
        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(6, 6));
        ImGui.Indent(10);
        
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            ImGui.TextUnformatted($"ℹ {title}");
        }
        UiSharedService.TextWrapped(content);
        
        ImGui.Unindent(10);
        ImGui.Dummy(new Vector2(6, 6));
        ImGui.EndGroup();
        
        var boxEnd = ImGui.GetItemRectMax();
        
        // Ensure minimum height for consistency
        var actualHeight = boxEnd.Y - boxStart.Y;
        if (actualHeight < boxHeight)
        {
            ImGui.Dummy(new Vector2(0, boxHeight - actualHeight));
            boxEnd = ImGui.GetItemRectMax();
        }
        
        var bgColor = new Vector4(color.X, color.Y, color.Z, 0.1f);
        drawList.AddRectFilled(boxStart, boxEnd, ImGui.ColorConvertFloat4ToU32(bgColor), 4.0f);
        drawList.AddRect(boxStart, boxEnd, ImGui.ColorConvertFloat4ToU32(color), 4.0f, ImDrawFlags.None, 1.0f);
    }

    private void DrawWarningBox(string title, string content, Vector4 color)
    {
        DrawInfoBox($"⚠ {title}", content, color);
    }

    private void DrawErrorBox(string title, string content, Vector4 color)
    {
        DrawInfoBox($"❌ {title}", content, color);
    }

    private void GetToSLocalization(int changeLanguageTo = -1)
    {
        if (changeLanguageTo != -1)
        {
            _uiShared.LoadLocalization(_languages.ElementAt(changeLanguageTo).Value);
        }

        _tosParagraphs = [Strings.ToS.Paragraph1, Strings.ToS.Paragraph2, Strings.ToS.Paragraph3, Strings.ToS.Paragraph4, Strings.ToS.Paragraph5, Strings.ToS.Paragraph6];
    }

    [GeneratedRegex("^[A-Z0-9]{64}$")]
    private static partial Regex Base32Regex();
}
