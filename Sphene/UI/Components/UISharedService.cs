using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Sphene.API.Data;
using Sphene.FileCache;
using Sphene.Interop.Ipc;
using Sphene.Localization;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI.Design;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.SignalR;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Collections;
using System.IO;

namespace Sphene.UI;

public partial class UiSharedService : DisposableMediatorSubscriberBase
{
    public readonly FileDialogManager FileDialogManager;
    private const string _notesEnd = "##SPHENE_USER_NOTES_END##";
    private const string _notesStart = "##SPHENE_USER_NOTES_START##";
    private readonly ApiController _apiController;
    private readonly CacheMonitor _cacheMonitor;
    private readonly SpheneConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly IpcManager _ipcManager;
    private readonly Dalamud.Localization _localization;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private static readonly ConcurrentDictionary<string, Vector2> _pendingWindowSizes = new(StringComparer.Ordinal);
    private readonly TokenProvider _tokenProvider;
    private bool _brioExists = false;
    private bool _bypassEmoteExists = false;
    private bool _cacheDirectoryHasOtherFilesThanCache = false;
    private bool _cacheDirectoryIsValidPath = true;
    private bool _customizePlusExists = false;
    private Task<Uri?>? _discordOAuthCheck;
    private Task<string?>? _discordOAuthGetCode;
    private CancellationTokenSource _discordOAuthGetCts = new();
    private Task<Dictionary<string, string>>? _discordOAuthUIDs;
    private bool _glamourerExists = false;
    private bool _heelsExists = false;
    private bool _honorificExists = false;
    private bool _isDirectoryWritable = false;
    private bool _isOneDrive = false;
    private bool _isPenumbraDirectory = false;
    private bool _downloadFolderIsValidPath = true;
    private bool _isDownloadFolderWritable = false;
    private bool _isDownloadFolderOneDrive = false;
    private bool _isDownloadFolderPenumbra = false;
    private bool _moodlesExists = false;
    private Dictionary<string, DateTime> _oauthTokenExpiry = new(StringComparer.Ordinal);
    private bool _penumbraExists = false;
    private bool _petNamesExists = false;
    private int _serverSelectionIndex = -1;

    public UiSharedService(ILogger<UiSharedService> logger, IpcManager ipcManager, ApiController apiController,
        CacheMonitor cacheMonitor, FileDialogManager fileDialogManager,
        SpheneConfigService configService, DalamudUtilService dalamudUtil, IDalamudPluginInterface pluginInterface,
        ITextureProvider textureProvider,
        Dalamud.Localization localization,
        ServerConfigurationManager serverManager, TokenProvider tokenProvider, SpheneMediator mediator) : base(logger, mediator)
    {
        _ipcManager = ipcManager;
        _apiController = apiController;
        _cacheMonitor = cacheMonitor;
        FileDialogManager = fileDialogManager;
        _configService = configService;
        _dalamudUtil = dalamudUtil;
        _pluginInterface = pluginInterface;
        _localization = localization;
        _serverConfigurationManager = serverManager;
        _tokenProvider = tokenProvider;
        _localization.SetupWithLangCode("en");

        _isDirectoryWritable = IsDirectoryWritable(_configService.Current.CacheFolder);

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) =>
        {
            _penumbraExists = _ipcManager.Penumbra.APIAvailable;
            _glamourerExists = _ipcManager.Glamourer.APIAvailable;
            _customizePlusExists = _ipcManager.CustomizePlus.APIAvailable;
            _heelsExists = _ipcManager.Heels.APIAvailable;
            _honorificExists = _ipcManager.Honorific.APIAvailable;
            _moodlesExists = _ipcManager.Moodles.APIAvailable;
            _petNamesExists = _ipcManager.PetNames.APIAvailable;
            _brioExists = _ipcManager.Brio.APIAvailable;
            _bypassEmoteExists = _ipcManager.BypassEmote.APIAvailable;
        });

        UidFont = _pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        {
            e.OnPreBuild(tk =>
            {
                var fontPath = Path.Combine(_pluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty, "Resources", "Fonts", "Lato-Regular.ttf");
                if (File.Exists(fontPath))
                {
                     tk.AddFontFromFile(fontPath, new SafeFontConfig
                     {
                         SizePx = 35
                     });
                }
                else
                {
                    tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansCjkMedium, new()
                    {
                        SizePx = 35
                    });
                }
            });
        });
        DisplayFont = _pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        {
            e.OnPreBuild(tk =>
            {
                var fontPath = Path.Combine(_pluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty, "Resources", "Fonts", "Lato-Regular.ttf");
                if (File.Exists(fontPath))
                {
                     tk.AddFontFromFile(fontPath, new SafeFontConfig
                     {
                         SizePx = 26
                     });
                }
                else
                {
                    tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansCjkMedium, new()
                    {
                        SizePx = 26
                    });
                }
            });
        });
        GameFont = _pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new(GameFontFamilyAndSize.Axis12));
        IconFont = _pluginInterface.UiBuilder.IconFontFixedWidthHandle;

        SpheneUI.Initialize(logger, Mediator, IconFont, UidFont, DisplayFont, GameFont);
        SpheneUI.Media.SetTextureProvider(textureProvider);
    }

    public static string DoubleNewLine => Environment.NewLine + Environment.NewLine;
    public ApiController ApiController => _apiController;

    public bool EditTrackerPosition { get; set; }

    public IFontHandle GameFont { get; init; }
    public bool HasValidPenumbraModPath => !(_ipcManager.Penumbra.ModDirectory ?? string.Empty).IsNullOrEmpty() && Directory.Exists(_ipcManager.Penumbra.ModDirectory);

    public IFontHandle DisplayFont { get; init; }

    public IFontHandle IconFont { get; init; }
    public bool IsInGpose => _dalamudUtil.IsInGpose;

    public Dictionary<uint, string> JobData => _dalamudUtil.JobData.Value;
    public string PlayerName => _dalamudUtil.GetPlayerName();

    public IFontHandle UidFont { get; init; }
    public Dictionary<ushort, string> WorldData => _dalamudUtil.WorldData.Value;
    public uint WorldId => _dalamudUtil.GetHomeWorldId();

    public string GetPreferredUserDisplayName(string? uid, string? aliasOrUid)
    {
        return _serverConfigurationManager.GetPreferredUserDisplayName(uid, aliasOrUid);
    }

    public string GetPreferredUserDisplayName(UserData user)
    {
        return _serverConfigurationManager.GetPreferredUserDisplayName(user);
    }


    public static string ByteToString(long bytes, bool addSuffix = true)
    {
        string[] suffix = ["B", "KiB", "MiB", "GiB", "TiB"];
        int i;
        double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }

        return addSuffix ? $"{dblSByte:0.00} {suffix[i]}" : $"{dblSByte:0.00}";
    }









    public static bool CtrlPressed() => (GetKeyState(0xA2) & 0x8000) != 0 || (GetKeyState(0xA3) & 0x8000) != 0;







    public static string GetNotes(List<Pair> pairs)
    {
        StringBuilder sb = new();
        sb.AppendLine(_notesStart);
        foreach (var entry in pairs)
        {
            var note = entry.GetNote();
            if (note.IsNullOrEmpty()) continue;

            sb.Append(entry.UserData.UID).Append(":\"").Append(entry.GetNote()).AppendLine("\"");
        }
        sb.AppendLine(_notesEnd);

        return sb.ToString();
    }


    public static float GetBaseFolderWidth(float widthOffset = 0f)
    {
        return SpheneUI.Layout.WindowContentWidth() - 32f + widthOffset;
    }

    public static float GetSyncshellFolderWidth(float widthOffset = 0f)
    {
        return SpheneUI.Layout.WindowContentWidth() - 22f + widthOffset;
    }

    public static bool IsDirectoryWritable(string dirPath, bool throwIfFails = false)
    {
        try
        {
            using FileStream fs = File.Create(
                       Path.Combine(
                           dirPath,
                           Path.GetRandomFileName()
                       ),
                       1,
                       FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            if (throwIfFails)
                throw;

            return false;
        }
    }





    public static bool ShiftPressed() => (GetKeyState(0xA1) & 0x8000) != 0 || (GetKeyState(0xA0) & 0x8000) != 0;



    public bool ApplyNotesFromClipboard(string notes, bool overwrite)
    {
        var splitNotes = notes.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
        var splitNotesStart = splitNotes.FirstOrDefault();
        var splitNotesEnd = splitNotes.LastOrDefault();
        if (!string.Equals(splitNotesStart, _notesStart, StringComparison.Ordinal) || !string.Equals(splitNotesEnd, _notesEnd, StringComparison.Ordinal))
        {
            return false;
        }

        splitNotes.RemoveAll(n => string.Equals(n, _notesStart, StringComparison.Ordinal) || string.Equals(n, _notesEnd, StringComparison.Ordinal));

        foreach (var note in splitNotes)
        {
            try
            {
                var splittedEntry = note.Split(":", 2, StringSplitOptions.RemoveEmptyEntries);
                var uid = splittedEntry[0];
                var comment = splittedEntry[1].Trim('"');
                if (_serverConfigurationManager.GetNoteForUid(uid) != null && !overwrite) continue;
                _serverConfigurationManager.SetNoteForUid(uid, comment);
            }
            catch
            {
                Logger.LogWarning("Could not parse {note}", note);
            }
        }

        _serverConfigurationManager.SaveNotes();

        return true;
    }



    public void DrawCacheDirectorySetting()
    {
        SpheneUI.Text.ColoredWrapped("Note: The storage folder should be somewhere close to root (i.e. C:\\SpheneStorage) in a new empty folder. DO NOT point this to your game folder. DO NOT point this to your Penumbra folder.", ImGuiColors.DalamudYellow);
        var cacheDirectory = _configService.Current.CacheFolder;
        var monitoringActive = _cacheMonitor.SpheneWatcher != null;
        var (_, cacheBrowsePressed) = SpheneUI.Controls.InputTextWithButtons("Storage Folder##cache",
            ref cacheDirectory, 255, ImGuiInputTextFlags.ReadOnly,
            buttons: new SpheneUI.Controls.InputButtonSpec(FontAwesomeIcon.Folder,
                monitoringActive
                    ? "Stop the Monitoring before changing the Storage folder. As long as monitoring is active, you cannot change the Storage folder location."
                    : "Pick the Sphene storage folder.",
                Enabled: !monitoringActive));
        if (cacheBrowsePressed == 0)
        {
            FileDialogManager.OpenFolderDialog("Pick Sphene Storage Folder", (success, path) =>
            {
                if (!success) return;

                _isOneDrive = path.Contains("onedrive", StringComparison.OrdinalIgnoreCase);
                _isPenumbraDirectory = string.Equals(path.ToLowerInvariant(), _ipcManager.Penumbra.ModDirectory?.ToLowerInvariant(), StringComparison.Ordinal);
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                _cacheDirectoryHasOtherFilesThanCache = false;
                var backupsRoot = Path.Combine(path, "texture_backups");
                foreach (var file in files)
                {
                    if (!string.IsNullOrEmpty(backupsRoot) && file.StartsWith(backupsRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogDebug("Ignoring backup file under texture_backups: {file}", file);
                        continue;
                    }
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.Length != 40 && !string.Equals(fileName, "desktop", StringComparison.OrdinalIgnoreCase))
                    {
                        _cacheDirectoryHasOtherFilesThanCache = true;
                        Logger.LogWarning("Found illegal file in {path}: {file}", path, file);
                        break;
                    }
                }
                var dirs = Directory.GetDirectories(path);
                var invalidDirs = dirs.Where(dir => !Path.GetFileName(dir).Equals("texture_backups", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (invalidDirs.Any())
                {
                    _cacheDirectoryHasOtherFilesThanCache = true;
                    Logger.LogWarning("Found folders in {path} not belonging to Sphene: {dirs}", path, string.Join(", ", invalidDirs));
                }

                _isDirectoryWritable = IsDirectoryWritable(path);
                _cacheDirectoryIsValidPath = PathRegex().IsMatch(path);

                if (!string.IsNullOrEmpty(path)
                    && Directory.Exists(path)
                    && _isDirectoryWritable
                    && !_isPenumbraDirectory
                    && !_isOneDrive
                    && !_cacheDirectoryHasOtherFilesThanCache
                    && _cacheDirectoryIsValidPath)
                {
                    _configService.Current.CacheFolder = path;
                    _configService.Save();
                    _cacheMonitor.StartSpheneWatcher(path);
                    _cacheMonitor.InvokeScan();
                }
            }, _dalamudUtil.IsWine ? @"Z:\" : @"C:\");
        }
        SpheneUI.Controls.HelpText("Folder where Sphene stores all synced mod files. Pick an empty folder close to the drive root.");

        if (_isPenumbraDirectory)
        {
            SpheneUI.Text.ColoredWrapped("Do not point the storage path directly to the Penumbra directory. If necessary, make a subfolder in it.", ImGuiColors.DalamudRed);
        }
        else if (_isOneDrive)
        {
            SpheneUI.Text.ColoredWrapped("Do not point the storage path to a folder in OneDrive. Do not use OneDrive folders for any Mod related functionality.", ImGuiColors.DalamudRed);
        }
        else if (!_isDirectoryWritable)
        {
            SpheneUI.Text.ColoredWrapped("The folder you selected does not exist or cannot be written to. Please provide a valid path.", ImGuiColors.DalamudRed);
        }
        else if (_cacheDirectoryHasOtherFilesThanCache)
        {
            SpheneUI.Text.ColoredWrapped("Your selected directory has files or directories inside that are not Sphene related. Use an empty directory or a previous Sphene storage directory only.", ImGuiColors.DalamudRed);
        }
        else if (!_cacheDirectoryIsValidPath)
        {
            SpheneUI.Text.ColoredWrapped("Your selected directory contains illegal characters unreadable by FFXIV. " +
                             "Restrict yourself to latin letters (A-Z), underscores (_), dashes (-) and arabic numbers (0-9).", ImGuiColors.DalamudRed);
        }

        float maxCacheSize = (float)_configService.Current.MaxLocalCacheInGiB;
        if (SpheneUI.Controls.SliderFloat("Maximum Storage Size in GiB", ref maxCacheSize, 1f, 200f, "%.2f GiB"))
        {
            _configService.Current.MaxLocalCacheInGiB = maxCacheSize;
            _configService.Save();
        }
        SpheneUI.Controls.HelpText("The storage is automatically governed by Sphene. It will clear itself automatically once it reaches the set capacity by removing the oldest unused files. You typically do not need to clear it yourself.");
    }

    public void DrawPenumbraModDownloadFolderSetting()
    {
        var downloadFolder = _configService.Current.PenumbraModDownloadFolder;
        var caps = string.IsNullOrEmpty(downloadFolder)
            ? new[] { new SpheneUI.Controls.InputButtonSpec(FontAwesomeIcon.Folder, "Pick the mod download folder.") }
            : new[]
            {
                new SpheneUI.Controls.InputButtonSpec(FontAwesomeIcon.Folder, "Pick the mod download folder."),
                new SpheneUI.Controls.InputButtonSpec(FontAwesomeIcon.Times, "Clear the mod download folder.")
            };
        var (_, downloadPressed) = SpheneUI.Controls.InputTextWithButtons("Download Folder##penumbra",
            ref downloadFolder, 255, ImGuiInputTextFlags.ReadOnly, buttons: caps);
        if (downloadPressed == 0)
        {
            FileDialogManager.OpenFolderDialog("Pick Mod Download Folder", (success, path) =>
            {
                if (!success) return;

                _isDownloadFolderOneDrive = path.Contains("onedrive", StringComparison.OrdinalIgnoreCase);
                _isDownloadFolderPenumbra = string.Equals(path.ToLowerInvariant(), _ipcManager.Penumbra.ModDirectory?.ToLowerInvariant(), StringComparison.Ordinal);

                _isDownloadFolderWritable = IsDirectoryWritable(path);
                _downloadFolderIsValidPath = PathRegex().IsMatch(path);

                if (!string.IsNullOrEmpty(path)
                    && _isDownloadFolderWritable
                    && !_isDownloadFolderPenumbra
                    && !_isDownloadFolderOneDrive
                    && _downloadFolderIsValidPath)
                {
                    _configService.Current.PenumbraModDownloadFolder = path;
                    _configService.Save();
                }
            }, _dalamudUtil.IsWine ? @"Z:\" : @"C:\");
        }
        else if (downloadPressed == 1)
        {
            _configService.Current.PenumbraModDownloadFolder = string.Empty;
            _configService.Save();
        }
        SpheneUI.Controls.HelpText("Optional: Select a folder where downloaded mod files (PMP) will be stored. If left empty, the default cache folder will be used.");

        if (_isDownloadFolderPenumbra)
        {
            SpheneUI.Text.ColoredWrapped("Do not point the download path directly to the Penumbra directory.", ImGuiColors.DalamudRed);
        }
        else if (_isDownloadFolderOneDrive)
        {
            SpheneUI.Text.ColoredWrapped("Do not point the download path to a folder in OneDrive.", ImGuiColors.DalamudRed);
        }
        else if (!string.IsNullOrEmpty(downloadFolder) && !_isDownloadFolderWritable)
        {
            SpheneUI.Text.ColoredWrapped("The folder you selected does not exist or cannot be written to. Please provide a valid path.", ImGuiColors.DalamudRed);
        }
        else if (!_downloadFolderIsValidPath)
        {
            SpheneUI.Text.ColoredWrapped("Your selected directory contains illegal characters unreadable by FFXIV. " +
                             "Restrict yourself to latin letters (A-Z), underscores (_), dashes (-) and arabic numbers (0-9).", ImGuiColors.DalamudRed);
        }

        if (string.IsNullOrEmpty(downloadFolder))
        {
            SpheneUI.Text.ColoredWrapped("No folder selected. Using default Sphene Storage folder.", ImGuiColors.HealerGreen);
        }
    }


















    public void DrawFileScanState()
    {
        ImGui.AlignTextToFramePadding();
        SpheneUI.Text.Plain("File Scanner Status");
        ImGui.SameLine();
        if (_cacheMonitor.IsScanRunning)
        {
            ImGui.AlignTextToFramePadding();

            SpheneUI.Text.Plain("Scan is running");
            SpheneUI.Text.Plain("Current Progress:");
            ImGui.SameLine();
            SpheneUI.Text.Plain(_cacheMonitor.TotalFiles == 1
                ? "Collecting files"
                : $"Processing {_cacheMonitor.CurrentFileProgress}/{_cacheMonitor.TotalFilesStorage} from storage ({_cacheMonitor.TotalFiles} scanned in)");
            SpheneUI.AttachTooltip("Note: it is possible to have more files in storage than scanned in, " +
                "this is due to the scanner normally ignoring those files but the game loading them in and using them on your character, so they get " +
                "added to the local storage.");
        }
        else if (_cacheMonitor.HaltScanLocks.Any(f => f.Value > 0))
        {
            ImGui.AlignTextToFramePadding();

            SpheneUI.Text.Plain("Halted (" + string.Join(", ", _cacheMonitor.HaltScanLocks.Where(f => f.Value > 0).Select(locker => locker.Key + ": " + locker.Value + " halt requests")) + ")");
            ImGui.SameLine();
            if (SpheneUI.Controls.Button("Reset halt requests##clearlocks"))
            {
                _cacheMonitor.ResetLocks();
            }
        }
        else
        {
            SpheneUI.Text.Plain("Idle");
            if (_configService.Current.InitialScanComplete)
            {
                ImGui.SameLine();
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Play, "Force rescan"))
                {
                    _cacheMonitor.InvokeScan();
                }
            }
        }
    }















    public void DrawOAuth(ServerStorage selectedServer)
    {
        var oauthToken = selectedServer.OAuthToken;
        _ = ImRaii.PushIndent(10f);
        if (oauthToken == null)
        {
            if (_discordOAuthCheck == null)
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.QuestionCircle, "Check if Server supports Discord OAuth2"))
                {
                    _discordOAuthCheck = _serverConfigurationManager.CheckDiscordOAuth(selectedServer.ServerUri);
                }
            }
            else
            {
                if (!_discordOAuthCheck.IsCompleted)
                {
                    SpheneUI.Text.ColoredWrapped($"Checking OAuth2 compatibility with {selectedServer.ServerUri}", ImGuiColors.DalamudYellow);
                }
                else
                {
                    if (_discordOAuthCheck.Result != null)
                    {
                        SpheneUI.Text.ColoredWrapped("Server is compatible with Discord OAuth2", ImGuiColors.HealerGreen);
                    }
                    else
                    {
                        SpheneUI.Text.ColoredWrapped("Server is not compatible with Discord OAuth2", ImGuiColors.DalamudRed);
                    }
                }
            }

            if (_discordOAuthCheck != null && _discordOAuthCheck.IsCompleted)
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowRight, "Authenticate with Server"))
                {
                    _discordOAuthGetCode = _serverConfigurationManager.GetDiscordOAuthToken(_discordOAuthCheck.Result!, selectedServer.ServerUri, _discordOAuthGetCts.Token);
                }
                else if (_discordOAuthGetCode != null && !_discordOAuthGetCode.IsCompleted)
                {
                    SpheneUI.Text.Wrapped("A browser window has been opened, follow it to authenticate. Click the button below if you accidentally closed the window and need to restart the authentication.");
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Ban, "Cancel Authentication"))
                    {
                        _discordOAuthGetCts = _discordOAuthGetCts.CancelRecreate();
                        _discordOAuthGetCode = null;
                    }
                }
                else if (_discordOAuthGetCode != null && _discordOAuthGetCode.IsCompleted)
                {
                    SpheneUI.Text.Wrapped("Discord OAuth is completed, status: ");
                    ImGui.SameLine();
                    if (_discordOAuthGetCode.Result != null)
                    {
                        selectedServer.OAuthToken = _discordOAuthGetCode.Result;
                        _discordOAuthGetCode = null;
                        _serverConfigurationManager.Save();
                        SpheneUI.Text.ColoredWrapped("Success", ImGuiColors.HealerGreen);
                    }
                    else
                    {
                        SpheneUI.Text.ColoredWrapped("Failed, please check /xllog for more information", ImGuiColors.DalamudRed);
                    }
                }
            }
        }

        if (oauthToken != null)
        {
            if (!_oauthTokenExpiry.TryGetValue(oauthToken, out DateTime tokenExpiry))
            {
                try
                {
                    var handler = new JwtSecurityTokenHandler();
                    var jwt = handler.ReadJwtToken(oauthToken);
                    tokenExpiry = _oauthTokenExpiry[oauthToken] = jwt.ValidTo;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not parse OAuth token, deleting");
                    selectedServer.OAuthToken = null;
                    _serverConfigurationManager.Save();
                }
            }

            if (tokenExpiry > DateTime.UtcNow)
            {
                SpheneUI.Text.ColoredWrapped($"OAuth2 is enabled, linked to: Discord User {_serverConfigurationManager.GetDiscordUserFromToken(selectedServer)}", ImGuiColors.HealerGreen);
                SpheneUI.Text.Wrapped($"The OAuth2 token will expire on {tokenExpiry:yyyy-MM-dd} and automatically renew itself during login on or after {(tokenExpiry - TimeSpan.FromDays(7)):yyyy-MM-dd}.");
                using (ImRaii.Disabled(!CtrlPressed()))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Exclamation, "Renew OAuth2 token manually") && CtrlPressed())
                    {
                        _ = _tokenProvider.TryUpdateOAuth2LoginTokenAsync(selectedServer, forced: true)
                            .ContinueWith((_) => _apiController.CreateConnectionsAsync());
                    }
                }
                SpheneUI.Controls.HelpText("Hold CTRL to manually refresh your OAuth2 token. Normally you do not need to do this.");
                SpheneUI.Layout.Space(SpheneUI.Spacing.X10);

                if ((_discordOAuthUIDs == null || _discordOAuthUIDs.IsCompleted)
                    && SpheneUI.Icons.TextButton(FontAwesomeIcon.Question, "Check Discord Connection"))
                {
                    _discordOAuthUIDs = _serverConfigurationManager.GetUIDsWithDiscordToken(selectedServer.ServerUri, oauthToken);
                }
                else if (_discordOAuthUIDs != null)
                {
                    if (!_discordOAuthUIDs.IsCompleted)
                    {
                        SpheneUI.Text.ColoredWrapped("Checking accounts on server", ImGuiColors.DalamudYellow);
                    }
                    else
                    {
                        var foundUids = _discordOAuthUIDs.Result?.Count ?? 0;
                        var primaryUid = _discordOAuthUIDs.Result?.FirstOrDefault() ?? new KeyValuePair<string, string>(string.Empty, string.Empty);
                        var vanity = string.IsNullOrEmpty(primaryUid.Value) ? "-" : primaryUid.Value;
                        if (foundUids > 0)
                        {
                            SpheneUI.Text.ColoredWrapped($"Found {foundUids} accounts on the server (vanity id: {vanity})",
                                ImGuiColors.HealerGreen);
                        }
                        else
                        {
                            SpheneUI.Text.ColoredWrapped($"Found no accounts linked to this OAuth2 account", ImGuiColors.DalamudRed);
                        }
                    }
                }
            }
            else
            {
                SpheneUI.Text.ColoredWrapped("The OAuth2 token is stale and expired. Please renew the OAuth2 connection.", ImGuiColors.DalamudRed);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Exclamation, "Renew OAuth2 connection"))
                {
                    selectedServer.OAuthToken = null;
                    _serverConfigurationManager.Save();
                    _ = _serverConfigurationManager.CheckDiscordOAuth(selectedServer.ServerUri)
                        .ContinueWith(async (urlTask) =>
                        {
                            var url = await urlTask.ConfigureAwait(false);
                            var token = await _serverConfigurationManager.GetDiscordOAuthToken(url!, selectedServer.ServerUri, CancellationToken.None).ConfigureAwait(false);
                            selectedServer.OAuthToken = token;
                            _serverConfigurationManager.Save();
                            await _apiController.CreateConnectionsAsync().ConfigureAwait(false);
                        });
                }
            }

            DrawUnlinkOAuthButton(selectedServer);
        }
    }

    public enum RepoAddResult
    {
        NoChange,
        Added,
        Enabled
    }

    [StructLayout(LayoutKind.Auto)]
    public readonly record struct PluginInstallState(bool IsInstalled, bool IsEnabled);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarQube", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "Accessing internal Dalamud methods for plugin management.")]
    public async Task<RepoAddResult> AddRepoViaReflectionAsync(string url, string name)
    {
        try
        {
            Logger.LogDebug("Starting reflection-based repo addition...");

            var assembly = typeof(IDalamudPluginInterface).Assembly;
            var serviceType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "Service`1", StringComparison.Ordinal) && t.IsGenericType);
            var configType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "DalamudConfiguration", StringComparison.Ordinal));
            var pluginManagerType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "PluginManager", StringComparison.Ordinal));

            if (serviceType == null || configType == null || pluginManagerType == null)
            {
                Logger.LogError("Could not find Service<>, DalamudConfiguration, or PluginManager types.");
                return RepoAddResult.NoChange;
            }

            var configService = serviceType.MakeGenericType(configType);
            var configGetter = configService.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (configGetter == null)
            {
                Logger.LogError("Could not find Service.Get() method.");
                return RepoAddResult.NoChange;
            }

            var dalamudConfig = configGetter.Invoke(null, null);
            if (dalamudConfig == null)
            {
                Logger.LogError("Could not retrieve DalamudConfiguration instance.");
                return RepoAddResult.NoChange;
            }

            var repoListProp = configType.GetProperty("ThirdPartyRepositories", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? configType.GetProperty("ThirdRepoList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (repoListProp == null)
            {
                Logger.LogError("Could not find ThirdPartyRepositories or ThirdRepoList property.");
                return RepoAddResult.NoChange;
            }

            var repoList = repoListProp.GetValue(dalamudConfig) as IList;
            if (repoList == null)
            {
                Logger.LogError("ThirdPartyRepositories is null or not an IList.");
                return RepoAddResult.NoChange;
            }

            // Check if already exists
            bool alreadyExists = false;
            bool wasEnabled = false;
            foreach (var repo in repoList)
            {
                var urlProp = repo.GetType().GetProperty("Url");
                var currentUrl = urlProp?.GetValue(repo)?.ToString();
                if (string.Equals(currentUrl, url, StringComparison.Ordinal))
                {
                    Logger.LogDebug("Repository already exists in runtime config.");
                    alreadyExists = true;
                    // Ensure it is enabled
                    var enabledProp = repo.GetType().GetProperty("IsEnabled");
                    if (enabledProp?.GetValue(repo) is bool isEnabled)
                    {
                        wasEnabled = isEnabled;
                        if (!isEnabled)
                        {
                            enabledProp.SetValue(repo, true);
                        }
                    }
                    break;
                }
            }

            var result = RepoAddResult.NoChange;

            if (!alreadyExists)
            {
                // Create new entry
                var repoType = repoList.GetType().GetGenericArguments()[0];
                var newRepo = Activator.CreateInstance(repoType);

                if (newRepo == null)
                {
                    Logger.LogError("Could not create ThirdPartyRepository instance.");
                    return RepoAddResult.NoChange;
                }

                repoType.GetProperty("Url")?.SetValue(newRepo, url);
                repoType.GetProperty("Name")?.SetValue(newRepo, name);
                repoType.GetProperty("IsEnabled")?.SetValue(newRepo, true);
                
                // Try to set IsThirdParty if it exists
                var isThirdPartyProp = repoType.GetProperty("IsThirdParty");
                if (isThirdPartyProp != null)
                {
                    isThirdPartyProp.SetValue(newRepo, true);
                }

                repoList.Add(newRepo);
                Logger.LogDebug("Added new repo to list.");
                result = RepoAddResult.Added;
            }
            else if (!wasEnabled)
            {
                Logger.LogDebug("Repository existed but was disabled. Enabled it.");
                result = RepoAddResult.Enabled;
            }

            if (result == RepoAddResult.NoChange)
            {
                return RepoAddResult.NoChange;
            }

            var queueSaveMethod = configType.GetMethod("QueueSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (queueSaveMethod != null)
            {
                queueSaveMethod.Invoke(dalamudConfig, null);
                Logger.LogDebug("Queued Dalamud configuration save.");
            }
            else
            {
                // Fallback to Save() if QueueSave() is missing (unlikely in recent Dalamud versions)
                var saveMethod = configType.GetMethod("Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (saveMethod != null)
                {
                    try
                    {
                        saveMethod.Invoke(dalamudConfig, null);
                        Logger.LogDebug("Saved Dalamud configuration.");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to Save() configuration (likely thread safety). Continuing...");
                    }
                }
            }
            
            Mediator.Publish(new NotificationMessage("BypassEmote", "Repository added to Dalamud Config.", NotificationType.Info));

            // Notify PluginManager to reload repos from config
            var pluginManagerService = serviceType.MakeGenericType(pluginManagerType);
            var pluginManagerGetter = pluginManagerService.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (pluginManagerGetter != null)
            {
                var pluginManager = pluginManagerGetter.Invoke(null, null);
                if (pluginManager != null)
                {
                    var setReposMethod = pluginManagerType.GetMethod("SetPluginReposFromConfigAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (setReposMethod != null)
                    {
                        Logger.LogDebug("Calling SetPluginReposFromConfigAsync(true)...");
                        var task = setReposMethod.Invoke(pluginManager, new object[] { true }) as Task;
                        if (task != null) await task.ConfigureAwait(false);
                        Logger.LogDebug("SetPluginReposFromConfigAsync completed.");
                    }
                    else
                    {
                        Logger.LogError("Could not find SetPluginReposFromConfigAsync method.");
                    }
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to add repo via reflection.");
            return RepoAddResult.NoChange;
        }
    }

    public PluginInstallState GetPluginInstallState(params string[] internalNames)
    {
        foreach (var internalName in internalNames)
        {
            var plugin = _pluginInterface.InstalledPlugins
                .FirstOrDefault(p => string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
            if (plugin == null) continue;

            var enabledProp = plugin.GetType().GetProperty("IsEnabled");
            if (enabledProp?.GetValue(plugin) is bool enabledValue)
            {
                return new PluginInstallState(true, enabledValue);
            }

            var loadedProp = plugin.GetType().GetProperty("IsLoaded");
            if (loadedProp?.GetValue(plugin) is bool loadedValue)
            {
                return new PluginInstallState(true, loadedValue);
            }

            return new PluginInstallState(true, true);
        }

        return new PluginInstallState(false, false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarQube", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "Accessing internal Dalamud methods for plugin management.")]
    public async Task<bool> EnablePluginViaReflectionAsync(params string[] internalNames)
    {
        try
        {
            var assembly = typeof(IDalamudPluginInterface).Assembly;
            var serviceType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "Service`1", StringComparison.Ordinal) && t.IsGenericType);
            var pluginManagerType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "PluginManager", StringComparison.Ordinal));

            if (serviceType == null || pluginManagerType == null)
            {
                Logger.LogError("Could not find Service<> or PluginManager types.");
                return false;
            }

            var pluginManagerService = serviceType.MakeGenericType(pluginManagerType);
            var pluginManagerGetter = pluginManagerService.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (pluginManagerGetter == null)
            {
                Logger.LogError("Could not find Service.Get() method for PluginManager.");
                return false;
            }

            var pluginManager = pluginManagerGetter.Invoke(null, null);
            if (pluginManager == null)
            {
                Logger.LogError("Could not retrieve PluginManager instance.");
                return false;
            }

            var installedPluginsProp = pluginManagerType.GetProperty("InstalledPlugins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (installedPluginsProp == null)
            {
                Logger.LogError("Could not find InstalledPlugins property.");
                return false;
            }

            var installedPlugins = installedPluginsProp.GetValue(pluginManager) as IEnumerable;
            if (installedPlugins == null)
            {
                Logger.LogError("InstalledPlugins is null.");
                return false;
            }

            foreach (var internalName in internalNames)
            {
                foreach (var plugin in installedPlugins)
                {
                    var nameProp = plugin.GetType().GetProperty("InternalName");
                    var currentName = nameProp?.GetValue(plugin)?.ToString();
                    if (!string.Equals(currentName, internalName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var isLoadedProp = plugin.GetType().GetProperty("IsLoaded");
                    if (isLoadedProp?.GetValue(plugin) is bool isLoaded && isLoaded)
                    {
                        return true;
                    }

                    var loadMethod = plugin.GetType().GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (loadMethod == null)
                    {
                        Logger.LogError("Could not find LoadAsync on LocalPlugin.");
                        return false;
                    }

                    var parameters = loadMethod.GetParameters();
                    object? result = parameters.Length switch
                    {
                        1 => loadMethod.Invoke(plugin, new object[] { PluginLoadReason.Installer }),
                        2 => loadMethod.Invoke(plugin, new object[] { PluginLoadReason.Installer, false }),
                        _ => null
                    };

                    if (result is Task task) await task.ConfigureAwait(false);
                    return result != null;
                }
            }

            Logger.LogDebug("EnablePluginViaReflectionAsync: plugin not found in InstalledPlugins.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to enable plugin via reflection.");
        }

        return false;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarQube", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "Accessing internal Dalamud methods for plugin management.")]
    public async Task<bool> DisablePluginViaReflectionAsync(params string[] internalNames)
    {
        try
        {
            var assembly = typeof(IDalamudPluginInterface).Assembly;
            var serviceType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "Service`1", StringComparison.Ordinal) && t.IsGenericType);
            var pluginManagerType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "PluginManager", StringComparison.Ordinal));

            if (serviceType == null || pluginManagerType == null)
            {
                Logger.LogError("Could not find Service<> or PluginManager types.");
                return false;
            }

            var pluginManagerService = serviceType.MakeGenericType(pluginManagerType);
            var pluginManagerGetter = pluginManagerService.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (pluginManagerGetter == null)
            {
                Logger.LogError("Could not find Service.Get() method for PluginManager.");
                return false;
            }

            var pluginManager = pluginManagerGetter.Invoke(null, null);
            if (pluginManager == null)
            {
                Logger.LogError("Could not retrieve PluginManager instance.");
                return false;
            }

            var installedPluginsProp = pluginManagerType.GetProperty("InstalledPlugins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (installedPluginsProp == null)
            {
                Logger.LogError("Could not find InstalledPlugins property.");
                return false;
            }

            var installedPlugins = installedPluginsProp.GetValue(pluginManager) as IEnumerable;
            if (installedPlugins == null)
            {
                Logger.LogError("InstalledPlugins is null.");
                return false;
            }

            foreach (var internalName in internalNames)
            {
                foreach (var plugin in installedPlugins)
                {
                    var nameProp = plugin.GetType().GetProperty("InternalName");
                    var currentName = nameProp?.GetValue(plugin)?.ToString();
                    if (!string.Equals(currentName, internalName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var isLoadedProp = plugin.GetType().GetProperty("IsLoaded");
                    if (isLoadedProp?.GetValue(plugin) is bool isLoaded && !isLoaded)
                    {
                        return true;
                    }

                    var unloadMethod = plugin.GetType().GetMethod("UnloadAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (unloadMethod == null)
                    {
                        Logger.LogError("Could not find UnloadAsync on LocalPlugin.");
                        return false;
                    }

                    object? result;
                    var parameters = unloadMethod.GetParameters();
                    if (parameters.Length == 0)
                    {
                        result = unloadMethod.Invoke(plugin, null);
                    }
                    else if (parameters.Length == 1)
                    {
                        var paramType = parameters[0].ParameterType;
                        object? paramValue = paramType.IsEnum
                            ? Enum.Parse(paramType, "WaitBeforeDispose")
                            : Activator.CreateInstance(paramType);
                        result = unloadMethod.Invoke(plugin, new[] { paramValue });
                    }
                    else
                    {
                        result = null;
                    }

                    if (result is Task task) await task.ConfigureAwait(false);
                    return result != null;
                }
            }

            Logger.LogDebug("DisablePluginViaReflectionAsync: plugin not found in InstalledPlugins.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to disable plugin via reflection.");
        }

        return false;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarQube", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields", Justification = "Accessing internal Dalamud methods for plugin management.")]
    public async Task InstallPluginViaReflectionAsync(string pluginInternalName)
    {
        try
        {
            var assembly = typeof(IDalamudPluginInterface).Assembly;
            var serviceType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "Service`1", StringComparison.Ordinal) && t.IsGenericType);
            var pluginManagerType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "PluginManager", StringComparison.Ordinal));

            if (serviceType == null || pluginManagerType == null)
            {
                Logger.LogError("Could not find Service<> or PluginManager types.");
                return;
            }

            var pluginManagerService = serviceType.MakeGenericType(pluginManagerType);
            var pluginManagerGetter = pluginManagerService.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (pluginManagerGetter == null)
            {
                Logger.LogError("Could not find Service.Get() method for PluginManager.");
                return;
            }

            var pluginManager = pluginManagerGetter.Invoke(null, null);
            if (pluginManager == null)
            {
                Logger.LogError("Could not retrieve PluginManager instance.");
                return;
            }

            // Reload Plugin Masters
            var reloadMethod = pluginManagerType.GetMethod("ReloadPluginMastersAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (reloadMethod != null)
            {
                Logger.LogDebug("Reloading plugin masters...");
                var task = reloadMethod.Invoke(pluginManager, new object[] { true }) as Task;
                if (task != null) await task.ConfigureAwait(false);
            }

            // Get AvailablePlugins
            var availablePluginsProp = pluginManagerType.GetProperty("AvailablePlugins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (availablePluginsProp == null)
            {
                Logger.LogError("Could not find AvailablePlugins property.");
                return;
            }

            var availablePlugins = availablePluginsProp.GetValue(pluginManager) as IEnumerable;
            if (availablePlugins == null)
            {
                Logger.LogError("AvailablePlugins is null.");
                return;
            }

            object? targetManifest = null;
            foreach (var manifest in availablePlugins)
            {
                var internalNameProp = manifest.GetType().GetProperty("InternalName");
                var internalName = internalNameProp?.GetValue(manifest)?.ToString();
                
                if (string.Equals(internalName, pluginInternalName, StringComparison.Ordinal))
                {
                    targetManifest = manifest;
                    break;
                }
            }

            if (targetManifest == null)
            {
                Logger.LogError("Could not find manifest for {PluginName}.", pluginInternalName);
                Mediator.Publish(new NotificationMessage("Installation Failed", $"Could not find {pluginInternalName} in available plugins.", NotificationType.Error));
                return;
            }

            Logger.LogDebug("Found manifest for {PluginName}. Installing...", pluginInternalName);

            // InstallPluginAsync
            var installMethod = pluginManagerType.GetMethod("InstallPluginAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (installMethod == null)
            {
                Logger.LogError("Could not find InstallPluginAsync method.");
                return;
            }

            // PluginLoadReason.Installer = 2
            var reasonEnumType = assembly.DefinedTypes.FirstOrDefault(t => string.Equals(t.Name, "PluginLoadReason", StringComparison.Ordinal));
            if (reasonEnumType == null)
            {
                Logger.LogError("Could not find PluginLoadReason enum.");
                return;
            }
            
            var reasonValue = Enum.ToObject(reasonEnumType, 2); 

            // Parameters: manifest, useTesting, reason, inheritedWorkingPluginId
            var parameters = new object?[] { targetManifest, false, reasonValue, null };
            
            var installTask = installMethod.Invoke(pluginManager, parameters) as Task;
            if (installTask != null)
            {
                await installTask.ConfigureAwait(false);
                Logger.LogDebug("Installation task completed.");
                Mediator.Publish(new NotificationMessage(pluginInternalName, "Installation started successfully.", NotificationType.Success));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during reflection-based installation.");
        }
    }

    private const string SeaOfStarsRepoUrl = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/main/repo.json";
    private const string BypassEmoteRepoUrl = "https://raw.githubusercontent.com/Aspher0/BypassEmote/refs/heads/main/repo.json";

    public bool DrawOtherPluginState()
    {
        SpheneUI.Text.Plain("Mandatory Plugins:");

        DrawPluginInstallStatus("Penumbra", _penumbraExists, "Penumbra", SeaOfStarsRepoUrl, "SeaOfStars Repo", 150);
        DrawPluginInstallStatus("Glamourer", _glamourerExists, "Glamourer", SeaOfStarsRepoUrl, "SeaOfStars Repo");

        SpheneUI.Text.Plain("Optional Plugins:");
        
        DrawPluginInstallStatus("SimpleHeels", _heelsExists, "SimpleHeels", SeaOfStarsRepoUrl, "SeaOfStars Repo", 150);
        DrawPluginInstallStatus("Customize+", _customizePlusExists, "CustomizePlus", SeaOfStarsRepoUrl, "SeaOfStars Repo");
        DrawPluginInstallStatus("Honorific", _honorificExists, "Honorific");
        DrawPluginInstallStatus("Moodles", _moodlesExists, "Moodles", SeaOfStarsRepoUrl, "SeaOfStars Repo");
        DrawPluginInstallStatus("PetNicknames", _petNamesExists, "PetRenamer");
        DrawPluginInstallStatus("Brio", _brioExists, "Brio", SeaOfStarsRepoUrl, "SeaOfStars Repo");
        DrawPluginInstallStatus("BypassEmote", _bypassEmoteExists, "BypassEmote", BypassEmoteRepoUrl, "BypassEmote Repo");

        if (!_penumbraExists || !_glamourerExists)
        {
            SpheneUI.Text.Colored("You need to install both Penumbra and Glamourer and keep them up to date to use Sphene.", ImGuiColors.DalamudRed);
            return false;
        }

        return true;
    }

    private void DrawPluginInstallStatus(string label, bool exists, string internalName, string? repoUrl = null, string? repoName = null, float? sameLineOffset = null)
    {
        if (sameLineOffset.HasValue)
            ImGui.SameLine(sameLineOffset.Value);
        else
            ImGui.SameLine();

        SpheneUI.Text.Colored(label, (exists ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed));
        
        if (!exists)
        {
             if (ImGui.IsItemClicked())
             {
                 _ = Task.Run(async () =>
                 {
                     try
                     {
                         if (!string.IsNullOrEmpty(repoUrl) && !string.IsNullOrEmpty(repoName))
                         {
                             Logger.LogDebug("Adding repo {RepoName} for {Plugin}...", repoName, internalName);
                             await AddRepoViaReflectionAsync(repoUrl, repoName).ConfigureAwait(false);
                             await Task.Delay(1000).ConfigureAwait(false);
                         }
                         
                         Logger.LogDebug("Installing {Plugin}...", internalName);
                        await InstallPluginViaReflectionAsync(internalName).ConfigureAwait(false);
                    }
                     catch (Exception ex)
                     {
                         Logger.LogError(ex, "Failed to install {Plugin}", internalName);
                         Mediator.Publish(new NotificationMessage("Error", $"Failed to install {label}. Check logs.", NotificationType.Error));
                     }
                 });
                 
                 Mediator.Publish(new NotificationMessage(label, "Installation started...", NotificationType.Info));
             }
             SpheneUI.AttachTooltip($"{label} is unavailable. Click to install.");
        }
        else
        {
             SpheneUI.AttachTooltip($"{label} is available and up to date.");
        }
    }

    public int DrawServiceSelection(bool selectOnChange = false, bool showConnect = true)
    {
        string[] comboEntries = _serverConfigurationManager.GetServerNames();

        if (_serverSelectionIndex == -1)
        {
            _serverSelectionIndex = Array.IndexOf(_serverConfigurationManager.GetServerApiUrls(), _serverConfigurationManager.CurrentApiUrl);
        }
        if (_serverSelectionIndex == -1 || _serverSelectionIndex >= comboEntries.Length)
        {
            _serverSelectionIndex = 0;
        }
        for (int i = 0; i < comboEntries.Length; i++)
        {
            if (string.Equals(_serverConfigurationManager.CurrentServer?.ServerName, comboEntries[i], StringComparison.OrdinalIgnoreCase))
                comboEntries[i] += " [Current]";
        }
        using (var controlStyle = SpheneUI.Controls.PushValueControlStyle())
        {
            if (ImGui.BeginCombo("Select Service", comboEntries[_serverSelectionIndex]))
            {
                for (int i = 0; i < comboEntries.Length; i++)
                {
                    bool isSelected = _serverSelectionIndex == i;
                    if (SpheneUI.Controls.Selectable(comboEntries[i], isSelected))
                    {
                        _serverSelectionIndex = i;
                        if (selectOnChange)
                        {
                            _serverConfigurationManager.SelectServer(i);
                        }
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }
        }

        if (showConnect)
        {
            ImGui.SameLine();
            var text = "Connect";
            if (_serverSelectionIndex == _serverConfigurationManager.CurrentServerIndex) text = "Reconnect";
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Link, text))
            {
                _serverConfigurationManager.SelectServer(_serverSelectionIndex);
                _ = _apiController.CreateConnectionsAsync();
            }
        }

        return _serverSelectionIndex;
    }

    public void DrawUIDComboForAuthentication(int indexOffset, Authentication item, string serverUri, ILogger? logger = null)
    {
        using (ImRaii.Disabled(_discordOAuthUIDs == null))
        {
            var aliasPairs = _discordOAuthUIDs?.Result?.Select(t => new UidAliasPair(t.Key, t.Value)).ToList() ?? [new UidAliasPair(item.UID ?? null, null)];
            var uidComboName = "Account###" + item.CharacterName + item.WorldId + serverUri + indexOffset + aliasPairs.Count;
            SpheneUI.Controls.Combo(uidComboName, aliasPairs,
                (v) =>
                {
                    if (v is null)
                        return "No account set";

                    if (!string.IsNullOrEmpty(v.Alias))
                    {
                        return v.Alias;
                    }

                    if (string.IsNullOrEmpty(v.UID))
                        return "No account set";

                    return v.UID;
                },
                (v) =>
                {
                    if (!string.Equals(v?.UID ?? null, item.UID, StringComparison.Ordinal))
                    {
                        item.UID = v?.UID ?? null;
                        _serverConfigurationManager.Save();
                    }
                },
                aliasPairs.Find(f => string.Equals(f.UID, item.UID, StringComparison.Ordinal)) ?? default);
        }

        if (_discordOAuthUIDs == null)
        {
            SpheneUI.AttachTooltip("Use the button above to update your accounts from the service before you can assign them to characters.");
        }
    }

    public void DrawUnlinkOAuthButton(ServerStorage selectedServer)
    {
        using (ImRaii.Disabled(!CtrlPressed()))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Unlink OAuth2 Connection") && UiSharedService.CtrlPressed())
            {
                selectedServer.OAuthToken = null;
                _serverConfigurationManager.Save();
                ResetOAuthTasksState();
            }
        }
        SpheneUI.Controls.HelpText("Hold CTRL to unlink the current OAuth2 connection.");
    }

    public void DrawUpdateOAuthUIDsButton(ServerStorage selectedServer)
    {
        if (!selectedServer.UseOAuth2)
            return;

        using (ImRaii.Disabled(string.IsNullOrEmpty(selectedServer.OAuthToken)))
        {
            if ((_discordOAuthUIDs == null || _discordOAuthUIDs.IsCompleted)
                && SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowsSpin, "Update accounts from service")
                && !string.IsNullOrEmpty(selectedServer.OAuthToken))
            {
                _discordOAuthUIDs = _serverConfigurationManager.GetUIDsWithDiscordToken(selectedServer.ServerUri, selectedServer.OAuthToken);
            }
        }
        DateTime tokenExpiry = DateTime.MinValue;
        if (!string.IsNullOrEmpty(selectedServer.OAuthToken) && !_oauthTokenExpiry.TryGetValue(selectedServer.OAuthToken, out tokenExpiry))
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jwt = handler.ReadJwtToken(selectedServer.OAuthToken);
                tokenExpiry = _oauthTokenExpiry[selectedServer.OAuthToken] = jwt.ValidTo;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not parse OAuth token, deleting");
                selectedServer.OAuthToken = null;
                _serverConfigurationManager.Save();
                tokenExpiry = DateTime.MinValue;
            }
        }
        if (string.IsNullOrEmpty(selectedServer.OAuthToken) || tokenExpiry < DateTime.UtcNow)
        {
            SpheneUI.Text.ColoredWrapped("You have no OAuth token or the OAuth token is expired. Please use the Service Configuration to link your OAuth2 account or refresh the token.", ImGuiColors.DalamudRed);
        }
    }







    public void LoadLocalization(string languageCode)
    {
        _localization.SetupWithLangCode(languageCode);
    }


    [LibraryImport("user32")]
    internal static partial short GetKeyState(int nVirtKey);

    internal void ResetOAuthTasksState()
    {
        _discordOAuthCheck = null;
        _discordOAuthGetCts = _discordOAuthGetCts.CancelRecreate();
        _discordOAuthGetCode = null;
        _discordOAuthUIDs = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;

        base.Dispose(disposing);

        _discordOAuthGetCts.Cancel();
        _discordOAuthGetCts.Dispose();
        UidFont.Dispose();
        GameFont.Dispose();
    }


    
    




    [GeneratedRegex(@"^(?:[a-zA-Z]:\\[\w\s\-\\]+?|\/(?:[\w\s\-\/])+?)$", RegexOptions.ECMAScript, 5000)]
    private static partial Regex PathRegex();





    // Apply any pending window resize for the specified window
    public static void ApplyPendingWindowResize(string windowName)
    {
        if (_pendingWindowSizes.TryGetValue(windowName, out var newSize))
        {
            ImGui.SetNextWindowSize(newSize);
            _pendingWindowSizes.TryRemove(windowName, out _);
        }
    }

    public void OpenPluginInstaller(string searchText)
    {
        try
        {
            _pluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.AllPlugins, searchText);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to open plugin installer");
        }
    }

    // Store a pending window size to be applied next frame
    public static void SetPendingWindowSize(string windowName, Vector2 size)
    {
        _pendingWindowSizes[windowName] = size;
    }

    public sealed record IconScaleData(Vector2 IconSize, Vector2 NormalizedIconScale, float OffsetX, float IconScaling);
    private sealed record UidAliasPair(string? UID, string? Alias);
}
