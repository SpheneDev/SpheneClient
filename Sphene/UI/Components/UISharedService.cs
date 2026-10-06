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
    private static readonly ConcurrentDictionary<string, string[]> _tooltipSegmentsCache = new(StringComparer.Ordinal);
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
                    tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansCjkMedium, new()
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
                if (text.IndexOf(TooltipSeparator, StringComparison.Ordinal) >= 0)
                {
                    var splitText = _tooltipSegmentsCache.GetOrAdd(text,
                        t => t.Split(TooltipSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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

    /// <summary>Plain text in the themed text color — replaces raw ImGui.Text/TextUnformatted.</summary>
    public static void Text(string text) => ImGui.TextUnformatted(text);

    /// <summary>One themed vertical gap — replaces raw ImGui.Spacing().</summary>
    public static void DrawSpacing() => ImGui.Spacing();

    /// <summary>
    /// Thin horizontal divider spanning the bounded content width, drawn in the
    /// design border color — replaces raw ImGui.Separator() inside settings
    /// content so dividers follow the same token everywhere.
    /// </summary>
    public static void DrawSeparator()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var cursor = ImGui.GetCursorScreenPos();
        var width = BoundedContentAvailX();
        var thickness = MathF.Max(1f, scale);
        var gap = SpheneDesign.Gap * 0.5f;
        var y = cursor.Y + gap;
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(cursor.X, y), new Vector2(cursor.X + width, y),
            ImGui.ColorConvertFloat4ToU32(SpheneDesign.Border with { W = 0.35f }), thickness);
        ImGui.Dummy(new Vector2(width, gap * 2f + thickness));
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
        var monitoringActive = _cacheMonitor.SpheneWatcher != null;
        var (_, cacheBrowsePressed) = DrawInputTextWithButtons("Storage Folder##cache",
            ref cacheDirectory, 255, ImGuiInputTextFlags.ReadOnly,
            buttons: new InputButtonSpec(FontAwesomeIcon.Folder,
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
        DrawHelpText("Folder where Sphene stores all synced mod files. Pick an empty folder close to the drive root.");

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
        if (DrawSliderFloat("Maximum Storage Size in GiB", ref maxCacheSize, 1f, 200f, "%.2f GiB"))
        {
            _configService.Current.MaxLocalCacheInGiB = maxCacheSize;
            _configService.Save();
        }
        DrawHelpText("The storage is automatically governed by Sphene. It will clear itself automatically once it reaches the set capacity by removing the oldest unused files. You typically do not need to clear it yourself.");
    }

    public void DrawPenumbraModDownloadFolderSetting()
    {
        var downloadFolder = _configService.Current.PenumbraModDownloadFolder;
        var caps = string.IsNullOrEmpty(downloadFolder)
            ? new[] { new InputButtonSpec(FontAwesomeIcon.Folder, "Pick the mod download folder.") }
            : new[]
            {
                new InputButtonSpec(FontAwesomeIcon.Folder, "Pick the mod download folder."),
                new InputButtonSpec(FontAwesomeIcon.Trash, "Reset to default (use Cache folder)"),
            };
        var (_, downloadPressed) = DrawInputTextWithButtons("Mod Download Folder##download",
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
        DrawHelpText("Optional: Select a folder where downloaded mod files (PMP) will be stored. If left empty, the default cache folder will be used.");

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
    }

    public T? DrawCombo<T>(string comboName, IEnumerable<T> comboItems, Func<T?, string> toName,
        Action<T?>? onSelected = null, T? initialSelectedItem = default, float trailingWidth = 0f,
        float preferredWidth = -1f)
    {
        if (!comboItems.Any()) return default;

        if (!_selectedComboItems.TryGetValue(comboName, out var selectedItem) && selectedItem == null)
        {
            selectedItem = initialSelectedItem;
            _selectedComboItems[comboName] = selectedItem;
        }

        var autoDock = preferredWidth <= 0f;
        if (preferredWidth <= 0f)
        {
            preferredWidth = ComboFitWidth(comboItems.Select(toName));
        }
        var comboId = PrepareLabeledControl(comboName, trailingWidth, preferredWidth,
            dockRight: autoDock);

        using var style = PushValueControlStyle();
        if (ImGui.BeginCombo(comboId, selectedItem == null ? "Unset Value" : toName((T?)selectedItem)))
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

        SpheneDesignLint.CheckItemWithinBound(comboName);
        return (T?)_selectedComboItems[comboName];
    }

    /// <summary>
    /// Standard compact width for numeric/small value controls
    /// (InputInt, DragFloat). Sized to sit right after their row label.
    /// </summary>
    public static float SettingRowCompactWidth => 140f * ImGuiHelpers.GlobalScale;

    /// <summary>
    /// Prepares a settings row for a labeled value control: a visible label
    /// renders grey in the left column and the control docks to the real
    /// content bound (card edge inside card regions, the window content edge
    /// otherwise). Standard-tier controls always dock to the bound; only
    /// controls with an explicit <paramref name="preferredWidth"/> narrower
    /// than ~45% of the row sit right after their label so no dead space
    /// opens up. Returns the id to pass
    /// to the ImGui widget. Hidden labels ("##id") pass through unchanged;
    /// mid-line calls only clamp the control width so a trailing label still
    /// fits inside the bound. <paramref name="preferredWidth"/> replaces
    /// the pushed item width when callers want a specific control size.
    /// </summary>
    private static string PrepareLabeledControl(string label, float trailingWidth = 0f, float preferredWidth = -1f,
        bool dockRight = false)
        => PrepareLabeledControl(label, trailingWidth, preferredWidth, dockRight, out _);

    /// <summary>
    /// Same as <see cref="PrepareLabeledControl(string, float, float, bool)"/>
    /// but also reports the width applied via SetNextItemWidth — composite
    /// controls (input + attached button) need it to subdivide their frame.
    /// </summary>
    private static string PrepareLabeledControl(string label, float trailingWidth, float preferredWidth,
        bool dockRight, out float controlWidth)
    {
        controlWidth = 0f;
        var labelSplit = label.IndexOf("##", StringComparison.Ordinal);
        var text = labelSplit < 0 ? label : label[..labelSplit];
        if (text.Length == 0)
        {
            var hiddenPos = ImGui.GetCursorScreenPos();
            var hiddenBound = SpheneWindowChrome.CurrentCardContentRightBound;
            if (hiddenBound > 0f)
            {
                // The help column reserve also applies to hidden-label
                // controls so they dock flush with labeled rows.
                hiddenBound -= MathF.Max(trailingWidth, SpheneDesign.TrailingIconReserve);
            }
            if (preferredWidth <= 0f && dockRight && hiddenBound > 0f)
            {
                // No explicit width: dock a standard-tier control to the
                // shared edge like the visible-label rows do.
                var autoWidth = MathF.Max(60f * ImGuiHelpers.GlobalScale,
                    MathF.Min(ImGui.CalcItemWidth(), (hiddenBound - hiddenPos.X) * 0.5f));
                if (hiddenBound - autoWidth > hiddenPos.X)
                {
                    ImGui.SetCursorScreenPos(new Vector2(hiddenBound - autoWidth, hiddenPos.Y));
                }
                ImGui.SetNextItemWidth(autoWidth);
                controlWidth = autoWidth;
            }
            else if (preferredWidth > 0f)
            {
                var width = preferredWidth;
                if (hiddenBound > 0f)
                {
                    width = MathF.Min(width, MathF.Max(0f, hiddenBound - hiddenPos.X));
                    if (dockRight && hiddenBound - width > hiddenPos.X)
                    {
                        ImGui.SetCursorScreenPos(new Vector2(hiddenBound - width, hiddenPos.Y));
                    }
                }
                ImGui.SetNextItemWidth(width);
                controlWidth = width;
            }
            return label;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var cursor = ImGui.GetCursorScreenPos();
        var rightBound = SpheneWindowChrome.CurrentCardContentRightBound;
        var leftBound = SpheneWindowChrome.CurrentCardContentLeftBound;
        if (rightBound <= 0f)
        {
            rightBound = cursor.X + ImGui.GetContentRegionAvail().X;
        }
        // Reserve the same help-icon column the toggles use so every value
        // control ends flush with the switch edge instead of overshooting it.
        rightBound -= MathF.Max(trailingWidth, SpheneDesign.TrailingIconReserve);
        if (leftBound <= 0f)
        {
            leftBound = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMin().X;
        }
        var avail = MathF.Max(0f, rightBound - cursor.X);
        var labelWidth = ImGui.CalcTextSize(text).X;
        // Standard tier never exceeds half the row so labels stay readable and
        // controls do not sprawl across the card.
        var desiredWidth = preferredWidth > 0f
            ? preferredWidth
            : MathF.Min(ImGui.CalcItemWidth(), avail * 0.5f);
        controlWidth = MathF.Max(60f * scale,
            MathF.Min(desiredWidth, avail - labelWidth - ImGui.GetStyle().ItemInnerSpacing.X));

        // Line start also covers indented sub-rows: when the previous item sits
        // on a line above the cursor nothing was drawn on this row yet.
        var isLineStart = cursor.X <= leftBound + 1f
            || ImGui.GetItemRectMin().Y < cursor.Y - 1f;
        if (isLineStart)
        {
            ImGui.AlignTextToFramePadding();
            var lineY = ImGui.GetCursorScreenPos().Y;
            ColorText(text, ImGuiColors.DalamudGrey);
            // Standard-tier controls always dock to the right bound. Only
            // compact controls with an explicit preferred width sit right
            // after their label so no dead space opens up between them.
            var controlX = dockRight || preferredWidth <= 0f || controlWidth >= avail * 0.45f
                ? rightBound - controlWidth
                : cursor.X + labelWidth + ImGui.GetStyle().ItemInnerSpacing.X * 2f;
            ImGui.SetCursorScreenPos(new Vector2(controlX, lineY));
            ImGui.SetNextItemWidth(controlWidth);
            return labelSplit < 0 ? "##" + label : label[labelSplit..];
        }

        // Mid-line: keep the original label so its trailing text still renders
        // next to the control inside the reserved space.
        ImGui.SetNextItemWidth(controlWidth);
        return label;
    }

    /// <summary>Content-fit width for a combo: longest item text plus frame and arrow.</summary>
    private static float ComboFitWidth(IEnumerable<string> itemTexts)
    {
        var fitContent = 0f;
        foreach (var itemText in itemTexts)
        {
            fitContent = MathF.Max(fitContent, ImGui.CalcTextSize(itemText).X);
        }
        var minWidth = SettingRowCompactWidth;
        var maxWidth = MathF.Max(minWidth, ImGui.CalcItemWidth());
        return Math.Clamp(
            fitContent + ImGui.GetStyle().FramePadding.X * 2f + ImGui.GetFrameHeight()
                + ImGui.GetStyle().ItemInnerSpacing.X,
            minWidth, maxWidth);
    }

    private static readonly ConcurrentDictionary<uint, float> _toggleAnimStates = new();
    private const float ToggleAnimDuration = 0.15f;

    /// <summary>Advances the slide-toggle animation (0 = off, 1 = on) for the given item id.</summary>
    public static float GetToggleAnimValue(uint animKey, bool isOn)
    {
        var target = isOn ? 1f : 0f;
        var t = _toggleAnimStates.GetOrAdd(animKey, target);
        if (MathF.Abs(t - target) > 0.001f)
        {
            t = Math.Clamp(t + (target > t ? 1f : -1f) * ImGui.GetIO().DeltaTime / ToggleAnimDuration, 0f, 1f);
            _toggleAnimStates[animKey] = t;
        }
        return t;
    }

    /// <summary>Draws the slide-toggle track and knob into the given rect.</summary>
    public static void DrawSwitchVisual(ImDrawListPtr drawList, Vector2 trackMin, Vector2 trackMax, float animT, float alpha = 1f)
    {
        var trackHeight = trackMax.Y - trackMin.Y;
        var trackWidth = trackMax.X - trackMin.X;
        var offTrack = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.FrameBg));
        var trackColor = Vector4.Lerp(offTrack, SpheneCustomTheme.Colors.AccentBlue, animT);
        trackColor.W *= alpha;
        drawList.AddRectFilled(trackMin, trackMax, ImGui.ColorConvertFloat4ToU32(trackColor),
            trackHeight * 0.5f, ImDrawFlags.RoundCornersAll);
        var knobRadius = trackHeight * 0.5f - 2f * ImGuiHelpers.GlobalScale;
        var knobX = trackMin.X + trackHeight * 0.5f + animT * (trackWidth - trackHeight);
        drawList.AddCircleFilled(new Vector2(knobX, trackMin.Y + trackHeight * 0.5f), knobRadius,
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.95f * alpha)));
    }

    private sealed class MultiPopScope(int styleVars, int styleColors) : IDisposable
    {
        public void Dispose()
        {
            if (styleVars > 0)
            {
                ImGui.PopStyleVar(styleVars);
            }
            if (styleColors > 0)
            {
                ImGui.PopStyleColor(styleColors);
            }
        }
    }

    /// <summary>
    /// Pushes the modern value-control styling shared by the <c>Draw*</c> row
    /// wrappers: rounded frames, a knob-shaped accent grab, a subtle outline and
    /// accent-tinted hover/active states, plus rounded combo popups. Matches the
    /// slide-switch aesthetic so all value controls read as one control family.
    /// </summary>
    public static IDisposable PushValueControlStyle()
    {
        var frameHeight = ImGui.GetFrameHeight();
        var accent = SpheneCustomTheme.Colors.AccentBlue;
        var frameBg = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.FrameBg));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, SpheneDesign.ControlRadius);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, SpheneDesign.ControlRadius);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, frameHeight * SpheneDesign.GrabMinSizeFactor);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, SpheneDesign.ControlBorderSize);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, SpheneDesign.PopupRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, SpheneDesign.PopupBorderSize);
        var border = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.Border));
        ImGui.PushStyleColor(ImGuiCol.Border, border with { W = MathF.Max(border.W, 0.35f) });
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, accent with { W = 0.9f });
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, accent);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, accent);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Lerp(frameBg, accent, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Lerp(frameBg, accent, 0.2f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, accent with { W = 0.25f });
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, accent with { W = 0.4f });
        return new MultiPopScope(6, 8);
    }

    /// <summary>
    /// Drop-in replacement for <c>ImGui.Checkbox</c> on settings rows: draws the
    /// label on the left and an animated slide toggle docked to the row's right
    /// bound. The whole row is clickable and tinted while hovered. A trailing
    /// icon column is reserved so a following <see cref="DrawHelpText"/> marker
    /// still fits on the same line. Returns true when the value changed.
    /// </summary>
    public static bool DrawToggle(string label, ref bool value, bool indented = false, float trailingWidth = 0f,
        string? menuPopupId = null)
    {
        var idSplit = label.IndexOf("##", StringComparison.Ordinal);
        var text = (idSplit < 0 ? label : label[..idSplit]);
        var scale = ImGuiHelpers.GlobalScale;
        var cursor = ImGui.GetCursorScreenPos();
        var rightBound = SpheneWindowChrome.CurrentCardContentRightBound;
        if (rightBound <= 0f)
        {
            rightBound = cursor.X + ImGui.GetContentRegionAvail().X;
        }
        var helpReserve = MathF.Max(SpheneDesign.TrailingIconReserve, trailingWidth);
        var frameHeight = ImGui.GetFrameHeight();
        var rowEndX = rightBound - helpReserve;

        var trackHeight = MathF.Round(frameHeight * 0.7f);
        var trackWidth = trackHeight * 1.8f;
        var trackMin = new Vector2(rowEndX - trackWidth, cursor.Y + (frameHeight - trackHeight) * 0.5f);
        var trackMax = trackMin + new Vector2(trackWidth, trackHeight);

        var menuSize = menuPopupId != null ? frameHeight : 0f;
        var innerGap = ImGui.GetStyle().ItemInnerSpacing.X;
        var menuMin = new Vector2(trackMin.X - innerGap - menuSize, cursor.Y);
        var buttonEndX = menuPopupId != null ? menuMin.X - innerGap : rowEndX;
        var buttonWidth = MathF.Max(buttonEndX - cursor.X, frameHeight);

        var toggled = ImGui.InvisibleButton(label, new Vector2(buttonWidth, frameHeight));
        if (menuPopupId != null)
        {
            ImGui.SetItemAllowOverlap();
        }
        if (toggled)
        {
            value = !value;
        }
        var hovered = ImGui.IsItemHovered();
        var alpha = ImGui.GetStyle().Alpha;

        var animKey = ImGui.GetID(label);
        var t = GetToggleAnimValue(animKey, value);

        var drawList = ImGui.GetWindowDrawList();
        if (hovered)
        {
            var tint = SpheneCustomTheme.Colors.AccentBlue with { W = 0.07f * alpha };
            drawList.AddRectFilled(
                new Vector2(cursor.X - 2f * scale, cursor.Y),
                new Vector2(rightBound, cursor.Y + frameHeight),
                ImGui.ColorConvertFloat4ToU32(tint), 4f * scale, ImDrawFlags.RoundCornersAll);
        }

        if (menuPopupId != null)
        {
            ImGui.SetCursorScreenPos(menuMin);
            var menuClicked = ImGui.InvisibleButton(label + "##menu", new Vector2(menuSize, frameHeight));
            var menuHovered = ImGui.IsItemHovered();
            if (menuHovered)
            {
                var menuTint = SpheneCustomTheme.Colors.AccentBlue with { W = 0.16f * alpha };
                drawList.AddRectFilled(menuMin, menuMin + new Vector2(menuSize, frameHeight),
                    ImGui.ColorConvertFloat4ToU32(menuTint), 4f * scale, ImDrawFlags.RoundCornersAll);
            }
            var dotRadius = MathF.Max(1f, 1.5f * scale);
            var dotSpacing = menuSize * 0.22f;
            var dotX = menuMin.X + menuSize * 0.5f;
            var dotY = menuMin.Y + frameHeight * 0.5f;
            var dotColor = ImGui.GetColorU32(menuHovered ? ImGuiCol.Text : ImGuiCol.TextDisabled);
            for (var i = -1; i <= 1; i++)
            {
                drawList.AddCircleFilled(new Vector2(dotX, dotY + i * dotSpacing), dotRadius, dotColor);
            }
            if (menuClicked)
            {
                ImGui.OpenPopup(menuPopupId);
            }
            AttachToolTip("More options");
        }

        DrawSwitchVisual(drawList, trackMin, trackMax, t, alpha);

        if (text.Length > 0)
        {
            ImGui.SetCursorScreenPos(cursor);
            ImGui.AlignTextToFramePadding();
            ColorText(indented ? "  " + text : text,
                indented ? ImGuiColors.DalamudGrey3 : ImGuiColors.DalamudGrey);
        }

        SpheneDesignLint.CheckItemWithinBound(label);
        return toggled;
    }

    /// <summary>SliderInt with the settings-row convention (label left, control sized to the bound).</summary>
    public static bool DrawSliderInt(string label, ref int value, int min, int max, string? format = null,
        float trailingWidth = 0f, float preferredWidth = -1f)
    {
        using var style = PushValueControlStyle();
        var changed = ImGui.SliderInt(PrepareLabeledControl(label, trailingWidth, preferredWidth), ref value, min, max, format ?? "%d");
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
    }

    /// <summary>SliderFloat with the settings-row convention (label left, control sized to the bound).</summary>
    public static bool DrawSliderFloat(string label, ref float value, float min, float max, string? format = null,
        float trailingWidth = 0f, float preferredWidth = -1f)
    {
        using var style = PushValueControlStyle();
        var changed = ImGui.SliderFloat(PrepareLabeledControl(label, trailingWidth, preferredWidth), ref value, min, max, format ?? "%.3f");
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
    }

    /// <summary>SliderFloat2 with the settings-row convention (label left, control sized to the bound).</summary>
    public static bool DrawSliderFloat2(string label, ref Vector2 value, float min, float max, string? format = null,
        float trailingWidth = 0f, float preferredWidth = -1f)
    {
        using var style = PushValueControlStyle();
        var changed = ImGui.SliderFloat2(PrepareLabeledControl(label, trailingWidth, preferredWidth), ref value, min, max, format ?? "%.3f");
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
    }

    /// <summary>DragFloat with the settings-row convention (label left, compact numeric tier).</summary>
    public static bool DrawDragFloat(string label, ref float value, float speed, float min, float max,
        string? format = null, float trailingWidth = 0f, float preferredWidth = -1f)
    {
        using var style = PushValueControlStyle();
        var changed = ImGui.DragFloat(PrepareLabeledControl(label, trailingWidth,
            preferredWidth > 0f ? preferredWidth : SettingRowCompactWidth), ref value, speed, min, max, format ?? "%.3f");
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
    }

    /// <summary>DragFloat2 with the settings-row convention (label left, compact numeric tier).</summary>
    public static bool DrawDragFloat2(string label, ref Vector2 value, float speed, float min, float max,
        string? format = null, float trailingWidth = 0f, float preferredWidth = -1f)
    {
        using var style = PushValueControlStyle();
        var changed = ImGui.DragFloat2(PrepareLabeledControl(label, trailingWidth,
            preferredWidth > 0f ? preferredWidth : SettingRowCompactWidth), ref value, speed, min, max, format ?? "%.3f");
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
    }

    /// <summary>InputInt with the settings-row convention (label left, compact numeric tier).</summary>
    public static bool DrawInputInt(string label, ref int value, int step = 1, int stepFast = 100,
        float trailingWidth = 0f, float preferredWidth = -1f)
    {
        using var style = PushValueControlStyle();
        var changed = ImGui.InputInt(PrepareLabeledControl(label, trailingWidth,
            preferredWidth > 0f ? preferredWidth : SettingRowCompactWidth), ref value, step, stepFast);
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
    }

    /// <summary>InputText with the settings-row convention (label left, control sized to the bound).</summary>
    public static bool DrawInputText(string label, ref string value, int maxLength,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, float trailingWidth = 0f,
        float preferredWidth = -1f, bool dockRight = false)
    {
        using var style = PushValueControlStyle();
        var changed = ImGui.InputText(PrepareLabeledControl(label, trailingWidth, preferredWidth, dockRight), ref value, maxLength, flags);
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
    }

    /// <summary>InputTextWithHint with the settings-row convention (label left, control sized to the bound).</summary>
    public static bool DrawInputTextWithHint(string label, string hint, ref string value, int maxLength,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, float trailingWidth = 0f, float preferredWidth = -1f)
    {
        using var style = PushValueControlStyle();
        var changed = ImGui.InputTextWithHint(PrepareLabeledControl(label, trailingWidth, preferredWidth), hint, ref value, maxLength, flags);
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
    }

    /// <summary>InputTextMultiline with the settings-row convention (label left, control sized to the bound).</summary>
    public static bool DrawInputTextMultiline(string label, ref string value, int maxLength, Vector2 size,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, float trailingWidth = 0f, float preferredWidth = -1f)
    {
        using var style = PushValueControlStyle();
        var changed = ImGui.InputTextMultiline(PrepareLabeledControl(label, trailingWidth, preferredWidth), ref value, maxLength, size, flags);
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
    }

    /// <summary>
    /// One action cap fused to the right edge of an input — an icon glyph, or
    /// <paramref name="Text"/> for labeled caps (e.g. "Browse").
    /// </summary>
    public readonly record struct InputButtonSpec(FontAwesomeIcon Icon = FontAwesomeIcon.None,
        string? Tooltip = null, bool Enabled = true, string? Label = null);

    /// <summary>
    /// Text input with action buttons fused into its right edge: one rounded
    /// frame encloses the field and every cap (input-group look). The composite
    /// keeps the control width a plain <see cref="DrawInputText"/> would occupy,
    /// so the buttons live inside the field's footprint instead of extending
    /// the row. Returns whether the text changed and which cap was pressed
    /// (-1 = none).
    /// </summary>
    public (bool Changed, int PressedButton) DrawInputTextWithButtons(
        string label, ref string value, int maxLength,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None,
        float trailingWidth = 0f, float preferredWidth = -1f,
        params InputButtonSpec[] buttons)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var id = PrepareLabeledControl(label, trailingWidth, preferredWidth,
            dockRight: true, out var controlWidth);
        var height = ImGui.GetFrameHeight();
        var framePadX = ImGui.GetStyle().FramePadding.X;

        var capWidths = new float[buttons.Length];
        var capsWidth = 0f;
        for (var i = 0; i < buttons.Length; i++)
        {
            var b = buttons[i];
            capWidths[i] = b.Label is { Length: > 0 } t
                ? ImGui.CalcTextSize(t).X + framePadX * 2f
                : height;
            capsWidth += capWidths[i];
        }
        if (controlWidth <= 0f)
        {
            controlWidth = MathF.Min(ImGui.CalcItemWidth(), BoundedContentAvailX());
        }
        var fieldWidth = MathF.Max(60f * scale, controlWidth - capsWidth);

        var pos = ImGui.GetCursorScreenPos();
        var radius = SpheneDesign.ControlRadius;
        var drawList = ImGui.GetWindowDrawList();
        var size = new Vector2(controlWidth, height);

        drawList.AddRectFilled(pos, pos + size,
            ImGui.ColorConvertFloat4ToU32(SpheneDesign.FrameBg), radius);

        // The field renders with a stripped frame so the composite frame
        // stays the only visible border.
        ImGui.SetNextItemWidth(fieldWidth);
        bool fieldChanged;
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero)
            .Push(ImGuiCol.FrameBgHovered, Vector4.Zero)
            .Push(ImGuiCol.FrameBgActive, Vector4.Zero))
        {
            fieldChanged = ImGui.InputText(id, ref value, maxLength, flags);
        }
        var fieldActive = ImGui.IsItemActive();

        var pressedButton = -1;
        var anyCapHovered = false;
        var cursorX = pos.X + fieldWidth;
        for (var i = 0; i < buttons.Length; i++)
        {
            var b = buttons[i];
            var capPos = new Vector2(cursorX, pos.Y);
            var capSize = new Vector2(capWidths[i], height);
            var isLast = i == buttons.Length - 1;
            cursorX += capWidths[i];

            bool capPressed;
            ImGui.SetCursorScreenPos(capPos);
            if (b.Enabled)
            {
                capPressed = ImGui.InvisibleButton($"##cap{i}{id}", capSize);
            }
            else
            {
                ImGui.Dummy(capSize);
                capPressed = false;
            }
            var capHovered = ImGui.IsItemHovered();
            if (capPressed)
            {
                pressedButton = i;
            }
            if (b.Tooltip != null)
            {
                AttachToolTip(b.Tooltip);
            }
            anyCapHovered |= capHovered && b.Enabled;

            var capBg = !b.Enabled
                ? Vector4.Zero
                : capHovered
                    ? SpheneDesign.FrameBgHover
                    : new Vector4(1f, 1f, 1f, 0.05f);
            if (capBg.W > 0f)
            {
                drawList.AddRectFilled(capPos + new Vector2(0f, 1f),
                    capPos + capSize - new Vector2(0f, 1f),
                    ImGui.ColorConvertFloat4ToU32(capBg), isLast ? radius : 0f,
                    isLast ? ImDrawFlags.RoundCornersRight : ImDrawFlags.None);
            }
            drawList.AddLine(new Vector2(capPos.X, pos.Y + 3f * scale),
                new Vector2(capPos.X, pos.Y + height - 3f * scale),
                ImGui.ColorConvertFloat4ToU32(SpheneDesign.Border with { W = 0.3f }),
                MathF.Max(1f, scale));

            var glyphColor = !b.Enabled
                ? SpheneDesign.TextMuted with { W = 0.45f }
                : capHovered ? SpheneDesign.Accent : SpheneDesign.TextSecondary;
            var glyphColorU32 = ImGui.ColorConvertFloat4ToU32(glyphColor);
            if (b.Label is { Length: > 0 } capText)
            {
                var gs = ImGui.CalcTextSize(capText);
                drawList.AddText(capPos + (capSize - gs) * 0.5f, glyphColorU32, capText);
            }
            else
            {
                var glyph = b.Icon.ToIconString();
                using (IconFont.Push())
                {
                    var gs = ImGui.CalcTextSize(glyph);
                    drawList.AddText(capPos + (capSize - gs) * 0.5f, glyphColorU32, glyph);
                }
            }
        }

        var borderColor = fieldActive || anyCapHovered
            ? SpheneDesign.Accent with { W = 0.75f }
            : SpheneDesign.Border with { W = 0.28f };
        drawList.AddRect(pos, pos + size, ImGui.ColorConvertFloat4ToU32(borderColor),
            radius, ImDrawFlags.None, MathF.Max(1f, scale));

        SpheneDesignLint.CheckItemWithinBound(label);
        return (fieldChanged, pressedButton);
    }

    /// <summary>Combo (index) with the settings-row convention (label left, control sized to the bound).</summary>
    public static bool DrawLabeledCombo(string label, ref int currentItem, string[] items, int itemsCount,
        float trailingWidth = 0f, float preferredWidth = -1f)
    {
        using var style = PushValueControlStyle();
        var changed = ImGui.Combo(PrepareLabeledControl(label, trailingWidth,
            preferredWidth > 0f ? preferredWidth : ComboFitWidth(items.Take(itemsCount)),
            dockRight: preferredWidth <= 0f), ref currentItem, items, itemsCount);
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
    }

    /// <summary>Combo (\0-separated items) with the settings-row convention (label left, control sized to the bound).</summary>
    public static bool DrawLabeledCombo(string label, ref int currentItem, string itemsSeparatedByZeros,
        int popupMaxHeightInItems = -1, float trailingWidth = 0f, float preferredWidth = -1f)
    {
        using var style = PushValueControlStyle();
        var changed = ImGui.Combo(PrepareLabeledControl(label, trailingWidth,
            preferredWidth > 0f ? preferredWidth : ComboFitWidth(itemsSeparatedByZeros.Split('\0', StringSplitOptions.RemoveEmptyEntries)),
            dockRight: preferredWidth <= 0f), ref currentItem, itemsSeparatedByZeros,
            popupMaxHeightInItems);
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
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

    /// <summary>
    /// Draws a ? help marker in the reserved right column of the current row
    /// (same line as the previous item when it fits, right-aligned on the next
    /// line otherwise) so all help icons share one flush column.
    /// </summary>
    public void DrawHelpText(string helpText)
    {
        var iconW = GetIconSize(FontAwesomeIcon.QuestionCircle).X;
        var bound = SpheneWindowChrome.CurrentCardContentRightBound;
        if (bound <= 0f)
        {
            bound = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        }
        var targetX = bound - iconW;
        var lastMax = ImGui.GetItemRectMax();
        if (targetX > lastMax.X + 1f)
        {
            // Pin to the reserved right column so every help icon sits flush
            // behind the row's control or switch.
            ImGui.SameLine(0f, targetX - lastMax.X);
        }
        else
        {
            SameLineIfFits(iconW);
        }
        // The glyph is painted through the draw list, centered inside the
        // row's frame box — IconText would render at the line's text base
        // offset, which differs per row type (toggle rows end in a text item,
        // composite rows in a cap button). A Dummy registers the frame box as
        // the item so hover and the tooltip cover exactly the glyph.
        var cursor = ImGui.GetCursorScreenPos();
        var frameH = SpheneDesign.ControlHeight;
        var glyph = FontAwesomeIcon.QuestionCircle.ToIconString();
        using (IconFont.Push())
        {
            var glyphSize = ImGui.CalcTextSize(glyph);
            ImGui.GetWindowDrawList().AddText(new Vector2(cursor.X,
                cursor.Y + (frameH - glyphSize.Y) * 0.5f),
                ImGui.GetColorU32(ImGuiCol.TextDisabled), glyph);
        }
        ImGui.Dummy(new Vector2(iconW, frameH));
        AttachToolTip(helpText);
    }

    /// <summary>
    /// Content width remaining up to the real bound (card edge inside card
    /// regions, window content edge otherwise). <c>GetContentRegionAvail</c>
    /// measures past card bounds inside card tables – use this instead.
    /// </summary>
    public static float BoundedContentAvailX()
    {
        var bound = SpheneWindowChrome.CurrentCardContentRightBound;
        if (bound > 0f)
        {
            return MathF.Max(0f, bound - ImGui.GetCursorScreenPos().X);
        }
        return ImGui.GetContentRegionAvail().X;
    }

    /// <summary>
    /// Remaining row width up to the shared control edge: the card bound minus
    /// the trailing icon column that labeled controls and toggles reserve,
    /// minus optional extra trailing content drawn via SameLine.
    /// </summary>
    public static float FillRowControlWidth(float extraTrailing = 0f)
    {
        return MathF.Max(0f, BoundedContentAvailX()
            - SpheneDesign.TrailingIconReserve - extraTrailing);
    }

    /// <summary>
    /// Moves the cursor so a hidden-label control of <paramref name="width"/>
    /// ends flush at the shared control edge (bound minus the trailing icon
    /// column and <paramref name="extraTrailing"/> for items that follow on
    /// the same row).
    /// </summary>
    public static void DockControlToRightEdge(float width, float extraTrailing = 0f)
    {
        var reserve = SpheneDesign.TrailingIconReserve + extraTrailing;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX()
            + MathF.Max(0f, BoundedContentAvailX() - reserve - width));
    }

    /// <summary>
    /// Color picker in the settings-row convention: grey label left, a right-
    /// docked <see cref="DrawColorSwatchButton"/> that opens a
    /// <see cref="SpheneWindowChrome.BeginStyledPopup"/> hosting ImGui's full
    /// ColorPicker4 — the picker popup gets the themed rounding/border instead
    /// of the raw ColorEdit4 chrome.
    /// </summary>
    public static bool DrawColorEdit(string label, ref Vector4 color, float trailingWidth = 0f)
    {
        var swatchSize = SpheneDesign.SwatchSize;
        var id = PrepareLabeledControl(label, trailingWidth, swatchSize, dockRight: true);
        var pickerPopupId = id + "_picker";
        if (DrawColorSwatchButton(id, color))
        {
            ImGui.OpenPopup(pickerPopupId);
        }
        AttachToolTip("Click to pick a color.");
        var changed = false;
        if (SpheneWindowChrome.BeginStyledPopup(pickerPopupId))
        {
            changed = ImGui.ColorPicker4("##picker" + id, ref color,
                ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoLabel);
            SpheneWindowChrome.EndStyledPopup();
        }
        SpheneDesignLint.CheckItemWithinBound(label);
        return changed;
    }

    /// <summary>
    /// Design-system push button: <see cref="PushValueControlStyle"/> rounding
    /// plus a shared hover/active treatment. <paramref name="danger"/> tints the
    /// button red for destructive actions. Use instead of raw ImGui.Button.
    /// </summary>
    public static bool DrawButton(string label, Vector2 size = default, bool danger = false)
    {
        using var style = PushValueControlStyle();
        using var tint = ImRaii.PushColor(ImGuiCol.Button,
                SpheneDesign.Danger with { W = 0.55f }, danger)
            .Push(ImGuiCol.ButtonHovered, SpheneDesign.Danger with { W = 0.8f }, danger)
            .Push(ImGuiCol.ButtonActive, SpheneDesign.Danger with { W = 0.95f }, danger);
        var clicked = ImGui.Button(label, size);
        SpheneDesignLint.CheckItemWithinBound(label);
        return clicked;
    }

    /// <summary>
    /// Square icon-only button in the swatch language — frame fill, border,
    /// centered FontAwesome glyph. Use for compact actions next to value
    /// controls; pair with <see cref="AttachToolTip"/> for the description.
    /// </summary>
    public static bool DrawIconButton(string id, FontAwesomeIcon icon,
        bool enabled = true, float? size = null)
    {
        var side = size ?? SpheneDesign.ControlHeight;
        var pos = ImGui.GetCursorScreenPos();
        bool clicked;
        using (ImRaii.Disabled(!enabled))
        {
            clicked = ImGui.InvisibleButton(id, new Vector2(side, side));
        }
        var hovered = enabled && ImGui.IsItemHovered();
        var pressed = enabled && ImGui.IsItemActive();
        var fill = pressed ? SpheneDesign.FrameBgActive
            : hovered ? SpheneDesign.FrameBgHover
            : SpheneDesign.FrameBg;
        var accent = SpheneDesign.Accent;
        var border = pressed || hovered
            ? accent with { W = SpheneDesign.SwatchBorderAlphaHover }
            : SpheneDesign.Border with { W = SpheneDesign.SwatchBorderAlpha };
        var radius = SpheneDesign.ControlRadius;
        var max = pos + new Vector2(side, side);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(fill), radius);
        drawList.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(border), radius,
            ImDrawFlags.RoundCornersAll, SpheneDesign.ControlBorderSize);
        var glyph = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var glyphSize = ImGui.CalcTextSize(glyph);
        var glyphColor = enabled ? SpheneDesign.TextPrimary : SpheneDesign.TextMuted;
        drawList.AddText(new Vector2(pos.X + (side - glyphSize.X) * 0.5f,
            pos.Y + (side - glyphSize.Y) * 0.5f),
            ImGui.ColorConvertFloat4ToU32(glyphColor), glyph);
        ImGui.PopFont();
        SpheneDesignLint.CheckItemWithinBound(id);
        return clicked;
    }

    /// <summary>
    /// Confirm/cancel pair split across the bounded content width — the modal
    /// dialog pattern. Returns which button was clicked.
    /// </summary>
    public static int DrawDialogButtons(string id, string confirmLabel, string cancelLabel,
        bool confirmDanger = false)
    {
        var spacing = SpheneDesign.Gap;
        var buttonWidth = (BoundedContentAvailX() - spacing) * 0.5f;
        var size = new Vector2(buttonWidth, 0f);
        var clicked = 0;
        if (DrawButton(confirmLabel + "##" + id, size, confirmDanger))
        {
            clicked = 1;
        }
        ImGui.SameLine();
        if (DrawButton(cancelLabel + "##" + id, size))
        {
            clicked = 2;
        }
        return clicked;
    }

    /// <summary>
    /// Compact pill toggle for inline groups (several toggles sharing one row
    /// via SameLine): accent fill + accent text while on, frame fill + muted
    /// text while off. Use instead of raw ImGui.Checkbox in grouped rows.
    /// </summary>
    public static bool DrawToggleChip(string label, ref bool value)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var accent = SpheneDesign.Accent;
        var idSplit = label.IndexOf("##", StringComparison.Ordinal);
        var text = idSplit < 0 ? label : label[..idSplit];
        var height = ImGui.GetFrameHeight();
        var width = ImGui.CalcTextSize(text).X + height * 0.9f;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(label, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        if (clicked)
        {
            value = !value;
        }
        var frameBg = SpheneDesign.FrameBg;
        var fill = value
            ? accent with { W = 0.85f }
            : hovered
                ? Vector4.Lerp(frameBg, accent, 0.15f)
                : frameBg;
        var border = value
            ? accent with { W = SpheneDesign.SwatchBorderAlphaSelected }
            : hovered
                ? accent with { W = SpheneDesign.SwatchBorderAlphaHover }
                : new Vector4(1f, 1f, 1f, SpheneDesign.SwatchBorderAlpha);
        var max = pos + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(fill), height * 0.5f);
        drawList.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(border), height * 0.5f,
            ImDrawFlags.None, 1f * scale);
        var textSize = ImGui.CalcTextSize(text);
        var textColor = value ? new Vector4(1f, 1f, 1f, 1f) : SpheneDesign.TextSecondary;
        drawList.AddText(new Vector2(pos.X + (width - textSize.X) * 0.5f,
            pos.Y + (height - textSize.Y) * 0.5f), ImGui.ColorConvertFloat4ToU32(textColor), text);
        SpheneDesignLint.CheckItemWithinBound(label);
        return clicked;
    }

    /// <summary>
    /// Selectable list row in the value-control language — use instead of raw
    /// ImGui.Selectable inside settings content.
    /// </summary>
    public static bool DrawSelectable(string label, bool selected,
        ImGuiSelectableFlags flags = ImGuiSelectableFlags.None, Vector2 size = default)
    {
        using var style = PushValueControlStyle();
        var clicked = ImGui.Selectable(label, selected, flags, size);
        SpheneDesignLint.CheckItemWithinBound(label);
        return clicked;
    }

    /// <summary>
    /// Collapsible sub-section header in the design language: chevron + label,
    /// accent text while hovered, hover row tint, open state persisted in the
    /// window storage. Replaces ImGui.CollapsingHeader — returns whether the
    /// section is open; draw the section content inside the if-block.
    /// </summary>
    public bool DrawCollapsibleSection(string label, bool defaultOpen = false)
    {
        var storage = ImGui.GetStateStorage();
        var storageId = ImGui.GetID("collapsible##" + label);
        var open = storage.GetInt(storageId, defaultOpen ? 1 : 0) != 0;

        var scale = ImGuiHelpers.GlobalScale;
        var cursor = ImGui.GetCursorScreenPos();
        var height = ImGui.GetFrameHeight();
        var width = BoundedContentAvailX();
        var pressed = ImGui.InvisibleButton("##sectionToggle" + label, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        if (pressed)
        {
            open = !open;
            storage.SetInt(storageId, open ? 1 : 0);
        }

        var drawList = ImGui.GetWindowDrawList();
        if (hovered)
        {
            var tint = SpheneDesign.Accent with { W = 0.08f * ImGui.GetStyle().Alpha };
            drawList.AddRectFilled(cursor, cursor + new Vector2(width, height),
                ImGui.ColorConvertFloat4ToU32(tint), SpheneDesign.SwatchRadius(height));
        }

        ImGui.SetCursorScreenPos(cursor + new Vector2(2f * scale,
            (height - ImGui.GetTextLineHeight()) * 0.5f));
        IconText(open ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight,
            hovered ? SpheneDesign.Accent : SpheneDesign.TextMuted);
        ImGui.SameLine(0f, SpheneDesign.Gap * 0.5f);
        ColorText(label, open ? SpheneDesign.TextPrimary : SpheneDesign.TextSecondary);

        SpheneDesignLint.CheckItemWithinBound(label);
        return open;
    }

    /// <summary>
    /// Rounded color swatch button in the value-control language: filled with
    /// the color, subtle border that brightens on hover and turns accent when
    /// <paramref name="selected"/>. Never opens a picker — the caller owns the
    /// click (returns true) and decides what to show (popup, palette…).
    /// </summary>
    public static bool DrawColorSwatchButton(string id, Vector4 color, float size = -1f,
        bool selected = false)
    {
        var scale = ImGuiHelpers.GlobalScale;
        if (size < 0f)
        {
            size = SpheneDesign.SwatchSize;
        }
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var fill = hovered
            ? new Vector4(MathF.Min(1f, color.X * SpheneDesign.SwatchHoverBrighten + SpheneDesign.SwatchHoverBrightenAdd),
                MathF.Min(1f, color.Y * SpheneDesign.SwatchHoverBrighten + SpheneDesign.SwatchHoverBrightenAdd),
                MathF.Min(1f, color.Z * SpheneDesign.SwatchHoverBrighten + SpheneDesign.SwatchHoverBrightenAdd), color.W)
            : color;
        var rounding = SpheneDesign.SwatchRadius(size);
        var accent = SpheneCustomTheme.Colors.AccentBlue;
        var border = selected
            ? new Vector4(accent.X, accent.Y, accent.Z, SpheneDesign.SwatchBorderAlphaSelected)
            : hovered
                ? new Vector4(accent.X, accent.Y, accent.Z, SpheneDesign.SwatchBorderAlphaHover)
                : new Vector4(1f, 1f, 1f, SpheneDesign.SwatchBorderAlpha);
        var max = pos + new Vector2(size, size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(fill), rounding);
        drawList.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(border), rounding,
            ImDrawFlags.None, selected ? SpheneDesign.SwatchSelectedThickness * scale : 1f * scale);
        SpheneDesignLint.CheckItemWithinBound(id);
        return clicked;
    }

    /// <summary>
    /// Same swatch button as <see cref="DrawColorSwatchButton"/> but renders a
    /// short glyph (symbol, letter) centered instead of a color fill — used for
    /// palette pickers whose entries are characters rather than colors.
    /// </summary>
    public static bool DrawGlyphSwatchButton(string id, string glyph, float size = -1f,
        bool selected = false)
    {
        var scale = ImGuiHelpers.GlobalScale;
        if (size < 0f)
        {
            size = SpheneDesign.SwatchSize;
        }
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var style = ImGui.GetStyle();
        var fill = hovered
            ? style.Colors[(int)ImGuiCol.FrameBgHovered]
            : style.Colors[(int)ImGuiCol.FrameBg];
        var accent = SpheneCustomTheme.Colors.AccentBlue;
        var border = selected
            ? new Vector4(accent.X, accent.Y, accent.Z, SpheneDesign.SwatchBorderAlphaSelected)
            : hovered
                ? new Vector4(accent.X, accent.Y, accent.Z, SpheneDesign.SwatchBorderAlphaHover)
                : new Vector4(1f, 1f, 1f, SpheneDesign.SwatchBorderAlpha);
        var max = pos + new Vector2(size, size);
        var rounding = SpheneDesign.SwatchRadius(size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(fill), rounding);
        drawList.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(border), rounding,
            ImDrawFlags.None, selected ? SpheneDesign.SwatchSelectedThickness * scale : 1f * scale);
        if (glyph.Length > 0)
        {
            var textSize = ImGui.CalcTextSize(glyph);
            drawList.AddText(new Vector2(pos.X + (size - textSize.X) * 0.5f,
                pos.Y + (size - textSize.Y) * 0.5f), ImGui.GetColorU32(ImGuiCol.Text), glyph);
        }
        SpheneDesignLint.CheckItemWithinBound(id);
        return clicked;
    }

    /// <summary>
    /// SameLine only when the next item of the given width still fits inside
    /// the real content bound; otherwise stays on the new line so the item is
    /// never clipped at the card edge. Returns whether SameLine was applied.
    /// </summary>
    public static bool SameLineIfFits(float nextItemWidth)
    {
        var cursor = ImGui.GetCursorScreenPos();
        var bound = SpheneWindowChrome.CurrentCardContentRightBound;
        var rightBound = bound > 0f ? bound : cursor.X + ImGui.GetContentRegionAvail().X;
        // SameLine continues at the previous item's right edge, not at the
        // line start — measuring from the cursor would always claim a full
        // line of room and let items render past the bound.
        var itemStartX = MathF.Max(cursor.X, ImGui.GetItemRectMax().X);
        if (itemStartX + ImGui.GetStyle().ItemSpacing.X + nextItemWidth > rightBound)
        {
            return false;
        }
        ImGui.SameLine();
        return true;
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
        using (var controlStyle = PushValueControlStyle())
        {
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
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGui.ColorConvertFloat4ToU32(SpheneCustomTheme.CurrentTheme.CompactText));
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
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, ImGui.ColorConvertFloat4ToU32(SpheneCustomTheme.CurrentTheme.CompactText));
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
        drawList.AddRect(pos, endPos, ImGui.ColorConvertFloat4ToU32(border), borderRadius, ImDrawFlags.RoundCornersAll, theme.CompactFrameBorderSize);

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
