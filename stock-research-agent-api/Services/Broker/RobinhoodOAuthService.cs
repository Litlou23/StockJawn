using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Broker;

public record RobinhoodOAuthMetadata(
    string AuthorizationEndpoint, string TokenEndpoint, string RegistrationEndpoint, string Scope);

public record RobinhoodOAuthStatus
{
    public bool Connected { get; init; }
    public string? ClientId { get; init; }
    public string? RedirectUri { get; init; }
    public string? UpdatedAt { get; init; }
    public string? LastRefreshAt { get; init; }
    public string? LastError { get; init; }
    public List<string> Problems { get; init; } = [];
}

// OAuth 2.1 + PKCE flow published in Robinhood's MCP discovery metadata
// (/.well-known/oauth-authorization-server/mcp/trading): public client, dynamic registration,
// authorization_code once in a browser, then refresh_token unattended.
public class RobinhoodOAuthService
{
    private const string Table = "robinhood_oauth_credentials";
    private const string RowId = "default";
    private const string DefaultMcpUrl = "https://agent.robinhood.com/mcp/trading";
    private const string DefaultMetadataUrl = "https://agent.robinhood.com/.well-known/oauth-authorization-server/mcp/trading";
    private static readonly TimeSpan PendingLoginTtl = TimeSpan.FromMinutes(10);

    private readonly SupabaseClient _db;
    private readonly ILogger<RobinhoodOAuthService> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingLogin> _pending = new();

    private readonly string _resource;
    private readonly string _metadataUrl;
    private readonly string _configuredRedirectUri;
    private readonly byte[]? _encryptionKey;
    private readonly string? _keyProblem;

    private RobinhoodOAuthMetadata? _metadata;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    private sealed record PendingLogin(string Verifier, string ClientId, string RedirectUri, DateTimeOffset CreatedAt);

    public RobinhoodOAuthService(SupabaseClient db, IConfiguration configuration, ILogger<RobinhoodOAuthService> logger)
    {
        _db = db;
        _logger = logger;
        _resource = configuration["ROBINHOOD_MCP_URL"] is { Length: > 0 } u ? u : DefaultMcpUrl;
        _metadataUrl = configuration["ROBINHOOD_OAUTH_METADATA_URL"] is { Length: > 0 } m ? m : DefaultMetadataUrl;
        _configuredRedirectUri = configuration["ROBINHOOD_OAUTH_REDIRECT_URI"] ?? "";

        // Key comes from JOB_RUN_SECRET so there's no extra setting; changing that secret means redoing the Robinhood login.
        var secret = configuration["JOB_RUN_SECRET"] ?? "";
        if (secret.Length < 16)
            _keyProblem = "JOB_RUN_SECRET must be set (16+ characters) to protect the stored Robinhood login";
        else
            _encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32,
                info: Encoding.UTF8.GetBytes("stockjawn-robinhood-token-v1"));
    }

    public bool CanStoreTokens => _encryptionKey is not null && _db.IsConfigured;

    // ── Step 1: start the one-time browser login ────────────────────

    public async Task<string> BuildLoginUrlAsync(string fallbackRedirectUri, CancellationToken ct)
    {
        if (!CanStoreTokens)
            throw new InvalidOperationException(_keyProblem ?? "Supabase is not configured");

        var meta = await GetMetadataAsync(ct);
        var redirectUri = string.IsNullOrWhiteSpace(_configuredRedirectUri) ? fallbackRedirectUri : _configuredRedirectUri;
        var clientId = await EnsureClientRegisteredAsync(meta, redirectUri, ct);

        PrunePending();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        _pending[state] = new PendingLogin(verifier, clientId, redirectUri, DateTimeOffset.UtcNow);

        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["scope"] = meta.Scope,
            ["resource"] = _resource,
        };
        var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{meta.AuthorizationEndpoint}?{qs}";
    }

    // ── Step 2: Robinhood redirects back here with ?code&state ──────

    public async Task CompleteLoginAsync(string code, string state, CancellationToken ct)
    {
        if (!_pending.TryRemove(state, out var pending) || DateTimeOffset.UtcNow - pending.CreatedAt > PendingLoginTtl)
            throw new InvalidOperationException("Login link expired or unknown — start the login again");

        var meta = await GetMetadataAsync(ct);
        var json = await PostTokenAsync(meta, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = pending.RedirectUri,
            ["client_id"] = pending.ClientId,
            ["code_verifier"] = pending.Verifier,
            ["resource"] = _resource,
        }, ct);

        var refresh = json["refresh_token"]?.ToString();
        if (string.IsNullOrWhiteSpace(refresh))
            throw new InvalidOperationException("Robinhood did not return a refresh token — unattended use won't work with this login");

        await SaveAsync(new Dictionary<string, object?>
        {
            ["refresh_token_enc"] = Encrypt(refresh),
            ["last_error"] = null,
            ["updated_at"] = DateTimeOffset.UtcNow,
        });
        CacheAccessToken(json);
        _logger.LogInformation("[robinhood-oauth] Login complete — refresh token stored (client {ClientId})", pending.ClientId);
    }

    // ── Step 3: unattended access tokens ────────────────────────────

    public async Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
            return _accessToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
                return _accessToken;

            var row = await LoadAsync();
            var clientId = row?["client_id"]?.ToString();
            var enc = row?["refresh_token_enc"]?.ToString();
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(enc) || _encryptionKey is null)
            {
                _logger.LogWarning("[robinhood-oauth] No stored login — run the one-time login first");
                return null;
            }

            var meta = await GetMetadataAsync(ct);
            JsonObject json;
            try
            {
                json = await PostTokenAsync(meta, new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = Decrypt(enc),
                    ["client_id"] = clientId,
                    ["resource"] = _resource,
                }, ct);
            }
            catch (Exception ex)
            {
                await SaveAsync(new Dictionary<string, object?> { ["last_error"] = Truncate(ex.Message), ["updated_at"] = DateTimeOffset.UtcNow });
                _accessToken = null;
                _logger.LogError(ex, "[robinhood-oauth] Refresh failed");
                return null;
            }

            // Save a rotated refresh token before using the new access token, so a crash can't strand the login.
            var update = new Dictionary<string, object?>
            {
                ["last_refresh_at"] = DateTimeOffset.UtcNow,
                ["last_error"] = null,
                ["updated_at"] = DateTimeOffset.UtcNow,
            };
            if (json["refresh_token"]?.ToString() is { Length: > 0 } rotated)
                update["refresh_token_enc"] = Encrypt(rotated);

            if (!await SaveAsync(update))
                _logger.LogCritical("[robinhood-oauth] Could not persist refreshed token — next restart may need a new login");

            CacheAccessToken(json);
            return _accessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<RobinhoodOAuthStatus> GetStatusAsync()
    {
        var problems = new List<string>();
        if (_keyProblem is not null) problems.Add(_keyProblem);
        if (!_db.IsConfigured) problems.Add("Supabase is not configured");

        var row = _db.IsConfigured ? await LoadAsync() : null;
        return new RobinhoodOAuthStatus
        {
            Connected = !string.IsNullOrWhiteSpace(row?["refresh_token_enc"]?.ToString()),
            ClientId = row?["client_id"]?.ToString(),
            RedirectUri = row?["redirect_uri"]?.ToString(),
            UpdatedAt = row?["updated_at"]?.ToString(),
            LastRefreshAt = row?["last_refresh_at"]?.ToString(),
            LastError = row?["last_error"]?.ToString(),
            Problems = problems,
        };
    }

    // ── Internals ───────────────────────────────────────────────────

    private async Task<RobinhoodOAuthMetadata> GetMetadataAsync(CancellationToken ct)
    {
        if (_metadata is not null) return _metadata;

        var body = await _http.GetStringAsync(_metadataUrl, ct);
        var json = JsonNode.Parse(body) as JsonObject ?? throw new InvalidOperationException("OAuth metadata is not a JSON object");

        string Req(string key) => json[key]?.ToString() is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"OAuth metadata is missing {key}");

        var grants = (json["grant_types_supported"] as JsonArray)?.Select(g => g?.ToString()).ToList() ?? [];
        if (!grants.Contains("refresh_token"))
            throw new InvalidOperationException("Robinhood OAuth metadata no longer lists refresh_token — unattended login unsupported");

        var scope = (json["scopes_supported"] as JsonArray)?.Select(s => s?.ToString()).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
        _metadata = new RobinhoodOAuthMetadata(Req("authorization_endpoint"), Req("token_endpoint"), Req("registration_endpoint"), scope);
        return _metadata;
    }

    private async Task<string> EnsureClientRegisteredAsync(RobinhoodOAuthMetadata meta, string redirectUri, CancellationToken ct)
    {
        var row = await LoadAsync();
        if (row?["client_id"]?.ToString() is { Length: > 0 } existing && row["redirect_uri"]?.ToString() == redirectUri)
            return existing;

        var payload = new JsonObject
        {
            ["client_name"] = "StockJawn Executor",
            ["redirect_uris"] = new JsonArray(JsonValue.Create(redirectUri)),
            ["grant_types"] = new JsonArray(JsonValue.Create("authorization_code"), JsonValue.Create("refresh_token")),
            ["response_types"] = new JsonArray(JsonValue.Create("code")),
            ["token_endpoint_auth_method"] = "none",
        };
        if (!string.IsNullOrEmpty(meta.Scope)) payload["scope"] = meta.Scope;

        using var resp = await _http.PostAsync(meta.RegistrationEndpoint,
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Client registration failed: HTTP {(int)resp.StatusCode} {Truncate(text)}");

        var clientId = JsonNode.Parse(text)?["client_id"]?.ToString();
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Client registration returned no client_id");

        // New client_id invalidates any old refresh token.
        await SaveAsync(new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["refresh_token_enc"] = null,
            ["registered_at"] = DateTimeOffset.UtcNow,
            ["updated_at"] = DateTimeOffset.UtcNow,
        });
        _logger.LogInformation("[robinhood-oauth] Registered client {ClientId} for {RedirectUri}", clientId, redirectUri);
        return clientId;
    }

    private async Task<JsonObject> PostTokenAsync(RobinhoodOAuthMetadata meta, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(meta.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token request ({form["grant_type"]}) failed: HTTP {(int)resp.StatusCode} {Truncate(text)}");
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("Token response is not a JSON object");
    }

    private void CacheAccessToken(JsonObject json)
    {
        _accessToken = json["access_token"]?.ToString();
        var expiresIn = int.TryParse(json["expires_in"]?.ToString(), out var e) ? e : 300;
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresIn - 60));
    }

    private async Task<JsonObject?> LoadAsync()
        => (await _db.SelectAsync(Table, filter: $"id=eq.{RowId}", limit: 1)).FirstOrDefault();

    // Dictionary values keep explicit nulls, so a cleared column really gets cleared.
    private async Task<bool> SaveAsync(Dictionary<string, object?> fields)
    {
        var existing = await LoadAsync();
        if (existing is null)
        {
            fields["id"] = RowId;
            return (await _db.InsertAsync(Table, fields)).Count == 1;
        }
        return await _db.UpdateAsync(Table, $"id=eq.{RowId}", fields);
    }

    private string Encrypt(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_encryptionKey!, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    private string Decrypt(string packed)
    {
        var all = Convert.FromBase64String(packed);
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(_encryptionKey!, 16);
        aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }

    private void PrunePending()
    {
        foreach (var (k, v) in _pending)
            if (DateTimeOffset.UtcNow - v.CreatedAt > PendingLoginTtl) _pending.TryRemove(k, out _);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "..." : s;
}
