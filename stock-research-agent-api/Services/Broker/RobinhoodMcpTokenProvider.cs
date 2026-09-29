using System.Text.Json.Nodes;

namespace StockResearchAgent.Api.Services.Broker;

// oauth_stored = the one-time browser login (RobinhoodOAuthService) with the refresh token kept in Supabase.
public enum RobinhoodMcpAuthMode { none, bearer, oauth_refresh, oauth_stored }

// Robinhood hasn't published how a headless client should authenticate, so the
// token source is swappable via ROBINHOOD_MCP_AUTH_MODE without touching the adapter.
public interface IRobinhoodMcpTokenProvider
{
    RobinhoodMcpAuthMode Mode { get; }
    bool IsConfigured { get; }
    Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct);
}

public class RobinhoodMcpTokenProvider : IRobinhoodMcpTokenProvider
{
    private readonly ILogger<RobinhoodMcpTokenProvider> _logger;
    private readonly RobinhoodOAuthService _oauth;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly string _staticToken;
    private readonly string _tokenUrl;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _scope;
    private string _refreshToken;

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    public RobinhoodMcpAuthMode Mode { get; }
    public bool IsConfigured { get; }

    public RobinhoodMcpTokenProvider(IConfiguration configuration, RobinhoodOAuthService oauth, ILogger<RobinhoodMcpTokenProvider> logger)
    {
        _logger = logger;
        _oauth = oauth;

        var modeRaw = configuration["ROBINHOOD_MCP_AUTH_MODE"] ?? "bearer";
        Mode = Enum.TryParse<RobinhoodMcpAuthMode>(modeRaw.Trim().ToLowerInvariant(), out var m)
            ? m
            : RobinhoodMcpAuthMode.bearer;

        _staticToken = configuration["ROBINHOOD_MCP_ACCESS_TOKEN"] ?? "";
        _tokenUrl = configuration["ROBINHOOD_MCP_OAUTH_TOKEN_URL"] ?? "";
        _clientId = configuration["ROBINHOOD_MCP_OAUTH_CLIENT_ID"] ?? "";
        _clientSecret = configuration["ROBINHOOD_MCP_OAUTH_CLIENT_SECRET"] ?? "";
        _scope = configuration["ROBINHOOD_MCP_OAUTH_SCOPE"] ?? "";
        _refreshToken = configuration["ROBINHOOD_MCP_OAUTH_REFRESH_TOKEN"] ?? "";

        IsConfigured = Mode switch
        {
            RobinhoodMcpAuthMode.none => true,
            RobinhoodMcpAuthMode.bearer => !string.IsNullOrWhiteSpace(_staticToken),
            RobinhoodMcpAuthMode.oauth_refresh => !string.IsNullOrWhiteSpace(_tokenUrl)
                && !string.IsNullOrWhiteSpace(_clientId)
                && !string.IsNullOrWhiteSpace(_refreshToken),
            RobinhoodMcpAuthMode.oauth_stored => _oauth.CanStoreTokens,
            _ => false,
        };

        if (!IsConfigured)
            _logger.LogWarning("[robinhood-auth] Not configured for mode={Mode}", Mode);
    }

    public async Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        switch (Mode)
        {
            case RobinhoodMcpAuthMode.none:
                return null;
            case RobinhoodMcpAuthMode.bearer:
                return _staticToken;
            case RobinhoodMcpAuthMode.oauth_stored:
                return await _oauth.GetAccessTokenAsync(forceRefresh, ct);
            case RobinhoodMcpAuthMode.oauth_refresh:
                break;
            default:
                return null;
        }

        if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
            return _accessToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
                return _accessToken;

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken,
                ["client_id"] = _clientId,
            };
            if (!string.IsNullOrWhiteSpace(_clientSecret)) form["client_secret"] = _clientSecret;
            if (!string.IsNullOrWhiteSpace(_scope)) form["scope"] = _scope;

            using var resp = await _http.PostAsync(_tokenUrl, new FormUrlEncodedContent(form), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("[robinhood-auth] Token refresh failed: {Status} {Body}", resp.StatusCode, Truncate(body));
                _accessToken = null;
                return null;
            }

            var json = JsonNode.Parse(body);
            _accessToken = json?["access_token"]?.ToString();
            var expiresIn = int.TryParse(json?["expires_in"]?.ToString(), out var e) ? e : 300;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresIn - 60));

            var rotated = json?["refresh_token"]?.ToString();
            if (!string.IsNullOrWhiteSpace(rotated) && rotated != _refreshToken)
            {
                // PORT-LATER: rotated refresh tokens are held in memory only; a restart falls back to the configured one.
                _logger.LogWarning("[robinhood-auth] Refresh token was rotated; new value is not persisted");
                _refreshToken = rotated;
            }

            return _accessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "..." : s;
}
