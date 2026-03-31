using Dalamud.Bindings.ImGui;
using Sphene.SpheneConfiguration;
using Sphene.SpheneConfiguration.Models;
using Sphene.Services.Mediator;

namespace Sphene.UI.Components;

public static class TransfersOptionBlock
{
    public static void DrawGlobalReceiveLimitValueOption(SpheneConfigService configService, SpheneMediator mediator, float itemWidth, string blockId = "GlobalReceiveLimitValue")
    {
        ImGui.PushID(blockId);
        try
        {
            var downloadSpeedLimit = configService.Current.DownloadSpeedLimitInBytes;
            ImGui.SetNextItemWidth(itemWidth);
            if (ImGui.InputInt("###speedlimit", ref downloadSpeedLimit))
            {
                configService.Current.DownloadSpeedLimitInBytes = downloadSpeedLimit;
                configService.Save();
                mediator.Publish(new DownloadLimitChangedMessage());
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawGlobalReceiveLimitSpeedUnitOption(SpheneConfigService configService, UiSharedService uiShared, SpheneMediator mediator, string blockId = "GlobalReceiveLimitSpeedUnit")
    {
        ImGui.PushID(blockId);
        try
        {
            uiShared.DrawCombo("###speed", [DownloadSpeeds.Bps, DownloadSpeeds.KBps, DownloadSpeeds.MBps],
                s => s switch
                {
                    DownloadSpeeds.Bps => "Byte/s",
                    DownloadSpeeds.KBps => "KB/s",
                    DownloadSpeeds.MBps => "MB/s",
                    _ => throw new NotSupportedException()
                }, s =>
                {
                    configService.Current.DownloadSpeedType = s;
                    configService.Save();
                    mediator.Publish(new DownloadLimitChangedMessage());
                }, configService.Current.DownloadSpeedType);
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawMaximumParallelDataStreamsOption(SpheneConfigService configService, string blockId = "MaximumParallelDataStreams")
    {
        ImGui.PushID(blockId);
        try
        {
            var maxParallelDownloads = configService.Current.ParallelDownloads;
            if (ImGui.SliderInt("Maximum Parallel Data Streams", ref maxParallelDownloads, 1, 10))
            {
                configService.Current.ParallelDownloads = maxParallelDownloads;
                configService.Save();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawUseSpheneCdnDirectDownloadsOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "UseSpheneCdnDirectDownloads")
    {
        ImGui.PushID(blockId);
        try
        {
            var useSpheneCdn = configService.Current.UseSpheneCdnDirectDownloads;
            if (ImGui.Checkbox("Use CDN direct downloads", ref useSpheneCdn))
            {
                configService.Current.UseSpheneCdnDirectDownloads = useSpheneCdn;
                configService.Save();
            }

            uiShared.DrawHelpText("When enabled, Sphene downloads files directly from the CDN https://sphene.cloud when possible and falls back to the file server if needed."
                                  + UiSharedService.TooltipSeparator
                                  + "This is usually faster because Global Receive Limit / Download Speed Limit does not apply to CDN direct downloads."
                                  + UiSharedService.TooltipSeparator
                                  + "When disabled, Sphene always uses the legacy file server download flow.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawAllowReceivingPenumbraModPackagesOption(SpheneConfigService configService, UiSharedService uiShared, Sphene.WebAPI.ApiController apiController, string blockId = "AllowReceivingPenumbraModPackages")
    {
        ImGui.PushID(blockId);
        try
        {
            var allowPenumbraMods = configService.Current.AllowReceivingPenumbraMods;
            if (ImGui.Checkbox("Allow receiving Penumbra mod packages", ref allowPenumbraMods))
            {
                configService.Current.AllowReceivingPenumbraMods = allowPenumbraMods;
                configService.Save();
                _ = apiController.UserUpdatePenumbraReceivePreference(allowPenumbraMods);
            }

            uiShared.DrawHelpText("When disabled, incoming Penumbra mod packages are ignored and no install popups are shown.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawUseAlternativeTransmissionMethodOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "UseAlternativeTransmissionMethod")
    {
        ImGui.PushID(blockId);
        try
        {
            var useAlternativeUpload = configService.Current.UseAlternativeFileUpload;
            if (ImGui.Checkbox("Use Alternative Transmission Method", ref useAlternativeUpload))
            {
                configService.Current.UseAlternativeFileUpload = useAlternativeUpload;
                configService.Save();
            }

            uiShared.DrawHelpText("Attempts a single-shot transmission instead of streaming. Not usually required; enable only if you encounter transfer issues.");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static bool DrawShowSeparateTransmissionMonitorOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowSeparateTransmissionMonitor")
    {
        ImGui.PushID(blockId);
        try
        {
            var showTransferWindow = configService.Current.ShowTransferWindow;
            if (ImGui.Checkbox("Show separate transmission monitor", ref showTransferWindow))
            {
                configService.Current.ShowTransferWindow = showTransferWindow;
                configService.Save();
            }

            uiShared.DrawHelpText($"The transmission monitor displays current progress of active data streams.{Environment.NewLine}{Environment.NewLine}" +
                $"Status indicators:{Environment.NewLine}W = Waiting for Slot (see Maximum Parallel Data Streams){Environment.NewLine}" +
                $"Q = Queued on Network Node, awaiting signal{Environment.NewLine}" +
                $"P = Processing transmission (receiving data){Environment.NewLine}" +
                $"D = Decompressing received data");
            return configService.Current.ShowTransferWindow;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawEditTransmissionMonitorPositionOption(UiSharedService uiShared, string blockId = "EditTransmissionMonitorPosition")
    {
        ImGui.PushID(blockId);
        try
        {
            var editTransferWindowPosition = uiShared.EditTrackerPosition;
            if (ImGui.Checkbox("Edit Transmission Monitor position", ref editTransferWindowPosition))
            {
                uiShared.EditTrackerPosition = editTransferWindowPosition;
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static bool DrawShowTransmissionIndicatorsBelowPlayersOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowTransmissionIndicatorsBelowPlayers")
    {
        ImGui.PushID(blockId);
        try
        {
            var showTransferBars = configService.Current.ShowTransferBars;
            if (ImGui.Checkbox("Show transmission indicators below players", ref showTransferBars))
            {
                configService.Current.ShowTransferBars = showTransferBars;
                configService.Save();
            }

            uiShared.DrawHelpText("This will render a progress indicator during data reception at the feet of the connected player.");
            return showTransferBars;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawShowTransmissionTextOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowTransmissionText")
    {
        ImGui.PushID(blockId);
        try
        {
            var transferBarShowText = configService.Current.TransferBarsShowText;
            if (ImGui.Checkbox("Show Transmission Text", ref transferBarShowText))
            {
                configService.Current.TransferBarsShowText = transferBarShowText;
                configService.Save();
            }

            uiShared.DrawHelpText("Shows transmission text (amount of MiB received) in the progress indicators");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawTransmissionIndicatorWidthOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "TransmissionIndicatorWidth")
    {
        ImGui.PushID(blockId);
        try
        {
            var transferBarWidth = configService.Current.TransferBarsWidth;
            if (ImGui.SliderInt("Transmission Indicator Width", ref transferBarWidth, 10, 500))
            {
                configService.Current.TransferBarsWidth = transferBarWidth;
                configService.Save();
            }

            uiShared.DrawHelpText("Width of the displayed transmission indicators (will never be less wide than the displayed text)");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawTransmissionIndicatorHeightOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "TransmissionIndicatorHeight")
    {
        ImGui.PushID(blockId);
        try
        {
            var transferBarHeight = configService.Current.TransferBarsHeight;
            if (ImGui.SliderInt("Transmission Indicator Height", ref transferBarHeight, 2, 50))
            {
                configService.Current.TransferBarsHeight = transferBarHeight;
                configService.Save();
            }

            uiShared.DrawHelpText("Height of the displayed transmission indicators (will never be less tall than the displayed text)");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static bool DrawShowTransmittingTextBelowPlayersOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "ShowTransmittingTextBelowPlayers")
    {
        ImGui.PushID(blockId);
        try
        {
            var showUploading = configService.Current.ShowUploading;
            if (ImGui.Checkbox("Show 'Transmitting' text below players that are currently transmitting", ref showUploading))
            {
                configService.Current.ShowUploading = showUploading;
                configService.Save();
            }

            uiShared.DrawHelpText("This will render a 'Transmitting' text at the feet of the player that is in progress of transmitting data.");
            return showUploading;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    public static void DrawLargeFontForTransmittingTextOption(SpheneConfigService configService, UiSharedService uiShared, string blockId = "LargeFontForTransmittingText")
    {
        ImGui.PushID(blockId);
        try
        {
            var showUploadingBigText = configService.Current.ShowUploadingBigText;
            if (ImGui.Checkbox("Large font for 'Transmitting' text", ref showUploadingBigText))
            {
                configService.Current.ShowUploadingBigText = showUploadingBigText;
                configService.Save();
            }

            uiShared.DrawHelpText("This will render an 'Transferring' text in a larger font.");
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
