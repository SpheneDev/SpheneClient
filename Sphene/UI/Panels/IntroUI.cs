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
using ShrinkU.Configuration;
using ShrinkU.UI;

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

    // Modern UI constants - Optimized for reduced scrolling
    
    private const float CARD_PADDING = 12.0f;
    private const float BUTTON_HEIGHT = 32.0f;
    private const float HEADER_HEIGHT = 32.0f;
    private const float BUTTON_AREA_HEIGHT = 80.0f; // Increased to accommodate info boxes and multiple elements

    public IntroUi(ILogger<IntroUi> logger, UiSharedService uiShared, SpheneConfigService configService,
        CacheMonitor fileCacheManager, ServerConfigurationManager serverConfigurationManager, SpheneMediator spheneMediator,
        PerformanceCollectorService performanceCollectorService, DalamudUtilService dalamudUtilService,
        ShrinkUHostService shrinkuHostService, ShrinkUConfigService shrinkuConfig,
        ConversionUI shrinkuConversion, FirstRunSetupUI shrinkuFirstRun) : base(logger, spheneMediator, "Sphene Setup", performanceCollectorService)
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

        // Modern header styling
        DrawModernHeader();

        // Calculate available content height (window height minus header and button area)
        var windowHeight = ImGui.GetWindowSize().Y;
        var headerHeight = HEADER_HEIGHT + 10; // Header + spacing
        var contentHeight = windowHeight - headerHeight - BUTTON_AREA_HEIGHT - 20; // Extra margin

        using (var contentChild = ImRaii.Child("ContentArea", new Vector2(0, contentHeight), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (contentChild)
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
        var contentWidth = ImGui.GetContentRegionAvail().X;
        var headerHeight = HEADER_HEIGHT;
        
        // Background for header
        var drawList = ImGui.GetWindowDrawList();
        var headerStart = ImGui.GetCursorScreenPos();
        var headerEnd = new Vector2(headerStart.X + contentWidth, headerStart.Y + headerHeight);
        
        drawList.AddRectFilled(headerStart, headerEnd, ImGui.GetColorU32(ImGuiCol.FrameBg), 8.0f);
        
        // Center the title - reduced padding
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 4);
        using (_uiShared.UidFont.Push())
        {
            var titleText = "Sphene Network Setup";
            var titleSize = ImGui.CalcTextSize(titleText);
            ImGui.SetCursorPosX((contentWidth - titleSize.X) * 0.5f);
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

        _uiShared.DrawOtherPluginState();
    }

    private void DrawShrinkUPageContent()
    {
        DrawModernCard(() =>
        {
            DrawSectionHeader("ShrinkU", Dalamud.Interface.FontAwesomeIcon.Tools);
            UiSharedService.TextWrapped("Manage texture backups and convert formats to keep your Penumbra-based mods healthy. ShrinkU is bundled with Sphene and integrates with the same Window System.");

            ImGui.Spacing();
            DrawInfoBox("Backup Location", "Backups are stored under Sphene's cache in 'texture_backups'. First run will configure this automatically.", ImGuiColors.HealerGreen);

            ImGui.Spacing();
            DrawInfoBox("Integration Toggle", "You can enable or disable ShrinkU integration anytime in Settings → Appearance.", ImGuiColors.DalamudGrey3);

            ImGui.Spacing();
        });
    }

    private void DrawAgreementPageContent()
    {
        
            // Language selector in top right
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
            
            // Terms content area without scrollbars
            using (var child = ImRaii.Child("TermsContent", new Vector2(0, 300), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
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
        }, new Vector4(0.44f, 0.47f, 0.78f, 1.0f), 200f, 50f, false, 0.5f);

        ImGui.Spacing();
        
        // Network Configuration
        DrawModernCard(() =>
        {
            DrawSectionHeader("Network Configuration", Dalamud.Interface.FontAwesomeIcon.Cog);
            
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
            }, showIcon: false);
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
                }, new Vector4(0.25f, 0.70f, 0.35f, 1.0f), showIcon: false);
            }
            else
            {
                DrawModernButton($"Please Wait ({_timeoutLabel})", Dalamud.Interface.FontAwesomeIcon.Clock, () => { }, ImGuiColors.DalamudYellow, null, null, true, null, false);
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
                }, new Vector4(0.25f, 0.70f, 0.35f, 1.0f), showIcon: false);
            }
        }
        else if (_configService.Current.AcceptedAgreement 
                 && !string.IsNullOrEmpty(_configService.Current.CacheFolder)
                 && _configService.Current.InitialScanComplete
                 && Directory.Exists(_configService.Current.CacheFolder)
                 && !_configService.Current.HasSeenSyncshellSettings)
        {
            // ShrinkU step: continue to authentication
            DrawModernButton("Continue to Authentication", Dalamud.Interface.FontAwesomeIcon.ArrowRight, () =>
            {
                _configService.Current.HasSeenSyncshellSettings = true;
                _configService.Save();
                _showShrinkUPage = false;
            }, new Vector4(0.25f, 0.70f, 0.35f, 1.0f), showIcon: false);
        }
        else if (!_uiShared.ApiController.ServerAlive && _useLegacyLogin && _secretKey.Length == 64 && Base32Regex().IsMatch(_secretKey))
        {
            DrawModernButton("Save Authentication Key", Dalamud.Interface.FontAwesomeIcon.Save, () =>
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
            }, new Vector4(0.25f, 0.70f, 0.35f, 1.0f), showIcon: false);
        }
    }

    // Modern UI Helper Methods
    private static void DrawModernCard(Action content)
    {
        var drawList = ImGui.GetWindowDrawList();
        var cardStart = ImGui.GetCursorScreenPos();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        
        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(CARD_PADDING, CARD_PADDING * 0.5f));
        ImGui.Indent(CARD_PADDING);
        // Constrain text wrapping to inner width, without forcing child height
        content();
        ImGui.PopTextWrapPos();
        
        ImGui.Unindent(CARD_PADDING);
        ImGui.Dummy(new Vector2(CARD_PADDING, CARD_PADDING * 0.5f));
        ImGui.EndGroup();
        
        var cardMaxY = ImGui.GetItemRectMax().Y;
        var cardEnd = new Vector2(cardStart.X + availableWidth, cardMaxY);
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
        ImGui.Spacing();
    }

    private void DrawSubsectionHeader(string title, Dalamud.Interface.FontAwesomeIcon icon)
    {
        _uiShared.IconText(icon);
        ImGui.SameLine();
        ImGui.Text(title);
        ImGui.Spacing();
    }

    private void DrawModernButton(string text, Dalamud.Interface.FontAwesomeIcon icon, Action onClick, Vector4? color = null, float? width = null, float? height = null, bool disabled = false, float? textScale = null, bool showIcon = true)
    {
        var contentAvail = ImGui.GetContentRegionAvail();
        var targetWidth = width.HasValue ? width.Value * ImGuiHelpers.GlobalScale : contentAvail.X;
        var targetHeight = height.HasValue ? height.Value * ImGuiHelpers.GlobalScale : Math.Max(contentAvail.Y, ImGui.GetFrameHeight());
        var buttonSize = new Vector2(targetWidth, targetHeight);
        var actualColor = color ?? new Vector4(0.2f, 0.6f, 0.9f, 1.0f);

        // Draw full-height custom button with centered icon+text
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var end = new Vector2(start.X + buttonSize.X, start.Y + buttonSize.Y);

        // Use invisible button for input handling across the full rect
        if (disabled) ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton("##modern-button", buttonSize);
        if (disabled) ImGui.EndDisabled();

        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var baseCol = actualColor;
        if (disabled)
        {
            baseCol = new Vector4(baseCol.X, baseCol.Y, baseCol.Z, baseCol.W * 0.6f);
        }
        var col = active ? baseCol * 0.9f : hovered ? baseCol * 1.1f : baseCol;
        var colU32 = ImGui.ColorConvertFloat4ToU32(new Vector4(col.X, col.Y, col.Z, col.W));

        // Background
        var rounding = ImGui.GetStyle().FrameRounding;
        drawList.AddRectFilled(start, end, colU32, rounding);

        // Center icon + text
        var textCol = disabled ? new Vector4(0.25f, 0.25f, 0.25f, 0.7f) : new Vector4(0.1f, 0.1f, 0.1f, 1.0f);
        using (ImRaii.PushColor(ImGuiCol.Text, textCol))
        {
            var spacing = showIcon ? 3f * ImGuiHelpers.GlobalScale : 0f;
            Vector2 iconSize = Vector2.Zero;
            if (showIcon)
            {
                using (_uiShared.IconFont.Push())
                    iconSize = ImGui.CalcTextSize(icon.ToIconString());
            }
            Vector2 textSize;
            using (_uiShared.UidFont.Push())
            {
                if (textScale.HasValue) ImGui.SetWindowFontScale(textScale.Value);
                textSize = ImGui.CalcTextSize(text);
                if (textScale.HasValue) ImGui.SetWindowFontScale(1f);
            }
            var totalWidth = (showIcon ? iconSize.X : 0f) + spacing + textSize.X;
            var totalHeight = Math.Max(iconSize.Y, textSize.Y);
            var contentPos = new Vector2(
                start.X + (buttonSize.X - totalWidth) * 0.5f,
                start.Y + (buttonSize.Y - totalHeight) * 0.5f
            );
            if (showIcon)
            {
                using (_uiShared.IconFont.Push())
                {
                    drawList.AddText(contentPos, ImGui.GetColorU32(ImGuiCol.Text), icon.ToIconString());
                }
            }
            var textPos = new Vector2(contentPos.X + (showIcon ? iconSize.X : 0f) + spacing, contentPos.Y);
            using (_uiShared.UidFont.Push())
            {
                if (textScale.HasValue) ImGui.SetWindowFontScale(textScale.Value);
                drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), text);
                if (textScale.HasValue) ImGui.SetWindowFontScale(1f);
            }
        }

        if (clicked && !disabled)
        {
            onClick();
        }
    }

    private void DrawInfoBox(string title, string content, Vector4 color)
    {
        var drawList = ImGui.GetWindowDrawList();
        var boxStart = ImGui.GetCursorScreenPos();
        var containerWidth = ImGui.GetContentRegionAvail().X;
        var style = ImGui.GetStyle();
        var rightPadding = style.ItemSpacing.X + 8f; // ensure right inner spacing
        
        // Reserve consistent height for info boxes
        var boxHeight = BUTTON_HEIGHT + 12; // Button height + padding
        
        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(6, 6));
        ImGui.Indent(10);
        var innerWidth = Math.Max(0f, ImGui.GetContentRegionAvail().X - rightPadding);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + innerWidth);
        
        // Determine icon based on potential prefix in title and render using FontAwesome
        var displayTitle = title ?? string.Empty;
        var icon = Dalamud.Interface.FontAwesomeIcon.InfoCircle;
        if (displayTitle.StartsWith("⚠ ", StringComparison.Ordinal))
        {
            icon = Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle;
            displayTitle = displayTitle.Substring(2).TrimStart();
        }
        else if (displayTitle.StartsWith("❌ ", StringComparison.Ordinal))
        {
            icon = Dalamud.Interface.FontAwesomeIcon.ExclamationCircle;
            displayTitle = displayTitle.Substring(2).TrimStart();
        }
        else if (displayTitle.StartsWith("ℹ ", StringComparison.Ordinal))
        {
            icon = Dalamud.Interface.FontAwesomeIcon.InfoCircle;
            displayTitle = displayTitle.Substring(2).TrimStart();
        }

        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            _uiShared.IconText(icon);
            ImGui.SameLine();
            ImGui.TextUnformatted(displayTitle);
        }
        UiSharedService.TextWrapped(content, ImGui.GetCursorPosX() + innerWidth);
        
        ImGui.Unindent(10);
        ImGui.Dummy(new Vector2(6, 6));
        ImGui.EndGroup();
        
        var boxEndRect = ImGui.GetItemRectMax();
        var boxEnd = new Vector2(boxStart.X + Math.Max(0f, containerWidth - rightPadding), boxEndRect.Y);
        
        // Ensure minimum height for consistency
        var actualHeight = boxEndRect.Y - boxStart.Y;
        if (actualHeight < boxHeight)
        {
            ImGui.Dummy(new Vector2(0, boxHeight - actualHeight));
            var newBoxEndRect = ImGui.GetItemRectMax();
            boxEnd = new Vector2(boxStart.X + Math.Max(0f, containerWidth - rightPadding), newBoxEndRect.Y);
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

    [GeneratedRegex("^[A-Z0-9]{64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Base32Regex();
}
