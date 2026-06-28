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

    // Free tier: 8 requests/minute, 800/day. We allow 7/min to stay safe.
    private const int MaxRequestsPerMinute = 7;
    private static readonly SemaphoreSlim _throttle = new(1, 1);
    private static readonly Queue<DateTimeOffset> _requestTimestamps = new();

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly bool _configured;
    private readonly ILogger<TwelveDataProvider> _logger;

    public TwelveDataProvider(IConfiguration configuration, ILogger<TwelveDataProvider> logger)
    {
        _logger = logger;
        _apiKey = configuration["TWELVE_DATA_API_KEY"] ?? "";
        _configured = !string.IsNullOrWhiteSpace(_apiKey);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        if (!_configured)
            _logger.LogWarning("[twelve-data] TWELVE_DATA_API_KEY not set -- market data unavailable");
    }

    public bool IsConfigured => _configured;

    /// <summary>
    /// Waits if necessary to stay within the free-tier rate limit (7 req/min).
    /// </summary>
    private async Task ThrottleAsync()
    {
        await _throttle.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            // Remove timestamps older than 60 seconds
            while (_requestTimestamps.Count > 0 && (now - _requestTimestamps.Peek()).TotalSeconds > 60)
                _requestTimestamps.Dequeue();

            if (_requestTimestamps.Count >= MaxRequestsPerMinute)
            {
                var oldest = _requestTimestamps.Peek();
                var waitMs = (int)(60_000 - (now - oldest).TotalMilliseconds) + 500; // +500ms buffer
                if (waitMs > 0)
                {
                    _logger.LogInformation("[twelve-data] Rate limit reached, waiting {WaitMs}ms", waitMs);
                    await Task.Delay(waitMs);
                }
                // Clean again after waiting
                now = DateTimeOffset.UtcNow;
                while (_requestTimestamps.Count > 0 && (now - _requestTimestamps.Peek()).TotalSeconds > 60)
                    _requestTimestamps.Dequeue();
            }

            _requestTimestamps.Enqueue(DateTimeOffset.UtcNow);
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

        await ThrottleAsync();
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

        await ThrottleAsync();
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
