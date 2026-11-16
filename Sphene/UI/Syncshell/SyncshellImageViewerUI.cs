using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.API.Dto.Group;
using Sphene.Services;
using Sphene.Services.Mediator;
using Microsoft.Extensions.Logging;
using Sphene.UI.Components;
using Sphene.UI.Design;

namespace Sphene.UI.Syncshell;

/// <summary>
/// Standalone photo viewer popped out of a syncshell community gallery · the
/// same lightbox chrome as the in-page overlay, with the image filling the
/// window. Opened via <see cref="OpenSyncshellImageViewerMessage"/>.
/// </summary>
public sealed class SyncshellImageViewerUI : WindowMediatorSubscriberBase
{
    private List<SyncshellGalleryImageDto> _images = [];
    private int _index;
    private string _gid = string.Empty;

    public SyncshellImageViewerUI(ILogger<SyncshellImageViewerUI> logger,
        SpheneMediator mediator, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Gallery###SyncshellImageViewer", performanceCollectorService)
    {
        SizeConstraints = new()
        {
            MinimumSize = new(520, 420)
        };

        Mediator.Subscribe<OpenSyncshellImageViewerMessage>(this, (msg) =>
        {
            _images = msg.Images;
            _index = msg.StartIndex;
            _gid = msg.GroupGid;
            WindowName = $"{msg.Title ?? $"{msg.GroupGid} · Gallery"}###SyncshellImageViewer";
            IsOpen = true;
            BringToFront();
        });

        Flags = ImGuiWindowFlags.NoCollapse;
    }

    // The viewer chrome (backdrop, close circle, nav) is self-contained · no
    // chrome band on top.
    protected override bool ChromeHasTitleBar => false;

    protected override void DrawInternal()
    {
        // No PageHeader here: the viewer paints its own close into the same
        // top-right zone the "?" help dock occupies, and first-submitted-wins
        // makes those hits ambiguous. The window title already names the
        // gallery; the help text adds nothing inside a dedicated viewer.
        if (_images.Count == 0)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.Images, "No photos",
                "Nothing to show right now.");
            return;
        }

        var keepOpen = SyncshellImageViewer.Draw(_images, _index, "viewer-popout:",
            _gid, closeOnBackdrop: false,
            setIndex: i => _index = i,
            onClose: () => IsOpen = false,
            onPopOut: null, standalone: true);
        if (!keepOpen)
        {
            IsOpen = false;
        }
    }
}
