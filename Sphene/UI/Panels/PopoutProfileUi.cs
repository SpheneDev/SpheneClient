using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Sphene.API.Data.Extensions;
using Sphene.SpheneConfiguration;
using Sphene.PlayerData.Pairs;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Microsoft.Extensions.Logging;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Panels;

public class PopoutProfileUi : WindowMediatorSubscriberBase
{
    private readonly SpheneProfileManager _spheneProfileManager;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private Vector2 _lastMainPos = Vector2.Zero;
    private Vector2 _lastMainSize = Vector2.Zero;
    private byte[] _lastProfilePicture = [];
    private byte[] _lastSupporterPicture = [];
    private Pair? _pair;
    private string? _pairKey;

    public PopoutProfileUi(ILogger<PopoutProfileUi> logger, SpheneMediator mediator, ServerConfigurationManager serverManager, SpheneConfigService SpheneConfigService,
        SpheneProfileManager spheneProfileManager, PairManager pairManager, PerformanceCollectorService performanceCollectorService) : base(logger, mediator, "###SphenePopoutProfileUI", performanceCollectorService)
    {
        _serverManager = serverManager;
        _spheneProfileManager = spheneProfileManager;
        _pairManager = pairManager;
        Flags = ImGuiWindowFlags.NoDecoration;

        Mediator.Subscribe<ProfilePopoutToggle>(this, (msg) =>
        {
            IsOpen = msg.Pair != null;
            _pair = msg.Pair;
            _pairKey = msg.Pair == null ? null : $"popout:{msg.Pair.UserData.UID}";
            _lastProfilePicture = [];
            _lastSupporterPicture = [];
        });

        Mediator.Subscribe<CompactUiChange>(this, (msg) =>
        {
            if (msg.Size != Vector2.Zero)
            {
                var border = ImGui.GetStyle().WindowBorderSize;
                var padding = ImGui.GetStyle().WindowPadding;
                Size = new(256 + (padding.X * 2) + border, msg.Size.Y / ImGuiHelpers.GlobalScale);
                _lastMainSize = msg.Size;
            }
            var mainPos = msg.Position == Vector2.Zero ? _lastMainPos : msg.Position;
            if (SpheneConfigService.Current.ProfilePopoutRight)
            {
                Position = new(mainPos.X + _lastMainSize.X * ImGuiHelpers.GlobalScale, mainPos.Y);
            }
            else
            {
                Position = new(mainPos.X - Size!.Value.X * ImGuiHelpers.GlobalScale, mainPos.Y);
            }

            if (msg.Position != Vector2.Zero)
            {
                _lastMainPos = msg.Position;
            }
        });

        IsOpen = false;
    }

    // AlwaysAutoResize fights the fold animation — the window would not shrink.
    protected override bool ChromeCollapsible => false;

    protected override void DrawInternal()
    {
        if (_pair == null) return;

        try
        {
            var spacing = ImGui.GetStyle().ItemSpacing;

            var spheneProfile = _spheneProfileManager.GetSpheneProfile(_pair.UserData);

            var profileKey = _pairKey!;
            if (!spheneProfile.ImageData.Value.SequenceEqual(_lastProfilePicture))
            {
                _lastProfilePicture = spheneProfile.ImageData.Value;
                SpheneUI.Media.Evict(profileKey);
            }
            var textureWrap = SpheneUI.Media.Texture(profileKey, _lastProfilePicture);

            var supporterKey = profileKey + ":supporter";
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

            SpheneUI.Text.Big(_pair.UserData.AliasOrUID, ImGuiColors.HealerGreen);

            ImGuiHelpers.ScaledDummy(spacing.Y, spacing.Y);
            var textPos = ImGui.GetCursorPosY();
            SpheneUI.Layout.Separator();
            var imagePos = ImGui.GetCursorPos();
            ImGuiHelpers.ScaledDummy(256, 256 * ImGuiHelpers.GlobalScale + spacing.Y);
            var note = _serverManager.GetNoteForUid(_pair.UserData.UID);
            if (!string.IsNullOrEmpty(note))
            {
                SpheneUI.Text.Colored(note, ImGuiColors.DalamudGrey);
            }
        string status = _pair.IsMutuallyVisible ? "Visible" : (_pair.IsOnline ? "Online" : "Offline");
        SpheneUI.Text.Colored(status, (_pair.IsMutuallyVisible || _pair.IsOnline) ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
        if (_pair.IsMutuallyVisible)
            {
                ImGui.SameLine();
                SpheneUI.Text.Plain($"({_pair.PlayerName})");
            }
            if (_pair.UserPair.IndividualPairStatus == API.Data.Enum.IndividualPairStatus.Bidirectional)
            {
                SpheneUI.Text.Plain("Directly paired");
                if (_pair.UserPair.OwnPermissions.IsPaused())
                {
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("You: paused", ImGuiColors.DalamudYellow);
                }
                if (_pair.UserPair.OtherPermissions.IsPaused())
                {
                    ImGui.SameLine();
                    SpheneUI.Text.Colored("They: paused", ImGuiColors.DalamudYellow);
                }
            }
            if (_pair.UserPair.Groups.Any())
            {
                SpheneUI.Text.Plain("Paired through Syncshells:");
                foreach (var group in _pair.UserPair.Groups)
                {
                    var groupNote = _serverManager.GetNoteForGid(group);
                    var groupName = _pairManager.GroupPairs.First(f => string.Equals(f.Key.GID, group, StringComparison.Ordinal)).Key.GroupAliasOrGID;
                    var groupString = string.IsNullOrEmpty(groupNote) ? groupName : $"{groupNote} ({groupName})";
                    SpheneUI.Text.Plain("- " + groupString);
                }
            }

            SpheneUI.Layout.Separator();
            var font = SpheneUI.GameFont.Push();
            var remaining = ImGui.GetWindowContentRegionMax().Y - ImGui.GetCursorPosY();
            var descText = spheneProfile.Description;
            var textSize = ImGui.CalcTextSize(descText, wrapWidth: 256f * ImGuiHelpers.GlobalScale);
            bool trimmed = textSize.Y > remaining;
            while (textSize.Y > remaining && descText.Contains(' '))
            {
                descText = descText[..descText.LastIndexOf(' ')].TrimEnd();
                textSize = ImGui.CalcTextSize(descText + $"...{Environment.NewLine}[Open Full Profile for complete description]", wrapWidth: 256f * ImGuiHelpers.GlobalScale);
            }
            SpheneUI.Text.Wrapped(trimmed ? descText + $"...{Environment.NewLine}[Open Full Profile for complete description]" : spheneProfile.Description);
            font.Dispose();

            var padding = ImGui.GetStyle().WindowPadding.X / 2;
            if (textureWrap != null)
            {
                var imgMin = new Vector2(rectMin.X + padding, rectMin.Y + spacing.Y + imagePos.Y);
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
}
