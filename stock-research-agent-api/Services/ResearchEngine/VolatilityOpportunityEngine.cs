using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Feature computation layer that produces per-stock volatility context.
/// Sits between IndicatorEngine and ScoringEngine — reads existing indicator
/// output plus raw OHLCV bars and computes ~15 volatility features.
///
/// Phase 1: feature generation only.
/// Does NOT generate predictions, trade signals, or modify scoring.
/// </summary>
public class VolatilityOpportunityEngine
{
    private const int HistoryWindowDays = 60;
    private const int AccelerationWindow = 5;
    private const int VolumePersistenceWindow = 3;

    /// <summary>
    /// Compute a <see cref="VolatilityOpportunityAssessment"/> from existing pipeline data.
    /// All inputs are already available in the Morning Scan pipeline — no new data fetches.
    /// </summary>
    /// <param name="ticker">Stock ticker.</param>
    /// <param name="bars">Recent OHLCV bars, newest first (same ordering as IndicatorEngine).</param>
    /// <param name="indicators">Output of <see cref="IndicatorEngine.Compute"/>.</param>
    /// <param name="news">News context from MarketSnapshot (for catalyst age).</param>
    /// <param name="marketRegime">Current market regime (read-only context, not modified).</param>
    public VolatilityOpportunityAssessment Assess(
        string ticker,
        List<MarketSnapshotBar> bars,
        TechnicalIndicators indicators,
        List<MarketSnapshotNews> news,
        MarketRegimeResult? marketRegime = null)
    {
        var skipped = new List<string>();
        int count = bars.Count;

        // ── ATR Percentile ──────────────────────────────────────
        var (atrPercentile, atrAcceleration) = ComputeAtrFeatures(bars, indicators, skipped);

        // ── Bandwidth Percentile & Direction ────────────────────
        var (bwPercentile, bwDirection) = ComputeBandwidthFeatures(bars, indicators, skipped);

        // ── Per-Stock Volatility Regime ─────────────────────────
        var regime = ClassifyVolatilityRegime(atrPercentile, bwPercentile);

        // ── Gap Features ────────────────────────────────────────
        var (gapPct, gapDir, gapType, gapWithVol) = ComputeGapFeatures(bars, indicators, skipped);

        // ── Support / Resistance Distance ───────────────────────
        var (distSupport, distResistance) = ComputeSupportResistanceDistance(bars, indicators, skipped);

        // ── Volume Ratio Persistence ────────────────────────────
        var volPersistence = ComputeVolumeRatioPersistence(bars, skipped);

        // ── Catalyst Age ────────────────────────────────────────
        var catalystAge = ComputeCatalystAge(news, skipped);

        // ── Opportunity Classification ──────────────────────────
        var opportunity = ClassifyOpportunity(
            indicators, atrPercentile, atrAcceleration,
            bwPercentile, bwDirection, regime,
            gapPct, gapDir, gapType, gapWithVol,
            distSupport, volPersistence,
            marketRegime);

        return new VolatilityOpportunityAssessment
        {
            Ticker = ticker,
            AssessedAt = DateTimeOffset.UtcNow,

            AtrPercentile = atrPercentile,
            AtrAcceleration = atrAcceleration,
            BandwidthPercentile = bwPercentile,
            BandwidthDirection = bwDirection,
            StockVolRegime = regime,

            GapPercent = gapPct,
            GapDir = gapDir,
            GapClassification = gapType,
            GapWithVolume = gapWithVol,

            DistanceFromSupport = distSupport,
            DistanceFromResistance = distResistance,

            VolumeRatioPersistence = volPersistence,
            CatalystAgeHours = catalystAge,

            Opportunity = opportunity,
            OpportunityScore = 0, // Phase 3: scoring engine consumes classification, not a score
            RiskModifier = 0,     // Phase 3: risk modifier computed by learning

            FeaturesSkipped = skipped,
            BarsUsedForHistory = Math.Min(count, HistoryWindowDays),
        };
    }

    // ─────────────────────────────────────────────────────────────
    // Opportunity classification
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Deterministic opportunity classification. Each type has documented rules.
    /// Order matters — first match wins. More specific patterns are checked first.
    /// </summary>
    private static OpportunityType ClassifyOpportunity(
        TechnicalIndicators indicators,
        double? atrPctile, double? atrAccel,
        double? bwPctile, double? bwDirection,
        StockVolatilityRegime regime,
        double? gapPct, GapDirection gapDir, GapType gapType, bool gapWithVol,
        double? distSupport, double? volPersistence,
        MarketRegimeResult? marketRegime)
    {
        // ── VolatilityTrap ──────────────────────────────────────
        // High volatility but no volume behind it = thin market, unpredictable.
        // Rule: ATR pctile > 85 AND volume persistence < 0.8
        if (atrPctile > 85 && volPersistence is not null && volPersistence < 0.8)
            return OpportunityType.VolatilityTrap;

        // ── DipAfterPanic ───────────────────────────────────────
        // Large gap down with volume + oversold RSI + not in crisis.
        // Rule: gap down >= 5% AND RSI < 35 AND volume ratio > 1.5
        //       AND market regime is NOT CrisisSelling (if known)
        if (gapDir == GapDirection.Down
            && gapType >= GapType.Large
            && gapWithVol
            && indicators.Rsi14 is not null && indicators.Rsi14 < 35
            && !IsCrisisRegime(marketRegime))
            return OpportunityType.DipAfterPanic;

        // ── ExhaustionReversal ───────────────────────────────────
        // Extreme volatility + overbought/oversold + ATR decelerating.
        // Rule: ATR pctile > 90 AND (RSI > 75 OR RSI < 25) AND ATR acceleration < 0
        if (atrPctile > 90
            && indicators.Rsi14 is not null
            && (indicators.Rsi14 > 75 || indicators.Rsi14 < 25)
            && atrAccel is not null && atrAccel < 0)
            return OpportunityType.ExhaustionReversal;

        // ── SqueezeBreakout ─────────────────────────────────────
        // Compressed volatility now expanding with a Donchian breakout.
        // Rule: bandwidth pctile < 20 AND bandwidth direction > 0 (expanding)
        //       AND (Donchian breakout OR Donchian breakdown)
        if (bwPctile is not null && bwPctile < 20
            && bwDirection is not null && bwDirection > 0
            && (indicators.DonchianBreakout == true || indicators.DonchianBreakdown == true))
            return OpportunityType.SqueezeBreakout;

        // ── MomentumContinuation ────────────────────────────────
        // Gap up with volume in an expanding volatility regime, RSI mid-range.
        // Rule: gap up AND volume ratio > 2.0 AND RSI 45-72 AND ATR pctile > 60
        if (gapDir == GapDirection.Up
            && indicators.VolumeRatio is not null && indicators.VolumeRatio > 2.0
            && indicators.Rsi14 is not null && indicators.Rsi14 >= 45 && indicators.Rsi14 <= 72
            && atrPctile > 60)
            return OpportunityType.MomentumContinuation;

        // ── FailedBounce ────────────────────────────────────────
        // Price near support + high volatility + volume drying up.
        // Rule: distance from support < 2% AND ATR pctile > 70
        //       AND volume persistence < 0.9 AND RSI < 40
        if (distSupport is not null && distSupport < 2.0
            && atrPctile > 70
            && volPersistence is not null && volPersistence < 0.9
            && indicators.Rsi14 is not null && indicators.Rsi14 < 40)
            return OpportunityType.FailedBounce;

        return OpportunityType.None;
    }

    /// <summary>Check if market is in a crisis-selling regime where mean reversion is dangerous.</summary>
    private static bool IsCrisisRegime(MarketRegimeResult? regime)
    {
        if (regime is null) return false;
        return regime.ActiveRegimes.Any(r =>
            r.Type is MarketRegimeType.RiskOff or MarketRegimeType.Contraction
            && r.Confidence > 0.7);
    }

    // ─────────────────────────────────────────────────────────────
    // ATR features
    // ─────────────────────────────────────────────────────────────

    private static (double? percentile, double? acceleration) ComputeAtrFeatures(
        List<MarketSnapshotBar> bars, TechnicalIndicators indicators, List<string> skipped)
    {
        if (indicators.Atr14 is null)
        {
            skipped.Add("AtrPercentile (ATR14 not computed)");
            skipped.Add("AtrAcceleration (ATR14 not computed)");
            return (null, null);
        }

        int count = bars.Count;
        // Need enough bars for a rolling ATR history
        int minBarsForPercentile = 15 + HistoryWindowDays; // 14 for ATR + 60 for history
        if (count < minBarsForPercentile)
        {
            skipped.Add($"AtrPercentile (need {minBarsForPercentile} bars, have {count})");
        }

        // Compute ATR for each possible 14-bar window in the history
        var atrHistory = ComputeRollingAtr(bars, 14, HistoryWindowDays);
        double? percentile = null;
        if (atrHistory.Count >= 10) // need reasonable sample
        {
            var currentAtr = indicators.Atr14.Value;
            int belowCount = atrHistory.Count(a => a < currentAtr);
            percentile = Math.Round(100.0 * belowCount / atrHistory.Count, 1);
        }
        else if (!skipped.Any(s => s.StartsWith("AtrPercentile")))
        {
            skipped.Add($"AtrPercentile (only {atrHistory.Count} ATR samples)");
        }

        // ATR acceleration: compare ATR now vs 5 bars ago
        double? acceleration = null;
        if (count >= 15 + AccelerationWindow)
        {
            var atrNow = indicators.Atr14.Value;
            var atrPast = ComputeAtrAt(bars, 14, AccelerationWindow);
            if (atrPast is not null && atrPast > 0)
                acceleration = Math.Round((atrNow - atrPast.Value) / atrPast.Value * 100, 2);
        }
        else
        {
            skipped.Add($"AtrAcceleration (need {15 + AccelerationWindow} bars, have {count})");
        }

        return (percentile, acceleration);
    }

    // ─────────────────────────────────────────────────────────────
    // Bandwidth features
    // ─────────────────────────────────────────────────────────────

    private static (double? percentile, double? direction) ComputeBandwidthFeatures(
        List<MarketSnapshotBar> bars, TechnicalIndicators indicators, List<string> skipped)
    {
        if (indicators.BollingerBandwidth is null)
        {
            skipped.Add("BandwidthPercentile (Bollinger not computed)");
            skipped.Add("BandwidthDirection (Bollinger not computed)");
            return (null, null);
        }

        var bwHistory = ComputeRollingBandwidth(bars, 20, HistoryWindowDays);
        double? percentile = null;
        if (bwHistory.Count >= 10)
        {
            var currentBw = indicators.BollingerBandwidth.Value;
            int belowCount = bwHistory.Count(b => b < currentBw);
            percentile = Math.Round(100.0 * belowCount / bwHistory.Count, 1);
        }
        else
        {
            skipped.Add($"BandwidthPercentile (only {bwHistory.Count} bandwidth samples)");
        }

        // Direction: slope of last 5 bandwidth values
        double? direction = null;
        if (bwHistory.Count >= AccelerationWindow)
        {
            // bwHistory[0] is the most recent, take 5 most recent and compute slope
            var recentBw = bwHistory.Take(AccelerationWindow).Reverse().ToList();
            direction = Math.Round(ComputeLinearRegressionSlope(recentBw), 4);
        }
        else
        {
            skipped.Add("BandwidthDirection (insufficient bandwidth history)");
        }

        return (percentile, direction);
    }

    // ─────────────────────────────────────────────────────────────
    // Volatility regime classification
    // ─────────────────────────────────────────────────────────────

    private static StockVolatilityRegime ClassifyVolatilityRegime(double? atrPctile, double? bwPctile)
    {
        if (atrPctile is null && bwPctile is null)
            return StockVolatilityRegime.Unknown;

        // Use whichever is available; prefer both
        var atr = atrPctile ?? 50.0; // assume mid if unavailable
        var bw = bwPctile ?? 50.0;

        if (atr > 90)
            return StockVolatilityRegime.Extreme;
        if (atr > 80 || bw > 80)
            return StockVolatilityRegime.Expanding;
        if (atr < 20 && bw < 20)
            return StockVolatilityRegime.Squeeze;

        return StockVolatilityRegime.Normal;
    }

    // ─────────────────────────────────────────────────────────────
    // Gap features
    // ─────────────────────────────────────────────────────────────

    private static (double? pct, GapDirection dir, GapType type, bool withVolume) ComputeGapFeatures(
        List<MarketSnapshotBar> bars, TechnicalIndicators indicators, List<string> skipped)
    {
        if (bars.Count < 2)
        {
            skipped.Add("GapFeatures (need at least 2 bars)");
            return (null, GapDirection.None, GapType.NoGap, false);
        }

        var todayOpen = bars[0].Open;
        var yesterdayClose = bars[1].Close;

        if (yesterdayClose <= 0)
        {
            skipped.Add("GapFeatures (yesterday close is zero)");
            return (null, GapDirection.None, GapType.NoGap, false);
        }

        var gapPct = Math.Round((todayOpen - yesterdayClose) / yesterdayClose * 100, 2);
        var absGap = Math.Abs(gapPct);

        var direction = gapPct > 0 ? GapDirection.Up
                      : gapPct < 0 ? GapDirection.Down
                      : GapDirection.None;

        var classification = absGap switch
        {
            < 1.0 => GapType.NoGap,
            < 3.0 => GapType.Small,
            < 5.0 => GapType.Significant,
            < 10.0 => GapType.Large,
            _ => GapType.Extreme,
        };

        // Gap with volume: current bar volume > 1.5x average
        var withVolume = indicators.VolumeRatio is not null && indicators.VolumeRatio > 1.5;

        return (gapPct, direction, classification, withVolume);
    }

    // ─────────────────────────────────────────────────────────────
    // Support / Resistance distance
    // ─────────────────────────────────────────────────────────────

    private static (double? support, double? resistance) ComputeSupportResistanceDistance(
        List<MarketSnapshotBar> bars, TechnicalIndicators indicators, List<string> skipped)
    {
        if (bars.Count == 0)
        {
            skipped.Add("SupportResistance (no bars)");
            return (null, null);
        }

        var price = bars[0].Close;

        double? distSupport = null;
        if (indicators.DonchianLow20 is not null && indicators.DonchianLow20 > 0)
            distSupport = Math.Round((price - indicators.DonchianLow20.Value) / indicators.DonchianLow20.Value * 100, 2);
        else
            skipped.Add("DistanceFromSupport (DonchianLow20 not computed)");

        double? distResistance = null;
        if (indicators.DonchianHigh20 is not null && indicators.DonchianHigh20 > 0)
            distResistance = Math.Round((price - indicators.DonchianHigh20.Value) / indicators.DonchianHigh20.Value * 100, 2);
        else
            skipped.Add("DistanceFromResistance (DonchianHigh20 not computed)");

        return (distSupport, distResistance);
    }

    // ─────────────────────────────────────────────────────────────
    // Volume ratio persistence
    // ─────────────────────────────────────────────────────────────

    private static double? ComputeVolumeRatioPersistence(List<MarketSnapshotBar> bars, List<string> skipped)
    {
        if (bars.Count < VolumePersistenceWindow + 1) // need 3 bars + enough to compute avg
        {
            skipped.Add($"VolumeRatioPersistence (need {VolumePersistenceWindow + 1} bars)");
            return null;
        }

        var avgVolume = bars.Select(b => b.Volume).Average();
        if (avgVolume <= 0) return null;

        var ratios = bars.Take(VolumePersistenceWindow).Select(b => b.Volume / avgVolume);
        return Math.Round(ratios.Average(), 2);
    }

    // ─────────────────────────────────────────────────────────────
    // Catalyst age
    // ─────────────────────────────────────────────────────────────

    private static double? ComputeCatalystAge(List<MarketSnapshotNews> news, List<string> skipped)
    {
        if (news.Count == 0)
        {
            skipped.Add("CatalystAge (no news available)");
            return null;
        }

        var mostRecent = news
            .Select(n => DateTimeOffset.TryParse(n.PublishedAt, out var dt) ? dt : (DateTimeOffset?)null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        if (mostRecent == DateTimeOffset.MinValue)
        {
            skipped.Add("CatalystAge (no parseable dates)");
            return null;
        }

        return Math.Round((DateTimeOffset.UtcNow - mostRecent).TotalHours, 1);
    }

    // ═════════════════════════════════════════════════════════════
    // Helpers — rolling indicator computation
    // ═════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute ATR14 at each position in the bar series to build a historical distribution.
    /// Returns values newest-first, up to <paramref name="maxSamples"/> entries.
    /// </summary>
    private static List<double> ComputeRollingAtr(List<MarketSnapshotBar> bars, int period, int maxSamples)
    {
        var results = new List<double>();
        int minBars = period + 1; // need period TRs, each needs current + previous bar
        int limit = Math.Min(bars.Count - minBars + 1, maxSamples);

        for (int offset = 0; offset < limit; offset++)
        {
            var trs = new List<double>();
            for (int i = offset; i < offset + period && i + 1 < bars.Count; i++)
            {
                var tr = Math.Max(bars[i].High - bars[i].Low,
                    Math.Max(Math.Abs(bars[i].High - bars[i + 1].Close),
                             Math.Abs(bars[i].Low - bars[i + 1].Close)));
                trs.Add(tr);
            }

            if (trs.Count == period)
                results.Add(trs.Average());
        }

        return results;
    }

    /// <summary>Compute ATR at a specific offset into the bar history.</summary>
    private static double? ComputeAtrAt(List<MarketSnapshotBar> bars, int period, int offset)
    {
        if (offset + period + 1 > bars.Count) return null;

        var trs = new List<double>();
        for (int i = offset; i < offset + period && i + 1 < bars.Count; i++)
        {
            var tr = Math.Max(bars[i].High - bars[i].Low,
                Math.Max(Math.Abs(bars[i].High - bars[i + 1].Close),
                         Math.Abs(bars[i].Low - bars[i + 1].Close)));
            trs.Add(tr);
        }

        return trs.Count == period ? trs.Average() : null;
    }

    /// <summary>
    /// Compute Bollinger Bandwidth at each position in the bar series.
    /// Returns values newest-first, up to <paramref name="maxSamples"/> entries.
    /// </summary>
    private static List<double> ComputeRollingBandwidth(List<MarketSnapshotBar> bars, int period, int maxSamples)
    {
        var results = new List<double>();
        int limit = Math.Min(bars.Count - period + 1, maxSamples);

        for (int offset = 0; offset < limit; offset++)
        {
            var closes = new List<double>();
            for (int i = offset; i < offset + period; i++)
                closes.Add(bars[i].Close);

            var sma = closes.Average();
            if (sma <= 0) continue;

            var stdDev = Math.Sqrt(closes.Select(c => Math.Pow(c - sma, 2)).Average());
            var upper = sma + 2 * stdDev;
            var lower = sma - 2 * stdDev;
            var bandwidth = (upper - lower) / sma * 100;
            results.Add(Math.Round(bandwidth, 2));
        }

        return results;
    }

    /// <summary>Least-squares slope of a value series.</summary>
    private static double ComputeLinearRegressionSlope(List<double> values)
    {
        int n = values.Count;
        if (n < 2) return 0;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += values[i];
            sumXY += i * values[i];
            sumX2 += i * i;
        }
        var denom = n * sumX2 - sumX * sumX;
        return denom == 0 ? 0 : (n * sumXY - sumX * sumY) / denom;
    }
}
