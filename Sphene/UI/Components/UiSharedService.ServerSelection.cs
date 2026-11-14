using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.SpheneConfiguration.Models;
using Sphene.UI.Design;

namespace Sphene.UI;

public partial class UiSharedService
{
    private int _serverSelectionIndex = -1;

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
        if (SpheneUI.Controls.BeginComboBox("Select Service", comboEntries[_serverSelectionIndex]))
        {
            for (int i = 0; i < comboEntries.Length; i++)
            {
                bool isSelected = _serverSelectionIndex == i;
                if (SpheneUI.Controls.Selectable(comboEntries[i], isSelected))
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

            SpheneUI.Controls.EndComboBox();
        }

        if (showConnect)
        {
            ImGui.SameLine();
            var text = "Connect";
            if (_serverSelectionIndex == _serverConfigurationManager.CurrentServerIndex) text = "Reconnect";
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Link, text))
            {
                _serverConfigurationManager.SelectServer(_serverSelectionIndex);
                _ = _apiController.CreateConnectionsAsync();
            }
        }

        return _serverSelectionIndex;
    }

    public void DrawUIDComboForAuthentication(int indexOffset, Authentication item, string serverUri, ILogger? logger = null)
    {
        using (ImRaii.Disabled(_discordOAuthUIDs is not { IsCompletedSuccessfully: true }))
        {
            var aliasPairs = _discordOAuthUIDs is { IsCompletedSuccessfully: true }
                ? _discordOAuthUIDs.Result?.Select(t => new UidAliasPair(t.Key, t.Value)).ToList() ?? [new UidAliasPair(item.UID ?? null, null)]
                : [new UidAliasPair(item.UID ?? null, null)];
            var uidComboName = "Account###" + item.CharacterName + item.WorldId + serverUri + indexOffset + aliasPairs.Count;
            SpheneUI.Controls.Combo(uidComboName, aliasPairs,
                (v) =>
                {
                    if (v is null)
                        return "No account set";

                    if (!string.IsNullOrEmpty(v.Alias))
                    {
                        return v.Alias;
                    }

                    if (string.IsNullOrEmpty(v.UID))
                        return "No account set";

                    return v.UID;
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
            SpheneUI.AttachTooltip("Use the button above to update your accounts from the service before you can assign them to characters.");
        }
    }
}
