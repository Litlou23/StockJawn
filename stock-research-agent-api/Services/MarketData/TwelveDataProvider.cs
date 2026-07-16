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
    // Grow tier: 30/min, 5000/day. Pro: 120/min, unlimited.
    private readonly int _maxRequestsPerMinute;
    private readonly int _maxRequestsPerDay;
    private readonly int _minGapMs; // minimum ms between requests to avoid bursts
    private static readonly SemaphoreSlim _throttle = new(1, 1);
    private static DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;
    private static int _requestsThisMinute;
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

        // Allow override for paid plans: TWELVE_DATA_RPM and TWELVE_DATA_DAILY
        _maxRequestsPerMinute = int.TryParse(configuration["TWELVE_DATA_RPM"], out var rpm) ? rpm : 7;
        _maxRequestsPerDay = int.TryParse(configuration["TWELVE_DATA_DAILY"], out var daily) ? daily : 750;
        // Space requests evenly: 60s / rpm + 500ms buffer → e.g. 7 rpm = ~9s gap
        _minGapMs = (60_000 / _maxRequestsPerMinute) + 500;

        if (!_configured)
            _logger.LogWarning("[twelve-data] TWELVE_DATA_API_KEY not set -- market data unavailable");
        else
            _logger.LogInformation("[twelve-data] Configured: {RPM} req/min, {Daily} req/day", _maxRequestsPerMinute, _maxRequestsPerDay);
    }

    public bool IsConfigured => _configured;

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
            var resp = await _http.GetStringAsync(url);
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
            var resp = await _http.GetStringAsync(url);
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
}
