using System.Text.Json;
using System.Text.Json.Nodes;

namespace StockResearchAgent.Api.Services.UniverseDiscovery;

/// <summary>
/// Configuration for Financial Modeling Prep API.
/// Reads from environment variables / IConfiguration.
/// </summary>
public class FmpOptions
{
    /// <summary>API key (env var: FMP_API_KEY).</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>Base URL (default: https://financialmodelingprep.com).</summary>
    public string BaseUrl { get; init; } = "https://financialmodelingprep.com";

    /// <summary>Whether the FMP provider is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Max requests per minute (Starter plan = 300/min, default 60 for safety).</summary>
    public int RequestsPerMinute { get; init; } = 60;

    /// <summary>Max events to emit per discovery scan.</summary>
    public int MaxEventsPerRun { get; init; } = 200;

    /// <summary>HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 20;
}

/// <summary>
/// Low-level HTTP client for Financial Modeling Prep (FMP) API.
/// Starter plan: 300 requests/min, 20 GB bandwidth/30 days, stable API routes.
/// API key passed as ?apikey= query parameter.
///
/// Endpoints used (Starter plan):
///   - /stable/news/stock-latest (company news)
///   - /stable/news/press-releases (press releases)
///   - /stable/earnings-calendar (earnings calendar)
///   - /stable/sec-filings-search/search-by-form-type (SEC filings)
///   - /stable/upgrades-downgrades-grading (analyst upgrades/downgrades)
///   - /stable/insider-trading/latest (insider trading activity)
/// </summary>
public class FmpClient
{
    private readonly HttpClient _http;
    private readonly FmpOptions _options;
    private readonly bool _configured;
    private readonly ILogger<FmpClient> _logger;

    // Simple rate limiter: track last request time
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _rateLock = new(1, 1);

    public FmpClient(IConfiguration configuration, ILogger<FmpClient> logger)
    {
        _logger = logger;

        var apiKey = configuration["FMP_API_KEY"] ?? "";
        var baseUrl = configuration["FMP_BASE_URL"] ?? "https://financialmodelingprep.com";
        var enabled = configuration["FMP_ENABLED"] != "false"; // enabled by default if key is set
        var requestsPerMin = int.TryParse(configuration["FMP_REQUESTS_PER_MINUTE"], out var rpm) ? rpm : 60;
        var maxEvents = int.TryParse(configuration["FMP_MAX_EVENTS_PER_RUN"], out var me) ? me : 200;
        var timeout = int.TryParse(configuration["FMP_TIMEOUT_SECONDS"], out var ts) ? ts : 20;

        _options = new FmpOptions
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl.TrimEnd('/'),
            Enabled = enabled,
            RequestsPerMinute = requestsPerMin,
            MaxEventsPerRun = maxEvents,
            TimeoutSeconds = timeout,
        };

        _configured = !string.IsNullOrWhiteSpace(apiKey) && enabled;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };

        if (!_configured)
            _logger.LogWarning("[fmp] FMP_API_KEY not set or FMP_ENABLED=false — FMP discovery unavailable");
    }

    public bool IsConfigured => _configured;
    public FmpOptions Options => _options;

    // ── DTOs ────────────────────────────────────────────────────

    public record FmpNewsArticle(
        string Symbol,
        string Title,
        string Text,
        string PublishedDate,
        string Site,
        string Url,
        DateTimeOffset ParsedDate);

    public record FmpPressRelease(
        string Symbol,
        string Title,
        string Text,
        string Date,
        DateTimeOffset ParsedDate);

    public record FmpEarningsEntry(
        string Symbol,
        string Date,
        double? EpsEstimated,
        double? RevenueEstimated,
        string? FiscalDateEnding);

    public record FmpSecFiling(
        string Symbol,
        string FormType,
        string FilingDate,
        string AcceptedDate,
        string Cik,
        string Link,
        DateTimeOffset ParsedDate);

    public record FmpUpgradeDowngrade(
        string Symbol,
        string PublishedDate,
        string GradingCompany,
        string NewGrade,
        string PreviousGrade,
        string Action,
        DateTimeOffset ParsedDate);

    public record FmpInsiderTrade(
        string Symbol,
        string ReportingName,
        string TransactionType,
        double SecuritiesTransacted,
        double Price,
        string FilingDate,
        string TransactionDate,
        string TypeOfOwner,
        DateTimeOffset ParsedDate);

    // ── API methods ─────────────────────────────────────────────

    /// <summary>
    /// Get latest stock news. Basic plan endpoint.
    /// </summary>
    public async Task<List<FmpNewsArticle>> GetStockNewsAsync(int limit = 50)
    {
        if (!_configured) return [];

        var url = $"{_options.BaseUrl}/stable/news/stock-latest?page=0&limit={limit}&apikey={_options.ApiKey}";

        try
        {
            await ThrottleAsync();
            _logger.LogInformation("[fmp] Fetching stock news (limit={Limit})", limit);
            var resp = await _http.GetStringAsync(url);
            var json = JsonNode.Parse(resp);

            if (json is not JsonArray arr) return [];

            var results = new List<FmpNewsArticle>();
            foreach (var item in arr)
            {
                var symbol = item?["symbol"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(symbol) || symbol.Contains(',')) continue; // skip multi-ticker articles for now
                if (symbol.Contains('.') || symbol.Length > 5) continue; // US equities only

                var publishedDate = item?["publishedDate"]?.ToString() ?? "";
                var parsed = DateTimeOffset.TryParse(publishedDate, out var dt)
                    ? dt : DateTimeOffset.UtcNow;

                results.Add(new FmpNewsArticle(
                    Symbol: symbol.ToUpperInvariant(),
                    Title: item?["title"]?.ToString() ?? "",
                    Text: item?["text"]?.ToString() ?? "",
                    PublishedDate: publishedDate,
                    Site: item?["site"]?.ToString() ?? "",
                    Url: item?["url"]?.ToString() ?? "",
                    ParsedDate: parsed));
            }

            _logger.LogInformation("[fmp] Got {Count} stock news articles", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[fmp] Stock news fetch failed");
            return [];
        }
    }

    /// <summary>
    /// Get latest press releases. Basic plan endpoint.
    /// </summary>
    public async Task<List<FmpPressRelease>> GetPressReleasesAsync(int limit = 30)
    {
        if (!_configured) return [];

        var url = $"{_options.BaseUrl}/stable/news/press-releases?page=0&limit={limit}&apikey={_options.ApiKey}";

        try
        {
            await ThrottleAsync();
            _logger.LogInformation("[fmp] Fetching press releases (limit={Limit})", limit);
            var resp = await _http.GetStringAsync(url);
            var json = JsonNode.Parse(resp);

            if (json is not JsonArray arr) return [];

            var results = new List<FmpPressRelease>();
            foreach (var item in arr)
            {
                var symbol = item?["symbol"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(symbol) || symbol.Contains('.') || symbol.Length > 5) continue;

                var dateStr = item?["date"]?.ToString() ?? "";
                var parsed = DateTimeOffset.TryParse(dateStr, out var dt)
                    ? dt : DateTimeOffset.UtcNow;

                results.Add(new FmpPressRelease(
                    Symbol: symbol.ToUpperInvariant(),
                    Title: item?["title"]?.ToString() ?? "",
                    Text: item?["text"]?.ToString() ?? "",
                    Date: dateStr,
                    ParsedDate: parsed));
            }

            _logger.LogInformation("[fmp] Got {Count} press releases", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[fmp] Press releases fetch failed");
            return [];
        }
    }

    /// <summary>
    /// Get earnings calendar. Basic plan endpoint.
    /// </summary>
    public async Task<List<FmpEarningsEntry>> GetEarningsCalendarAsync(int daysAhead = 7)
    {
        if (!_configured) return [];

        var from = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(daysAhead).ToString("yyyy-MM-dd");
        var url = $"{_options.BaseUrl}/stable/earnings-calendar?from={from}&to={to}&apikey={_options.ApiKey}";

        try
        {
            await ThrottleAsync();
            _logger.LogInformation("[fmp] Fetching earnings calendar {From} to {To}", from, to);
            var resp = await _http.GetStringAsync(url);
            var json = JsonNode.Parse(resp);

            if (json is not JsonArray arr) return [];

            var results = new List<FmpEarningsEntry>();
            foreach (var item in arr)
            {
                var symbol = item?["symbol"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(symbol) || symbol.Contains('.') || symbol.Length > 5) continue;

                results.Add(new FmpEarningsEntry(
                    Symbol: symbol.ToUpperInvariant(),
                    Date: item?["date"]?.ToString() ?? "",
                    EpsEstimated: double.TryParse(item?["epsEstimated"]?.ToString(), out var eps) ? eps : null,
                    RevenueEstimated: double.TryParse(item?["revenueEstimated"]?.ToString(), out var rev) ? rev : null,
                    FiscalDateEnding: item?["fiscalDateEnding"]?.ToString()));
            }

            _logger.LogInformation("[fmp] Got {Count} upcoming earnings entries", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[fmp] Earnings calendar fetch failed");
            return [];
        }
    }

    public record FmpProfile(string Symbol, string Sector, string Industry, double Price);
    public record FmpScreenRow(string Symbol, string Sector, string Industry, double Price, double Volume);

    public async Task<FmpProfile?> GetProfileAsync(string symbol)
    {
        if (!_configured) return null;
        var url = $"{_options.BaseUrl}/stable/profile?symbol={Uri.EscapeDataString(symbol)}&apikey={_options.ApiKey}";
        try
        {
            await ThrottleAsync();
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            if (JsonNode.Parse(await resp.Content.ReadAsStringAsync()) is not JsonArray { Count: > 0 } arr) return null;
            var p = arr[0]!;
            return new FmpProfile(symbol.ToUpperInvariant(), p["sector"]?.ToString() ?? "", p["industry"]?.ToString() ?? "",
                double.TryParse(p["price"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px) ? px : 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[fmp] profile {Symbol} failed", symbol);
            return null;
        }
    }

    // US-listed, actively traded common stocks in a sector within a price band (null if the plan refuses).
    public async Task<List<FmpScreenRow>?> ScreenAsync(string sector, double minPrice, double maxPrice, double minVolume, int limit = 200)
    {
        if (!_configured) return null;
        var url = $"{_options.BaseUrl}/stable/company-screener?sector={Uri.EscapeDataString(sector)}&priceMoreThan={minPrice}&priceLowerThan={maxPrice}" +
                  $"&volumeMoreThan={minVolume}&isEtf=false&isFund=false&isActivelyTrading=true&country=US&limit={limit}&apikey={_options.ApiKey}";
        try
        {
            await ThrottleAsync();
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[fmp] screener returned {Status}", (int)resp.StatusCode);
                return null;
            }
            if (JsonNode.Parse(await resp.Content.ReadAsStringAsync()) is not JsonArray arr) return null;
            static double D(JsonNode? n) => double.TryParse(n?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            return arr.Where(i => i is not null).Select(i => new FmpScreenRow(
                    (i!["symbol"]?.ToString() ?? "").ToUpperInvariant(), i["sector"]?.ToString() ?? "", i["industry"]?.ToString() ?? "",
                    D(i["price"]), D(i["volume"])))
                .Where(r => r.Symbol.Length is >= 1 and <= 5 && r.Symbol.All(char.IsLetter))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[fmp] screener failed");
            return null;
        }
    }

    public record FmpEconomicEvent(string Event, string Country, string Date, string? Impact, double? Actual, double? Estimate, double? Previous);

    // Economic calendar (jobs, CPI, FOMC...). Dates are UTC. Fallback for when Finnhub's plan refuses it.
    public async Task<List<FmpEconomicEvent>> GetEconomicCalendarAsync(int daysAhead = 14)
    {
        if (!_configured) return [];
        var from = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(daysAhead).ToString("yyyy-MM-dd");
        var url = $"{_options.BaseUrl}/stable/economic-calendar?from={from}&to={to}&apikey={_options.ApiKey}";
        try
        {
            await ThrottleAsync();
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[fmp] Economic calendar returned {Status}", (int)resp.StatusCode);
                return [];
            }
            if (JsonNode.Parse(await resp.Content.ReadAsStringAsync()) is not JsonArray arr) return [];
            static double? D(JsonNode? n) => double.TryParse(n?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
            return arr.Where(i => i is not null).Select(i => new FmpEconomicEvent(
                    i!["event"]?.ToString() ?? "",
                    i["country"]?.ToString() ?? "",
                    i["date"]?.ToString() ?? "",
                    i["impact"]?.ToString(),
                    D(i["actual"]), D(i["estimate"]), D(i["previous"])))
                .Where(e => e.Event.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[fmp] Economic calendar fetch failed");
            return [];
        }
    }

    /// <summary>
    /// Search SEC filings by form type (e.g., "10-K", "8-K", "4").
    /// Basic plan endpoint.
    /// </summary>
    public async Task<List<FmpSecFiling>> GetSecFilingsAsync(string formType = "8-K", int limit = 30)
    {
        if (!_configured) return [];

        var url = $"{_options.BaseUrl}/stable/sec-filings-search/search-by-form-type?formType={formType}&limit={limit}&apikey={_options.ApiKey}";

        try
        {
            await ThrottleAsync();
            _logger.LogInformation("[fmp] Fetching SEC filings (form={FormType}, limit={Limit})", formType, limit);
            var resp = await _http.GetStringAsync(url);
            var json = JsonNode.Parse(resp);

            if (json is not JsonArray arr) return [];

            var results = new List<FmpSecFiling>();
            foreach (var item in arr)
            {
                var symbol = item?["symbol"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(symbol) || symbol.Contains('.') || symbol.Length > 5) continue;

                var filingDate = item?["fillingDate"]?.ToString() ?? item?["filingDate"]?.ToString() ?? "";
                var acceptedDate = item?["acceptedDate"]?.ToString() ?? "";
                var parsed = DateTimeOffset.TryParse(acceptedDate, out var dt)
                    ? dt : (DateTimeOffset.TryParse(filingDate, out var dt2) ? dt2 : DateTimeOffset.UtcNow);

                results.Add(new FmpSecFiling(
                    Symbol: symbol.ToUpperInvariant(),
                    FormType: item?["formType"]?.ToString() ?? formType,
                    FilingDate: filingDate,
                    AcceptedDate: acceptedDate,
                    Cik: item?["cik"]?.ToString() ?? "",
                    Link: item?["finalLink"]?.ToString() ?? item?["link"]?.ToString() ?? "",
                    ParsedDate: parsed));
            }

            _logger.LogInformation("[fmp] Got {Count} SEC filings ({FormType})", results.Count, formType);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[fmp] SEC filings fetch failed for {FormType}", formType);
            return [];
        }
    }

    /// <summary>
    /// Get latest analyst upgrades/downgrades. Starter plan endpoint.
    /// Endpoint: /stable/upgrades-downgrades-grading
    /// </summary>
    public async Task<List<FmpUpgradeDowngrade>> GetUpgradesDowngradesAsync(int limit = 50)
    {
        if (!_configured) return [];

        var url = $"{_options.BaseUrl}/stable/upgrades-downgrades-grading?limit={limit}&apikey={_options.ApiKey}";

        try
        {
            await ThrottleAsync();
            _logger.LogInformation("[fmp] Fetching analyst upgrades/downgrades (limit={Limit})", limit);
            var resp = await _http.GetStringAsync(url);
            var json = JsonNode.Parse(resp);

            if (json is not JsonArray arr) return [];

            var results = new List<FmpUpgradeDowngrade>();
            foreach (var item in arr)
            {
                var symbol = item?["symbol"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(symbol) || symbol.Contains('.') || symbol.Length > 5) continue;

                var publishedDate = item?["publishedDate"]?.ToString() ?? item?["date"]?.ToString() ?? "";
                var parsed = DateTimeOffset.TryParse(publishedDate, out var dt)
                    ? dt : DateTimeOffset.UtcNow;

                results.Add(new FmpUpgradeDowngrade(
                    Symbol: symbol.ToUpperInvariant(),
                    PublishedDate: publishedDate,
                    GradingCompany: item?["gradingCompany"]?.ToString() ?? "",
                    NewGrade: item?["newGrade"]?.ToString() ?? "",
                    PreviousGrade: item?["previousGrade"]?.ToString() ?? "",
                    Action: item?["action"]?.ToString() ?? "",
                    ParsedDate: parsed));
            }

            _logger.LogInformation("[fmp] Got {Count} analyst upgrades/downgrades", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[fmp] Analyst upgrades/downgrades fetch failed");
            return [];
        }
    }

    /// <summary>
    /// Get latest insider trades. Starter plan endpoint.
    /// Endpoint: /stable/insider-trading/latest
    /// </summary>
    public async Task<List<FmpInsiderTrade>> GetLatestInsiderTradesAsync(int limit = 50)
    {
        if (!_configured) return [];

        var url = $"{_options.BaseUrl}/stable/insider-trading/latest?page=0&limit={limit}&apikey={_options.ApiKey}";

        try
        {
            await ThrottleAsync();
            _logger.LogInformation("[fmp] Fetching insider trades (limit={Limit})", limit);
            var resp = await _http.GetStringAsync(url);
            var json = JsonNode.Parse(resp);

            if (json is not JsonArray arr) return [];

            var results = new List<FmpInsiderTrade>();
            foreach (var item in arr)
            {
                var symbol = item?["symbol"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(symbol) || symbol.Contains('.') || symbol.Length > 5) continue;

                var filingDate = item?["filingDate"]?.ToString() ?? "";
                var transactionDate = item?["transactionDate"]?.ToString() ?? "";
                var parsed = DateTimeOffset.TryParse(filingDate, out var dt)
                    ? dt : (DateTimeOffset.TryParse(transactionDate, out var dt2) ? dt2 : DateTimeOffset.UtcNow);

                var securitiesTransacted = double.TryParse(item?["securitiesTransacted"]?.ToString(), out var sec) ? sec : 0;
                var price = double.TryParse(item?["price"]?.ToString(), out var p) ? p : 0;

                results.Add(new FmpInsiderTrade(
                    Symbol: symbol.ToUpperInvariant(),
                    ReportingName: item?["reportingName"]?.ToString() ?? "",
                    TransactionType: item?["transactionType"]?.ToString() ?? "",
                    SecuritiesTransacted: securitiesTransacted,
                    Price: price,
                    FilingDate: filingDate,
                    TransactionDate: transactionDate,
                    TypeOfOwner: item?["typeOfOwner"]?.ToString() ?? "",
                    ParsedDate: parsed));
            }

            _logger.LogInformation("[fmp] Got {Count} insider trades", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[fmp] Insider trades fetch failed");
            return [];
        }
    }

    // ── Rate limiting ───────────────────────────────────────────

    private async Task ThrottleAsync()
    {
        if (_options.RequestsPerMinute <= 0) return;

        var minInterval = TimeSpan.FromSeconds(60.0 / _options.RequestsPerMinute);

        await _rateLock.WaitAsync();
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastRequestAt;
            if (elapsed < minInterval)
            {
                var delay = minInterval - elapsed;
                _logger.LogDebug("[fmp] Rate limiting: waiting {Delay:F1}s", delay.TotalSeconds);
                await Task.Delay(delay);
            }
            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _rateLock.Release();
        }
    }
}
