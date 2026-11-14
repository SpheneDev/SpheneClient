using System.IdentityModel.Tokens.Jwt;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Sphene.SpheneConfiguration.Models;
using Sphene.UI.Design;
using Sphene.Utils;

namespace Sphene.UI;

public partial class UiSharedService
{
    private Task<Uri?>? _discordOAuthCheck;
    private Task<string?>? _discordOAuthGetCode;
    private CancellationTokenSource _discordOAuthGetCts = new();
    private Task<Dictionary<string, string>>? _discordOAuthUIDs;
    private Dictionary<string, DateTime> _oauthTokenExpiry = new(StringComparer.Ordinal);

    public void DrawOAuth(ServerStorage selectedServer)
    {
        var oauthToken = selectedServer.OAuthToken;
        _ = ImRaii.PushIndent(10f);
        if (oauthToken == null)
        {
            if (_discordOAuthCheck == null)
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.QuestionCircle, "Check if Server supports Discord OAuth2"))
                {
                    _discordOAuthCheck = _serverConfigurationManager.CheckDiscordOAuth(selectedServer.ServerUri);
                }
            }
            else
            {
                if (!_discordOAuthCheck.IsCompleted)
                {
                    SpheneUI.Text.ColoredWrapped($"Checking OAuth2 compatibility with {selectedServer.ServerUri}", ImGuiColors.DalamudYellow);
                }
                else
                {
                    if (_discordOAuthCheck.Result != null)
                    {
                        SpheneUI.Text.ColoredWrapped("Server is compatible with Discord OAuth2", ImGuiColors.HealerGreen);
                    }
                    else
                    {
                        SpheneUI.Text.ColoredWrapped("Server is not compatible with Discord OAuth2", ImGuiColors.DalamudRed);
                    }
                }
            }

            if (_discordOAuthCheck != null && _discordOAuthCheck.IsCompleted)
            {
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowRight, "Authenticate with Server"))
                {
                    _discordOAuthGetCode = _serverConfigurationManager.GetDiscordOAuthToken(_discordOAuthCheck.Result!, selectedServer.ServerUri, _discordOAuthGetCts.Token);
                }
                else if (_discordOAuthGetCode != null && !_discordOAuthGetCode.IsCompleted)
                {
                    SpheneUI.Text.Wrapped("A browser window has been opened, follow it to authenticate. Click the button below if you accidentally closed the window and need to restart the authentication.");
                    SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Ban, "Cancel Authentication"))
                    {
                        _discordOAuthGetCts = _discordOAuthGetCts.CancelRecreate();
                        _discordOAuthGetCode = null;
                    }
                }
                else if (_discordOAuthGetCode != null && _discordOAuthGetCode.IsCompleted)
                {
                    SpheneUI.Text.Wrapped("Discord OAuth is completed, status: ");
                    ImGui.SameLine();
                    if (_discordOAuthGetCode.Result != null)
                    {
                        selectedServer.OAuthToken = _discordOAuthGetCode.Result;
                        _discordOAuthGetCode = null;
                        _serverConfigurationManager.Save();
                        SpheneUI.Text.ColoredWrapped("Success", ImGuiColors.HealerGreen);
                    }
                    else
                    {
                        SpheneUI.Text.ColoredWrapped("Failed, please check /xllog for more information", ImGuiColors.DalamudRed);
                    }
                }
            }
        }

        if (oauthToken != null)
        {
            if (!_oauthTokenExpiry.TryGetValue(oauthToken, out DateTime tokenExpiry))
            {
                try
                {
                    var handler = new JwtSecurityTokenHandler();
                    var jwt = handler.ReadJwtToken(oauthToken);
                    tokenExpiry = _oauthTokenExpiry[oauthToken] = jwt.ValidTo;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not parse OAuth token, deleting");
                    selectedServer.OAuthToken = null;
                    _serverConfigurationManager.Save();
                }
            }

            if (tokenExpiry > DateTime.UtcNow)
            {
                SpheneUI.Text.ColoredWrapped($"OAuth2 is enabled, linked to: Discord User {_serverConfigurationManager.GetDiscordUserFromToken(selectedServer)}", ImGuiColors.HealerGreen);
                SpheneUI.Text.Wrapped($"The OAuth2 token will expire on {tokenExpiry:yyyy-MM-dd} and automatically renew itself during login on or after {(tokenExpiry - TimeSpan.FromDays(7)):yyyy-MM-dd}.");
                using (ImRaii.Disabled(!CtrlPressed()))
                {
                    if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Exclamation, "Renew OAuth2 token manually") && CtrlPressed())
                    {
                        _ = _tokenProvider.TryUpdateOAuth2LoginTokenAsync(selectedServer, forced: true)
                            .ContinueWith((_) => _apiController.CreateConnectionsAsync());
                    }
                }
                SpheneUI.Controls.HelpText("Hold CTRL to manually refresh your OAuth2 token. Normally you do not need to do this.");
                SpheneUI.Layout.Space(SpheneUI.Spacing.X10);

                if ((_discordOAuthUIDs == null || _discordOAuthUIDs.IsCompleted)
                    && SpheneUI.Icons.TextButton(FontAwesomeIcon.Question, "Check Discord Connection"))
                {
                    _discordOAuthUIDs = _serverConfigurationManager.GetUIDsWithDiscordToken(selectedServer.ServerUri, oauthToken);
                }
                else if (_discordOAuthUIDs != null)
                {
                    if (!_discordOAuthUIDs.IsCompleted)
                    {
                        SpheneUI.Text.ColoredWrapped("Checking accounts on server", ImGuiColors.DalamudYellow);
                    }
                    else
                    {
                        var foundUids = _discordOAuthUIDs.Result?.Count ?? 0;
                        var primaryUid = _discordOAuthUIDs.Result?.FirstOrDefault() ?? new KeyValuePair<string, string>(string.Empty, string.Empty);
                        var vanity = string.IsNullOrEmpty(primaryUid.Value) ? "-" : primaryUid.Value;
                        if (foundUids > 0)
                        {
                            SpheneUI.Text.ColoredWrapped($"Found {foundUids} accounts on the server (vanity id: {vanity})",
                                ImGuiColors.HealerGreen);
                        }
                        else
                        {
                            SpheneUI.Text.ColoredWrapped($"Found no accounts linked to this OAuth2 account", ImGuiColors.DalamudRed);
                        }
                    }
                }
            }
            else
            {
                SpheneUI.Text.ColoredWrapped("The OAuth2 token is stale and expired. Please renew the OAuth2 connection.", ImGuiColors.DalamudRed);
                SpheneUI.Layout.VerticalSpace(SpheneUI.Spacing.Row);
                if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Exclamation, "Renew OAuth2 connection"))
                {
                    selectedServer.OAuthToken = null;
                    _serverConfigurationManager.Save();
                    _ = _serverConfigurationManager.CheckDiscordOAuth(selectedServer.ServerUri)
                        .ContinueWith(async (urlTask) =>
                        {
                            var url = await urlTask.ConfigureAwait(false);
                            var token = await _serverConfigurationManager.GetDiscordOAuthToken(url!, selectedServer.ServerUri, CancellationToken.None).ConfigureAwait(false);
                            selectedServer.OAuthToken = token;
                            _serverConfigurationManager.Save();
                            await _apiController.CreateConnectionsAsync().ConfigureAwait(false);
                        });
                }
            }

            DrawUnlinkOAuthButton(selectedServer);
        }
    }
    public void DrawUnlinkOAuthButton(ServerStorage selectedServer)
    {
        using (ImRaii.Disabled(!CtrlPressed()))
        {
            if (SpheneUI.Icons.TextButton(FontAwesomeIcon.Trash, "Unlink OAuth2 Connection") && UiSharedService.CtrlPressed())
            {
                selectedServer.OAuthToken = null;
                _serverConfigurationManager.Save();
                ResetOAuthTasksState();
            }
        }
        SpheneUI.Controls.HelpText("Hold CTRL to unlink the current OAuth2 connection.");
    }

    public void DrawUpdateOAuthUIDsButton(ServerStorage selectedServer)
    {
        if (!selectedServer.UseOAuth2)
            return;

        using (ImRaii.Disabled(string.IsNullOrEmpty(selectedServer.OAuthToken)))
        {
            if ((_discordOAuthUIDs == null || _discordOAuthUIDs.IsCompleted)
                && SpheneUI.Icons.TextButton(FontAwesomeIcon.ArrowsSpin, "Update accounts from service")
                && !string.IsNullOrEmpty(selectedServer.OAuthToken))
            {
                _discordOAuthUIDs = _serverConfigurationManager.GetUIDsWithDiscordToken(selectedServer.ServerUri, selectedServer.OAuthToken);
            }
        }
        DateTime tokenExpiry = DateTime.MinValue;
        if (!string.IsNullOrEmpty(selectedServer.OAuthToken) && !_oauthTokenExpiry.TryGetValue(selectedServer.OAuthToken, out tokenExpiry))
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jwt = handler.ReadJwtToken(selectedServer.OAuthToken);
                tokenExpiry = _oauthTokenExpiry[selectedServer.OAuthToken] = jwt.ValidTo;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not parse OAuth token, deleting");
                selectedServer.OAuthToken = null;
                _serverConfigurationManager.Save();
                tokenExpiry = DateTime.MinValue;
            }
        }
        if (string.IsNullOrEmpty(selectedServer.OAuthToken) || tokenExpiry < DateTime.UtcNow)
        {
            SpheneUI.Text.ColoredWrapped("You have no OAuth token or the OAuth token is expired. Please use the Service Configuration to link your OAuth2 account or refresh the token.", ImGuiColors.DalamudRed);
        }
    }
    internal void ResetOAuthTasksState()
    {
        _discordOAuthCheck = null;
        _discordOAuthGetCts = _discordOAuthGetCts.CancelRecreate();
        _discordOAuthGetCode = null;
        _discordOAuthUIDs = null;
    }

}
