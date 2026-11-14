using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Sphene.API.Data;
using Sphene.FileCache;
using Sphene.Interop.Ipc;
using Sphene.Localization;
using Sphene.SpheneConfiguration;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.UI.Design;
using Sphene.Utils;
using Sphene.WebAPI;
using Sphene.WebAPI.SignalR;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;
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
        // Mid-size heading face for markdown h2/h3 - sits between the body
        // font and DisplayFont so every heading level stays crisp instead of
        // relying on SetWindowFontScale's raster upscale.
        HeadingFont = _pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        {
            e.OnPreBuild(tk =>
            {
                var fontPath = Path.Combine(_pluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty, "Resources", "Fonts", "Lato-Regular.ttf");
                if (File.Exists(fontPath))
                {
                     tk.AddFontFromFile(fontPath, new SafeFontConfig
                     {
                         SizePx = 20
                     });
                }
                else
                {
                    tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansCjkMedium, new()
                    {
                        SizePx = 20
                    });
                }
            });
        });
        GameFont = _pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new(GameFontFamilyAndSize.Axis12));
        IconFont = _pluginInterface.UiBuilder.IconFontFixedWidthHandle;

        SpheneUI.Initialize(logger, Mediator, IconFont, UidFont, DisplayFont, HeadingFont, GameFont);
        SpheneUI.MarkdownFontResolver = MarkdownFontForSize;
        SpheneUI.Density.SetOverrideProvider(() => _configService.Current.LayoutDensity switch
        {
            Sphene.SpheneConfiguration.Models.LayoutDensityMode.Compact => SpheneUI.DensityProfile.Compact,
            Sphene.SpheneConfiguration.Models.LayoutDensityMode.Balanced => SpheneUI.DensityProfile.Balanced,
            Sphene.SpheneConfiguration.Models.LayoutDensityMode.Comfortable => SpheneUI.DensityProfile.Comfortable,
            _ => null,
        });
        SpheneUI.Media.SetTextureProvider(textureProvider);
    }

    public static string DoubleNewLine => Environment.NewLine + Environment.NewLine;
    public ApiController ApiController => _apiController;

    public bool EditTrackerPosition { get; set; }

    public IFontHandle GameFont { get; init; }
    public bool HasValidPenumbraModPath => !(_ipcManager.Penumbra.ModDirectory ?? string.Empty).IsNullOrEmpty() && Directory.Exists(_ipcManager.Penumbra.ModDirectory);

    public IFontHandle DisplayFont { get; init; }

    public IFontHandle HeadingFont { get; init; }

    public IFontHandle IconFont { get; init; }
    public bool IsInGpose => _dalamudUtil.IsInGpose;

    public Dictionary<uint, string> JobData => _dalamudUtil.JobData.Value;
    public string PlayerName => _dalamudUtil.GetPlayerName();

    public IFontHandle UidFont { get; init; }
    public Dictionary<ushort, string> WorldData => _dalamudUtil.WorldData.Value;
    public uint WorldId => _dalamudUtil.GetHomeWorldId();

    /// <summary>Lazy crisp font handles for markdown &lt;size=N&gt; - one
    /// delegate handle per requested point size, disposed with the service.</summary>
    private readonly ConcurrentDictionary<int, IFontHandle> _markdownFonts = new();

    private IFontHandle MarkdownFontForSize(int pt)
    {
        pt = Math.Clamp(pt, 8, 32);
        return _markdownFonts.GetOrAdd(pt, size =>
            _pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
            {
                e.OnPreBuild(tk =>
                {
                    var fontPath = Path.Combine(_pluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty, "Resources", "Fonts", "Lato-Regular.ttf");
                    if (File.Exists(fontPath))
                    {
                        tk.AddFontFromFile(fontPath, new SafeFontConfig { SizePx = size });
                    }
                    else
                    {
                        tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansCjkMedium, new()
                        {
                            SizePx = size
                        });
                    }
                });
            }));
    }

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

    public void LoadLocalization(string languageCode)
    {
        _localization.SetupWithLangCode(languageCode);
    }

    [LibraryImport("user32")]
    internal static partial short GetKeyState(int nVirtKey);

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;

        base.Dispose(disposing);

        _discordOAuthGetCts.Cancel();
        _discordOAuthGetCts.Dispose();
        UidFont.Dispose();
        DisplayFont.Dispose();
        HeadingFont.Dispose();
        GameFont.Dispose();
        SpheneUI.MarkdownFontResolver = null;
        foreach (var handle in _markdownFonts.Values)
        {
            handle.Dispose();
        }
        _markdownFonts.Clear();
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

    // Store a pending window size to be applied next frame
    public static void SetPendingWindowSize(string windowName, Vector2 size)
    {
        _pendingWindowSizes[windowName] = size;
    }

    public sealed record IconScaleData(Vector2 IconSize, Vector2 NormalizedIconScale, float OffsetX, float IconScaling);
    private sealed record UidAliasPair(string? UID, string? Alias);
}
