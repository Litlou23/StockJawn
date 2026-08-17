using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchEngine;

namespace StockResearchAgent.Api.Services.Backtesting;

/// <summary>
/// Builds a <see cref="MarketSnapshot"/> for any past date using stored
/// historical candles. Produces output structurally identical to what the
/// live <see cref="MarketSnapshotBuilder"/> creates, so the existing
/// scoring pipeline can consume it without modification.
///
/// All technical indicators (RSI, MACD, EMA, Bollinger, etc.) are computed
/// from the candle array — no API calls required, no rate limits.
/// News and fundamentals are unavailable for historical dates (set to empty/null).
/// </summary>
public class HistoricalMarketSnapshotBuilder
{
    /// <summary>Number of bars to feed into IndicatorEngine.Compute (most recent bars ending at target date).</summary>
    private const int RecentBarCount = 20;

    /// <summary>Number of bars of history needed before the target date for accurate EMA/MACD computation.</summary>
    private const int EmaWarmupBars = 60;

    private readonly HistoricalDataLoader _dataLoader;
    private readonly ILogger<HistoricalMarketSnapshotBuilder> _logger;

    public HistoricalMarketSnapshotBuilder(
        HistoricalDataLoader dataLoader,
        ILogger<HistoricalMarketSnapshotBuilder> logger)
    {
        _dataLoader = dataLoader;
        _logger = logger;
    }

    /// <summary>
    /// Build a MarketSnapshot for a ticker at a specific historical date.
    /// Returns null if insufficient candle data exists.
    /// </summary>
    public async Task<MarketSnapshot?> BuildAsync(string ticker, DateOnly targetDate, string runId)
    {
        // Pull enough history for EMA warmup + the bars the indicators need
        var lookbackStart = targetDate.AddDays(-(EmaWarmupBars + RecentBarCount + 30)); // extra buffer for weekends/holidays
        var candles = await _dataLoader.GetCandlesAsync(ticker, lookbackStart, targetDate);

        if (candles.Count < 5)
        {
            _logger.LogDebug("[hist-snapshot] Skipping {Ticker} at {Date} — only {Count} candles",
                ticker, targetDate, candles.Count);
            return null;
        }

        // Find the target date's candle (or closest prior trading day)
        var targetCandle = candles.LastOrDefault(c => c.Date <= targetDate);
        if (targetCandle is null) return null;

        // Get candles up to and including the target date (most recent first for IndicatorEngine)
        var candlesUpToTarget = candles
            .Where(c => c.Date <= targetCandle.Date)
            .OrderByDescending(c => c.Date)
            .ToList();

        if (candlesUpToTarget.Count < 5) return null;

        // Build MarketSnapshotBars (most recent first — same order as live pipeline)
        var recentBars = candlesUpToTarget
            .Take(RecentBarCount)
            .Select(c => new MarketSnapshotBar
            {
                Date = c.Date.ToString("yyyy-MM-dd"),
                Open = c.Open,
                High = c.High,
                Low = c.Low,
                Close = c.Close,
                Volume = c.Volume,
            })
            .ToList();

        // Build synthetic quote from the target date's candle
        var prevCandle = candlesUpToTarget.Count > 1 ? candlesUpToTarget[1] : null;
        var change = prevCandle is not null ? targetCandle.Close - prevCandle.Close : 0;
        var changePct = prevCandle is not null && prevCandle.Close > 0
            ? (change / prevCandle.Close) * 100 : 0;

        var quote = new MarketSnapshotQuote
        {
            Price = targetCandle.Close,
            Open = targetCandle.Open,
            High = targetCandle.High,
            Low = targetCandle.Low,
            PreviousClose = prevCandle?.Close ?? targetCandle.Open,
            Volume = targetCandle.Volume,
            Change = change,
            ChangePercent = changePct,
            Timestamp = targetCandle.Date.ToString("yyyy-MM-dd"),
        };

        // Compute TechnicalContext (summary strings) from bars
        var technicalContext = ComputeTechnicalContext(recentBars);

        var warnings = new List<string> { "historical_snapshot", "no_news_data", "no_fundamentals" };

        return new MarketSnapshot
        {
            Id = "",
            RunId = runId,
            Ticker = ticker,
            Quote = quote,
            RecentBars = recentBars,
            TechnicalContext = technicalContext,
            NewsContext = [],        // no historical news
            Fundamentals = null,     // no historical fundamentals
            DataAvailability = new MarketSnapshotAvailability
            {
                MarketDataAvailable = true,
                NewsAvailable = false,
                FundamentalsAvailable = false,
                OptionsChainAvailable = false,
                Warnings = warnings,
            },
            CreatedAt = new DateTimeOffset(targetCandle.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        };
    }

    /// <summary>
    /// Compute full TechnicalIndicators for a ticker at a historical date,
    /// including MACD and EMA computed from candle math (no API calls).
    /// Returns null if insufficient data.
    /// </summary>
    public async Task<TechnicalIndicators?> ComputeIndicatorsAsync(
        string ticker, DateOnly targetDate)
    {
        var lookbackStart = targetDate.AddDays(-(EmaWarmupBars + RecentBarCount + 30));
        var candles = await _dataLoader.GetCandlesAsync(ticker, lookbackStart, targetDate);

        if (candles.Count < 5) return null;

        var candlesUpToTarget = candles
            .Where(c => c.Date <= targetDate)
            .OrderByDescending(c => c.Date)
            .ToList();

        if (candlesUpToTarget.Count < 5) return null;

        var recentBars = candlesUpToTarget
            .Take(RecentBarCount)
            .Select(c => new MarketSnapshotBar
            {
                Date = c.Date.ToString("yyyy-MM-dd"),
                Open = c.Open, High = c.High,
                Low = c.Low, Close = c.Close,
                Volume = c.Volume,
            })
            .ToList();

        // Base indicators from IndicatorEngine (RSI, Bollinger, ATR, etc.)
        var indicators = IndicatorEngine.Compute(recentBars);

        // Compute MACD and EMA from full candle history (needs 50+ bars for accuracy)
        var allCloses = candlesUpToTarget
            .Select(c => c.Close)
            .Reverse()     // chronological order for EMA computation
            .ToList();

        var apiMacd = ComputeMacd(allCloses);
        var apiEma = ComputeEmas(allCloses);

        return IndicatorEngine.MergeApiIndicators(indicators, apiMacd, apiEma);
    }

    /// <summary>
    /// Build a BenchmarkContext for a historical date using stored SPY/QQQ candles.
    /// </summary>
    public async Task<BenchmarkContext> ComputeBenchmarkAsync(
        MarketSnapshotQuote tickerQuote, DateOnly targetDate, string? sector = null)
    {
        var spyQuote = await BuildSyntheticQuoteAsync("SPY", targetDate);
        var qqqQuote = await BuildSyntheticQuoteAsync("QQQ", targetDate);

        // SPY EMA20 from historical candles
        double? spyEma20 = null;
        var spyCandles = await GetCandlesForEma("SPY", targetDate, 30);
        if (spyCandles.Count >= 20)
        {
            var spyCloses = spyCandles.Select(c => c.Close).Reverse().ToList();
            spyEma20 = ComputeEma(spyCloses, 20);
        }

        // Sector ETF data
        string? sectorEtf = IndicatorEngine.GetSectorEtf(sector);
        double? sectorEtfPrice = null;
        double? sectorEtfEma = null;
        if (sectorEtf is not null)
        {
            var etfQuote = await BuildSyntheticQuoteAsync(sectorEtf, targetDate);
            sectorEtfPrice = etfQuote?.Price;

            var etfCandles = await GetCandlesForEma(sectorEtf, targetDate, 35);
            if (etfCandles.Count >= 26)
            {
                var etfCloses = etfCandles.Select(c => c.Close).Reverse().ToList();
                sectorEtfEma = ComputeEma(etfCloses, 26);
            }
        }

        return IndicatorEngine.ComputeBenchmarkContext(
            tickerQuote, spyQuote, qqqQuote,
            spyEma20, sectorEtf, sectorEtfPrice, sectorEtfEma);
    }

    // ── Private helpers ─────────────────────────────────────────

    /// <summary>
    /// Build a synthetic MarketSnapshotQuote for an ETF at a historical date.
    /// </summary>
    private async Task<MarketSnapshotQuote?> BuildSyntheticQuoteAsync(string ticker, DateOnly targetDate)
    {
        var lookback = targetDate.AddDays(-10);
        var candles = await _dataLoader.GetCandlesAsync(ticker, lookback, targetDate);
        if (candles.Count < 2) return null;

        var latest = candles.Last(c => c.Date <= targetDate);
        var prev = candles.LastOrDefault(c => c.Date < latest.Date);
        if (prev is null) return null;

        var change = latest.Close - prev.Close;
        return new MarketSnapshotQuote
        {
            Price = latest.Close,
            Open = latest.Open,
            High = latest.High,
            Low = latest.Low,
            PreviousClose = prev.Close,
            Volume = latest.Volume,
            Change = change,
            ChangePercent = prev.Close > 0 ? (change / prev.Close) * 100 : 0,
            Timestamp = latest.Date.ToString("yyyy-MM-dd"),
        };
    }

    private async Task<List<HistoricalCandle>> GetCandlesForEma(string ticker, DateOnly targetDate, int daysBack)
    {
        var start = targetDate.AddDays(-(daysBack + 15)); // buffer for weekends
        var candles = await _dataLoader.GetCandlesAsync(ticker, start, targetDate);
        return candles.Where(c => c.Date <= targetDate).OrderByDescending(c => c.Date).ToList();
    }

    /// <summary>
    /// Compute TechnicalContext summary strings from bars (same as TwelveDataProvider.ComputeTechnicalContext).
    /// </summary>
    private static MarketSnapshotTechnical? ComputeTechnicalContext(List<MarketSnapshotBar> bars)
    {
        if (bars.Count < 5) return null;

        var recent5 = bars.Take(5).Select(b => b.Close).ToList();
        var trendDirection = recent5[0] > recent5[^1] ? "bullish"
            : recent5[0] < recent5[^1] ? "bearish" : "neutral";

        var sma5 = recent5.Average();
        var sma20 = bars.Count >= 20 ? bars.Take(20).Average(b => b.Close) : sma5;
        var maPosition = sma5 > sma20 ? "above" : "below";
        var maSummary = $"SMA5 ({sma5:F2}) {maPosition} SMA20 ({sma20:F2})";

        var roc = bars.Count >= 5 && bars[^1].Close > 0
            ? ((bars[0].Close - bars[^1].Close) / bars[^1].Close) * 100 : 0;
        var momSummary = roc > 1 ? $"Momentum up ({roc:F1}%)"
            : roc < -1 ? $"Momentum down ({roc:F1}%)"
            : $"Momentum flat ({roc:F1}%)";

        var avgVol = bars.Average(b => b.Volume);
        var latestVol = bars[0].Volume;
        var volRatio = avgVol > 0 ? latestVol / avgVol : 1;
        var volSummary = volRatio > 1.5 ? $"Volume elevated ({volRatio:F1}x avg)"
            : volRatio < 0.7 ? $"Volume below average ({volRatio:F1}x avg)"
            : $"Volume normal ({volRatio:F1}x avg)";

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

    // ── EMA / MACD math ─────────────────────────────────────────

    /// <summary>
    /// Compute EMA for a given period from chronologically-ordered closes.
    /// Returns the most recent EMA value, or null if insufficient data.
    /// </summary>
    internal static double? ComputeEma(List<double> closesChronological, int period)
    {
        if (closesChronological.Count < period) return null;

        double multiplier = 2.0 / (period + 1);
        // Seed with SMA of first `period` values
        double ema = closesChronological.Take(period).Average();

        for (int i = period; i < closesChronological.Count; i++)
        {
            ema = (closesChronological[i] - ema) * multiplier + ema;
        }

        return Math.Round(ema, 4);
    }

    /// <summary>
    /// Compute MACD (12, 26, 9) from chronologically-ordered closes.
    /// Returns (macdLine, signalLine, histogram) or null if insufficient data.
    /// </summary>
    internal static (double MacdLine, double Signal, double Histogram)? ComputeMacd(
        List<double> closesChronological)
    {
        if (closesChronological.Count < 35) return null; // need 26 for slow EMA + 9 for signal

        double fastMult = 2.0 / 13;  // EMA12
        double slowMult = 2.0 / 27;  // EMA26
        double sigMult = 2.0 / 10;   // Signal EMA9

        // Seed EMAs with SMA
        double fastEma = closesChronological.Take(12).Average();
        double slowEma = closesChronological.Take(26).Average();

        // Build MACD line series from bar 26 onward
        var macdValues = new List<double>();

        // Warm up fast EMA to bar 26
        for (int i = 12; i < 26; i++)
            fastEma = (closesChronological[i] - fastEma) * fastMult + fastEma;

        // From bar 26, compute both EMAs and MACD line
        for (int i = 26; i < closesChronological.Count; i++)
        {
            fastEma = (closesChronological[i] - fastEma) * fastMult + fastEma;
            slowEma = (closesChronological[i] - slowEma) * slowMult + slowEma;
            macdValues.Add(fastEma - slowEma);
        }

        if (macdValues.Count < 9) return null;

        // Compute signal line (EMA9 of MACD values)
        double signal = macdValues.Take(9).Average();
        for (int i = 9; i < macdValues.Count; i++)
            signal = (macdValues[i] - signal) * sigMult + signal;

        var macdLine = macdValues[^1];
        var histogram = macdLine - signal;

        return (Math.Round(macdLine, 4), Math.Round(signal, 4), Math.Round(histogram, 4));
    }

    /// <summary>
    /// Compute EMA12, EMA26, EMA50 from chronologically-ordered closes.
    /// </summary>
    internal static (double? Ema12, double? Ema26, double? Ema50)? ComputeEmas(
        List<double> closesChronological)
    {
        var ema12 = ComputeEma(closesChronological, 12);
        var ema26 = ComputeEma(closesChronological, 26);
        var ema50 = ComputeEma(closesChronological, 50);

        if (ema12 is null && ema26 is null && ema50 is null) return null;
        return (ema12, ema26, ema50);
    }
}
