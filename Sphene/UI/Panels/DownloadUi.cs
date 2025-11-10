using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Sphene.SpheneConfiguration;
using Sphene.PlayerData.Handlers;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.WebAPI.Files;
using Sphene.WebAPI.Files.Models;
using Sphene.UI.Theme;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Numerics;

namespace Sphene.UI.Panels;

public class DownloadUi : WindowMediatorSubscriberBase
{
    private readonly SpheneConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, Dictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileUploadManager _fileTransferManager;
    private readonly UiSharedService _uiShared;
    private readonly ConcurrentDictionary<GameObjectHandler, bool> _uploadingPlayers = new();

    public DownloadUi(ILogger<DownloadUi> logger, DalamudUtilService dalamudUtilService, SpheneConfigService configService,
        FileUploadManager fileTransferManager, SpheneMediator mediator, UiSharedService uiShared, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Sphene Downloads", performanceCollectorService)
    {
        _dalamudUtilService = dalamudUtilService;
        _configService = configService;
        _fileTransferManager = fileTransferManager;
        _uiShared = uiShared;

        SizeConstraints = new WindowSizeConstraints()
        {
            MaximumSize = new Vector2(500, 90),
            MinimumSize = new Vector2(500, 90),
        };

        Flags |= ImGuiWindowFlags.NoMove;
        Flags |= ImGuiWindowFlags.NoBackground;
        Flags |= ImGuiWindowFlags.NoInputs;
        Flags |= ImGuiWindowFlags.NoNavFocus;
        Flags |= ImGuiWindowFlags.NoResize;
        Flags |= ImGuiWindowFlags.NoScrollbar;
        Flags |= ImGuiWindowFlags.NoTitleBar;
        Flags |= ImGuiWindowFlags.NoDecoration;
        Flags |= ImGuiWindowFlags.NoFocusOnAppearing;

        DisableWindowSounds = true;

        ForceMainWindow = true;

        IsOpen = true;

        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.DownloadId] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.DownloadId, out _));
        Mediator.Subscribe<GposeStartMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<GposeEndMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<PlayerUploadingMessage>(this, (msg) =>
        {
            if (msg.IsUploading)
            {
                _uploadingPlayers[msg.Handler] = true;
            }
            else
            {
                _uploadingPlayers.TryRemove(msg.Handler, out _);
            }
        });
    }

    protected override void DrawInternal()
    {
        if (_configService.Current.ShowTransferWindow)
        {
            try
            {
                if (_fileTransferManager.CurrentUploads.Any())
                {
                    var currentUploads = _fileTransferManager.CurrentUploads.ToList();
                    var totalUploads = currentUploads.Count;

                    var doneUploads = currentUploads.Count(c => c.IsTransferred);
                    var totalUploaded = currentUploads.Sum(c => c.Transferred);
                    var totalToUpload = currentUploads.Sum(c => c.Total);

                    UiSharedService.DrawOutlinedFont($"▲", ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.SameLine();
                    var xDistance = ImGui.GetCursorPosX();
                    UiSharedService.DrawOutlinedFont($"Compressing+Uploading {doneUploads}/{totalUploads}",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.NewLine();
                    ImGui.SameLine(xDistance);
                    UiSharedService.DrawOutlinedFont(
                        $"{UiSharedService.ByteToString(totalUploaded, addSuffix: false)}/{UiSharedService.ByteToString(totalToUpload)}",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);

                    if (_currentDownloads.Any()) ImGui.Separator();
                }
            }
            catch
            {
                // ignore errors thrown from UI
            }

            try
            {
                foreach (var item in _currentDownloads.ToList())
                {
                    var dlSlot = item.Value.Count(c => c.Value.DownloadStatus == DownloadStatus.WaitingForSlot);
                    var dlQueue = item.Value.Count(c => c.Value.DownloadStatus == DownloadStatus.WaitingForQueue);
                    var dlProg = item.Value.Count(c => c.Value.DownloadStatus == DownloadStatus.Downloading);
                    var dlDecomp = item.Value.Count(c => c.Value.DownloadStatus == DownloadStatus.Decompressing);
                    var totalFiles = item.Value.Sum(c => c.Value.TotalFiles);
                    var transferredFiles = item.Value.Sum(c => c.Value.TransferredFiles);
                    var totalBytes = item.Value.Sum(c => c.Value.TotalBytes);
                    var transferredBytes = item.Value.Sum(c => c.Value.TransferredBytes);

                    UiSharedService.DrawOutlinedFont($"▼", ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.SameLine();
                    var xDistance = ImGui.GetCursorPosX();
                    UiSharedService.DrawOutlinedFont(
                        $"{item.Key.Name} [W:{dlSlot}/Q:{dlQueue}/P:{dlProg}/D:{dlDecomp}]",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.NewLine();
                    ImGui.SameLine(xDistance);
                    UiSharedService.DrawOutlinedFont(
                        $"{transferredFiles}/{totalFiles} ({UiSharedService.ByteToString(transferredBytes, addSuffix: false)}/{UiSharedService.ByteToString(totalBytes)})",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                }
            }
            catch
            {
                // ignore errors thrown from UI
            }
        }

        // Show preview transmission bar if enabled in theme settings (independent of ShowTransferBars setting)
        const int transparency = 100;
        const int dlBarBorder = 3;
        
        if (SpheneCustomTheme.CurrentTheme.ShowProgressBarPreview)
        {
            var playerScreenPos = _dalamudUtilService.WorldToScreen(_dalamudUtilService.GetPlayerCharacter());
            if (playerScreenPos != Vector2.Zero)
            {
                var theme = SpheneCustomTheme.CurrentTheme;
                int previewBarHeight = Math.Max((int)theme.CompactTransmissionBarHeight, 10);
                int previewBarWidth = Math.Max((int)theme.CompactTransmissionBarWidth, 50);

                var previewBarStart = new Vector2(playerScreenPos.X - previewBarWidth / 2f, playerScreenPos.Y - previewBarHeight / 2f);
                var previewBarEnd = new Vector2(playerScreenPos.X + previewBarWidth / 2f, playerScreenPos.Y + previewBarHeight / 2f);
                var drawList = ImGui.GetBackgroundDrawList();
                
                // Use theme-configured colors with transparency for preview
                var bgColor = theme.CompactTransmissionBarBackground;
                var fgColor = theme.CompactTransmissionBarForeground;
                var borderColor = theme.CompactTransmissionBarBorder;
                var rounding = theme.TransmissionBarRounding;
                
                drawList.AddRectFilled(
                    previewBarStart with { X = previewBarStart.X - dlBarBorder - 1, Y = previewBarStart.Y - dlBarBorder - 1 },
                    previewBarEnd with { X = previewBarEnd.X + dlBarBorder + 1, Y = previewBarEnd.Y + dlBarBorder + 1 },
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, transparency / 255f)), rounding + 1);
                drawList.AddRectFilled(previewBarStart with { X = previewBarStart.X - dlBarBorder, Y = previewBarStart.Y - dlBarBorder },
                    previewBarEnd with { X = previewBarEnd.X + dlBarBorder, Y = previewBarEnd.Y + dlBarBorder },
                    ImGui.ColorConvertFloat4ToU32(new Vector4(borderColor.X, borderColor.Y, borderColor.Z, transparency / 255f)), rounding);
                drawList.AddRectFilled(previewBarStart, previewBarEnd,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(bgColor.X, bgColor.Y, bgColor.Z, transparency / 255f)), rounding);
                
                // Use the configurable preview fill percentage
                var previewProgress = theme.ProgressBarPreviewFill / 100.0f;
                drawList.AddRectFilled(previewBarStart,
                    previewBarEnd with { X = previewBarStart.X + (previewProgress * previewBarWidth) },
                    ImGui.ColorConvertFloat4ToU32(new Vector4(fgColor.X, fgColor.Y, fgColor.Z, transparency / 255f)), rounding);

                // Show preview text with configurable fill percentage (independent of TransferBarsShowText setting for preview)
                var previewText = $"Preview {theme.ProgressBarPreviewFill:F1}%";
                var textSize = ImGui.CalcTextSize(previewText);
                UiSharedService.DrawOutlinedFont(drawList, previewText,
                    playerScreenPos with { X = playerScreenPos.X - textSize.X / 2f - 1, Y = playerScreenPos.Y - textSize.Y / 2f - 1 },
                    UiSharedService.Color(255, 255, 255, transparency),
                    UiSharedService.Color(0, 0, 0, transparency), 1);
            }
        }

        if (_configService.Current.ShowTransferBars)
        {

            foreach (var transfer in _currentDownloads.ToList())
            {
                var screenPos = _dalamudUtilService.WorldToScreen(transfer.Key.GetGameObject());
                if (screenPos == Vector2.Zero) continue;

                var totalBytes = transfer.Value.Sum(c => c.Value.TotalBytes);
                var transferredBytes = transfer.Value.Sum(c => c.Value.TransferredBytes);

                var maxDlText = $"{UiSharedService.ByteToString(totalBytes, addSuffix: false)}/{UiSharedService.ByteToString(totalBytes)}";
                var textSize = _configService.Current.TransferBarsShowText ? ImGui.CalcTextSize(maxDlText) : new Vector2(10, 10);

                // Use theme-configured transmission bar dimensions
                var theme = SpheneCustomTheme.CurrentTheme;
                int dlBarHeight = Math.Max((int)theme.CompactTransmissionBarHeight, _configService.Current.TransferBarsShowText ? (int)textSize.Y + 5 : 10);
                int dlBarWidth = Math.Max((int)theme.CompactTransmissionBarWidth, _configService.Current.TransferBarsShowText ? (int)textSize.X + 10 : 50);

                var dlBarStart = new Vector2(screenPos.X - dlBarWidth / 2f, screenPos.Y - dlBarHeight / 2f);
                var dlBarEnd = new Vector2(screenPos.X + dlBarWidth / 2f, screenPos.Y + dlBarHeight / 2f);
                var drawList = ImGui.GetBackgroundDrawList();
                
                // Use theme-configured colors with transparency
                var bgColor = theme.CompactTransmissionBarBackground;
                var fgColor = theme.CompactTransmissionBarForeground;
                var borderColor = theme.CompactTransmissionBarBorder;
                
                drawList.AddRectFilled(
                    dlBarStart with { X = dlBarStart.X - dlBarBorder - 1, Y = dlBarStart.Y - dlBarBorder - 1 },
                    dlBarEnd with { X = dlBarEnd.X + dlBarBorder + 1, Y = dlBarEnd.Y + dlBarBorder + 1 },
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, transparency / 255f)), 1);
                drawList.AddRectFilled(dlBarStart with { X = dlBarStart.X - dlBarBorder, Y = dlBarStart.Y - dlBarBorder },
                    dlBarEnd with { X = dlBarEnd.X + dlBarBorder, Y = dlBarEnd.Y + dlBarBorder },
                    ImGui.ColorConvertFloat4ToU32(new Vector4(borderColor.X, borderColor.Y, borderColor.Z, transparency / 255f)), 1);
                drawList.AddRectFilled(dlBarStart, dlBarEnd,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(bgColor.X, bgColor.Y, bgColor.Z, transparency / 255f)), 1);
                var dlProgressPercent = transferredBytes / (double)totalBytes;
                drawList.AddRectFilled(dlBarStart,
                    dlBarEnd with { X = dlBarStart.X + (float)(dlProgressPercent * dlBarWidth) },
                    ImGui.ColorConvertFloat4ToU32(new Vector4(fgColor.X, fgColor.Y, fgColor.Z, transparency / 255f)), 1);

                if (_configService.Current.TransferBarsShowText)
                {
                    var downloadText = $"{UiSharedService.ByteToString(transferredBytes, addSuffix: false)}/{UiSharedService.ByteToString(totalBytes)}";
                    UiSharedService.DrawOutlinedFont(drawList, downloadText,
                        screenPos with { X = screenPos.X - textSize.X / 2f - 1, Y = screenPos.Y - textSize.Y / 2f - 1 },
                        UiSharedService.Color(255, 255, 255, transparency),
                        UiSharedService.Color(0, 0, 0, transparency), 1);
                }
            }

            if (_configService.Current.ShowUploading)
            {
                foreach (var player in _uploadingPlayers.Select(p => p.Key).ToList())
                {
                    var screenPos = _dalamudUtilService.WorldToScreen(player.GetGameObject());
                    if (screenPos == Vector2.Zero) continue;

                    try
                    {
                        using var _ = _uiShared.UidFont.Push();
                        var uploadText = "Transferring";

                        var textSize = ImGui.CalcTextSize(uploadText);

                        var drawList = ImGui.GetBackgroundDrawList();
                        UiSharedService.DrawOutlinedFont(drawList, uploadText,
                            screenPos with { X = screenPos.X - textSize.X / 2f - 1, Y = screenPos.Y - textSize.Y / 2f - 1 },
                            UiSharedService.Color(255, 255, 0, transparency),
                            UiSharedService.Color(0, 0, 0, transparency), 2);
                    }
                    catch
                    {
                        // ignore errors thrown on UI
                    }
                }
            }
        }
    }

    public override bool DrawConditions()
    {
        if (_uiShared.EditTrackerPosition) return true;
        
        // Show preview if enabled in theme settings
        if (SpheneCustomTheme.CurrentTheme.ShowProgressBarPreview) return true;
        
        if (!_configService.Current.ShowTransferWindow && !_configService.Current.ShowTransferBars) return false;
        if (!_currentDownloads.Any() && !_fileTransferManager.CurrentUploads.Any() && !_uploadingPlayers.Any()) return false;
        if (!IsOpen) return false;
        return true;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        if (_uiShared.EditTrackerPosition)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
            Flags &= ~ImGuiWindowFlags.NoBackground;
            Flags &= ~ImGuiWindowFlags.NoInputs;
            Flags &= ~ImGuiWindowFlags.NoResize;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
            Flags |= ImGuiWindowFlags.NoBackground;
            Flags |= ImGuiWindowFlags.NoInputs;
            Flags |= ImGuiWindowFlags.NoResize;
        }

        var maxHeight = ImGui.GetTextLineHeight() * (_configService.Current.ParallelDownloads + 3);
        SizeConstraints = new()
        {
            MinimumSize = new Vector2(300, maxHeight),
            MaximumSize = new Vector2(300, maxHeight),
        };
    }
}
