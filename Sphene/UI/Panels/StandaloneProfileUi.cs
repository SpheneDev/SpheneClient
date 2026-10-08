using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Sphene.API.Data.Extensions;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Microsoft.Extensions.Logging;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public class StandaloneProfileUi : WindowMediatorSubscriberBase
{
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private bool _adjustedForScrollBars = false;
    private byte[] _lastProfilePicture = [];
    private byte[] _lastSupporterPicture = [];

    public StandaloneProfileUi(ILogger<StandaloneProfileUi> logger, SpheneMediator mediator, ServerConfigurationManager serverManager, SpheneProfileManager spheneProfileManager, PairManager pairManager, Pair pair,
        PerformanceCollectorService performanceCollector)
        : base(logger, mediator, "Sphene Profile of " + pair.UserData.AliasOrUID + "##SpheneStandaloneProfileUI" + pair.UserData.AliasOrUID, performanceCollector)
    {
        _serverManager = serverManager;
        _spheneProfileManager = spheneProfileManager;
        Pair = pair;
        _pairManager = pairManager;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize;

        var spacing = ImGui.GetStyle().ItemSpacing;

        Size = new(512 + spacing.X * 3 + ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().WindowBorderSize, 512);

        IsOpen = true;
    }

    public Pair Pair { get; init; }

    // AlwaysAutoResize fights the fold animation — the window would not shrink.
    protected override bool ChromeCollapsible => false;

    protected override void DrawInternal()
    {
        try
        {
            var spacing = ImGui.GetStyle().ItemSpacing;

            var spheneProfile = _spheneProfileManager.GetSpheneProfile(Pair.UserData);

            var profileKey = $"profile:{Pair.UserData.UID}";
            if (!spheneProfile.ImageData.Value.SequenceEqual(_lastProfilePicture))
            {
                _lastProfilePicture = spheneProfile.ImageData.Value;
                SpheneUI.Media.Evict(profileKey);
            }
            var textureWrap = SpheneUI.Media.Texture(profileKey, _lastProfilePicture);

            var supporterKey = $"supporter:{Pair.UserData.UID}";
            if (!spheneProfile.SupporterImageData.Value.SequenceEqual(_lastSupporterPicture))
            {
                _lastSupporterPicture = spheneProfile.SupporterImageData.Value;
                SpheneUI.Media.Evict(supporterKey);
            }
            var supporterTextureWrap = string.IsNullOrEmpty(spheneProfile.Base64SupporterPicture)
                ? null
                : SpheneUI.Media.Texture(supporterKey, _lastSupporterPicture);

            var drawList = ImGui.GetWindowDrawList();
            var rectMin = drawList.GetClipRectMin();
            var rectMax = drawList.GetClipRectMax();
            var headerSize = ImGui.GetCursorPosY() - ImGui.GetStyle().WindowPadding.Y;

            SpheneUI.Text.Big(Pair.UserData.AliasOrUID, ImGuiColors.HealerGreen);

            ImGuiHelpers.ScaledDummy(new Vector2(spacing.Y, spacing.Y));
            var textPos = ImGui.GetCursorPosY() - headerSize;
            SpheneUI.Layout.Separator();
            var pos = ImGui.GetCursorPos() with { Y = ImGui.GetCursorPosY() - headerSize };
            ImGuiHelpers.ScaledDummy(new Vector2(256, 256 + spacing.Y));
            var postDummy = ImGui.GetCursorPosY();
            ImGui.SameLine();
            var descriptionTextSize = ImGui.CalcTextSize(spheneProfile.Description, wrapWidth: 256f);
            var descriptionChildHeight = rectMax.Y - pos.Y - rectMin.Y - spacing.Y * 2;
            if (descriptionTextSize.Y > descriptionChildHeight && !_adjustedForScrollBars)
            {
                Size = Size!.Value with { X = Size.Value.X + ImGui.GetStyle().ScrollbarSize };
                _adjustedForScrollBars = true;
            }
            else if (descriptionTextSize.Y < descriptionChildHeight && _adjustedForScrollBars)
            {
                Size = Size!.Value with { X = Size.Value.X - ImGui.GetStyle().ScrollbarSize };
                _adjustedForScrollBars = false;
            }
            var childFrame = ImGuiHelpers.ScaledVector2(256 + ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().WindowBorderSize, descriptionChildHeight);
            childFrame = childFrame with
            {
                X = childFrame.X + (_adjustedForScrollBars ? ImGui.GetStyle().ScrollbarSize : 0),
                Y = childFrame.Y / ImGuiHelpers.GlobalScale
            };
            if (ImGui.BeginChildFrame(1000, childFrame))
            {
                using var _ = SpheneUI.GameFont.Push();
                SpheneUI.Text.Wrapped(spheneProfile.Description);
            }
            ImGui.EndChildFrame();

            ImGui.SetCursorPosY(postDummy);
            var note = _serverManager.GetNoteForUid(Pair.UserData.UID);
            if (!string.IsNullOrEmpty(note))
            {
                SpheneUI.Text.Colored(note, ImGuiColors.DalamudGrey);
            }
        string status = Pair.IsMutuallyVisible ? "Visible" : (Pair.IsOnline ? "Online" : "Offline");
        SpheneUI.Text.Colored(status, (Pair.IsMutuallyVisible || Pair.IsOnline) ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
        if (Pair.IsMutuallyVisible)
            {
                ImGui.SameLine();
                SpheneUI.Text.Plain($"({Pair.PlayerName})");
            }
            if (Pair.UserPair != null)
            {
                SpheneUI.Text.Plain("Directly paired");
                if (Pair.UserPair.OwnPermissions.IsPaused())
                {
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("You: paused", ImGuiColors.DalamudYellow);
                }
                if (Pair.UserPair.OtherPermissions.IsPaused())
                {
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("They: paused", ImGuiColors.DalamudYellow);
                }
            }

            if (Pair.UserPair != null && Pair.UserPair.Groups.Any())
            {
                SpheneUI.Text.Plain("Paired through Syncshells:");
                foreach (var group in Pair.UserPair.Groups)
                {
                    var groupNote = _serverManager.GetNoteForGid(group);
                    var groupName = _pairManager.GroupPairs.First(f => string.Equals(f.Key.GID, group, StringComparison.Ordinal)).Key.GroupAliasOrGID;
                    var groupString = string.IsNullOrEmpty(groupNote) ? groupName : $"{groupNote} ({groupName})";
                    SpheneUI.Text.Plain("- " + groupString);
                }
            }

            var padding = ImGui.GetStyle().WindowPadding.X / 2;
            if (textureWrap != null)
            {
                var imgMin = new Vector2(rectMin.X + padding, rectMin.Y + spacing.Y + pos.Y);
                SpheneUI.Media.ImageInRect(drawList, textureWrap, imgMin,
                    imgMin + new Vector2(256f * ImGuiHelpers.GlobalScale), ImageFit.Fit);
            }
            if (supporterTextureWrap != null)
            {
                const float iconSize = 38;
                SpheneUI.Media.ImageInRect(drawList, supporterTextureWrap,
                    new Vector2(rectMax.X - iconSize - spacing.X, rectMin.Y + (textPos / 2) - (iconSize / 2)),
                    new Vector2(rectMax.X - spacing.X, rectMin.Y + iconSize + (textPos / 2) - (iconSize / 2)),
                    ImageFit.Stretch);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during draw tooltip");
        }
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}
