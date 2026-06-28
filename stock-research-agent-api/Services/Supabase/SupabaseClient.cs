using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StockResearchAgent.Api.Services.Supabase;

/// <summary>
/// Lightweight PostgREST client for Supabase. Uses HttpClient directly
/// — no third-party NuGet packages. All calls go through the Supabase
/// REST API with the service-role key for full RLS bypass.
///
/// Environment variables:
///   SUPABASE_URL         - e.g. https://xxxx.supabase.co
///   SUPABASE_SERVICE_KEY - service_role key (never anon key for server jobs)
/// </summary>
public class SupabaseClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly bool _configured;
    private readonly ILogger<SupabaseClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public bool IsConfigured => _configured;

    public SupabaseClient(IConfiguration configuration, ILogger<SupabaseClient> logger)
    {
        _logger = logger;
        var url = configuration["SUPABASE_URL"] ?? "";
        var key = configuration["SUPABASE_SERVICE_KEY"] ?? "";

        _configured = !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key);

        _baseUrl = url.TrimEnd('/') + "/rest/v1";

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (_configured)
        {
            _http.DefaultRequestHeaders.Add("apikey", key);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    // -----------------------------------------------------------------------
    // SELECT
    // -----------------------------------------------------------------------

    public async Task<List<JsonObject>> SelectAsync(
        string table,
        string? filter = null,
        string? order = null,
        int? limit = null,
        string select = "*")
    {
        if (!_configured) return [];

        var url = $"{_baseUrl}/{table}?select={select}";
        if (filter is not null) url += $"&{filter}";
        if (order is not null) url += $"&order={order}";
        if (limit is not null) url += $"&limit={limit}";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[supabase] SELECT {Table} failed: {Status} {Body}", table, resp.StatusCode, body);
                return [];
            }
            return JsonSerializer.Deserialize<List<JsonObject>>(body) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[supabase] SELECT {Table} error", table);
            return [];
        }
    }

    public async Task<JsonObject?> SelectSingleAsync(string table, string? filter = null)
    {
        if (!_configured) return null;

        var url = $"{_baseUrl}/{table}?select=*";
        if (filter is not null) url += $"&{filter}";
        url += "&limit=1";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.pgrst.object+json"));

        try
        {
            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<JsonObject>(body);
        }
        catch { return null; }
    }

    // -----------------------------------------------------------------------
    // INSERT
    // -----------------------------------------------------------------------

    public async Task<List<JsonObject>> InsertAsync(string table, object rows, bool returnRows = true)
    {
        if (!_configured) return [];

        var url = $"{_baseUrl}/{table}";
        var json = JsonSerializer.Serialize(rows, JsonOpts);
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (returnRows)
        {
            req.Headers.Add("Prefer", "return=representation");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
        else
        {
            req.Headers.Add("Prefer", "return=minimal");
        }

        try
        {
            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[supabase] INSERT {Table} failed: {Status} {Body}", table, resp.StatusCode, body);
                return [];
            }
            if (!returnRows) return [];
            return JsonSerializer.Deserialize<List<JsonObject>>(body) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[supabase] INSERT {Table} error", table);
            return [];
        }
    }

    // -----------------------------------------------------------------------
    // UPDATE
    // -----------------------------------------------------------------------

    public async Task<bool> UpdateAsync(string table, string filter, object data)
    {
        if (!_configured) return false;

        var url = $"{_baseUrl}/{table}?{filter}";
        var json = JsonSerializer.Serialize(data, JsonOpts);
        var req = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Prefer", "return=minimal");

        try
        {
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                _logger.LogWarning("[supabase] UPDATE {Table} failed: {Status} {Body}", table, resp.StatusCode, body);
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[supabase] UPDATE {Table} error", table);
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // UPSERT
    // -----------------------------------------------------------------------

    public async Task<bool> UpsertAsync(string table, object data, string onConflict)
    {
        if (!_configured) return false;

        var url = $"{_baseUrl}/{table}?on_conflict={onConflict}";
        var json = JsonSerializer.Serialize(data, JsonOpts);
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Prefer", "resolution=merge-duplicates,return=minimal");

        try
        {
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                _logger.LogWarning("[supabase] UPSERT {Table} failed: {Status} {Body}", table, resp.StatusCode, body);
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[supabase] UPSERT {Table} error", table);
            return false;
        }
    }
}
