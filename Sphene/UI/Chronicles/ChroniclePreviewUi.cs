using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Sphene.API.Data;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.UI.Components;
using Sphene.UI.Design;
using Sphene.UI.Hub;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Sphene.UI.Chronicles;

/// <summary>
/// The Chronicle editor's Live Preview popout - renders the draft through the
/// same <see cref="ChronicleReaderView"/> the profile detail page uses, so the
/// visitor presentation cannot diverge from what will be published. The
/// window is movable/resizable, reflects unsaved edits every frame, and is
/// fully inert: no server writes, no comment/reaction affordances, and
/// opening or closing it never mutates the draft.
/// </summary>
public sealed class ChroniclePreviewUi : WindowMediatorSubscriberBase
{
    private const float MinHeightUnscaled = 380f;
    private const float InitialHeightUnscaled = 640f;

    private readonly SpheneProfileManager _spheneProfileManager;
    /// <summary>Widget instance - not a service; the preview binds it only so
    /// the reader surface can draw (ops stay null, nothing interactive).</summary>
    private readonly CommentThreadView _comments = new();
    private ChronicleComposer? _composer;
    private string _authorUid = string.Empty;
    private string _authorName = "Author";
    private Vector4 _accent = SpheneUI.Palette.SpheneGold;
    private IDalamudTextureWrap? _avatarWrap;
    private string _avatarKey = string.Empty;
    private byte[] _lastAvatarBytes = [];

    public ChroniclePreviewUi(ILogger<ChroniclePreviewUi> logger, SpheneMediator mediator,
        PerformanceCollectorService performanceCollectorService,
        SpheneProfileManager spheneProfileManager)
        : base(logger, mediator, "Chronicle Preview###ChroniclePreview", performanceCollectorService)
    {
        _spheneProfileManager = spheneProfileManager;
        SizeConstraints = new()
        {
            MinimumSize = new(SpheneHubUi.MinContentWidth, MinHeightUnscaled),
            MaximumSize = new(SpheneHubUi.MinContentWidth, 10000)
        };
        Size = new Vector2(SpheneHubUi.MinContentWidth, InitialHeightUnscaled);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;
        Mediator.Subscribe<OpenChroniclePreviewMessage>(this, msg =>
        {
            _composer = msg.Composer;
            _authorUid = msg.AuthorUid;
            _authorName = string.IsNullOrWhiteSpace(msg.AuthorDisplayName)
                ? "Author" : msg.AuthorDisplayName;
            _accent = msg.Accent;
            _avatarWrap = null;
            WindowName = $"{_authorName} · Chronicle Preview###ChroniclePreview";
            IsOpen = true;
        });
    }

    protected override string? ChromeTrailingText => "Draft preview - nothing is saved";

    /// <summary>Locks the width to the content column; the height range tracks
    /// the live work area - same fixed-width/resizable-height contract as the
    /// community window.</summary>
    public override void PreDraw()
    {
        base.PreDraw();
        if (SpheneUI.Chrome.CollapseAmount(WindowName) <= 0f
            && SizeConstraints is { } constraints)
        {
            constraints.MaximumSize = new Vector2(constraints.MinimumSize.X,
                SpheneUI.Chrome.WorkAreaMax.Y);
        }
    }

    protected override void DrawInternal()
    {
        var composer = _composer;
        if (composer == null || !composer.Open)
        {
            SpheneUI.Layout.EmptyState(FontAwesomeIcon.EyeSlash, "No draft",
                "The editor this preview was bound to has been closed.");
            return;
        }
        try
        {
            var profile = _spheneProfileManager.GetSpheneProfile(new UserData(_authorUid));
            byte[] bytes;
            try
            {
                bytes = profile.ImageData.Value;
            }
            catch (Exception)
            {
                bytes = [];
            }
            if (!bytes.SequenceEqual(_lastAvatarBytes))
            {
                SpheneUI.Media.Evict(_avatarKey);
                _lastAvatarBytes = bytes;
                _avatarKey = $"chronicle-preview:{_authorUid}:{bytes.GetHashCode()}";
                _avatarWrap = null;
            }
            _avatarWrap ??= bytes.Length == 0 ? null
                : SpheneUI.Media.Texture(_avatarKey, () => bytes);
            // Same card surface the published entries render inside, so
            // card geometry, fills and bounds match the list exactly.
            SpheneUI.Layout.Surface(() =>
            {
                ChronicleReaderView.Draw(composer.BuildPreviewDto(), _authorUid,
                    _authorName, _avatarWrap, _accent, _comments, ops: null,
                    previewNote: "Comments, reactions and profile links stay inert here - "
                        + "publish to make them live.");
            }, id: "chronicle-preview-card", border: true, accentColor: _accent);
            _lastFaultSignature = null;
        }
        catch (Exception ex)
        {
            // Log the real stack once per signature, then draw the fault
            // in-window - a rethrow would spam "failed to draw" every frame.
            var sig = ex.GetType().Name + ":" + ex.Message;
            if (!string.Equals(sig, _lastFaultSignature, StringComparison.Ordinal))
            {
                _lastFaultSignature = sig;
                _logger.LogError(ex, "Chronicle preview render fault");
            }
            SpheneUI.Layout.Callout("Preview render fault: " + ex.Message,
                FontAwesomeIcon.ExclamationTriangle, SpheneUI.Palette.Warning,
                title: "Preview error");
        }
    }

    private string? _lastFaultSignature;
}
