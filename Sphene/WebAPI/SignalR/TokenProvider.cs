using Sphene.API.Routes;
using Sphene.SpheneConfiguration.Models;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Services.ServerConfiguration;
using Sphene.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Reflection;
using Sphene.SpheneConfiguration;

namespace Sphene.WebAPI.SignalR;

public sealed class TokenProvider : IDisposable, IMediatorSubscriber
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TokenProvider> _logger;
    private readonly ServerConfigurationManager _serverManager;
    private readonly SpheneConfigService _configService;
    private readonly ConcurrentDictionary<JwtIdentifier, string> _tokenCache = new();

    public TokenProvider(ILogger<TokenProvider> logger, ServerConfigurationManager serverManager, DalamudUtilService dalamudUtil, SpheneMediator spheneMediator, HttpClient httpClient, SpheneConfigService configService)
    {
        _logger = logger;
        _serverManager = serverManager;
        _dalamudUtil = dalamudUtil;
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        Mediator = spheneMediator;
        _httpClient = httpClient;
        _configService = configService;
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) =>
        {
            _lastJwtIdentifier = null;
            _tokenCache.Clear();
        });
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) =>
        {
            _lastJwtIdentifier = null;
            _tokenCache.Clear();
        });
    }

    public SpheneMediator Mediator { get; }

    private JwtIdentifier? _lastJwtIdentifier;

    public void Dispose()
    {
        Mediator.UnsubscribeAll(this);
    }

    private Uri GetAuthServiceUrl()
    {
        var apiUrl = GetBaseApiUrl();
        var baseUrl = apiUrl
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase);
        
        // Replace port 6000 with 8080 for auth service
        if (baseUrl.Contains(":6000"))
        {
            baseUrl = baseUrl.Replace(":6000", ":8080");
        }
        else if (!baseUrl.Contains(":") && baseUrl.StartsWith("http://"))
        {
            // If no port specified, add 8080
            baseUrl += ":8080";
        }
        
        return new Uri(baseUrl);
    }

    private string GetBaseApiUrl()
    {
        try
        {
            var isTestBuild = (Assembly.GetExecutingAssembly().GetName().Version?.Revision ?? 0) != 0;
            if (isTestBuild && _configService.Current.UseTestServerOverride && !string.IsNullOrWhiteSpace(_configService.Current.TestServerApiUrl))
            {
                return _configService.Current.TestServerApiUrl.TrimEnd('/');
            }
        }
        catch { }
        return _serverManager.CurrentApiUrl;
    }

    public async Task<string> GetNewToken(bool isRenewal, JwtIdentifier identifier, CancellationToken ct)
    {
        Uri tokenUri;
        string response = string.Empty;
        HttpResponseMessage result;

        try
        {
            if (!isRenewal)
            {
                _logger.LogDebug("GetNewToken: Requesting");

                if (!_serverManager.CurrentServer.UseOAuth2)
                {
                    tokenUri = SpheneAuth.AuthFullPath(GetAuthServiceUrl());
                    var secretKey = _serverManager.GetSecretKey(out _)!;
                    var auth = secretKey.GetHash256();
                    _logger.LogInformation("Sending SecretKey Request to server with auth {auth}", string.Join("", identifier.SecretKeyOrOAuth.Take(10)));
                    result = await _httpClient.PostAsync(tokenUri, new FormUrlEncodedContent(
                    [
                            new KeyValuePair<string, string>("auth", auth),
                            new KeyValuePair<string, string>("charaIdent", await _dalamudUtil.GetPlayerNameHashedAsync().ConfigureAwait(false)),
                    ]), ct).ConfigureAwait(false);
                }
                else
                {
                    tokenUri = SpheneAuth.AuthWithOauthFullPath(GetAuthServiceUrl());
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, tokenUri.ToString());
                    request.Content = new FormUrlEncodedContent([
                        new KeyValuePair<string, string>("uid", identifier.UID),
                        new KeyValuePair<string, string>("charaIdent", identifier.CharaHash)
                        ]);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identifier.SecretKeyOrOAuth);
                    _logger.LogInformation("Sending OAuth Request to server with auth {auth}", string.Join("", identifier.SecretKeyOrOAuth.Take(10)));
                    result = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                }
            }
            else
            {
                _logger.LogDebug("GetNewToken: Renewal");

                tokenUri = SpheneAuth.RenewTokenFullPath(GetAuthServiceUrl());
                HttpRequestMessage request = new(HttpMethod.Get, tokenUri.ToString());
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenCache[identifier]);
                result = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            }

            response = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
            result.EnsureSuccessStatusCode();
            _tokenCache[identifier] = response;
        }
        catch (HttpRequestException ex)
        {
            _tokenCache.TryRemove(identifier, out _);

            _logger.LogError(ex, "GetNewToken: Failure to get token");

            if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                if (isRenewal)
                    Mediator.Publish(new NotificationMessage("Error refreshing token", "Your authentication token could not be renewed. Try reconnecting to Sphene manually.",
                    NotificationType.Error));
                else
                    Mediator.Publish(new NotificationMessage("Error generating token", "Your authentication token could not be generated. Check Sphenes Main UI (/sphene in chat) to see the error message.",
                    NotificationType.Error));
                Mediator.Publish(new DisconnectedMessage());
                throw new SpheneAuthFailureException(response);
            }

            throw;
        }

        var handler = new JwtSecurityTokenHandler();
        var jwtToken = handler.ReadJwtToken(response);
        _logger.LogTrace("GetNewToken: JWT {token}", response);
        _logger.LogDebug("GetNewToken: Valid until {date}, ValidClaim until {date}", jwtToken.ValidTo,
                new DateTime(long.Parse(jwtToken.Claims.Single(c => string.Equals(c.Type, "expiration_date", StringComparison.Ordinal)).Value), DateTimeKind.Utc));
        var dateTimeMinus10 = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(10));
        var dateTimePlus10 = DateTime.UtcNow.Add(TimeSpan.FromMinutes(10));
        var tokenTime = jwtToken.ValidTo.Subtract(TimeSpan.FromHours(6));
        if (tokenTime <= dateTimeMinus10 || tokenTime >= dateTimePlus10)
        {
            _tokenCache.TryRemove(identifier, out _);
            Mediator.Publish(new NotificationMessage("Invalid system clock", "The clock of your computer is invalid. " +
                "Sphene will not function properly if the time zone is not set correctly. " +
                "Please set your computers time zone correctly and keep your clock synchronized with the internet.",
                NotificationType.Error));
            throw new InvalidOperationException($"JwtToken is behind DateTime.UtcNow, DateTime.UtcNow is possibly wrong. DateTime.UtcNow is {DateTime.UtcNow}, JwtToken.ValidTo is {jwtToken.ValidTo}");
        }
        return response;
    }

    private async Task<JwtIdentifier?> GetIdentifier()
    {
        JwtIdentifier jwtIdentifier;
        try
        {
            var playerIdentifier = await _dalamudUtil.GetPlayerNameHashedAsync().ConfigureAwait(false);

            if (string.IsNullOrEmpty(playerIdentifier))
            {
                try
                {
                    var cid = await _dalamudUtil.GetCIDAsync().ConfigureAwait(false);
                    playerIdentifier = cid.ToString().GetHash256();
                    _logger.LogDebug("GetIdentifier: Fallback computed playerIdentifier from CID");
                }
                catch (Exception ex)
                {
                    _logger.LogTrace("GetIdentifier: PlayerIdentifier was null and CID fallback failed, returning last identifier {identifier}", _lastJwtIdentifier);
                    _logger.LogDebug(ex, "GetIdentifier: CID fallback failed");
                    return _lastJwtIdentifier;
                }
            }

            if (_serverManager.CurrentServer.UseOAuth2)
            {
                var (OAuthToken, UID) = _serverManager.GetOAuth2(out _)
                    ?? throw new InvalidOperationException("Requested OAuth2 but received null");

                jwtIdentifier = new(GetBaseApiUrl(),
                    playerIdentifier,
                    UID, OAuthToken);
            }
            else
            {
                var secretKey = _serverManager.GetSecretKey(out _)
                    ?? throw new InvalidOperationException("Requested SecretKey but received null");

                jwtIdentifier = new(GetBaseApiUrl(),
                                    playerIdentifier,
                                    string.Empty,
                                    secretKey);
            }
            _lastJwtIdentifier = jwtIdentifier;
        }
        catch (Exception ex)
        {
            if (_lastJwtIdentifier == null)
            {
                _logger.LogError("GetIdentifier: No last identifier found, aborting");
                return null;
            }

            _logger.LogWarning(ex, "GetIdentifier: Could not get JwtIdentifier for some reason or another, reusing last identifier {identifier}", _lastJwtIdentifier);
            jwtIdentifier = _lastJwtIdentifier;
        }

        _logger.LogDebug("GetIdentifier: Using identifier {identifier}", jwtIdentifier);
        return jwtIdentifier;
    }

    public async Task<string?> GetToken()
    {
        JwtIdentifier? jwtIdentifier = await GetIdentifier().ConfigureAwait(false);
        if (jwtIdentifier == null) return null;

        if (_tokenCache.TryGetValue(jwtIdentifier, out var token))
        {
            return token;
        }

        throw new InvalidOperationException("No token present");
    }

    public async Task<string?> GetOrUpdateToken(CancellationToken ct)
    {
        JwtIdentifier? jwtIdentifier = await GetIdentifier().ConfigureAwait(false);
        if (jwtIdentifier == null) return null;

        bool renewal = false;
        if (_tokenCache.TryGetValue(jwtIdentifier, out var token))
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);
            if (jwt.ValidTo == DateTime.MinValue || jwt.ValidTo.Subtract(TimeSpan.FromMinutes(5)) > DateTime.UtcNow)
            {
                return token;
            }

            _logger.LogDebug("GetOrUpdate: Cached token requires renewal, token valid to: {valid}, UtcTime is {utcTime}", jwt.ValidTo, DateTime.UtcNow);
            renewal = true;
        }
        else
        {
            _logger.LogDebug("GetOrUpdate: Did not find token in cache, requesting a new one");
        }

        _logger.LogTrace("GetOrUpdate: Getting new token");
        return await GetNewToken(renewal, jwtIdentifier, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryUpdateOAuth2LoginTokenAsync(ServerStorage currentServer, bool forced = false)
    {
        var oauth2 = _serverManager.GetOAuth2(out _);
        if (oauth2 == null) return false;

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(oauth2.Value.OAuthToken);
        if (!forced)
        {
            if (jwt.ValidTo == DateTime.MinValue || jwt.ValidTo.Subtract(TimeSpan.FromDays(7)) > DateTime.Now)
                return true;

            if (jwt.ValidTo < DateTime.UtcNow)
                return false;
        }

        var tokenUri = SpheneAuth.RenewOAuthTokenFullPath(GetAuthServiceUrl());
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, tokenUri.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauth2.Value.OAuthToken);
        _logger.LogInformation("Sending Request to server with auth {auth}", string.Join("", oauth2.Value.OAuthToken.Take(10)));
        var result = await _httpClient.SendAsync(request).ConfigureAwait(false);

        if (!result.IsSuccessStatusCode)
        {
            _logger.LogWarning("Could not renew OAuth2 Login token, error code {error}", result.StatusCode);
            currentServer.OAuthToken = null;
            _serverManager.Save();
            return false;
        }

        var newToken = await result.Content.ReadAsStringAsync().ConfigureAwait(false);
        currentServer.OAuthToken = newToken;
        _serverManager.Save();

        return true;
    }
}
