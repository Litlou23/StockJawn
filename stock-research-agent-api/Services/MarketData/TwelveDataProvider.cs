using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.MarketData;

/// <summary>
/// Calls Twelve Data /quote and /time_series endpoints.
/// API key read from TWELVE_DATA_API_KEY env var. Server-side only.
/// </summary>
public class TwelveDataProvider
{
    private const string BaseUrl = "https://api.twelvedata.com";

    // Rate limits — configurable via env vars for paid plans.
    // Free tier: 8 requests/minute, 800/day.
    // Growing plan: 55/min, unlimited/day. Pro: 120/min, unlimited.
    private readonly int _maxRequestsPerMinute;
    private readonly int _maxRequestsPerDay;
    private readonly int _minGapMs; // minimum ms between requests to avoid bursts
    private static readonly SemaphoreSlim _throttle = new(1, 1);
    private static DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;
    private static DateTimeOffset _minuteWindowStart = DateTimeOffset.MinValue;
    private static int _dailyRequestCount;
    private static DateTimeOffset _dailyResetDate = DateTimeOffset.MinValue;

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly bool _configured;
    private readonly ILogger<TwelveDataProvider> _logger;

    /// <summary>True when the daily quota has been exhausted. Resets at midnight UTC.</summary>
    public bool DailyQuotaExhausted { get; private set; }

    public TwelveDataProvider(IConfiguration configuration, ILogger<TwelveDataProvider> logger)
    {
        _logger = logger;
        _apiKey = configuration["TWELVE_DATA_API_KEY"] ?? "";
        _configured = !string.IsNullOrWhiteSpace(_apiKey);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Allow override via env: TWELVE_DATA_RPM and TWELVE_DATA_DAILY
        // Defaults match the Growing plan: 55 req/min, unlimited daily
        _maxRequestsPerMinute = int.TryParse(configuration["TWELVE_DATA_RPM"], out var rpm) ? rpm : 55;
        _maxRequestsPerDay = int.TryParse(configuration["TWELVE_DATA_DAILY"], out var daily) ? daily : 100_000;
        // Space requests evenly: 60s / rpm + 500ms buffer
        _minGapMs = (60_000 / _maxRequestsPerMinute) + 500;

        if (!_configured)
            _logger.LogWarning("[twelve-data] TWELVE_DATA_API_KEY not set -- market data unavailable");
        else
            _logger.LogInformation("[twelve-data] Configured: {RPM} req/min, {Daily} req/day", _maxRequestsPerMinute, _maxRequestsPerDay);
    }

    public bool IsConfigured => _configured;

    // -----------------------------------------------------------------------
    // Retry helper — retries transient failures (429, 503, timeouts, network)
    // -----------------------------------------------------------------------

    private const int MaxRetries = 2; // 3 attempts total

    /// <summary>
    /// GET with automatic retry on transient errors. On 429 retries, re-acquires
    /// a throttle slot to prevent retry pileups from exceeding rate limits.
    /// Returns the response body string, or throws if all attempts fail.
    /// </summary>
    private async Task<string> GetStringWithRetryAsync(string url, string endpoint, string ticker)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                // On retries, wait for a throttle slot so we don't pile up
                // and exceed the per-minute rate limit with concurrent retries.
                if (attempt > 0)
                    await ThrottleAsync();

                using var resp = await _http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();

                // Retry on rate-limit or server error
                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                {
                    _logger.LogWarning(
                        "[twelve-data] {Endpoint} for {Ticker} returned {Status}, retry {Attempt}/{Max}",
                        endpoint, ticker, (int)resp.StatusCode, attempt + 1, MaxRetries);
                    continue;
                }

                return body;
            }
            catch (TaskCanceledException) when (attempt < MaxRetries)
            {
                _logger.LogWarning(
                    "[twelve-data] {Endpoint} for {Ticker} timed out, retry {Attempt}/{Max}",
                    endpoint, ticker, attempt + 1, MaxRetries);
                lastException = new TimeoutException($"{endpoint} timed out for {ticker}");
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                _logger.LogWarning(ex,
                    "[twelve-data] {Endpoint} for {Ticker} network error, retry {Attempt}/{Max}",
                    endpoint, ticker, attempt + 1, MaxRetries);
                lastException = ex;
            }
        }

        throw lastException ?? new HttpRequestException($"{endpoint} failed for {ticker} after {MaxRetries + 1} attempts");
    }

    /// <summary>
    /// Waits if necessary to stay within rate limits (per-minute and daily).
    /// Returns false if the daily quota is exhausted — caller should skip the request.
    /// </summary>
    private async Task<bool> ThrottleAsync()
    {
        await _throttle.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;

            // Reset daily counter at midnight UTC
            if (now.Date > _dailyResetDate.Date)
            {
                _dailyRequestCount = 0;
                _dailyResetDate = now;
                DailyQuotaExhausted = false;
            }

            // Check daily quota
            if (_dailyRequestCount >= _maxRequestsPerDay)
            {
                if (!DailyQuotaExhausted)
                {
                    _logger.LogWarning("[twelve-data] Daily quota exhausted ({Used}/{Max}). " +
                        "Remaining tickers will proceed without market data. " +
                        "Set TWELVE_DATA_DAILY to increase or upgrade your plan.",
                        _dailyRequestCount, _maxRequestsPerDay);
                    DailyQuotaExhausted = true;
                }
                return false;
            }

            // Enforce minimum gap between requests to prevent bursts.
            // TwelveData rejects simultaneous requests even if under the per-minute cap.
            var elapsed = (now - _lastRequestTime).TotalMilliseconds;
            if (elapsed < _minGapMs)
            {
                var waitMs = (int)(_minGapMs - elapsed);
                _logger.LogDebug("[twelve-data] Spacing requests, waiting {WaitMs}ms", waitMs);
                await Task.Delay(waitMs);
            }

            _lastRequestTime = DateTimeOffset.UtcNow;
            _dailyRequestCount++;
            return true;
        }
        finally
        {
            _throttle.Release();
        }
    }

    // -----------------------------------------------------------------------
    // Quote
    // -----------------------------------------------------------------------

    public async Task<MarketSnapshotQuote?> GetQuoteAsync(string ticker)
    {
        if (!_configured) return null;

        if (!await ThrottleAsync()) return null;
        _logger.LogInformation("[twelve-data] calling /quote for {Ticker}", ticker);

        var url = $"{BaseUrl}/quote?symbol={ticker}&apikey={_apiKey}";
        try
        {
            var resp = await GetStringWithRetryAsync(url, "/quote", ticker);
            var json = JsonNode.Parse(resp);
            if (json is null || json["status"]?.ToString() == "error")
            {
                _logger.LogWarning("[twelve-data] Quote error for {Ticker}: {Resp}", ticker, resp[..Math.Min(200, resp.Length)]);
                return null;
            }

            return new MarketSnapshotQuote
            {
                Price = ParseDouble(json["close"]),
                Change = ParseDouble(json["change"]),
                ChangePercent = ParseDouble(json["percent_change"]),
                Volume = ParseDouble(json["volume"]),
                PreviousClose = ParseDouble(json["previous_close"]),
                Open = ParseDouble(json["open"]),
                High = ParseDouble(json["high"]),
                Low = ParseDouble(json["low"]),
                Timestamp = json["datetime"]?.ToString() ?? DateTimeOffset.UtcNow.ToString("o"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[twelve-data] Quote fetch failed for {Ticker}", ticker);
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Time Series (recent bars)
    // -----------------------------------------------------------------------

    public async Task<List<MarketSnapshotBar>> GetRecentBarsAsync(string ticker, int count = 20)
    {
        if (!_configured) return [];

        if (!await ThrottleAsync()) return [];
        _logger.LogInformation("[twelve-data] calling /time_series for {Ticker}", ticker);

        var url = $"{BaseUrl}/time_series?symbol={ticker}&interval=1day&outputsize={count}&apikey={_apiKey}";
        try
        {
            var resp = await GetStringWithRetryAsync(url, "/time_series", ticker);
            var json = JsonNode.Parse(resp);
            if (json is null || json["status"]?.ToString() == "error") return [];

            var values = json["values"]?.AsArray();
            if (values is null) return [];

            return values.Select(v => new MarketSnapshotBar
            {
                Date = v?["datetime"]?.ToString() ?? "",
                Open = ParseDouble(v?["open"]),
                High = ParseDouble(v?["high"]),
                Low = ParseDouble(v?["low"]),
                Close = ParseDouble(v?["close"]),
                Volume = ParseDouble(v?["volume"]),
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[twelve-data] Time series fetch failed for {Ticker}", ticker);
            return [];
        }
    }

    /// <summary>
    /// Fetch daily OHLCV candles for a date range. Used by backtest data loader.
    /// Returns bars in chronological order (oldest first).
    /// </summary>
    public async Task<List<MarketSnapshotBar>> GetHistoricalBarsAsync(
        string ticker, DateOnly startDate, DateOnly endDate, string interval = "1day")
    {
        if (!_configured) return [];

        if (!await ThrottleAsync()) return [];
        _logger.LogDebug("[twelve-data] calling /time_series historical for {Ticker} ({Start} → {End})",
            ticker, startDate, endDate);

        var url = $"{BaseUrl}/time_series?symbol={ticker}&interval={interval}" +
                  $"&start_date={startDate:yyyy-MM-dd}&end_date={endDate:yyyy-MM-dd}" +
                  $"&order=ASC&apikey={_apiKey}";
        try
        {
            var resp = await GetStringWithRetryAsync(url, "/time_series", ticker);
            var json = JsonNode.Parse(resp);
            if (json is null || json["status"]?.ToString() == "error") return [];

            var values = json["values"]?.AsArray();
            if (values is null) return [];

            return values.Select(v => new MarketSnapshotBar
            {
                Date = v?["datetime"]?.ToString() ?? "",
                Open = ParseDouble(v?["open"]),
                High = ParseDouble(v?["high"]),
                Low = ParseDouble(v?["low"]),
                Close = ParseDouble(v?["close"]),
                Volume = ParseDouble(v?["volume"]),
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[twelve-data] Historical fetch failed for {Ticker}", ticker);
            return [];
        }
    }

    // -----------------------------------------------------------------------
    // Technical Context (computed from bars)
    // -----------------------------------------------------------------------

    public MarketSnapshotTechnical? ComputeTechnicalContext(List<MarketSnapshotBar> bars)
    {
        if (bars.Count < 5) return null;

        // Trend from recent closes
        var recent5 = bars.Take(5).Select(b => b.Close).ToList();
        var trendDirection = recent5[0] > recent5[^1] ? "bullish" : recent5[0] < recent5[^1] ? "bearish" : "neutral";

        // Simple moving averages
        var sma5 = recent5.Average();
        var sma20 = bars.Count >= 20 ? bars.Take(20).Average(b => b.Close) : sma5;
        var maPosition = sma5 > sma20 ? "above" : "below";
        var maSummary = $"SMA5 ({sma5:F2}) {maPosition} SMA20 ({sma20:F2})";

        // Momentum (rate of change over 5 bars)
        var roc = bars.Count >= 5 && bars[^1].Close > 0
            ? ((bars[0].Close - bars[^1].Close) / bars[^1].Close) * 100
            : 0;
        var momSummary = roc > 1 ? $"Momentum up ({roc:F1}%)" : roc < -1 ? $"Momentum down ({roc:F1}%)" : $"Momentum flat ({roc:F1}%)";

        // Volume
        var avgVol = bars.Average(b => b.Volume);
        var latestVol = bars[0].Volume;
        var volRatio = avgVol > 0 ? latestVol / avgVol : 1;
        var volSummary = volRatio > 1.5 ? $"Volume elevated ({volRatio:F1}x avg)"
            : volRatio < 0.7 ? $"Volume below average ({volRatio:F1}x avg)"
            : $"Volume normal ({volRatio:F1}x avg)";

        // Relative strength note
        var rsNote = trendDirection == "bullish" && sma5 > sma20
            ? "Price above key averages, trend aligned"
            : trendDirection == "bearish" && sma5 < sma20
                ? "Price below key averages, downtrend intact"
                : "Mixed signals, trend and averages diverging";

        return new MarketSnapshotTechnical
        {
            TrendDirection = trendDirection,
            MovingAverageSummary = maSummary,
            MomentumSummary = momSummary,
            VolumeSummary = volSummary,
            RelativeStrengthNote = rsNote,
        };
    }

    // -----------------------------------------------------------------------
    // Technical Indicators (API-sourced — new signals not computable from bars)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fetches MACD (12, 26, 9) from the TwelveData /macd endpoint.
    /// Returns (macdLine, signalLine, histogram) or null if unavailable.
    /// </summary>
    public async Task<(double MacdLine, double Signal, double Histogram)?> GetMacdAsync(string ticker)
    {
        if (!_configured) return null;
        if (!await ThrottleAsync()) return null;
        _logger.LogDebug("[twelve-data] calling /macd for {Ticker}", ticker);

        var url = $"{BaseUrl}/macd?symbol={ticker}&interval=1day&fast_period=12&slow_period=26&signal_period=9&outputsize=1&apikey={_apiKey}";
        try
        {
            var resp = await GetStringWithRetryAsync(url, "/macd", ticker);
            var json = JsonNode.Parse(resp);
            if (json is null || json["status"]?.ToString() == "error") return null;

            var values = json["values"]?.AsArray();
            if (values is null || values.Count == 0) return null;

            var v = values[0];
            var macd = ParseDouble(v?["macd"]);
            var signal = ParseDouble(v?["macd_signal"]);
            var hist = ParseDouble(v?["macd_hist"]);

            return (Math.Round(macd, 4), Math.Round(signal, 4), Math.Round(hist, 4));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[twelve-data] MACD fetch failed for {Ticker}", ticker);
            return null;
        }
    }

    /// <summary>
    /// Fetches EMA values (12, 26, 50) in a single call using TwelveData /ema endpoint.
    /// Makes 3 separate calls (one per period) but each is small.
    /// </summary>
    public async Task<(double? Ema12, double? Ema26, double? Ema50)> GetEmaAsync(string ticker)
    {
        if (!_configured) return (null, null, null);

        async Task<double?> FetchEma(int period)
        {
            if (!await ThrottleAsync()) return null;
            _logger.LogDebug("[twelve-data] calling /ema({Period}) for {Ticker}", period, ticker);

            var url = $"{BaseUrl}/ema?symbol={ticker}&interval=1day&time_period={period}&outputsize=1&apikey={_apiKey}";
            try
            {
                var resp = await GetStringWithRetryAsync(url, $"/ema({period})", ticker);
                var json = JsonNode.Parse(resp);
                if (json is null || json["status"]?.ToString() == "error") return null;
                var values = json["values"]?.AsArray();
                if (values is null || values.Count == 0) return null;
                var emaStr = values[0]?["ema"]?.ToString();
                return double.TryParse(emaStr, out var ema) ? Math.Round(ema, 4) : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[twelve-data] EMA({Period}) fetch failed for {Ticker}", period, ticker);
                return null;
            }
        }

        // Sequential to respect rate limits
        var ema12 = await FetchEma(12);
        var ema26 = await FetchEma(26);
        var ema50 = await FetchEma(50);
        return (ema12, ema26, ema50);
    }

    // -----------------------------------------------------------------------
    // Fundamentals (company profile + statistics)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fetches company profile from TwelveData /profile endpoint.
    /// Returns sector, industry, market cap, employees, etc.
    /// </summary>
    public async Task<FundamentalsContext?> GetFundamentalsAsync(string ticker)
    {
        if (!_configured) return null;

        var warnings = new List<string>();
        var dataPoints = new List<string>();

        // Fetch profile
        if (!await ThrottleAsync()) return null;
        _logger.LogDebug("[twelve-data] calling /profile for {Ticker}", ticker);

        JsonNode? profileJson = null;
        try
        {
            var resp = await GetStringWithRetryAsync($"{BaseUrl}/profile?symbol={ticker}&apikey={_apiKey}", "/profile", ticker);
            profileJson = JsonNode.Parse(resp);
            if (profileJson?["status"]?.ToString() == "error")
            {
                warnings.Add($"profile_error: {profileJson["message"]}");
                profileJson = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[twelve-data] Profile fetch failed for {Ticker}", ticker);
            warnings.Add($"profile_exception: {ex.Message}");
        }

        // Fetch statistics
        if (!await ThrottleAsync()) return null;
        _logger.LogDebug("[twelve-data] calling /statistics for {Ticker}", ticker);

        JsonNode? statsJson = null;
        try
        {
            var resp = await GetStringWithRetryAsync($"{BaseUrl}/statistics?symbol={ticker}&apikey={_apiKey}", "/statistics", ticker);
            statsJson = JsonNode.Parse(resp);
            if (statsJson?["status"]?.ToString() == "error")
            {
                warnings.Add($"statistics_error: {statsJson["message"]}");
                statsJson = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[twelve-data] Statistics fetch failed for {Ticker}", ticker);
            warnings.Add($"statistics_exception: {ex.Message}");
        }

        if (profileJson is null && statsJson is null) return null;

        // Parse profile fields
        string? sector = null, industry = null, exchange = null;
        long? marketCap = null;
        int? employees = null;

        if (profileJson is not null)
        {
            sector = profileJson["sector"]?.ToString();
            industry = profileJson["industry"]?.ToString();
            exchange = profileJson["exchange"]?.ToString();
            if (!string.IsNullOrEmpty(sector)) dataPoints.Add("sector");
            if (!string.IsNullOrEmpty(industry)) dataPoints.Add("industry");
        }

        // Parse statistics fields
        var stats = statsJson?["statistics"];
        // TwelveData /statistics actual response nesting (verified against docs sample):
        //   statistics.valuations_metrics       — P/E, P/B, EV/EBITDA, market cap
        //   statistics.financials               — profit_margin, operating_margin, return_on_equity_ttm
        //   statistics.financials.income_statement — quarterly_revenue_growth, quarterly_earnings_growth_yoy
        //   statistics.financials.balance_sheet    — total_debt_to_equity_mrq, current_ratio_mrq
        //   statistics.stock_statistics            — short_percent_of_shares_outstanding
        //   statistics.stock_price_summary         — beta, fifty_two_week_high/low
        //   statistics.dividends_and_splits        — forward_annual_dividend_yield, payout_ratio
        var valuations = stats?["valuations_metrics"];
        var financials = stats?["financials"];
        var incomeStatement = financials?["income_statement"];
        var balanceSheet = financials?["balance_sheet"];
        var stockStats = stats?["stock_statistics"];
        var priceSummary = stats?["stock_price_summary"];

        double? peRatio = null, forwardPe = null, pbRatio = null, psRatio = null, evToEbitda = null;
        double? dividendYield = null, payoutRatio = null;
        double? profitMargin = null, operatingMargin = null, roe = null, debtToEquity = null, currentRatio = null;
        double? revenueGrowthYoy = null, earningsGrowthYoy = null, qRevGrowth = null, qEarnGrowth = null;
        double? shortPct = null, beta = null;
        double? fiftyTwoHigh = null, fiftyTwoLow = null;

        if (valuations is not null)
        {
            peRatio = ParseNullableDouble(valuations["trailing_pe"]);
            forwardPe = ParseNullableDouble(valuations["forward_pe"]);
            pbRatio = ParseNullableDouble(valuations["price_to_book_mrq"]);
            psRatio = ParseNullableDouble(valuations["price_to_sales_ttm"]);
            evToEbitda = ParseNullableDouble(valuations["enterprise_to_ebitda"]);
            marketCap = ParseNullableLong(valuations["market_capitalization"]);
            if (peRatio is not null) dataPoints.Add("pe_ratio");
            if (forwardPe is not null) dataPoints.Add("forward_pe");
            if (marketCap is not null) dataPoints.Add("market_cap");
        }

        // Margins and ROE are direct children of financials (not inside income_statement)
        if (financials is not null)
        {
            profitMargin = ParseNullableDouble(financials["profit_margin"]);
            operatingMargin = ParseNullableDouble(financials["operating_margin"]);
            roe = ParseNullableDouble(financials["return_on_equity_ttm"]);
            if (profitMargin is not null) dataPoints.Add("profit_margin");
            if (roe is not null) dataPoints.Add("roe");
        }

        // Growth metrics are inside income_statement
        if (incomeStatement is not null)
        {
            qRevGrowth = ParseNullableDouble(incomeStatement["quarterly_revenue_growth"]);
            qEarnGrowth = ParseNullableDouble(incomeStatement["quarterly_earnings_growth_yoy"]);
            if (qRevGrowth is not null) dataPoints.Add("quarterly_revenue_growth");
        }

        // Debt and liquidity ratios are inside balance_sheet
        if (balanceSheet is not null)
        {
            debtToEquity = ParseNullableDouble(balanceSheet["total_debt_to_equity_mrq"]);
            currentRatio = ParseNullableDouble(balanceSheet["current_ratio_mrq"]);
            if (debtToEquity is not null) dataPoints.Add("debt_to_equity");
        }

        // Use quarterly growth as YoY proxy (TwelveData doesn't provide annual growth fields)
        revenueGrowthYoy = qRevGrowth;
        earningsGrowthYoy = qEarnGrowth;

        if (stats is not null)
        {
            dividendYield = ParseNullableDouble(stats["dividends_and_splits"]?["forward_annual_dividend_yield"]);
            payoutRatio = ParseNullableDouble(stats["dividends_and_splits"]?["payout_ratio"]);
            if (dividendYield is not null) dataPoints.Add("dividend_yield");
        }

        // Beta and 52-week range are under stock_price_summary (not stock_statistics)
        if (priceSummary is not null)
        {
            beta = ParseNullableDouble(priceSummary["beta"]);
            fiftyTwoHigh = ParseNullableDouble(priceSummary["fifty_two_week_high"]);
            fiftyTwoLow = ParseNullableDouble(priceSummary["fifty_two_week_low"]);
            if (beta is not null) dataPoints.Add("beta");
            if (fiftyTwoHigh is not null) dataPoints.Add("52w_range");
        }

        // Short interest is under stock_statistics
        if (stockStats is not null)
        {
            shortPct = ParseNullableDouble(stockStats["short_percent_of_shares_outstanding"]);
            if (shortPct is not null) dataPoints.Add("short_interest");
        }

        if (profileJson is not null)
        {
            var empStr = profileJson["employees"]?.ToString();
            if (int.TryParse(empStr, out var emp)) employees = emp;
        }

        _logger.LogInformation("[twelve-data] Fundamentals for {Ticker}: {Count} data points", ticker, dataPoints.Count);

        return new FundamentalsContext
        {
            Sector = sector,
            Industry = industry,
            Exchange = exchange,
            MarketCap = marketCap,
            Employees = employees,
            PeRatio = peRatio,
            ForwardPe = forwardPe,
            PbRatio = pbRatio,
            PsRatio = psRatio,
            EvToEbitda = evToEbitda,
            DividendYield = dividendYield,
            PayoutRatio = payoutRatio,
            ProfitMargin = profitMargin,
            OperatingMargin = operatingMargin,
            ReturnOnEquity = roe,
            DebtToEquity = debtToEquity,
            CurrentRatio = currentRatio,
            RevenueGrowthYoy = revenueGrowthYoy,
            EarningsGrowthYoy = earningsGrowthYoy,
            QuarterlyRevenueGrowth = qRevGrowth,
            QuarterlyEarningsGrowth = qEarnGrowth,
            ShortPercentOfFloat = shortPct,
            Beta = beta,
            FiftyTwoWeekHigh = fiftyTwoHigh,
            FiftyTwoWeekLow = fiftyTwoLow,
            DataPoints = dataPoints,
            Warnings = warnings,
        };
    }

    // -----------------------------------------------------------------------
    // Market movers — top gainers/losers for universe discovery
    // -----------------------------------------------------------------------

    public record MarketMover(string Ticker, double PercentChange, double Volume);

    /// <summary>
    /// Fetch top market movers (gainers + losers) from the /market_movers endpoint.
    /// Returns tickers with their percent change and volume.
    /// Costs 100 API credits per call (one for gainers, one for losers = 200 total).
    /// </summary>
    public async Task<List<MarketMover>> GetMarketMoversAsync(int count = 20)
    {
        if (!_configured) return [];

        var movers = new List<MarketMover>();

        foreach (var direction in new[] { "gainers", "losers" })
        {
            if (!await ThrottleAsync()) break;

            var url = $"{BaseUrl}/market_movers/stocks?direction={direction}&outputsize={count}&apikey={_apiKey}";
            try
            {
                var body = await GetStringWithRetryAsync(url, "market_movers", direction);
                var json = JsonNode.Parse(body);
                var values = json?["values"] as JsonArray;
                if (values is null)
                {
                    _logger.LogWarning("[twelve-data] /market_movers/{Direction} returned no values", direction);
                    continue;
                }

                foreach (var item in values)
                {
                    if (item is null) continue;
                    var ticker = item["symbol"]?.ToString();
                    if (string.IsNullOrEmpty(ticker)) continue;

                    var pctChange = ParseDouble(item["percent_change"]);
                    var volume = ParseDouble(item["volume"]);
                    movers.Add(new MarketMover(ticker, pctChange, volume));
                }

                _logger.LogInformation("[twelve-data] /market_movers/{Direction}: {Count} tickers",
                    direction, values.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[twelve-data] /market_movers/{Direction} failed", direction);
            }
        }

        return movers;
    }

    // -----------------------------------------------------------------------
    // Provider health
    // -----------------------------------------------------------------------

    public async Task<object> GetProviderHealthAsync()
    {
        if (!_configured)
            return new { status = "not_configured", message = "TWELVE_DATA_API_KEY not set" };

        try
        {
            var quote = await GetQuoteAsync("SPY");
            return new
            {
                status = quote is not null ? "healthy" : "degraded",
                provider = "twelve-data",
                testTicker = "SPY",
                hasQuote = quote is not null,
            };
        }
        catch (Exception ex)
        {
            return new { status = "error", message = ex.Message };
        }
    }

    private static double ParseDouble(JsonNode? node)
    {
        if (node is null) return 0;
        var s = node.ToString();
        return double.TryParse(s, out var d) ? d : 0;
    }

    private static double? ParseNullableDouble(JsonNode? node)
    {
        if (node is null) return null;
        var s = node.ToString();
        if (string.IsNullOrWhiteSpace(s) || s == "null") return null;
        return double.TryParse(s, out var d) ? d : null;
    }

    private static long? ParseNullableLong(JsonNode? node)
    {
        if (node is null) return null;
        var s = node.ToString();
        if (string.IsNullOrWhiteSpace(s) || s == "null") return null;
        return long.TryParse(s, out var l) ? l : null;
    }
}
