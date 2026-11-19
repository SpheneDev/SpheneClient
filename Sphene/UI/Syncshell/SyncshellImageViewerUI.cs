using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Sphene.API.Data;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
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
    private readonly ApiController _apiController;
    private List<SyncshellGalleryImageDto> _images = [];
    private int _index;
    private string _gid = string.Empty;
    private bool _gripHeld;
    private float _gripAnim;

    public SyncshellImageViewerUI(ILogger<SyncshellImageViewerUI> logger,
        SpheneMediator mediator, ApiController apiController,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Gallery###SyncshellImageViewer", performanceCollectorService)
    {
        _apiController = apiController;
        Size = new Vector2(860, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new()
        {
            MinimumSize = new(520, 420)
        };

        Mediator.Subscribe<OpenSyncshellImageViewerMessage>(this, (msg) =>
        {
            _logger.LogDebug("[CommUi] viewer open gid={Gid} count={Count} (popout)",
                msg.GroupGid, msg.Images.Count);
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

    /// <summary>
    /// The theme sets WindowBorderSize 0, which also kills ImGui's native
    /// edge-resize hit zones - and items drawn over the corner
    /// (##viewer-img, filmstrip) win any item-based grip by submission
    /// order. A hairline border restores the native zones: they are
    /// hit-tested inside Begin before any item exists, so nothing can
    /// block them. The border color stays fully transparent so the
    /// window looks unchanged.
    /// </summary>
    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 6f);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        base.PreDraw();
    }

    public override void PostDraw()
    {
        base.PostDraw();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

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
            onClose: () =>
            {
                _logger.LogDebug("[CommUi] viewer closed (popout)");
                IsOpen = false;
            },
            onPopOut: null, standalone: true,
            onLike: i => _ = ToggleImageLike(i));
        if (!keepOpen)
        {
            _logger.LogDebug("[CommUi] viewer close via keepOpen (popout)");
            IsOpen = false;
        }
        // Themed resize grip over the native hit zone - the chrome hides the
        // stock grip color, so without the stamp the corner still drags but
        // shows nothing.
        SpheneUI.Chrome.GripCorner(ImGui.GetWindowPos() + ImGui.GetWindowSize(),
            _gripHeld, _gripAnim, out _gripHeld, out _gripAnim);
    }

    /// <summary>
    /// Optimistic like toggle · profile popouts arrive with an "own:{uid}"/
    /// "friend:{uid}" viewer key in the gid slot, community popouts carry the
    /// real group gid - that routes to the matching hub call. The list is a
    /// popout-local copy, so patching it only affects this window; the host
    /// list refreshes on its own.
    /// </summary>
    private async Task ToggleImageLike(int index)
    {
        if (index < 0 || index >= _images.Count)
        {
            return;
        }
        var img = _images[index];
        _images[index] = img with
        {
            LikedByMe = !img.LikedByMe,
            LikeCount = Math.Max(0, img.LikeCount + (img.LikedByMe ? -1 : 1))
        };
        try
        {
            var isProfile = _gid.StartsWith("own:", StringComparison.Ordinal)
                || _gid.StartsWith("friend:", StringComparison.Ordinal);
            if (isProfile)
            {
                var result = await _apiController.UserToggleProfileGalleryImageLike(
                    new UserProfileGalleryImageLikeDto(new UserData(_apiController.UID), img.Id))
                    .ConfigureAwait(false);
                if (result != null && index < _images.Count && _images[index].Id == result.ImageId)
                {
                    _images[index] = _images[index] with
                    {
                        LikedByMe = result.LikedByMe, LikeCount = result.LikeCount
                    };
                }
            }
            else
            {
                var result = await _apiController.GroupToggleCommunityGalleryImageLike(
                    new SyncshellGalleryImageLikeDto(new GroupData(_gid), img.Id, false, 0))
                    .ConfigureAwait(false);
                if (result != null && index < _images.Count && _images[index].Id == result.ImageId)
                {
                    _images[index] = _images[index] with
                    {
                        LikedByMe = result.Liked, LikeCount = result.Count
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Popout photo like toggle failed");
        }
    }
}
