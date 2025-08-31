using Dalamud.Bindings.ImGui;
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

    public IntroUi(ILogger<IntroUi> logger, UiSharedService uiShared, SpheneConfigService configService,
        CacheMonitor fileCacheManager, ServerConfigurationManager serverConfigurationManager, SpheneMediator mareMediator,
        PerformanceCollectorService performanceCollectorService, DalamudUtilService dalamudUtilService) : base(logger, mareMediator, "Sphene Setup", performanceCollectorService)
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
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(600, 2000),
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

        if (!_configService.Current.AcceptedAgreement && !_readFirstPage)
        {
            _uiShared.BigText("Welcome to the Sphene Network");
            ImGui.Separator();
            UiSharedService.TextWrapped("The Sphene Network allows you to synchronize your complete appearance and glamour with other connected souls across the realm. " +
                              "Through the power of Living Memory, your character's essence can be shared with trusted companions. " +
                              "Note that you will need Penumbra and Glamourer to channel this ancient technology.");
            UiSharedService.TextWrapped("Before you can access the Network's capabilities, we must establish the proper connections. Click next to begin the initialization sequence.");

            UiSharedService.ColorTextWrapped("Caution: Only modifications channeled through Penumbra can be transmitted via the Network. " +
                                 "Appearance data from other sources may cause synchronization errors or incomplete transfers. " +
                                 "To ensure perfect harmony with the Network, all modifications must flow through Penumbra's systems.", ImGuiColors.DalamudYellow);
            if (!_uiShared.DrawOtherPluginState()) return;
            ImGui.Separator();
            if (ImGui.Button("Next##toAgreement"))
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
            Vector2 textSize;
            using (_uiShared.UidFont.Push())
            {
                textSize = ImGui.CalcTextSize(Strings.ToS.LanguageLabel);
                ImGui.TextUnformatted(Strings.ToS.AgreementLabel);
            }

            ImGui.SameLine();
            var languageSize = ImGui.CalcTextSize(Strings.ToS.LanguageLabel);
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X - languageSize.X - 80);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textSize.Y / 2 - languageSize.Y / 2);

            ImGui.TextUnformatted(Strings.ToS.LanguageLabel);
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textSize.Y / 2 - (languageSize.Y + ImGui.GetStyle().FramePadding.Y) / 2);
            ImGui.SetNextItemWidth(80);
            if (ImGui.Combo("", ref _currentLanguage, _languages.Keys.ToArray(), _languages.Count))
            {
                GetToSLocalization(_currentLanguage);
            }

            ImGui.Separator();
            ImGui.SetWindowFontScale(1.5f);
            string readThis = Strings.ToS.ReadLabel;
            textSize = ImGui.CalcTextSize(readThis);
            ImGui.SetCursorPosX(ImGui.GetWindowSize().X / 2 - textSize.X / 2);
            UiSharedService.ColorText(readThis, ImGuiColors.DalamudRed);
            ImGui.SetWindowFontScale(1.0f);
            ImGui.Separator();

            UiSharedService.TextWrapped(_tosParagraphs![0]);
            UiSharedService.TextWrapped(_tosParagraphs![1]);
            UiSharedService.TextWrapped(_tosParagraphs![2]);
            UiSharedService.TextWrapped(_tosParagraphs![3]);
            UiSharedService.TextWrapped(_tosParagraphs![4]);
            UiSharedService.TextWrapped(_tosParagraphs![5]);

            ImGui.Separator();
            if (_timeoutTask?.IsCompleted ?? true)
            {
                if (ImGui.Button(Strings.ToS.AgreeLabel + "##toSetup"))
                {
                    _configService.Current.AcceptedAgreement = true;
                    _configService.Save();
                }
            }
            else
            {
                UiSharedService.TextWrapped(_timeoutLabel);
            }
        }
        else if (_configService.Current.AcceptedAgreement
                 && (string.IsNullOrEmpty(_configService.Current.CacheFolder)
                     || !_configService.Current.InitialScanComplete
                     || !Directory.Exists(_configService.Current.CacheFolder)))
        {
            using (_uiShared.UidFont.Push())
                ImGui.TextUnformatted("Memory Archive Configuration");

            ImGui.Separator();

            if (!_uiShared.HasValidPenumbraModPath)
            {
                UiSharedService.ColorTextWrapped("You do not have a valid Penumbra path set. Open Penumbra and set up a valid path for the mod directory.", ImGuiColors.DalamudRed);
            }
            else
            {
                UiSharedService.TextWrapped("To optimize data transmission efficiency, the Network must catalog your existing Penumbra modifications. " +
                           "Additionally, a local archive must be established where the Network will store synchronized character data. " +
                                     "Once the archive is configured and cataloging complete, you will be guided to Network registration.");
                UiSharedService.TextWrapped("Note: The initial cataloging process may take time depending on your modification library. Please allow the process to complete.");
                UiSharedService.ColorTextWrapped("Warning: Do not remove the FileCache.csv from your Dalamud Plugin Configurations after this step. " +
                                          "Deletion will trigger a complete re-cataloging of the Memory Archive on next startup.", ImGuiColors.DalamudYellow);
                UiSharedService.ColorTextWrapped("Warning: If cataloging stalls indefinitely, verify that your Penumbra directory structure is properly configured.", ImGuiColors.DalamudYellow);
                _uiShared.DrawCacheDirectorySetting();
            }

            if (!_cacheMonitor.IsScanRunning && !string.IsNullOrEmpty(_configService.Current.CacheFolder) && _uiShared.HasValidPenumbraModPath && Directory.Exists(_configService.Current.CacheFolder))
            {
                if (ImGui.Button("Start Scan##startScan"))
                {
                    _cacheMonitor.InvokeScan();
                }
            }
            else
            {
                _uiShared.DrawFileScanState();
            }
            if (!_dalamudUtilService.IsWine)
            {
                var useFileCompactor = _configService.Current.UseCompactor;
                if (ImGui.Checkbox("Use File Compactor", ref useFileCompactor))
                {
                    _configService.Current.UseCompactor = useFileCompactor;
                    _configService.Save();
                }
                UiSharedService.ColorTextWrapped("The File Compactor can save a tremendeous amount of space on the hard disk for downloads through Sphene. It will incur a minor CPU penalty on download but can speed up " +
                    "loading of other characters. It is recommended to keep it enabled. You can change this setting later anytime in the Sphene settings.", ImGuiColors.DalamudYellow);
            }
        }
        else if (!_uiShared.ApiController.ServerAlive)
        {
            using (_uiShared.UidFont.Push())
                ImGui.TextUnformatted("Network Authentication");
            ImGui.Separator();
            UiSharedService.TextWrapped("To access the Sphene Network, you must establish your identity within the system.");
            UiSharedService.TextWrapped("For the primary Network nodes, authentication is managed through the official Sphene Discord community. This ensures the security and integrity of the Network's core infrastructure.");
            UiSharedService.TextWrapped("To connect to the main server \"" + WebAPI.ApiController.MainServer + "\" join our Discord community and follow the authentication protocols in #sphene-service.");

            if (ImGui.Button("Join the Sphene Network Community"))
            {
                Util.OpenLink("https://discord.gg/GbnwsP2XsF");
            }

            UiSharedService.TextWrapped("For alternative Network nodes, contact the respective node administrators to obtain proper authentication credentials.");

            UiSharedService.DistanceSeparator();

            UiSharedService.TextWrapped("Once authenticated, you can establish your connection to the Network using the interface below.");

            int serverIdx = 0;
            var selectedServer = _serverConfigurationManager.GetServerByIndex(serverIdx);

            using (var node = ImRaii.TreeNode("Network Configuration", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (node)
                {
                    serverIdx = _uiShared.DrawServiceSelection(selectOnChange: true, showConnect: false);
                    if (serverIdx != _prevIdx)
                    {
                        _uiShared.ResetOAuthTasksState();
                        _prevIdx = serverIdx;
                    }

                    selectedServer = _serverConfigurationManager.GetServerByIndex(serverIdx);
                    _useLegacyLogin = !selectedServer.UseOAuth2;

                    if (ImGui.Checkbox("Use Legacy Authentication Protocol", ref _useLegacyLogin))
                    {
                        _serverConfigurationManager.GetServerByIndex(serverIdx).UseOAuth2 = !_useLegacyLogin;
                        _serverConfigurationManager.Save();
                    }
                }
            }

            if (_useLegacyLogin)
            {
                var text = "Enter Authentication Key";
                var buttonText = "Save";
                var buttonWidth = _secretKey.Length != 64 ? 0 : ImGuiHelpers.GetButtonSize(buttonText).X + ImGui.GetStyle().ItemSpacing.X;
                var textSize = ImGui.CalcTextSize(text);

                ImGuiHelpers.ScaledDummy(5);
                UiSharedService.DrawGroupedCenteredColorText("We recommend using the modern OAuth2 authentication protocol when available (supported by the main Network node). " +
                    "This method provides a streamlined connection process without requiring manual key management. " +
                    "Since you authenticate through Discord, the OAuth2 protocol offers a more secure and user-friendly experience.", ImGuiColors.DalamudYellow, 500);
                ImGuiHelpers.ScaledDummy(5);

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(text);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth() - ImGui.GetWindowContentRegionMin().X - buttonWidth - textSize.X);
                ImGui.InputText("", ref _secretKey, 64);
                if (_secretKey.Length > 0 && _secretKey.Length != 64)
                {
                    UiSharedService.ColorTextWrapped("Your authentication key must be exactly 64 characters long. Do not use your Lodestone credentials here.", ImGuiColors.DalamudRed);
                }
                else if (_secretKey.Length == 64 && !Base32Regex().IsMatch(_secretKey))
                {
                    UiSharedService.ColorTextWrapped("Your authentication key may only contain letters A-Z and numbers 2-7.", ImGuiColors.DalamudRed);
                }
                else if (_secretKey.Length == 64)
                {
                    ImGui.SameLine();
                    if (ImGui.Button(buttonText))
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
                    }
                }
            }
            else
            {
                if (string.IsNullOrEmpty(selectedServer.OAuthToken))
                {
                    UiSharedService.TextWrapped("Press the button below to verify Network OAuth2 compatibility. Then authenticate through Discord in your browser.");
                    _uiShared.DrawOAuth(selectedServer);
                }
                else
                {
                    UiSharedService.ColorTextWrapped($"Network authentication established. Connected as: Discord User {_serverConfigurationManager.GetDiscordUserFromToken(selectedServer)}", ImGuiColors.HealerGreen);
                    UiSharedService.TextWrapped("Now retrieve your Network identifiers to complete the connection process.");
                    _uiShared.DrawUpdateOAuthUIDsButton(selectedServer);
                    var playerName = _dalamudUtilService.GetPlayerName();
                    var playerWorld = _dalamudUtilService.GetHomeWorldId();
                    UiSharedService.TextWrapped($"Select the Network identifier for your character {_dalamudUtilService.GetPlayerName()}. If no identifiers appear, verify your Discord account connection. " +
                        $"If needed, use the unlink option below (hold CTRL to disconnect).");
                    _uiShared.DrawUnlinkOAuthButton(selectedServer);

                    var auth = selectedServer.Authentications.Find(a => string.Equals(a.CharacterName, playerName, StringComparison.Ordinal) && a.WorldId == playerWorld);
                    if (auth == null)
                    {
                        auth = new Authentication()
                        {
                            CharacterName = playerName,
                            WorldId = playerWorld
                        };
                        selectedServer.Authentications.Add(auth);
                        _serverConfigurationManager.Save();
                    }

                    _uiShared.DrawUIDComboForAuthentication(0, auth, selectedServer.ServerUri);

                    using (ImRaii.Disabled(string.IsNullOrEmpty(auth.UID)))
                    {
                        if (_uiShared.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Link, "Connect to Network"))
                        {
                            _ = Task.Run(() => _uiShared.ApiController.CreateConnectionsAsync());
                        }
                    }
                    if (string.IsNullOrEmpty(auth.UID))
                        UiSharedService.AttachToolTip("Select a Network identifier to establish connection");
                }
            }
        }
        else
        {
            Mediator.Publish(new SwitchToMainUiMessage());
            IsOpen = false;
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

    [GeneratedRegex("^[A-Z0-9]{64}$")]
    private static partial Regex Base32Regex();
}
