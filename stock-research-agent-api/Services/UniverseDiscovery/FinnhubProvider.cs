using System.Text.Json;
using System.Text.Json.Nodes;

namespace StockResearchAgent.Api.Services.UniverseDiscovery;

/// <summary>
/// Calls Finnhub free-tier endpoints to discover tickers with upcoming catalysts.
/// Free tier: 60 calls/min, sufficient for discovery.
/// API key from FINNHUB_API_KEY env var. Server-side only.
/// </summary>
public class FinnhubProvider
{
    private const string BaseUrl = "https://finnhub.io/api/v1";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly bool _configured;
    private readonly ILogger<FinnhubProvider> _logger;

    public FinnhubProvider(IConfiguration configuration, ILogger<FinnhubProvider> logger)
    {
        _logger = logger;
        _apiKey = configuration["FINNHUB_API_KEY"] ?? "";
        _configured = !string.IsNullOrWhiteSpace(_apiKey);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        if (!_configured)
            _logger.LogWarning("[finnhub] FINNHUB_API_KEY not set -- earnings/news discovery unavailable");
    }

    public bool IsConfigured => _configured;

    public record EarningsEntry(string Ticker, string Date, string? Hour, double? EstimateEps);
    public record NewsArticle(string Headline, string Summary, string Source, string Url, DateTimeOffset Datetime, List<string> RelatedTickers);

    /// <summary>
    /// Get upcoming earnings for the next N days. Each company reporting
    /// earnings is a potential catalyst-driven ticker to research.
    /// </summary>
    public async Task<List<EarningsEntry>> GetUpcomingEarningsAsync(int daysAhead = 7)
    {
        if (!_configured) return [];

        var from = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(daysAhead).ToString("yyyy-MM-dd");
        var url = $"{BaseUrl}/calendar/earnings?from={from}&to={to}&token={_apiKey}";

        try
        {
            _logger.LogInformation("[finnhub] Fetching earnings calendar {From} to {To}", from, to);
            var resp = await _http.GetStringAsync(url);
            var json = JsonNode.Parse(resp);
            var earningsCalendar = json?["earningsCalendar"]?.AsArray();
            if (earningsCalendar is null) return [];

            var results = new List<EarningsEntry>();
            foreach (var entry in earningsCalendar)
            {
                var symbol = entry?["symbol"]?.ToString();
                if (string.IsNullOrEmpty(symbol)) continue;

                // Filter to US exchanges only (simple heuristic: no dots in symbol)
                if (symbol.Contains('.')) continue;

                results.Add(new EarningsEntry(
                    Ticker: symbol,
                    Date: entry?["date"]?.ToString() ?? "",
                    Hour: entry?["hour"]?.ToString(),
                    EstimateEps: double.TryParse(entry?["epsEstimate"]?.ToString(), out var eps) ? eps : null
                ));
            }

            _logger.LogInformation("[finnhub] Found {Count} upcoming US earnings", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[finnhub] Earnings calendar fetch failed");
            return [];
        }
    }

    /// <summary>
    /// Get general market news. Finnhub returns articles with related ticker symbols.
    /// </summary>
    public async Task<List<NewsArticle>> GetMarketNewsAsync(string category = "general", int minItems = 20)
    {
        if (!_configured) return [];

        var url = $"{BaseUrl}/news?category={category}&minId=0&token={_apiKey}";

        try
        {
            _logger.LogInformation("[finnhub] Fetching market news (category={Category})", category);
            var resp = await _http.GetStringAsync(url);
            var json = JsonNode.Parse(resp);
            if (json is not JsonArray arr) return [];

            var results = new List<NewsArticle>();
            foreach (var item in arr)
            {
                var headline = item?["headline"]?.ToString() ?? "";
                var summary = item?["summary"]?.ToString() ?? "";
                var source = item?["source"]?.ToString() ?? "";
                var articleUrl = item?["url"]?.ToString() ?? "";
                var related = item?["related"]?.ToString() ?? "";
                var datetime = long.TryParse(item?["datetime"]?.ToString(), out var ts)
                    ? DateTimeOffset.FromUnixTimeSeconds(ts)
                    : DateTimeOffset.UtcNow;

                // "related" is a comma-separated list of tickers
                var relatedTickers = related
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(t => t.Length >= 1 && t.Length <= 5 && !t.Contains('.'))
                    .ToList();

                results.Add(new NewsArticle(headline, summary, source, articleUrl, datetime, relatedTickers));
            }

            _logger.LogInformation("[finnhub] Got {Count} news articles", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[finnhub] Market news fetch failed");
            return [];
        }
    }

    /// <summary>
    /// Get company-specific news for a ticker. Useful for deep-dive after discovery.
    /// </summary>
    public async Task<List<NewsArticle>> GetCompanyNewsAsync(string ticker, int daysBack = 3)
    {
        if (!_configured) return [];

        var from = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var url = $"{BaseUrl}/company-news?symbol={ticker}&from={from}&to={to}&token={_apiKey}";

        try
        {
            var resp = await _http.GetStringAsync(url);
            var json = JsonNode.Parse(resp);
            if (json is not JsonArray arr) return [];

            return arr.Select(item => new NewsArticle(
                item?["headline"]?.ToString() ?? "",
                item?["summary"]?.ToString() ?? "",
                item?["source"]?.ToString() ?? "",
                item?["url"]?.ToString() ?? "",
                long.TryParse(item?["datetime"]?.ToString(), out var ts)
                    ? DateTimeOffset.FromUnixTimeSeconds(ts)
                    : DateTimeOffset.UtcNow,
                (item?["related"]?.ToString() ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(t => t.Length >= 1 && t.Length <= 5)
                    .ToList()
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[finnhub] Company news fetch failed for {Ticker}", ticker);
            return [];
        }
    }
}
