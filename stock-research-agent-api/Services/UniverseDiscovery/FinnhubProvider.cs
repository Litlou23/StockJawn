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

    // Free tier: 60 calls/min. Enforce minimum gap to prevent bursts.
    // 60s/55 ≈ 1.1s + 100ms buffer = ~1.2s between requests.
    private const int MinGapMs = 1_200;
    private static readonly SemaphoreSlim _throttle = new(1, 1);
    private static DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;

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

    /// <summary>
    /// Waits if necessary to stay within the free-tier rate limit (55 req/min).
    /// </summary>
    private async Task ThrottleAsync()
    {
        await _throttle.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = (now - _lastRequestTime).TotalMilliseconds;
            if (elapsed < MinGapMs)
            {
                var waitMs = (int)(MinGapMs - elapsed);
                await Task.Delay(waitMs);
            }

            _lastRequestTime = DateTimeOffset.UtcNow;
        }
        finally
        {
            _throttle.Release();
        }
    }

    public record EarningsEntry(string Ticker, string Date, string? Hour, double? EstimateEps);
    public record EconomicEvent(string Event, string Country, string Date, string? Impact, double? Actual, double? Estimate, double? Previous);
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
            await ThrottleAsync();
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
    /// Get today's economic calendar events. Filters to US-only, high/medium impact.
    /// Used by the portfolio entry pipeline to detect macro shock days —
    /// e.g. consumer sentiment crash, CPI surprise, jobs miss.
    /// </summary>
    public async Task<List<EconomicEvent>> GetEconomicCalendarAsync(int daysAhead = 0)
    {
        if (!_configured) return [];

        var from = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(daysAhead).ToString("yyyy-MM-dd");
        var url = $"{BaseUrl}/calendar/economic?from={from}&to={to}&token={_apiKey}";

        try
        {
            await ThrottleAsync();
            _logger.LogInformation("[finnhub] Fetching economic calendar {From} to {To}", from, to);
            var resp = await _http.GetStringAsync(url);
            var json = JsonNode.Parse(resp);
            var events = json?["economicCalendar"]?.AsArray();
            if (events is null) return [];

            var results = new List<EconomicEvent>();
            foreach (var entry in events)
            {
                var country = entry?["country"]?.ToString() ?? "";
                // Only care about US economic data — that's what moves our market
                if (!country.Equals("US", StringComparison.OrdinalIgnoreCase)) continue;

                var impact = entry?["impact"]?.ToString();
                // Skip low-impact releases — they don't move the market
                if (impact is not null
                    && !impact.Equals("high", StringComparison.OrdinalIgnoreCase)
                    && !impact.Equals("medium", StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(new EconomicEvent(
                    Event: entry?["event"]?.ToString() ?? "",
                    Country: country,
                    Date: entry?["date"]?.ToString() ?? from,
                    Impact: impact,
                    Actual: double.TryParse(entry?["actual"]?.ToString(), out var actual) ? actual : null,
                    Estimate: double.TryParse(entry?["estimate"]?.ToString(), out var est) ? est : null,
                    Previous: double.TryParse(entry?["prev"]?.ToString(), out var prev) ? prev : null
                ));
            }

            _logger.LogInformation("[finnhub] Found {Count} US high/medium-impact economic events today", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[finnhub] Economic calendar fetch failed");
            return [];
        }
    }

    /// <summary>
    /// Check if any high-impact US economic data released today had a significant miss
    /// vs estimate. A big miss (consumer sentiment crashing from 67 to 51, retail sales
    /// dropping -0.6%) creates a macro shock that can drag the whole market.
    /// Returns true if at least one high-impact release missed estimates badly.
    /// </summary>
    public async Task<(bool IsMacroShock, List<string> ShockEvents)> DetectMacroShockAsync()
    {
        var events = await GetEconomicCalendarAsync();
        var shocks = new List<string>();

        foreach (var e in events)
        {
            // Only flag events where actual data has been released and we can compare
            if (e.Actual is null || e.Estimate is null) continue;
            if (e.Estimate.Value == 0) continue; // avoid divide by zero

            var missPct = Math.Abs((e.Actual.Value - e.Estimate.Value) / Math.Abs(e.Estimate.Value)) * 100;

            // High-impact event with >10% miss from estimate = macro shock
            // e.g. Consumer Sentiment: estimate 67, actual 51 → 24% miss
            // e.g. Retail Sales: estimate +0.1%, actual -0.6% → huge miss
            if (e.Impact?.Equals("high", StringComparison.OrdinalIgnoreCase) == true && missPct >= 10)
            {
                shocks.Add($"{e.Event}: actual={e.Actual:F2} vs est={e.Estimate:F2} ({missPct:F0}% miss)");
            }
            // Medium-impact with >20% miss
            else if (e.Impact?.Equals("medium", StringComparison.OrdinalIgnoreCase) == true && missPct >= 20)
            {
                shocks.Add($"{e.Event}: actual={e.Actual:F2} vs est={e.Estimate:F2} ({missPct:F0}% miss)");
            }
        }

        return (shocks.Count > 0, shocks);
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
            await ThrottleAsync();
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
            await ThrottleAsync();
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
