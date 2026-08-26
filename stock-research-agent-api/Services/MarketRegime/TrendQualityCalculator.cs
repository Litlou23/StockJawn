using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Backtesting;

namespace StockResearchAgent.Api.Services.MarketRegime;

/// <summary>
/// Computes trend-quality signals from raw SPY OHLC daily candles:
///   1. ADX-14                (trend strength)
///   2. Realized-vol ratio    (5-day RV / 20-day RV)
///   3. Higher-high count     (# of higher-highs in last 10 bars)
///
/// These feed the "IsTradeableRegime" gate — the discovery from our
/// Feb-May vs May-Aug sweeps was that the scoring engine wins when trends
/// exist and loses when they don't. This class quantifies "do trends exist."
///
/// Uses only OHLC (no volume, no options) so backtest and live share the
/// same math. All methods are static + pure — no I/O.
/// </summary>
public static class TrendQualityCalculator
{
    /// <summary>ADX above this = trend regime.</summary>
    public const double AdxTrendingFloor = 25.0;
    /// <summary>ADX below this = chop regime. Lowered from 20→15: ADX 18-20 is mild chop,
    /// not worth sitting in 100% cash for a week.</summary>
    public const double AdxChoppyCeiling = 15.0;
    /// <summary>Realized-vol ratio outside this band = regime transition / instability.</summary>
    public const double RvRatioLow = 0.7;
    public const double RvRatioHigh = 1.3;
    /// <summary>Higher-high count out of 10 that suggests a clean directional bias.</summary>
    public const int HhCountMin = 6;

    public record TrendQuality(
        double? Adx,
        double? RealizedVolRatio,
        int? HigherHighCount,
        bool IsTradeable,
        string Reason);

    /// <summary>
    /// Sweep-able thresholds. Defaults match the compile-time constants.
    /// Sweep sets these via the corresponding parameter override keys:
    ///   regime_adx_floor, regime_rv_low, regime_rv_high, regime_hh_min.
    /// </summary>
    public record TrendQualityThresholds(
        double AdxChoppyCeiling = 15.0,
        double RvRatioLow = 0.7,
        double RvRatioHigh = 1.3,
        int HhCountMin = 6);

    /// <summary>Build thresholds from a backtest ParameterOverrides bag; falls back to defaults.</summary>
    public static TrendQualityThresholds ThresholdsFromOverrides(Dictionary<string, double>? overrides)
    {
        if (overrides is null) return new TrendQualityThresholds();
        double Get(string key, double defaultValue)
            => overrides.TryGetValue(key, out var v) ? v : defaultValue;
        int GetInt(string key, int defaultValue)
            => overrides.TryGetValue(key, out var v) ? (int)Math.Round(v) : defaultValue;

        return new TrendQualityThresholds(
            AdxChoppyCeiling: Get("regime_adx_floor", 15.0),
            RvRatioLow: Get("regime_rv_low", 0.7),
            RvRatioHigh: Get("regime_rv_high", 1.3),
            HhCountMin: GetInt("regime_hh_min", 6));
    }

    /// <summary>
    /// Compute all three signals from the candle series (most recent last).
    /// Requires at least 30 candles for meaningful output; below that returns
    /// all-null + IsTradeable=true (fail-open) so early-history days aren't
    /// silently blocked.
    /// </summary>
    public static TrendQuality Evaluate(IReadOnlyList<HistoricalCandle> candles, TrendQualityThresholds? thresholds = null)
        => EvaluateOhlc(candles.Select(c => (c.High, c.Low, c.Close)).ToList(), thresholds);

    /// <summary>
    /// Live-pipeline overload — takes MarketSnapshotBar (what MarketDataService
    /// returns). Same math, same output.
    /// </summary>
    public static TrendQuality Evaluate(IReadOnlyList<MarketSnapshotBar> bars, TrendQualityThresholds? thresholds = null)
        => EvaluateOhlc(bars.Select(b => (b.High, b.Low, b.Close)).ToList(), thresholds);

    /// <summary>Shared core — takes just the OHLC series so backtest and live share the math.</summary>
    private static TrendQuality EvaluateOhlc(IReadOnlyList<(double High, double Low, double Close)> ohlc, TrendQualityThresholds? thresholds)
    {
        thresholds ??= new TrendQualityThresholds();
        if (ohlc.Count < 30)
            return new TrendQuality(null, null, null, IsTradeable: true,
                Reason: $"insufficient history ({ohlc.Count} candles) — defaulting to tradeable");

        var adx = ComputeAdx(ohlc, period: 14);
        var rvRatio = ComputeRealizedVolRatio(ohlc);
        var hhCount = ComputeHigherHighCount(ohlc, window: 10);

        var (tradeable, reason) = ClassifyTradeable(adx, rvRatio, hhCount, thresholds);
        return new TrendQuality(adx, rvRatio, hhCount, tradeable, reason);
    }

    // ────────────────────────────────────────────────────────────────
    // ADX-14 — Welles Wilder's Average Directional Index.
    //   1. TR (true range) = max(H-L, |H-prevClose|, |L-prevClose|)
    //   2. +DM = today's H - prev H (if > 0 AND > |today's L - prev L|; else 0)
    //   3. -DM = prev L - today's L (analogous)
    //   4. Wilder-smooth (SMA seed then EMA-style)
    //   5. +DI = 100 * smoothed(+DM) / ATR
    //   6. -DI = 100 * smoothed(-DM) / ATR
    //   7. DX  = 100 * |+DI - -DI| / (+DI + -DI)
    //   8. ADX = Wilder-smoothed DX
    // ────────────────────────────────────────────────────────────────
    public static double? ComputeAdx(IReadOnlyList<(double High, double Low, double Close)> candles, int period = 14)
    {
        if (candles.Count < period * 2 + 1) return null;

        int n = candles.Count;
        var tr = new double[n];
        var plusDm = new double[n];
        var minusDm = new double[n];

        for (int i = 1; i < n; i++)
        {
            var h = candles[i].High;
            var l = candles[i].Low;
            var prevClose = candles[i - 1].Close;
            var prevHigh = candles[i - 1].High;
            var prevLow = candles[i - 1].Low;

            tr[i] = Math.Max(h - l, Math.Max(Math.Abs(h - prevClose), Math.Abs(l - prevClose)));

            var up = h - prevHigh;
            var dn = prevLow - l;
            plusDm[i] = (up > dn && up > 0) ? up : 0;
            minusDm[i] = (dn > up && dn > 0) ? dn : 0;
        }

        // Wilder smoothing: seed with SMA(period), then rolling ((n-1)*prev + curr) / n
        var atr = new double[n];
        var plusSm = new double[n];
        var minusSm = new double[n];

        double sumTr = 0, sumPlus = 0, sumMinus = 0;
        for (int i = 1; i <= period; i++)
        {
            sumTr += tr[i];
            sumPlus += plusDm[i];
            sumMinus += minusDm[i];
        }
        atr[period] = sumTr;
        plusSm[period] = sumPlus;
        minusSm[period] = sumMinus;

        for (int i = period + 1; i < n; i++)
        {
            atr[i] = atr[i - 1] - (atr[i - 1] / period) + tr[i];
            plusSm[i] = plusSm[i - 1] - (plusSm[i - 1] / period) + plusDm[i];
            minusSm[i] = minusSm[i - 1] - (minusSm[i - 1] / period) + minusDm[i];
        }

        // DX starts at 'period', then ADX starts at 2*period (needs `period` DX values to seed)
        var dx = new double[n];
        for (int i = period; i < n; i++)
        {
            if (atr[i] <= 0) { dx[i] = 0; continue; }
            var plusDi = 100.0 * plusSm[i] / atr[i];
            var minusDi = 100.0 * minusSm[i] / atr[i];
            var sum = plusDi + minusDi;
            dx[i] = sum <= 0 ? 0 : 100.0 * Math.Abs(plusDi - minusDi) / sum;
        }

        // ADX seed: average of first `period` DX values (starting at index `period`)
        double dxSum = 0;
        int dxStart = period;
        int dxSeedEnd = dxStart + period - 1;
        if (dxSeedEnd >= n) return null;
        for (int i = dxStart; i <= dxSeedEnd; i++) dxSum += dx[i];
        double adx = dxSum / period;

        // Wilder-smooth ADX forward
        for (int i = dxSeedEnd + 1; i < n; i++)
            adx = ((adx * (period - 1)) + dx[i]) / period;

        return Math.Round(adx, 2);
    }

    /// <summary>Ratio of 5-day close-to-close realized vol to 20-day.</summary>
    public static double? ComputeRealizedVolRatio(IReadOnlyList<(double High, double Low, double Close)> candles)
    {
        if (candles.Count < 22) return null;

        var closes = candles.Select(c => c.Close).ToList();
        var last5 = RealizedVol(closes, 5);
        var last20 = RealizedVol(closes, 20);
        if (last20 <= 0) return null;
        return Math.Round(last5 / last20, 4);
    }

    private static double RealizedVol(IReadOnlyList<double> closes, int window)
    {
        int n = closes.Count;
        if (n < window + 1) return 0;
        var returns = new double[window];
        for (int i = 0; i < window; i++)
        {
            var idx = n - window + i;
            if (idx <= 0 || closes[idx - 1] <= 0) return 0;
            returns[i] = Math.Log(closes[idx] / closes[idx - 1]);
        }
        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / window;
        return Math.Sqrt(variance);
    }

    /// <summary>
    /// Count higher-highs in the last `window` candles. A higher-high is a
    /// candle whose High is greater than the previous candle's High.
    /// </summary>
    public static int? ComputeHigherHighCount(IReadOnlyList<(double High, double Low, double Close)> candles, int window = 10)
    {
        if (candles.Count < window + 1) return null;
        int hh = 0;
        for (int i = candles.Count - window; i < candles.Count; i++)
            if (candles[i].High > candles[i - 1].High) hh++;
        return hh;
    }

    /// <summary>
    /// Combine the three signals into a tradeable/not-tradeable decision.
    /// The rules mirror the "trend-following surfer" intuition: only paddle
    /// out when the water actually has waves.
    /// </summary>
    public static (bool tradeable, string reason) ClassifyTradeable(
        double? adx, double? rvRatio, int? higherHighCount, TrendQualityThresholds? thresholds = null)
    {
        thresholds ??= new TrendQualityThresholds();
        var problems = new List<string>();

        if (adx.HasValue && adx.Value < thresholds.AdxChoppyCeiling)
            problems.Add($"ADX {adx.Value:F1} < {thresholds.AdxChoppyCeiling} (chop)");

        if (rvRatio.HasValue && (rvRatio.Value < thresholds.RvRatioLow || rvRatio.Value > thresholds.RvRatioHigh))
            problems.Add($"realized-vol ratio {rvRatio.Value:F2} outside [{thresholds.RvRatioLow}, {thresholds.RvRatioHigh}] (unstable)");

        if (higherHighCount.HasValue && higherHighCount.Value < thresholds.HhCountMin
            && higherHighCount.Value > (10 - thresholds.HhCountMin))
            problems.Add($"HH count {higherHighCount.Value}/10 (no clear direction)");

        if (problems.Count == 0)
        {
            var parts = new List<string>();
            if (adx.HasValue) parts.Add($"ADX={adx.Value:F1}");
            if (rvRatio.HasValue) parts.Add($"RVratio={rvRatio.Value:F2}");
            if (higherHighCount.HasValue) parts.Add($"HH={higherHighCount.Value}/10");
            return (true, $"tradeable ({string.Join(", ", parts)})");
        }

        return (false, $"skip: {string.Join("; ", problems)}");
    }
}
