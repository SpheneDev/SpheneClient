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
using Sphene.FileCache;
using Sphene.Interop.Ipc;
using Sphene.Localization;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI.Styling;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.SignalR;
using Microsoft.Extensions.Logging;
using Sphene.UI.Theme;
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
    public const string TooltipSeparator = "--SEP--";
    public static readonly ImGuiWindowFlags PopupWindowFlags = ImGuiWindowFlags.NoResize |
                                               ImGuiWindowFlags.NoScrollbar |
                                           ImGuiWindowFlags.NoScrollWithMouse;

    public readonly FileDialogManager FileDialogManager;
    private const string _notesEnd = "##SPHENE_USER_NOTES_END##";
    private const string _notesStart = "##SPHENE_USER_NOTES_START##";

    public static void DrawSectionSeparator(string text)
    {
        var drawList = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetTextLineHeightWithSpacing();
        
        var textSize = ImGui.CalcTextSize(text);
        var lineY = cursor.Y + height / 2;
        var lineColor = ImGui.GetColorU32(ImGuiCol.Separator);
        
        // Draw left line
        var leftLineEnd = cursor.X + (availWidth - textSize.X) / 2 - 5;
        if (leftLineEnd > cursor.X)
            drawList.AddLine(new Vector2(cursor.X, lineY), new Vector2(leftLineEnd, lineY), lineColor);
            
        // Draw text
        ImGui.SetCursorScreenPos(new Vector2(cursor.X + (availWidth - textSize.X) / 2, cursor.Y));
        ImGui.TextUnformatted(text);
        
        // Draw right line
        var rightLineStart = cursor.X + (availWidth + textSize.X) / 2 + 5;
        if (rightLineStart < cursor.X + availWidth)
            drawList.AddLine(new Vector2(rightLineStart, lineY), new Vector2(cursor.X + availWidth, lineY), lineColor);
            
        ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + height));
    }
    private readonly ApiController _apiController;
    private readonly CacheMonitor _cacheMonitor;
    private readonly SpheneConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly IpcManager _ipcManager;
    private readonly Dalamud.Localization _localization;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Dictionary<string, object?> _selectedComboItems = new(StringComparer.Ordinal);
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private static readonly ConcurrentDictionary<string, Vector2> _pendingWindowSizes = new(StringComparer.Ordinal);
    private readonly ITextureProvider _textureProvider;
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
        _textureProvider = textureProvider;
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
                    tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansJpMedium, new()
                    {
                        SizePx = 35
                    });
                }
            });
        });
        GameFont = _pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new(GameFontFamilyAndSize.Axis12));
        IconFont = _pluginInterface.UiBuilder.IconFontFixedWidthHandle;
    }

    public static string DoubleNewLine => Environment.NewLine + Environment.NewLine;
    public ApiController ApiController => _apiController;

    public bool EditTrackerPosition { get; set; }

    public IFontHandle GameFont { get; init; }
    public bool HasValidPenumbraModPath => !(_ipcManager.Penumbra.ModDirectory ?? string.Empty).IsNullOrEmpty() && Directory.Exists(_ipcManager.Penumbra.ModDirectory);

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

    public static void AttachToolTip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            using (SpheneCustomTheme.ApplyTooltipTheme())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
                if (text.Contains(TooltipSeparator, StringComparison.Ordinal))
                {
                    var splitText = text.Split(TooltipSeparator, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < splitText.Length; i++)
                    {
                        ImGui.TextUnformatted(splitText[i]);
                        if (i != splitText.Length - 1) ImGui.Separator();
                    }
                }
                else
                {
                    ImGui.TextUnformatted(text);
                }
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }
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

    public static void CenterNextWindow(float width, float height, ImGuiCond cond = ImGuiCond.None)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(new Vector2(center.X - width / 2, center.Y - height / 2), cond);
    }

    public static uint Color(byte r, byte g, byte b, byte a)
    { uint ret = a; ret <<= 8; ret += b; ret <<= 8; ret += g; ret <<= 8; ret += r; return ret; }

    public static uint Color(Vector4 color)
    {
        uint ret = (byte)(color.W * 255);
        ret <<= 8;
        ret += (byte)(color.Z * 255);
        ret <<= 8;
        ret += (byte)(color.Y * 255);
        ret <<= 8;
        ret += (byte)(color.X * 255);
        return ret;
    }

    public static void ColorText(string text, Vector4 color)
    {
        using var raiicolor = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
    }

    public static void ColorTextWrapped(string text, Vector4 color, float wrapPos = 0)
    {
        using var raiicolor = ImRaii.PushColor(ImGuiCol.Text, color);
        TextWrapped(text, wrapPos);
    }

    public static bool CtrlPressed() => (GetKeyState(0xA2) & 0x8000) != 0 || (GetKeyState(0xA3) & 0x8000) != 0;

    public static void DrawGrouped(Action imguiDrawAction, float rounding = 5f, float? expectedWidth = null)
    {
        var cursorPos = ImGui.GetCursorPos();
        using (ImRaii.Group())
        {
            if (expectedWidth != null)
            {
                ImGui.Dummy(new(expectedWidth.Value, 0));
                ImGui.SetCursorPos(cursorPos);
            }

            imguiDrawAction.Invoke();
        }

        ImGui.GetWindowDrawList().AddRect(
            ImGui.GetItemRectMin() - ImGui.GetStyle().ItemInnerSpacing,
            ImGui.GetItemRectMax() + ImGui.GetStyle().ItemInnerSpacing,
            Color(ImGuiColors.DalamudGrey2), rounding);
    }

    public static void DrawGroupedCenteredColorText(string text, Vector4 color, float? maxWidth = null)
    {
        var availWidth = ImGui.GetContentRegionAvail().X;
        var textWidth = ImGui.CalcTextSize(text, wrapWidth: availWidth).X;
        if (maxWidth != null && textWidth > maxWidth * ImGuiHelpers.GlobalScale) textWidth = maxWidth.Value * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth / 2f) - (textWidth / 2f));
        DrawGrouped(() =>
        {
            ColorTextWrapped(text, color, ImGui.GetCursorPosX() + textWidth);
        }, expectedWidth: maxWidth == null ? null : maxWidth * ImGuiHelpers.GlobalScale);
    }

    public static void DrawOutlinedFont(string text, Vector4 fontColor, Vector4 outlineColor, int thickness)
    {
        var original = ImGui.GetCursorPos();

        using (ImRaii.PushColor(ImGuiCol.Text, outlineColor))
        {
            ImGui.SetCursorPos(original with { Y = original.Y - thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X - thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { Y = original.Y + thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X + thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X - thickness, Y = original.Y - thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X + thickness, Y = original.Y + thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X - thickness, Y = original.Y + thickness });
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original with { X = original.X + thickness, Y = original.Y - thickness });
            ImGui.TextUnformatted(text);
        }

        using (ImRaii.PushColor(ImGuiCol.Text, fontColor))
        {
            ImGui.SetCursorPos(original);
            ImGui.TextUnformatted(text);
            ImGui.SetCursorPos(original);
            ImGui.TextUnformatted(text);
        }
    }

    public static void DrawOutlinedFont(ImDrawListPtr drawList, string text, Vector2 textPos, uint fontColor, uint outlineColor, int thickness)
    {
        drawList.AddText(textPos with { Y = textPos.Y - thickness },
            outlineColor, text);
        drawList.AddText(textPos with { X = textPos.X - thickness },
            outlineColor, text);
        drawList.AddText(textPos with { Y = textPos.Y + thickness },
            outlineColor, text);
        drawList.AddText(textPos with { X = textPos.X + thickness },
            outlineColor, text);
        drawList.AddText(new Vector2(textPos.X - thickness, textPos.Y - thickness),
            outlineColor, text);
        drawList.AddText(new Vector2(textPos.X + thickness, textPos.Y + thickness),
            outlineColor, text);
        drawList.AddText(new Vector2(textPos.X - thickness, textPos.Y + thickness),
            outlineColor, text);
        drawList.AddText(new Vector2(textPos.X + thickness, textPos.Y - thickness),
            outlineColor, text);

        drawList.AddText(textPos, fontColor, text);
        drawList.AddText(textPos, fontColor, text);
    }

    public static void DrawTree(string leafName, Action drawOnOpened, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        using var tree = ImRaii.TreeNode(leafName, flags);
        if (tree)
        {
            drawOnOpened();
        }
    }

    public static Vector4 GetBoolColor(bool input) => input ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed;

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

    public static float GetWindowContentRegionWidth()
    {
        return ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
    }

    public static float GetBaseFolderWidth(float widthOffset = 0f)
    {
        return GetWindowContentRegionWidth() - 32f + widthOffset;
    }

    public static float GetSyncshellFolderWidth(float widthOffset = 0f)
    {
        return GetWindowContentRegionWidth() - 22f + widthOffset; // Separate width calculation for syncshell folders
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

    public static void ScaledNextItemWidth(float width)
    {
        ImGui.SetNextItemWidth(width * ImGuiHelpers.GlobalScale);
    }

    public static void ScaledSameLine(float offset)
    {
        ImGui.SameLine(offset * ImGuiHelpers.GlobalScale);
    }

    public static void SetScaledWindowSize(float width, bool centerWindow = true)
    {
        var newLineHeight = ImGui.GetCursorPosY();
        ImGui.NewLine();
        newLineHeight = ImGui.GetCursorPosY() - newLineHeight;
        var y = ImGui.GetCursorPos().Y + ImGui.GetWindowContentRegionMin().Y - newLineHeight * 2 - ImGui.GetStyle().ItemSpacing.Y;

        SetScaledWindowSize(width, y, centerWindow, scaledHeight: true);
    }

    public static void SetScaledWindowSize(float width, float height, bool centerWindow = true, bool scaledHeight = false)
    {
        ImGui.SameLine();
        var x = width * ImGuiHelpers.GlobalScale;
        var y = scaledHeight ? height : height * ImGuiHelpers.GlobalScale;

        if (centerWindow)
        {
            CenterWindow(x, y);
        }

        ImGui.SetWindowSize(new Vector2(x, y));
    }

    public static bool ShiftPressed() => (GetKeyState(0xA1) & 0x8000) != 0 || (GetKeyState(0xA0) & 0x8000) != 0;

    public static void TextWrapped(string text, float wrapPos = 0)
    {
        ImGui.PushTextWrapPos(wrapPos);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    public static Vector4 UploadColor((long, long) data) => data.Item1 == 0 ? ImGuiColors.DalamudGrey :
        data.Item1 == data.Item2 ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudYellow;

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

    public void BigText(string text, Vector4? color = null)
    {
        FontText(text, UidFont, color);
    }

    public void BooleanToColoredIcon(bool value, bool inline = true)
    {
        using var colorgreen = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, value);
        using var colorred = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed, !value);

        if (inline) ImGui.SameLine();

        if (value)
        {
            IconText(FontAwesomeIcon.Check);
        }
        else
        {
            IconText(FontAwesomeIcon.Times);
        }
    }

    public void DrawCacheDirectorySetting()
    {
        ColorTextWrapped("Note: The storage folder should be somewhere close to root (i.e. C:\\SpheneStorage) in a new empty folder. DO NOT point this to your game folder. DO NOT point this to your Penumbra folder.", ImGuiColors.DalamudYellow);
        var cacheDirectory = _configService.Current.CacheFolder;
        ScaledNextItemWidth(350);
        ImGui.InputText("Storage Folder##cache", ref cacheDirectory, 255, ImGuiInputTextFlags.ReadOnly);

        ImGui.SameLine();
        using (ImRaii.Disabled(_cacheMonitor.SpheneWatcher != null))
        {
            if (IconButton(FontAwesomeIcon.Folder))
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
        }
        if (_cacheMonitor.SpheneWatcher != null)
        {
            AttachToolTip("Stop the Monitoring before changing the Storage folder. As long as monitoring is active, you cannot change the Storage folder location.");
        }

        if (_isPenumbraDirectory)
        {
            ColorTextWrapped("Do not point the storage path directly to the Penumbra directory. If necessary, make a subfolder in it.", ImGuiColors.DalamudRed);
        }
        else if (_isOneDrive)
        {
            ColorTextWrapped("Do not point the storage path to a folder in OneDrive. Do not use OneDrive folders for any Mod related functionality.", ImGuiColors.DalamudRed);
        }
        else if (!_isDirectoryWritable)
        {
            ColorTextWrapped("The folder you selected does not exist or cannot be written to. Please provide a valid path.", ImGuiColors.DalamudRed);
        }
        else if (_cacheDirectoryHasOtherFilesThanCache)
        {
            ColorTextWrapped("Your selected directory has files or directories inside that are not Sphene related. Use an empty directory or a previous Sphene storage directory only.", ImGuiColors.DalamudRed);
        }
        else if (!_cacheDirectoryIsValidPath)
        {
            ColorTextWrapped("Your selected directory contains illegal characters unreadable by FFXIV. " +
                             "Restrict yourself to latin letters (A-Z), underscores (_), dashes (-) and arabic numbers (0-9).", ImGuiColors.DalamudRed);
        }

        float maxCacheSize = (float)_configService.Current.MaxLocalCacheInGiB;
        ScaledNextItemWidth(350);
        if (ImGui.SliderFloat("Maximum Storage Size in GiB", ref maxCacheSize, 1f, 200f, "%.2f GiB"))
        {
            _configService.Current.MaxLocalCacheInGiB = maxCacheSize;
            _configService.Save();
        }
        DrawHelpText("The storage is automatically governed by Sphene. It will clear itself automatically once it reaches the set capacity by removing the oldest unused files. You typically do not need to clear it yourself.");
    }

    public void DrawPenumbraModDownloadFolderSetting()
    {
        var downloadFolder = _configService.Current.PenumbraModDownloadFolder;
        ScaledNextItemWidth(350);
        ImGui.InputText("Mod Download Folder##download", ref downloadFolder, 255, ImGuiInputTextFlags.ReadOnly);

        ImGui.SameLine();
        ImGui.PushID("penumbraDownloadFolder");
        if (IconButton(FontAwesomeIcon.Folder))
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
        ImGui.PopID();
        
        // Add a clear button to reset to default (empty)
        if (!string.IsNullOrEmpty(downloadFolder))
        {
            ImGui.SameLine();
            if (IconButton(FontAwesomeIcon.Trash))
            {
                _configService.Current.PenumbraModDownloadFolder = string.Empty;
                _configService.Save();
            }
            AttachToolTip("Reset to default (use Cache folder)");
        }

        if (_isDownloadFolderPenumbra)
        {
            ColorTextWrapped("Do not point the download path directly to the Penumbra directory.", ImGuiColors.DalamudRed);
        }
        else if (_isDownloadFolderOneDrive)
        {
            ColorTextWrapped("Do not point the download path to a folder in OneDrive.", ImGuiColors.DalamudRed);
        }
        else if (!string.IsNullOrEmpty(downloadFolder) && !_isDownloadFolderWritable)
        {
            ColorTextWrapped("The folder you selected does not exist or cannot be written to. Please provide a valid path.", ImGuiColors.DalamudRed);
        }
        else if (!_downloadFolderIsValidPath)
        {
            ColorTextWrapped("Your selected directory contains illegal characters unreadable by FFXIV. " +
                             "Restrict yourself to latin letters (A-Z), underscores (_), dashes (-) and arabic numbers (0-9).", ImGuiColors.DalamudRed);
        }

        if (string.IsNullOrEmpty(downloadFolder))
        {
            ColorTextWrapped("No folder selected. Using default Sphene Storage folder.", ImGuiColors.HealerGreen);
        }

        DrawHelpText("Optional: Select a folder where downloaded mod files (PMP) will be stored. If left empty, the default cache folder will be used.");
    }

    public T? DrawCombo<T>(string comboName, IEnumerable<T> comboItems, Func<T?, string> toName,
        Action<T?>? onSelected = null, T? initialSelectedItem = default)
    {
        if (!comboItems.Any()) return default;

        if (!_selectedComboItems.TryGetValue(comboName, out var selectedItem) && selectedItem == null)
        {
            selectedItem = initialSelectedItem;
            _selectedComboItems[comboName] = selectedItem;
        }

        if (ImGui.BeginCombo(comboName, selectedItem == null ? "Unset Value" : toName((T?)selectedItem)))
        {
            foreach (var item in comboItems)
            {
                bool isSelected = EqualityComparer<T>.Default.Equals(item, (T?)selectedItem);
                if (ImGui.Selectable(toName(item), isSelected))
                {
                    _selectedComboItems[comboName] = item!;
                    onSelected?.Invoke(item!);
                }
            }

            ImGui.EndCombo();
        }

        return (T?)_selectedComboItems[comboName];
    }

    public void DrawFileScanState()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("File Scanner Status");
        ImGui.SameLine();
        if (_cacheMonitor.IsScanRunning)
        {
            ImGui.AlignTextToFramePadding();

            ImGui.TextUnformatted("Scan is running");
            ImGui.TextUnformatted("Current Progress:");
            ImGui.SameLine();
            ImGui.TextUnformatted(_cacheMonitor.TotalFiles == 1
                ? "Collecting files"
                : $"Processing {_cacheMonitor.CurrentFileProgress}/{_cacheMonitor.TotalFilesStorage} from storage ({_cacheMonitor.TotalFiles} scanned in)");
            AttachToolTip("Note: it is possible to have more files in storage than scanned in, " +
                "this is due to the scanner normally ignoring those files but the game loading them in and using them on your character, so they get " +
                "added to the local storage.");
        }
        else if (_cacheMonitor.HaltScanLocks.Any(f => f.Value > 0))
        {
            ImGui.AlignTextToFramePadding();

            ImGui.TextUnformatted("Halted (" + string.Join(", ", _cacheMonitor.HaltScanLocks.Where(f => f.Value > 0).Select(locker => locker.Key + ": " + locker.Value + " halt requests")) + ")");
            ImGui.SameLine();
            if (ImGui.Button("Reset halt requests##clearlocks"))
            {
                _cacheMonitor.ResetLocks();
            }
        }
        else
        {
            ImGui.TextUnformatted("Idle");
            if (_configService.Current.InitialScanComplete)
            {
                ImGui.SameLine();
                if (IconTextButton(FontAwesomeIcon.Play, "Force rescan"))
                {
                    _cacheMonitor.InvokeScan();
                }
            }
        }
    }

    public void DrawHelpText(string helpText)
    {
        ImGui.SameLine();
        IconText(FontAwesomeIcon.QuestionCircle, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        AttachToolTip(helpText);
    }

    public void DrawOAuth(ServerStorage selectedServer)
    {
        var oauthToken = selectedServer.OAuthToken;
        _ = ImRaii.PushIndent(10f);
        if (oauthToken == null)
        {
            if (_discordOAuthCheck == null)
            {
                if (IconTextButton(FontAwesomeIcon.QuestionCircle, "Check if Server supports Discord OAuth2"))
                {
                    _discordOAuthCheck = _serverConfigurationManager.CheckDiscordOAuth(selectedServer.ServerUri);
                }
            }
            else
            {
                if (!_discordOAuthCheck.IsCompleted)
                {
                    ColorTextWrapped($"Checking OAuth2 compatibility with {selectedServer.ServerUri}", ImGuiColors.DalamudYellow);
                }
                else
                {
                    if (_discordOAuthCheck.Result != null)
                    {
                        ColorTextWrapped("Server is compatible with Discord OAuth2", ImGuiColors.HealerGreen);
                    }
                    else
                    {
                        ColorTextWrapped("Server is not compatible with Discord OAuth2", ImGuiColors.DalamudRed);
                    }
                }
            }

            if (_discordOAuthCheck != null && _discordOAuthCheck.IsCompleted)
            {
                if (IconTextButton(FontAwesomeIcon.ArrowRight, "Authenticate with Server"))
                {
                    _discordOAuthGetCode = _serverConfigurationManager.GetDiscordOAuthToken(_discordOAuthCheck.Result!, selectedServer.ServerUri, _discordOAuthGetCts.Token);
                }
                else if (_discordOAuthGetCode != null && !_discordOAuthGetCode.IsCompleted)
                {
                    TextWrapped("A browser window has been opened, follow it to authenticate. Click the button below if you accidentally closed the window and need to restart the authentication.");
                    if (IconTextButton(FontAwesomeIcon.Ban, "Cancel Authentication"))
                    {
                        _discordOAuthGetCts = _discordOAuthGetCts.CancelRecreate();
                        _discordOAuthGetCode = null;
                    }
                }
                else if (_discordOAuthGetCode != null && _discordOAuthGetCode.IsCompleted)
                {
                    TextWrapped("Discord OAuth is completed, status: ");
                    ImGui.SameLine();
                    if (_discordOAuthGetCode.Result != null)
                    {
                        selectedServer.OAuthToken = _discordOAuthGetCode.Result;
                        _discordOAuthGetCode = null;
                        _serverConfigurationManager.Save();
                        ColorTextWrapped("Success", ImGuiColors.HealerGreen);
                    }
                    else
                    {
                        ColorTextWrapped("Failed, please check /xllog for more information", ImGuiColors.DalamudRed);
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
                ColorTextWrapped($"OAuth2 is enabled, linked to: Discord User {_serverConfigurationManager.GetDiscordUserFromToken(selectedServer)}", ImGuiColors.HealerGreen);
                TextWrapped($"The OAuth2 token will expire on {tokenExpiry:yyyy-MM-dd} and automatically renew itself during login on or after {(tokenExpiry - TimeSpan.FromDays(7)):yyyy-MM-dd}.");
                using (ImRaii.Disabled(!CtrlPressed()))
                {
                    if (IconTextButton(FontAwesomeIcon.Exclamation, "Renew OAuth2 token manually") && CtrlPressed())
                    {
                        _ = _tokenProvider.TryUpdateOAuth2LoginTokenAsync(selectedServer, forced: true)
                            .ContinueWith((_) => _apiController.CreateConnectionsAsync());
                    }
                }
                DrawHelpText("Hold CTRL to manually refresh your OAuth2 token. Normally you do not need to do this.");
                ImGuiHelpers.ScaledDummy(10f);

                if ((_discordOAuthUIDs == null || _discordOAuthUIDs.IsCompleted)
                    && IconTextButton(FontAwesomeIcon.Question, "Check Discord Connection"))
                {
                    _discordOAuthUIDs = _serverConfigurationManager.GetUIDsWithDiscordToken(selectedServer.ServerUri, oauthToken);
                }
                else if (_discordOAuthUIDs != null)
                {
                    if (!_discordOAuthUIDs.IsCompleted)
                    {
                        ColorTextWrapped("Checking UIDs on Server", ImGuiColors.DalamudYellow);
                    }
                    else
                    {
                        var foundUids = _discordOAuthUIDs.Result?.Count ?? 0;
                        var primaryUid = _discordOAuthUIDs.Result?.FirstOrDefault() ?? new KeyValuePair<string, string>(string.Empty, string.Empty);
                        var vanity = string.IsNullOrEmpty(primaryUid.Value) ? "-" : primaryUid.Value;
                        if (foundUids > 0)
                        {
                            ColorTextWrapped($"Found {foundUids} associated UIDs on the server, Primary UID: {primaryUid.Key} (Vanity UID: {vanity})",
                                ImGuiColors.HealerGreen);
                        }
                        else
                        {
                            ColorTextWrapped($"Found no UIDs associated to this linked OAuth2 account", ImGuiColors.DalamudRed);
                        }
                    }
                }
            }
            else
            {
                ColorTextWrapped("The OAuth2 token is stale and expired. Please renew the OAuth2 connection.", ImGuiColors.DalamudRed);
                if (IconTextButton(FontAwesomeIcon.Exclamation, "Renew OAuth2 connection"))
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
        ImGui.TextUnformatted("Mandatory Plugins:");

        DrawPluginInstallStatus("Penumbra", _penumbraExists, "Penumbra", SeaOfStarsRepoUrl, "SeaOfStars Repo", 150);
        DrawPluginInstallStatus("Glamourer", _glamourerExists, "Glamourer", SeaOfStarsRepoUrl, "SeaOfStars Repo");

        ImGui.TextUnformatted("Optional Plugins:");
        
        DrawPluginInstallStatus("SimpleHeels", _heelsExists, "SimpleHeels", SeaOfStarsRepoUrl, "SeaOfStars Repo", 150);
        DrawPluginInstallStatus("Customize+", _customizePlusExists, "CustomizePlus", SeaOfStarsRepoUrl, "SeaOfStars Repo");
        DrawPluginInstallStatus("Honorific", _honorificExists, "Honorific");
        DrawPluginInstallStatus("Moodles", _moodlesExists, "Moodles", SeaOfStarsRepoUrl, "SeaOfStars Repo");
        DrawPluginInstallStatus("PetNicknames", _petNamesExists, "PetRenamer");
        DrawPluginInstallStatus("Brio", _brioExists, "Brio", SeaOfStarsRepoUrl, "SeaOfStars Repo");
        DrawPluginInstallStatus("BypassEmote", _bypassEmoteExists, "BypassEmote", BypassEmoteRepoUrl, "BypassEmote Repo");

        if (!_penumbraExists || !_glamourerExists)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "You need to install both Penumbra and Glamourer and keep them up to date to use Sphene.");
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

        ColorText(label, GetBoolColor(exists));
        
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
             AttachToolTip($"{label} is unavailable. Click to install.");
        }
        else
        {
             AttachToolTip($"{label} is available and up to date.");
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
        if (ImGui.BeginCombo("Select Service", comboEntries[_serverSelectionIndex]))
        {
            for (int i = 0; i < comboEntries.Length; i++)
            {
                bool isSelected = _serverSelectionIndex == i;
                if (ImGui.Selectable(comboEntries[i], isSelected))
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

        if (showConnect)
        {
            ImGui.SameLine();
            var text = "Connect";
            if (_serverSelectionIndex == _serverConfigurationManager.CurrentServerIndex) text = "Reconnect";
            if (IconTextButton(FontAwesomeIcon.Link, text))
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
            var uidComboName = "UID###" + item.CharacterName + item.WorldId + serverUri + indexOffset + aliasPairs.Count;
            DrawCombo(uidComboName, aliasPairs,
                (v) =>
                {
                    if (v is null)
                        return "No UID set";

                    if (!string.IsNullOrEmpty(v.Alias))
                    {
                        return $"{v.UID} ({v.Alias})";
                    }

                    if (string.IsNullOrEmpty(v.UID))
                        return "No UID set";

                    return $"{v.UID}";
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
            AttachToolTip("Use the button above to update your UIDs from the service before you can assign UIDs to characters.");
        }
    }

    public void DrawUnlinkOAuthButton(ServerStorage selectedServer)
    {
        using (ImRaii.Disabled(!CtrlPressed()))
        {
            if (IconTextButton(FontAwesomeIcon.Trash, "Unlink OAuth2 Connection") && UiSharedService.CtrlPressed())
            {
                selectedServer.OAuthToken = null;
                _serverConfigurationManager.Save();
                ResetOAuthTasksState();
            }
        }
        DrawHelpText("Hold CTRL to unlink the current OAuth2 connection.");
    }

    public void DrawUpdateOAuthUIDsButton(ServerStorage selectedServer)
    {
        if (!selectedServer.UseOAuth2)
            return;

        using (ImRaii.Disabled(string.IsNullOrEmpty(selectedServer.OAuthToken)))
        {
            if ((_discordOAuthUIDs == null || _discordOAuthUIDs.IsCompleted)
                && IconTextButton(FontAwesomeIcon.ArrowsSpin, "Update UIDs from Service")
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
            ColorTextWrapped("You have no OAuth token or the OAuth token is expired. Please use the Service Configuration to link your OAuth2 account or refresh the token.", ImGuiColors.DalamudRed);
        }
    }

    public Vector2 GetIconButtonSize(FontAwesomeIcon icon)
    {
        using var font = IconFont.Push();
        return ImGuiHelpers.GetButtonSize(icon.ToIconString());
    }

    public Vector2 GetIconSize(FontAwesomeIcon icon)
    {
        using var font = IconFont.Push();
        return ImGui.CalcTextSize(icon.ToIconString());
    }

    public float GetIconTextButtonSize(FontAwesomeIcon icon, string text)
    {
        Vector2 vector;
        using (IconFont.Push())
            vector = ImGui.CalcTextSize(icon.ToIconString());

        Vector2 vector2 = ImGui.CalcTextSize(text);
        float num = 3f * ImGuiHelpers.GlobalScale;
        return vector.X + vector2.X + ImGui.GetStyle().FramePadding.X * 2f + num;
    }

    public bool IconButton(FontAwesomeIcon icon, float? height = null, float? iconScale = null, float? iconPixelSize = null, float? width = null, string? styleKey = null)
    {
        string text = icon.ToIconString();

        ImGui.PushID(text);
        Vector2 vector;
        using (IconFont.Push())
        {
            var scale = iconPixelSize.HasValue
                ? iconPixelSize.Value / ImGui.GetFontSize()
                : (iconScale ?? 1f);
            if (iconPixelSize.HasValue) scale = MathF.Max(1f, MathF.Round(scale));
            if (MathF.Abs(scale - 1f) > 0.0001f) ImGui.SetWindowFontScale(scale);
            vector = ImGui.CalcTextSize(text);
            if (MathF.Abs(scale - 1f) > 0.0001f) ImGui.SetWindowFontScale(1f);
        }
        ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
        Vector2 cursorScreenPos = ImGui.GetCursorScreenPos();
        float x = width ?? (vector.X + ImGui.GetStyle().FramePadding.X * 2f);
        float frameHeight = height ?? ImGui.GetFrameHeight();

        Vector2 buttonPos = cursorScreenPos;
        bool result;
        if (!string.IsNullOrEmpty(styleKey))
        {
            var theme = SpheneCustomTheme.CurrentTheme;
            if (theme.ButtonStyles.TryGetValue(styleKey, out var overrideStyle))
            {
                x += overrideStyle.WidthDelta;
                frameHeight += overrideStyle.HeightDelta;
                int pushedColors = 0;
                int pushedVars = 0;
                if (overrideStyle.Button.HasValue) { ImGui.PushStyleColor(ImGuiCol.Button, overrideStyle.Button.Value); pushedColors++; }
                if (overrideStyle.ButtonHovered.HasValue) { ImGui.PushStyleColor(ImGuiCol.ButtonHovered, overrideStyle.ButtonHovered.Value); pushedColors++; }
                if (overrideStyle.ButtonActive.HasValue) { ImGui.PushStyleColor(ImGuiCol.ButtonActive, overrideStyle.ButtonActive.Value); pushedColors++; }
                if (overrideStyle.Text.HasValue) { ImGui.PushStyleColor(ImGuiCol.Text, overrideStyle.Text.Value); pushedColors++; }
                // Always push a border color for buttons: override or fallback to theme.Border
                var borderColor = overrideStyle.Border.HasValue ? overrideStyle.Border.Value : theme.Border;
                ImGui.PushStyleColor(ImGuiCol.Border, borderColor); pushedColors++;
                if (overrideStyle.BorderSize.HasValue) { ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, overrideStyle.BorderSize.Value); pushedVars++; }
                else { ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f); pushedVars++; }
                result = ImGui.Button(string.Empty, new Vector2(x, frameHeight));
                if (pushedVars > 0) ImGui.PopStyleVar(pushedVars);
                if (pushedColors > 0) ImGui.PopStyleColor(pushedColors);
            }
            else
            {
                // No override style found: ensure button border uses generic theme border color
                int pushedColors = 0;
                ImGui.PushStyleColor(ImGuiCol.Border, SpheneCustomTheme.CurrentTheme.Border); pushedColors++;
                int pushedVars = 0;
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f); pushedVars++;
                result = ImGui.Button(string.Empty, new Vector2(x, frameHeight));
                if (pushedVars > 0) ImGui.PopStyleVar(pushedVars);
                if (pushedColors > 0) ImGui.PopStyleColor(pushedColors);
            }
        }
        else
        {
            // No style key: apply generic theme border color to decouple from CompactBorder
            int pushedColors = 0;
            ImGui.PushStyleColor(ImGuiCol.Border, SpheneCustomTheme.CurrentTheme.Border); pushedColors++;
            int pushedVars = 0;
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f); pushedVars++;
            result = ImGui.Button(string.Empty, new Vector2(x, frameHeight));
            if (pushedVars > 0) ImGui.PopStyleVar(pushedVars);
            if (pushedColors > 0) ImGui.PopStyleColor(pushedColors);
        }
        
        // Calculate perfect center position for the icon using stored button position
        float buttonCenterX = buttonPos.X + (x / 2f);
        float iconCenterX = buttonCenterX - (vector.X / 2f);
        
        Vector2 pos = new Vector2(iconCenterX,
            buttonPos.Y + frameHeight / 2f - (vector.Y / 2f));

        if (!string.IsNullOrEmpty(styleKey))
        {
            var theme = SpheneCustomTheme.CurrentTheme;
            if (theme.ButtonStyles.TryGetValue(styleKey, out var overrideStyle))
            {
                pos += overrideStyle.IconOffset;
            }
        }
        using (IconFont.Push())
        {
            var scale = iconPixelSize.HasValue
                ? iconPixelSize.Value / ImGui.GetFontSize()
                : (iconScale ?? 1f);
            if (iconPixelSize.HasValue) scale = MathF.Max(1f, MathF.Round(scale));
            if (MathF.Abs(scale - 1f) > 0.0001f) ImGui.SetWindowFontScale(scale);
            uint iconColorU32 = ImGui.GetColorU32(ImGuiCol.Text);
            if (!string.IsNullOrEmpty(styleKey))
            {
                var theme = SpheneCustomTheme.CurrentTheme;
                if (theme.ButtonStyles.TryGetValue(styleKey, out var overrideStyle) && overrideStyle.Icon.HasValue)
                {
                    iconColorU32 = ImGui.GetColorU32(overrideStyle.Icon.Value);
                }
            }
            windowDrawList.AddText(pos, iconColorU32, text);
            if (MathF.Abs(scale - 1f) > 0.0001f) ImGui.SetWindowFontScale(1f);
        }
        ImGui.PopID();

        if (result && !string.IsNullOrEmpty(styleKey) && ButtonStyleManagerUI.IsPickerEnabled)
        {
            Mediator.Publish(new ThemeNavigateToButtonSettingsMessage(styleKey));
            return false;
        }

        return result;
    }

    public void IconText(FontAwesomeIcon icon, uint color)
    {
        FontText(icon.ToIconString(), IconFont, color);
    }

    public void IconText(FontAwesomeIcon icon, Vector4? color = null)
    {
        IconText(icon, color == null ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(color.Value));
    }

    public bool IconTextButton(FontAwesomeIcon icon, string text, float? width = null, bool isInPopup = false, string? styleKey = null)
    {
        return IconTextButtonInternal(icon, text,
            isInPopup ? ColorHelpers.RgbaUintToVector4(ImGui.GetColorU32(ImGuiCol.PopupBg)) : null,
            width <= 0 ? null : width,
            null, null, styleKey);
    }

    public bool IconTextActionButton(FontAwesomeIcon icon, string text, float? width = null, string? styleKey = null)
    {
        var theme = SpheneCustomTheme.CurrentTheme;
        return IconTextButtonInternal(icon, text, theme.CompactActionButton, width <= 0 ? null : width,
            theme.CompactActionButtonHovered, theme.CompactActionButtonActive, styleKey);
    }

    public IDalamudTextureWrap LoadImage(byte[] imageData)
    {
        return _textureProvider.CreateFromImageAsync(imageData).Result;
    }

    public void LoadLocalization(string languageCode)
    {
        _localization.SetupWithLangCode(languageCode);
    }

    internal static void DistanceSeparator()
    {
        ImGuiHelpers.ScaledDummy(5);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(5);
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

    private static void CenterWindow(float width, float height, ImGuiCond cond = ImGuiCond.None)
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetWindowPos(new Vector2(center.X - width / 2, center.Y - height / 2), cond);
    }

    /// <summary>
    /// Draws a theme-aware status indicator with proper theming
    /// </summary>
    public static void DrawThemedStatusIndicator(string label, bool isActive, bool hasWarning = false, bool hasError = false)
    {
        var theme = SpheneCustomTheme.CurrentTheme;
        var statusColor = hasError
            ? theme.CompactServerStatusError
            : hasWarning
                ? theme.CompactServerStatusWarning
                : (isActive ? theme.CompactServerStatusConnected : theme.CompactTextSecondary);
        var drawList = ImGui.GetWindowDrawList();
        
        // Align text to frame padding for better vertical centering
        ImGui.AlignTextToFramePadding();
        var alignedPos = ImGui.GetCursorScreenPos();
        
        // Calculate circle center to align with text baseline
        var frameHeight = ImGui.GetFrameHeight();
        var circleCenter = new Vector2(alignedPos.X + 8, alignedPos.Y + frameHeight * 0.5f);
        
        // Draw status circle with proper vertical alignment
        drawList.AddCircleFilled(circleCenter, 6, ImGui.ColorConvertFloat4ToU32(statusColor));
        drawList.AddCircle(circleCenter, 6, ImGui.ColorConvertFloat4ToU32(theme.TextPrimary), 12, 1.5f);
        
        // Add glow effect for active status
        if (isActive && !hasError)
        {
            var glowColor = new Vector4(statusColor.X, statusColor.Y, statusColor.Z, 0.4f);
            drawList.AddCircle(circleCenter, 8, ImGui.ColorConvertFloat4ToU32(glowColor), 12, 2.0f);
        }
        
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 20);
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGui.ColorConvertFloat4ToU32(theme.TextPrimary));
        ImGui.Text(label);
    }
    
    /// <summary>
    /// Draws a theme-aware progress bar with crystalline styling
    /// </summary>
    public static void DrawThemedProgressBar(string label, float progress, string? overlay = null, Vector4? color = null)
    {
        // Draw label first
        ImGui.AlignTextToFramePadding();
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGui.ColorConvertFloat4ToU32(SpheneCustomTheme.CurrentTheme.TextPrimary));
        ImGui.TextUnformatted(label);
        
        // Use theme-configured size and call the main implementation
        var theme = SpheneCustomTheme.CurrentTheme;
        var barSize = new Vector2(theme.CompactProgressBarWidth, theme.CompactProgressBarHeight);
        DrawThemedProgressBar(progress, barSize, overlay, color);
    }
    
    /// <summary>
    /// Draws a theme-aware progress bar with crystalline styling
    /// </summary>
    public static void DrawThemedProgressBar(float progress, Vector2? size = null, string? overlay = null, Vector4? color = null)
    {
        var theme = SpheneCustomTheme.CurrentTheme;
        var barSize = size ?? new Vector2(theme.CompactProgressBarWidth, theme.CompactProgressBarHeight);
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var endPos = new Vector2(pos.X + barSize.X, pos.Y + barSize.Y);
        var borderRadius = theme.ProgressBarRounding;
        
        // Background - use theme-configured background color
        drawList.AddRectFilled(pos, endPos, ImGui.ColorConvertFloat4ToU32(theme.CompactProgressBarBackground), borderRadius);
        
        // Progress fill with gradient
        if (progress > 0)
        {
            var progressEnd = new Vector2(pos.X + (barSize.X * progress), pos.Y + barSize.Y);
            if (theme.ProgressBarUseGradient)
            {
                DrawRoundedHorizontalGradient(drawList, pos, progressEnd, borderRadius, theme.ProgressBarGradientStart, theme.ProgressBarGradientEnd);
            }
            else
            {
                var progressColorStart = color ?? theme.CompactProgressBarForeground;
                drawList.AddRectFilled(pos, progressEnd, ImGui.ColorConvertFloat4ToU32(progressColorStart), borderRadius);
            }
        }
        
        // Border - use theme-configured border color
        drawList.AddRect(pos, endPos, ImGui.ColorConvertFloat4ToU32(theme.CompactProgressBarBorder), borderRadius, ImDrawFlags.RoundCornersAll, theme.FrameBorderSize);
        
        // Overlay text
        if (!string.IsNullOrEmpty(overlay))
        {
            var textSize = ImGui.CalcTextSize(overlay);
            var textPos = new Vector2(
                pos.X + (barSize.X - textSize.X) * 0.5f,
                pos.Y + (barSize.Y - textSize.Y) * 0.5f
            );
            drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(theme.TextPrimary), overlay);
        }
        
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + barSize.Y + ImGui.GetStyle().ItemSpacing.Y);
    }

    public static void DrawTransmissionBar(string label, float progress, string? overlay = null, bool? isUpload = null)
    {
        ImGui.AlignTextToFramePadding();
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGui.ColorConvertFloat4ToU32(SpheneCustomTheme.CurrentTheme.TextPrimary));
        ImGui.TextUnformatted(label);
        var theme = SpheneCustomTheme.CurrentTheme;
        var width = theme.AutoTransmissionBarWidth ? Math.Max(50.0f, ImGui.GetContentRegionAvail().X) : theme.CompactTransmissionBarWidth;
        var height = theme.SeparateTransmissionBarStyles && isUpload.HasValue
            ? (isUpload.Value ? theme.UploadTransmissionBarHeight : theme.DownloadTransmissionBarHeight)
            : theme.CompactTransmissionBarHeight;
        var barSize = new Vector2(width, height);
        DrawTransmissionBar(progress, barSize, overlay, isUpload);
    }

    public static void DrawTransmissionBar(float progress, Vector2 size, string? overlay = null, bool? isUpload = null)
    {
        var theme = SpheneCustomTheme.CurrentTheme;
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var endPos = new Vector2(pos.X + size.X, pos.Y + size.Y);
        var borderRadius = theme.SeparateTransmissionBarStyles && isUpload.HasValue
            ? (isUpload.Value ? theme.UploadTransmissionBarRounding : theme.DownloadTransmissionBarRounding)
            : theme.TransmissionBarRounding;

        var bg = theme.SeparateTransmissionBarStyles && isUpload.HasValue
            ? (isUpload.Value ? theme.UploadTransmissionBarBackground : theme.DownloadTransmissionBarBackground)
            : theme.CompactTransmissionBarBackground;
        drawList.AddRectFilled(pos, endPos, ImGui.ColorConvertFloat4ToU32(bg), borderRadius);

        if (progress > 0)
        {
            var progressEnd = new Vector2(pos.X + (size.X * progress), pos.Y + size.Y);
            if (theme.SeparateTransmissionBarStyles && isUpload.HasValue)
            {
                if (isUpload.Value)
                {
                    if (theme.TransmissionUseGradient)
                    {
                        DrawRoundedHorizontalGradient(drawList, pos, progressEnd, borderRadius, theme.UploadTransmissionGradientStart, theme.UploadTransmissionGradientEnd);
                    }
                    else
                    {
                        drawList.AddRectFilled(pos, progressEnd, ImGui.ColorConvertFloat4ToU32(theme.UploadTransmissionBarForeground), borderRadius);
                    }
                }
                else
                {
                    if (theme.TransmissionUseGradient)
                    {
                        DrawRoundedHorizontalGradient(drawList, pos, progressEnd, borderRadius, theme.DownloadTransmissionGradientStart, theme.DownloadTransmissionGradientEnd);
                    }
                    else
                    {
                        drawList.AddRectFilled(pos, progressEnd, ImGui.ColorConvertFloat4ToU32(theme.DownloadTransmissionBarForeground), borderRadius);
                    }
                }
            }
            else
            {
                if (theme.TransmissionUseGradient)
                {
                    DrawRoundedHorizontalGradient(drawList, pos, progressEnd, borderRadius, theme.TransmissionGradientStart, theme.TransmissionGradientEnd);
                }
                else
                {
                    drawList.AddRectFilled(pos, progressEnd, ImGui.ColorConvertFloat4ToU32(theme.CompactTransmissionBarForeground), borderRadius);
                }
            }
        }

        var border = theme.SeparateTransmissionBarStyles && isUpload.HasValue
            ? (isUpload.Value ? theme.UploadTransmissionBarBorder : theme.DownloadTransmissionBarBorder)
            : theme.CompactTransmissionBarBorder;
        drawList.AddRect(pos, endPos, ImGui.ColorConvertFloat4ToU32(border), borderRadius, ImDrawFlags.RoundCornersAll, theme.FrameBorderSize);

        if (!string.IsNullOrEmpty(overlay))
        {
            var textSize = ImGui.CalcTextSize(overlay);
            var textPos = new Vector2(
                pos.X + (size.X - textSize.X) * 0.5f,
                pos.Y + (size.Y - textSize.Y) * 0.5f
            );
            drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(theme.TextPrimary), overlay);
        }

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + size.Y + ImGui.GetStyle().ItemSpacing.Y);
    }

    private static void DrawRoundedHorizontalGradient(ImDrawListPtr drawList, Vector2 a, Vector2 b, float rounding, Vector4 startColor, Vector4 endColor)
    {
        var width = b.X - a.X;
        if (width <= 0) return;
        var slices = Math.Clamp((int)(width / 8.0f), 8, 64);
        var step = width / slices;
        for (int i = 0; i < slices; i++)
        {
            var x0 = a.X + step * i;
            var x1 = i == slices - 1 ? b.X : a.X + step * (i + 1);
            var t = (float)i / (float)(slices - 1);
            var c = Vector4.Lerp(startColor, endColor, t);
            var flags = ImDrawFlags.None;
            var r = 0.0f;
            if (i == 0)
            {
                flags = ImDrawFlags.RoundCornersLeft;
                r = rounding;
            }
            else if (i == slices - 1)
            {
                flags = ImDrawFlags.RoundCornersRight;
                r = rounding;
            }
            drawList.AddRectFilled(new Vector2(x0, a.Y), new Vector2(x1, b.Y), ImGui.ColorConvertFloat4ToU32(c), r, flags);
        }
    }

    [GeneratedRegex(@"^(?:[a-zA-Z]:\\[\w\s\-\\]+?|\/(?:[\w\s\-\/])+?)$", RegexOptions.ECMAScript, 5000)]
    private static partial Regex PathRegex();

    private static void FontText(string text, IFontHandle font, Vector4? color = null)
    {
        FontText(text, font, color == null ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(color.Value));
    }

    private static void FontText(string text, IFontHandle font, uint color)
    {
        using var pushedFont = font.Push();
        using var pushedColor = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
    }


    private bool IconTextButtonInternal(FontAwesomeIcon icon, string text, Vector4? defaultColor = null, float? width = null, 
        Vector4? hoveredColor = null, Vector4? activeColor = null, string? styleKey = null)
    {
        int num = 0;
        int vars = 0;
        if (defaultColor.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, defaultColor.Value);
            num++;
        }
        if (hoveredColor.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoveredColor.Value);
            num++;
        }
        if (activeColor.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, activeColor.Value);
            num++;
        }
        if (!string.IsNullOrEmpty(styleKey))
        {
            var theme = SpheneCustomTheme.CurrentTheme;
            if (theme.ButtonStyles.TryGetValue(styleKey, out var overrideStyle))
            {
                if (overrideStyle.Button.HasValue) { ImGui.PushStyleColor(ImGuiCol.Button, overrideStyle.Button.Value); num++; }
                if (overrideStyle.ButtonHovered.HasValue) { ImGui.PushStyleColor(ImGuiCol.ButtonHovered, overrideStyle.ButtonHovered.Value); num++; }
                if (overrideStyle.ButtonActive.HasValue) { ImGui.PushStyleColor(ImGuiCol.ButtonActive, overrideStyle.ButtonActive.Value); num++; }
                if (overrideStyle.Text.HasValue) { ImGui.PushStyleColor(ImGuiCol.Text, overrideStyle.Text.Value); num++; }
                if (overrideStyle.Border.HasValue) { ImGui.PushStyleColor(ImGuiCol.Border, overrideStyle.Border.Value); num++; }
                else { ImGui.PushStyleColor(ImGuiCol.Border, theme.Border); num++; }
                if (overrideStyle.BorderSize.HasValue) { ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, overrideStyle.BorderSize.Value); vars++; }
                else { ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f); vars++; }
            }
        }

        ImGui.PushID(text);
        Vector2 vector;
        using (IconFont.Push())
            vector = ImGui.CalcTextSize(icon.ToIconString());
        Vector2 vector2 = ImGui.CalcTextSize(text);
        ImDrawListPtr windowDrawList = ImGui.GetWindowDrawList();
        Vector2 cursorScreenPos = ImGui.GetCursorScreenPos();
        float num2 = 3f * ImGuiHelpers.GlobalScale;
        float x = width ?? vector.X + vector2.X + ImGui.GetStyle().FramePadding.X * 2f + num2;
        float frameHeight = ImGui.GetFrameHeight();
        if (!string.IsNullOrEmpty(styleKey))
        {
            var theme = SpheneCustomTheme.CurrentTheme;
            if (theme.ButtonStyles.TryGetValue(styleKey, out var overrideStyle))
            {
                x += overrideStyle.WidthDelta;
                frameHeight += overrideStyle.HeightDelta;
            }
        }
        bool result = ImGui.Button(string.Empty, new Vector2(x, frameHeight));
        Vector2 pos = new Vector2(cursorScreenPos.X + ImGui.GetStyle().FramePadding.X, cursorScreenPos.Y + ImGui.GetStyle().FramePadding.Y);
        using (IconFont.Push())
        {
            if (!string.IsNullOrEmpty(styleKey))
            {
                var theme = SpheneCustomTheme.CurrentTheme;
                if (theme.ButtonStyles.TryGetValue(styleKey, out var overrideStyle))
                {
                    pos += overrideStyle.IconOffset;
                }
            }
            uint iconColorU32 = ImGui.GetColorU32(ImGuiCol.Text);
            if (!string.IsNullOrEmpty(styleKey))
            {
                var theme = SpheneCustomTheme.CurrentTheme;
                if (theme.ButtonStyles.TryGetValue(styleKey, out var overrideStyle) && overrideStyle.Icon.HasValue)
                {
                    iconColorU32 = ImGui.GetColorU32(overrideStyle.Icon.Value);
                }
            }
            windowDrawList.AddText(pos, iconColorU32, icon.ToIconString());
        }
        Vector2 pos2 = new Vector2(pos.X + vector.X + num2, cursorScreenPos.Y + ImGui.GetStyle().FramePadding.Y);
        uint labelColorU32 = ImGui.GetColorU32(ImGuiCol.Text);
        if (!string.IsNullOrEmpty(styleKey))
        {
            var theme = SpheneCustomTheme.CurrentTheme;
            if (theme.ButtonStyles.TryGetValue(styleKey, out var overrideStyle) && overrideStyle.Text.HasValue)
            {
                labelColorU32 = ImGui.GetColorU32(overrideStyle.Text.Value);
            }
        }
        windowDrawList.AddText(pos2, labelColorU32, text);
        ImGui.PopID();
        if (vars > 0)
        {
            ImGui.PopStyleVar(vars);
        }
        if (num > 0)
        {
            ImGui.PopStyleColor(num);
        }

        return result;
    }

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
