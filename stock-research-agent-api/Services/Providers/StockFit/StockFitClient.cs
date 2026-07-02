using System.Net.Http.Headers;

namespace StockResearchAgent.Api.Services.Providers.StockFit;

/// <summary>
/// Low-level StockFit HTTP client. Handles the API key header, timeouts,
/// and normalizes error paths. Returns the raw response body + status code
/// so the caller can decide whether the shape matched. Never throws on
/// non-2xx — instead surfaces the status and a warning string.
///
/// Environment variables (see appsettings.Development.json for local dev):
///   STOCKFIT_API_KEY   — required. If missing, IsConfigured == false and
///                         every call returns { status: 0, body: null }.
///   STOCKFIT_BASE_URL  — optional. Defaults to https://api.stockfit.io/v1.
///                         Override once you have the real docs.
///   STOCKFIT_AUTH_MODE — optional. "header" (default, X-API-Key) or
///                         "bearer" (Authorization: Bearer ...) or
///                         "query"  (append ?apikey=... to the URL).
/// </summary>
public sealed class StockFitClient
{
    // Real base URL confirmed from StockFit dashboard/docs. Endpoints live
    // under /api under /v1, e.g. https://api.stockfit.io/v1/api/news?symbol=AAPL.
    private const string DefaultBaseUrl = "https://api.stockfit.io/v1/api";
    private const int DefaultTimeoutSeconds = 20;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _authMode;
    private readonly string _apiKey;
    private readonly bool _configured;
    private readonly ILogger<StockFitClient> _logger;

    public bool IsConfigured => _configured;
    public string BaseUrl => _baseUrl;

    public StockFitClient(IConfiguration configuration, ILogger<StockFitClient> logger)
    {
        _logger = logger;
        _apiKey = configuration["STOCKFIT_API_KEY"] ?? "";
        _baseUrl = (configuration["STOCKFIT_BASE_URL"] ?? DefaultBaseUrl).TrimEnd('/');
        // StockFit's docs confirm Authorization: Bearer <token> is the real
        // auth. Default to bearer instead of the X-API-Key placeholder.
        _authMode = (configuration["STOCKFIT_AUTH_MODE"] ?? "bearer").ToLowerInvariant();
        _configured = !string.IsNullOrWhiteSpace(_apiKey);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StockResearchAgent/1.0");

        if (_configured)
        {
            switch (_authMode)
            {
                case "bearer":
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    break;
                case "query":
                    // Key is appended in BuildUrl instead.
                    break;
                case "header":
                default:
                    _http.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
                    break;
            }
        }
    }

    public record RawResponse(int StatusCode, string Body, string Endpoint, TimeSpan Elapsed);

    /// <summary>
    /// Perform a GET. Never throws — every failure is captured as a non-2xx
    /// status plus a diagnostic body ("timeout", "network error: ...", etc.).
    /// </summary>
    public async Task<RawResponse> GetAsync(string path, IDictionary<string, string>? query = null,
        CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(path, query);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (!_configured)
        {
            stopwatch.Stop();
            return new RawResponse(0, "stockfit_not_configured", url, stopwatch.Elapsed);
        }

        try
        {
            var resp = await _http.GetAsync(url, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[stockfit] {Path} returned {Status}", path, (int)resp.StatusCode);
            }
            return new RawResponse((int)resp.StatusCode, body, url, stopwatch.Elapsed);
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning("[stockfit] {Path} timed out after {Sec}s", path, DefaultTimeoutSeconds);
            return new RawResponse(-1, "timeout", url, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "[stockfit] {Path} network error", path);
            return new RawResponse(-1, $"network error: {ex.Message}", url, stopwatch.Elapsed);
        }
    }

    private string BuildUrl(string path, IDictionary<string, string>? query)
    {
        var normalizedPath = path.StartsWith('/') ? path : "/" + path;
        var url = _baseUrl + normalizedPath;

        var parts = new List<string>();
        if (query is not null)
        {
            foreach (var kv in query)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                parts.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
            }
        }
        if (_configured && _authMode == "query")
            parts.Add($"apikey={Uri.EscapeDataString(_apiKey)}");

        if (parts.Count > 0)
            url += (url.Contains('?') ? "&" : "?") + string.Join("&", parts);

        return url;
    }
}
