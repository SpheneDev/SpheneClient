using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Sphene.Services.ServerConfiguration;
using System.Numerics;
using Sphene.UI.Design;

namespace Sphene.UI.Components.Popup;

public class CensusPopupHandler : IPopupHandler
{
    private readonly ServerConfigurationManager _serverConfigurationManager;
    public CensusPopupHandler(ServerConfigurationManager serverConfigurationManager)
    {
        _serverConfigurationManager = serverConfigurationManager;
    }

    private Vector2 _size = new(600, 450);
    public Vector2 PopupSize => _size;

    public bool ShowClose => false;

    public void DrawContent()
    {
        var start = 0f;
        using (SpheneUI.UidFont.Push())
        {
            start = ImGui.GetCursorPosY() - ImGui.CalcTextSize("Sphene Census Data").Y;
            SpheneUI.Text.Wrapped("Sphene Census Participation");
        }
        ImGuiHelpers.ScaledDummy(5f);
        SpheneUI.Text.Wrapped("If you are seeing this popup you are updating from a Sphene version that did not collect census data. Please read the following carefully.");
        SpheneUI.Layout.Separator();
        SpheneUI.Text.Wrapped("Sphene Census is a data collecting service that can be used for statistical purposes. " +
            "All data collected through Sphene Census is temporary and will be stored associated with your UID on the connected service as long as you are connected. " +
            "The data cannot be used for long term tracking of individuals.");
        SpheneUI.Text.Wrapped("If enabled, Sphene Census will collect following data:" + Environment.NewLine
            + "- Currently connected World" + Environment.NewLine
            + "- Current Gender (reflecting Glamourer changes)" + Environment.NewLine
            + "- Current Race (reflecting Glamourer changes)" + Environment.NewLine
            + "- Current Clan (i.e. Seeker of the Sun, Keeper of the Moon, etc., reflecting Glamourer changes)");
        SpheneUI.Text.Wrapped("To consent to collecting census data press the appropriate button below.");
        SpheneUI.Text.Wrapped("This setting can be changed anytime in the Sphene Settings.");
        var width = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
        var buttonSize = ImGuiHelpers.GetButtonSize("I consent to send my census data");
        ImGuiHelpers.ScaledDummy(5f);
        if (SpheneUI.Controls.Button("I consent to send my census data", new Vector2(width, buttonSize.Y * 2.5f)))
        {
            _serverConfigurationManager.SendCensusData = true;
            _serverConfigurationManager.ShownCensusPopup = true;
            ImGui.CloseCurrentPopup();
        }
        ImGuiHelpers.ScaledDummy(1f);
        if (SpheneUI.Controls.Button("I do not consent to send my census data", new Vector2(width, buttonSize.Y)))
        {
            _serverConfigurationManager.SendCensusData = false;
            _serverConfigurationManager.ShownCensusPopup = true;
            ImGui.CloseCurrentPopup();
        }
        var height = ImGui.GetCursorPosY() - start;
        _size = _size with { Y = height };
    }
}
