using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Direction-neutral scoring engine. Each bucket independently produces
/// a bullish score and a bearish score. The winning direction is whichever
/// side has the stronger evidence by a configurable margin.
/// </summary>
public static class ScoringEngine
{
    // Configurable thresholds for direction determination
    private const double MinEdgeMargin = 15;
    private const double MinScoreForDirection = 20;

    public record BucketScores(double Bullish, double Bearish);

    public record ScoringResult
    {
        public double DirectionalScore { get; init; }
        public double BullishScore { get; init; }
        public double BearishScore { get; init; }
        public string WinningDirection { get; init; } = "neutral";
        public double DirectionMargin { get; init; }
        public int Confidence { get; init; }
        public string PredictionType { get; init; } = "";
        public int Risk { get; init; }
        public ScoringBreakdown Breakdown { get; init; } = new();
        public List<string> Signals { get; init; } = [];
    }

    public static ScoringResult Score(
        MarketSnapshot snapshot,
        TechnicalIndicators indicators,
        BenchmarkContext benchmark,
        Dictionary<string, double> weights,
        List<string> lessons)
    {
        var signals = new List<string>();

        // --- Dual-score bucket scoring ---
        var trend = ScoreTrend(indicators, signals);
        var momentum = ScoreMomentum(indicators, signals);
        var volume = ScoreVolume(indicators, signals);
        var volatility = ScoreVolatilitySetup(indicators, snapshot.Quote, signals);
        var market = ScoreMarketContext(benchmark, signals);
        var catalyst = ScoreCatalyst(snapshot, weights, signals);
        var learning = ScoreLearning(weights, lessons, signals);

        // Aggregate independent scores
        var bullishScore = trend.Bullish + momentum.Bullish + volume.Bullish
            + volatility.Bullish + market.Bullish + catalyst.Bullish + learning.Bullish;
        var bearishScore = trend.Bearish + momentum.Bearish + volume.Bearish
            + volatility.Bearish + market.Bearish + catalyst.Bearish + learning.Bearish;

        bullishScore = Math.Clamp(bullishScore, 0, 100);
        bearishScore = Math.Clamp(bearishScore, 0, 100);

        // Legacy directional score for backward compatibility
        var directionalScore = bullishScore - bearishScore;

        // --- Risk penalty (applied to confidence, not to direction) ---
        var riskPenalty = ScoreRiskPenalty(indicators, benchmark, snapshot, signals);

        // --- Direction determination ---
        var margin = Math.Abs(bullishScore - bearishScore);
        var (winningDirection, predType) = DeterminePredictionType(
            bullishScore, bearishScore, snapshot, indicators);

        // --- Data quality factor ---
        var availableSignals = indicators.IndicatorsComputed.Count;
        var totalPossibleSignals = availableSignals + indicators.IndicatorsSkipped.Count;
        var dataQuality = totalPossibleSignals > 0 ? (double)availableSignals / totalPossibleSignals : 0.5;
        var dataQualityFactor = 0.75 + (0.25 * dataQuality);

        // --- Confirmation multiplier ---
        var buckets = new BucketScores[]
        {
            trend, momentum, volume, market, catalyst, learning,
        };

        int aligned = 0, conflicting = 0;
        bool winIsBullish = winningDirection == "bullish";
        foreach (var bucket in buckets)
        {
            var net = bucket.Bullish - bucket.Bearish;
            if (Math.Abs(net) < 1) continue;
            bool bucketVotesBullish = net > 0;
            if (bucketVotesBullish == winIsBullish) aligned++;
            else conflicting++;
        }

        double confirmMult = aligned switch
        {
            >= 5 => 1.30,
            4 => 1.20,
            3 => 1.10,
            _ => 1.00,
        };
        confirmMult -= conflicting * 0.10;
        confirmMult = Math.Clamp(confirmMult, 0.75, 1.30);

        // --- Risk adjustment ---
        var riskAdj = 1.0 - Math.Min(Math.Abs(riskPenalty), 30) / 100.0;

        // --- Calibration factor from learning ---
        var calFactor = weights.GetValueOrDefault("calibration_factor", 1.0);
        calFactor = Math.Clamp(calFactor, 0.85, 1.15);

        // --- Final confidence (based on winning score, not signed directional) ---
        var winningScore = Math.Max(bullishScore, bearishScore);
        var baseConf = winningScore;
        var rawConfidence = baseConf * dataQualityFactor * confirmMult * riskAdj * calFactor;

        // --- Hard caps ---
        string? capReason = null;
        if (indicators.IndicatorsComputed.Count <= 3)
        {
            rawConfidence = Math.Min(rawConfidence, 45);
            capReason = "Only one signal bucket available";
        }
        if (trend.Bullish > 5 && trend.Bearish > 5 && momentum.Bullish > 5 && momentum.Bearish > 5)
        {
            // Both trend and momentum have meaningful signals on both sides = conflict
            rawConfidence = Math.Min(rawConfidence, 60);
            capReason = "Trend and momentum conflict";
        }
        if (market.Bearish > 5 && winningDirection == "bullish")
        {
            rawConfidence = Math.Min(rawConfidence, 65);
            capReason = "Strong market context conflict";
        }
        if (market.Bullish > 5 && winningDirection == "bearish")
        {
            rawConfidence = Math.Min(rawConfidence, 65);
            capReason = "Strong market context conflict";
        }

        int confidence = (int)Math.Round(Math.Clamp(rawConfidence, 0, 95));

        // --- Risk score ---
        int risk = ComputeRisk(snapshot, indicators, benchmark, predType, riskPenalty);

        // --- Actionability ---
        var (actionScore, actionTier, actionReasons) = ComputeActionability(
            confidence, riskReward: null,
            catalystScore: catalyst.Bullish - catalyst.Bearish,
            marketContextScore: market.Bullish - market.Bearish,
            dataQualityFactor: dataQualityFactor,
            confidenceCap: capReason);

        return new ScoringResult
        {
            DirectionalScore = Math.Round(directionalScore, 2),
            BullishScore = Math.Round(bullishScore, 2),
            BearishScore = Math.Round(bearishScore, 2),
            WinningDirection = winningDirection,
            DirectionMargin = Math.Round(margin, 2),
            Confidence = confidence,
            PredictionType = predType,
            Risk = risk,
            Signals = signals,
            Breakdown = new ScoringBreakdown
            {
                DirectionalScore = Math.Round(directionalScore, 2),
                BullishScore = Math.Round(bullishScore, 2),
                BearishScore = Math.Round(bearishScore, 2),
                WinningDirection = winningDirection,
                DirectionMargin = Math.Round(margin, 2),
                Confidence = confidence,
                ActionabilityScore = actionScore,
                ActionabilityTier = actionTier,
                DataQualityFactor = Math.Round(dataQualityFactor, 3),
                ConfirmationMultiplier = Math.Round(confirmMult, 3),
                AlignedBuckets = aligned,
                ConflictingBuckets = conflicting,
                RiskAdjustment = Math.Round(riskAdj, 3),
                CalibrationFactor = Math.Round(calFactor, 3),
                TrendScore = Math.Round(trend.Bullish - trend.Bearish, 2),
                TrendBullish = Math.Round(trend.Bullish, 2),
                TrendBearish = Math.Round(trend.Bearish, 2),
                MomentumScore = Math.Round(momentum.Bullish - momentum.Bearish, 2),
                MomentumBullish = Math.Round(momentum.Bullish, 2),
                MomentumBearish = Math.Round(momentum.Bearish, 2),
                VolumeScore = Math.Round(volume.Bullish - volume.Bearish, 2),
                VolumeBullish = Math.Round(volume.Bullish, 2),
                VolumeBearish = Math.Round(volume.Bearish, 2),
                VolatilitySetupScore = Math.Round(volatility.Bullish - volatility.Bearish, 2),
                VolatilityBullish = Math.Round(volatility.Bullish, 2),
                VolatilityBearish = Math.Round(volatility.Bearish, 2),
                MarketContextScore = Math.Round(market.Bullish - market.Bearish, 2),
                MarketContextBullish = Math.Round(market.Bullish, 2),
                MarketContextBearish = Math.Round(market.Bearish, 2),
                CatalystScore = Math.Round(catalyst.Bullish - catalyst.Bearish, 2),
                CatalystBullish = Math.Round(catalyst.Bullish, 2),
                CatalystBearish = Math.Round(catalyst.Bearish, 2),
                LearningScore = Math.Round(learning.Bullish - learning.Bearish, 2),
                LearningBullish = Math.Round(learning.Bullish, 2),
                LearningBearish = Math.Round(learning.Bearish, 2),
                RiskPenalty = Math.Round(riskPenalty, 2),
                IndicatorsUsed = indicators.IndicatorsComputed,
                IndicatorsSkipped = indicators.IndicatorsSkipped,
                ConfidenceCap = capReason,
                ActionabilityReasons = actionReasons,
            },
        };
    }

    // -----------------------------------------------------------------------
    // R/R-aware finalization (unchanged interface, works with new internals)
    // -----------------------------------------------------------------------

    public static ScoringResult FinalizeWithRiskReward(ScoringResult initial, double? riskReward)
    {
        var breakdown = initial.Breakdown;
        var reasons = new List<string>(breakdown.ActionabilityReasons);
        int confidence = initial.Confidence;
        string? capReason = breakdown.ConfidenceCap;

        if (riskReward is double rr and > 0 && rr < 1.2 && confidence > 55)
        {
            confidence = 55;
            capReason = $"R/R {rr:F2} < 1.2";
            reasons.Add($"Confidence capped at 55 — risk/reward {rr:F2} below 1.2");
        }

        var catalystNet = breakdown.CatalystBullish - breakdown.CatalystBearish;
        var marketNet = breakdown.MarketContextBullish - breakdown.MarketContextBearish;

        var (actionScore, actionTier, tierReasons) = ComputeActionability(
            confidence,
            riskReward: riskReward,
            catalystScore: catalystNet,
            marketContextScore: marketNet,
            dataQualityFactor: breakdown.DataQualityFactor,
            confidenceCap: capReason);
        reasons.AddRange(tierReasons.Where(r => !reasons.Contains(r)));

        return initial with
        {
            Confidence = confidence,
            Breakdown = breakdown with
            {
                Confidence = confidence,
                ActionabilityScore = actionScore,
                ActionabilityTier = actionTier,
                ConfidenceCap = capReason,
                ActionabilityReasons = reasons,
            },
        };
    }

    // -----------------------------------------------------------------------
    // Actionability tier (unchanged)
    // -----------------------------------------------------------------------

    public static (int Score, ActionabilityTier Tier, List<string> Reasons) ComputeActionability(
        int confidence,
        double? riskReward,
        double catalystScore,
        double marketContextScore,
        double dataQualityFactor,
        string? confidenceCap)
    {
        var reasons = new List<string>();

        ActionabilityTier tier = confidence switch
        {
            < 35 => ActionabilityTier.scan,
            < 55 => ActionabilityTier.watch_only,
            < 70 => ActionabilityTier.actionable,
            < 85 => ActionabilityTier.strong,
            _ => ActionabilityTier.strongest,
        };
        reasons.Add($"Base tier from confidence {confidence}: {tier}");

        if (riskReward is double rr and > 0)
        {
            if (rr < 1.5 && (tier == ActionabilityTier.strong || tier == ActionabilityTier.strongest))
            {
                if (Math.Abs(catalystScore) < 20)
                {
                    tier = ActionabilityTier.actionable;
                    reasons.Add($"Downgraded to actionable — R/R {rr:F2} < 1.5 and catalyst {catalystScore:F0} < 20");
                }
                else
                {
                    reasons.Add($"Strong tier held despite R/R {rr:F2} < 1.5 — catalyst {catalystScore:F0} very strong");
                }
            }
        }

        if (marketContextScore < -8 && tier >= ActionabilityTier.actionable)
        {
            tier = ActionabilityTier.watch_only;
            reasons.Add($"Downgraded to watch_only — market context {marketContextScore:F0} strongly conflicts");
        }

        if (dataQualityFactor < 0.85 && (tier == ActionabilityTier.strong || tier == ActionabilityTier.strongest))
        {
            tier = ActionabilityTier.actionable;
            reasons.Add($"Downgraded to actionable — data quality factor {dataQualityFactor:F2} below 0.85");
        }

        if (!string.IsNullOrEmpty(confidenceCap) && tier == ActionabilityTier.strongest)
        {
            tier = ActionabilityTier.strong;
            reasons.Add($"Downgraded to strong — confidence was capped ({confidenceCap})");
        }

        return (confidence, tier, reasons);
    }

    // -----------------------------------------------------------------------
    // Trend bucket: bullish 0..25, bearish 0..25
    // -----------------------------------------------------------------------

    private static BucketScores ScoreTrend(TechnicalIndicators ind, List<string> signals)
    {
        double bull = 0, bear = 0;

        if (ind.Sma5 is not null && ind.Sma20 is not null)
        {
            if (ind.Sma5AboveSma20) { bull += 8; signals.Add("Trend: SMA5 above SMA20"); }
            else { bear += 8; signals.Add("Trend: SMA5 below SMA20"); }
        }

        if (ind.Sma20 is not null)
        {
            if (ind.CloseAboveSma20) { bull += 6; signals.Add("Trend: close above SMA20"); }
            else { bear += 6; signals.Add("Trend: close below SMA20"); }
        }

        if (ind.LinearRegressionSlope is double slope)
        {
            if (slope > 0.5) { bull += 6; signals.Add($"Trend: strong upslope ({slope:F2})"); }
            else if (slope > 0.1) { bull += 3; signals.Add($"Trend: mild upslope ({slope:F2})"); }
            else if (slope < -0.5) { bear += 6; signals.Add($"Trend: strong downslope ({slope:F2})"); }
            else if (slope < -0.1) { bear += 3; signals.Add($"Trend: mild downslope ({slope:F2})"); }
        }

        if (ind.DonchianBreakout == true) { bull += 5; signals.Add("Trend: Donchian 20 breakout"); }
        else if (ind.DonchianBreakdown == true) { bear += 5; signals.Add("Trend: Donchian 20 breakdown"); }

        return new BucketScores(Math.Clamp(bull, 0, 25), Math.Clamp(bear, 0, 25));
    }

    // -----------------------------------------------------------------------
    // Momentum bucket: bullish 0..20, bearish 0..20
    // -----------------------------------------------------------------------

    private static BucketScores ScoreMomentum(TechnicalIndicators ind, List<string> signals)
    {
        double bull = 0, bear = 0;

        if (ind.Roc5 is double roc5)
        {
            if (roc5 > 2) { bull += 5; signals.Add($"Momentum: ROC5 strong up ({roc5:F1}%)"); }
            else if (roc5 > 0.5) { bull += 2; signals.Add($"Momentum: ROC5 up ({roc5:F1}%)"); }
            else if (roc5 < -2) { bear += 5; signals.Add($"Momentum: ROC5 strong down ({roc5:F1}%)"); }
            else if (roc5 < -0.5) { bear += 2; signals.Add($"Momentum: ROC5 down ({roc5:F1}%)"); }
        }

        if (ind.Roc10 is double roc10)
        {
            if (roc10 > 3) { bull += 4; signals.Add($"Momentum: ROC10 strong up ({roc10:F1}%)"); }
            else if (roc10 > 1) { bull += 2; signals.Add($"Momentum: ROC10 up ({roc10:F1}%)"); }
            else if (roc10 < -3) { bear += 4; signals.Add($"Momentum: ROC10 strong down ({roc10:F1}%)"); }
            else if (roc10 < -1) { bear += 2; signals.Add($"Momentum: ROC10 down ({roc10:F1}%)"); }
        }

        if (ind.Rsi14 is double rsi)
        {
            if (rsi > 70) { bear += 3; signals.Add($"Momentum: RSI overbought ({rsi:F0})"); }
            else if (rsi > 55) { bull += 4; signals.Add($"Momentum: RSI bullish ({rsi:F0})"); }
            else if (rsi < 30) { bull += 3; signals.Add($"Momentum: RSI oversold ({rsi:F0})"); }
            else if (rsi < 45) { bear += 4; signals.Add($"Momentum: RSI bearish ({rsi:F0})"); }
        }

        if (ind.StochasticCloseLocation is double stoch)
        {
            if (stoch > 80) { bull += 3; signals.Add($"Momentum: close near highs ({stoch:F0}%)"); }
            else if (stoch < 20) { bear += 3; signals.Add($"Momentum: close near lows ({stoch:F0}%)"); }
        }

        return new BucketScores(Math.Clamp(bull, 0, 20), Math.Clamp(bear, 0, 20));
    }

    // -----------------------------------------------------------------------
    // Volume bucket: bullish 0..15, bearish 0..15
    // -----------------------------------------------------------------------

    private static BucketScores ScoreVolume(TechnicalIndicators ind, List<string> signals)
    {
        double bull = 0, bear = 0;

        if (ind.VolumeRatio is double vr)
        {
            // High volume is directionally ambiguous on its own — it confirms
            // whichever direction price is moving. We add to both sides a small
            // amount, but OBV and price-volume confirmation resolve the direction.
            if (vr > 2.0) { bull += 3; bear += 3; signals.Add($"Volume: very elevated ({vr:F1}x avg)"); }
            else if (vr > 1.3) { bull += 2; bear += 2; signals.Add($"Volume: above average ({vr:F1}x avg)"); }
            else if (vr < 0.5) { signals.Add($"Volume: very low ({vr:F1}x avg)"); }
            else if (vr < 0.7) { signals.Add($"Volume: below average ({vr:F1}x avg)"); }
        }

        if (ind.ObvSlope is double obvS)
        {
            if (obvS > 0) { bull += 5; signals.Add("Volume: OBV trending up (accumulation)"); }
            else if (obvS < 0) { bear += 5; signals.Add("Volume: OBV trending down (distribution)"); }
        }

        if (ind.PriceVolumeConfirmation is bool pvc)
        {
            if (pvc) { bull += 4; signals.Add("Volume: price-volume confirmed"); }
            else { bear += 4; signals.Add("Volume: price-volume divergence"); }
        }

        return new BucketScores(Math.Clamp(bull, 0, 15), Math.Clamp(bear, 0, 15));
    }

    // -----------------------------------------------------------------------
    // Volatility setup bucket: bullish 0..10, bearish 0..10
    // -----------------------------------------------------------------------

    private static BucketScores ScoreVolatilitySetup(TechnicalIndicators ind, MarketSnapshotQuote? quote, List<string> signals)
    {
        double bull = 0, bear = 0;

        if (ind.BollingerBreakout == true && quote is not null)
        {
            if (quote.Price > (ind.BollingerUpper ?? 0))
            { bull += 5; signals.Add("Volatility: Bollinger upper breakout"); }
            else
            { bear += 5; signals.Add("Volatility: Bollinger lower breakdown"); }
        }

        if (ind.BollingerBandwidth is double bw)
        {
            // Squeeze is a setup signal — contributes to both sides mildly
            if (bw < 3) { bull += 2; bear += 2; signals.Add($"Volatility: Bollinger squeeze ({bw:F1}%)"); }
            else if (bw > 10) { signals.Add($"Volatility: bands very wide ({bw:F1}%)"); }
        }

        return new BucketScores(Math.Clamp(bull, 0, 10), Math.Clamp(bear, 0, 10));
    }

    // -----------------------------------------------------------------------
    // Market context bucket: bullish 0..15, bearish 0..15
    // -----------------------------------------------------------------------

    private static BucketScores ScoreMarketContext(BenchmarkContext ctx, List<string> signals)
    {
        double bull = 0, bear = 0;

        if (ctx.RelativeStrengthVsSpy is double relSpy)
        {
            if (relSpy > 1.5) { bull += 6; signals.Add($"Market: outperforming SPY (+{relSpy:F1}%)"); }
            else if (relSpy > 0.5) { bull += 3; signals.Add($"Market: beating SPY (+{relSpy:F1}%)"); }
            else if (relSpy < -1.5) { bear += 6; signals.Add($"Market: lagging SPY ({relSpy:F1}%)"); }
            else if (relSpy < -0.5) { bear += 3; signals.Add($"Market: trailing SPY ({relSpy:F1}%)"); }
        }

        if (ctx.RelativeStrengthVsQqq is double relQqq)
        {
            if (relQqq > 1.5) { bull += 4; signals.Add($"Market: outperforming QQQ (+{relQqq:F1}%)"); }
            else if (relQqq < -1.5) { bear += 4; signals.Add($"Market: lagging QQQ ({relQqq:F1}%)"); }
        }

        if (ctx.SpyTrend is not null)
        {
            if (ctx.SpyTrend == "bullish") { bull += 3; signals.Add("Market: SPY trend bullish"); }
            else if (ctx.SpyTrend == "bearish") { bear += 3; signals.Add("Market: SPY trend bearish"); }
        }
        if (ctx.QqqTrend is not null)
        {
            if (ctx.QqqTrend == "bullish") { bull += 2; signals.Add("Market: QQQ trend bullish"); }
            else if (ctx.QqqTrend == "bearish") { bear += 2; signals.Add("Market: QQQ trend bearish"); }
        }

        return new BucketScores(Math.Clamp(bull, 0, 15), Math.Clamp(bear, 0, 15));
    }

    // -----------------------------------------------------------------------
    // Catalyst bucket: bullish 0..25, bearish 0..25
    // Unknown/null sentiment contributes to NEITHER side.
    // -----------------------------------------------------------------------

    private static BucketScores ScoreCatalyst(MarketSnapshot snapshot, Dictionary<string, double> weights, List<string> signals)
    {
        var news = snapshot.NewsContext;
        if (news.Count == 0)
        {
            signals.Add("Catalyst: no recent news");
            return new BucketScores(0, 0);
        }

        double bull = 0, bear = 0;

        var sources = news.Select(n => n.SourceName).Distinct().Count();
        if (sources >= 3) { bull += 3; bear += 3; signals.Add($"Catalyst: {sources} sources confirming"); }
        else if (sources >= 2) { bull += 1; bear += 1; signals.Add($"Catalyst: {sources} sources"); }

        foreach (var item in news)
        {
            var catKey = item.CatalystType is not null ? $"catalyst_{item.CatalystType}" : null;
            var catW = catKey is not null ? weights.GetValueOrDefault(catKey, 1.0) : 1.0;
            var impactScore = item.ImportanceScore * catW * 3;

            if (item.Sentiment == "bullish")
            {
                bull += impactScore;
            }
            else if (item.Sentiment == "bearish")
            {
                bear += impactScore;
            }
            // null/unknown/neutral → no directional contribution

            var preview = item.Title.Length > 50 ? item.Title[..50] : item.Title;
            signals.Add($"Catalyst: \"{preview}\" ({item.Sentiment ?? "neutral"}, imp={item.ImportanceScore:F0})");
        }

        return new BucketScores(Math.Clamp(bull, 0, 25), Math.Clamp(bear, 0, 25));
    }

    // -----------------------------------------------------------------------
    // Learning bucket: bullish 0..15, bearish 0..15
    // -----------------------------------------------------------------------

    private static BucketScores ScoreLearning(Dictionary<string, double> weights, List<string> lessons, List<string> signals)
    {
        double bull = 0, bear = 0;

        // Direction-specific weights (e.g., technical_trend_bullish, technical_trend_bearish)
        var bullishWeights = weights
            .Where(w => w.Key.EndsWith("_bullish") && Math.Abs(w.Value - 1.0) > 0.15)
            .ToList();
        var bearishWeights = weights
            .Where(w => w.Key.EndsWith("_bearish") && Math.Abs(w.Value - 1.0) > 0.15)
            .ToList();

        if (bullishWeights.Count > 0)
        {
            var avgWeight = bullishWeights.Average(w => w.Value);
            bull = Math.Clamp((avgWeight - 1.0) * 30, 0, 15);
            signals.Add($"Learning: {bullishWeights.Count} bullish weights adjusted (avg {avgWeight:F2}x)");
        }
        if (bearishWeights.Count > 0)
        {
            var avgWeight = bearishWeights.Average(w => w.Value);
            bear = Math.Clamp((avgWeight - 1.0) * 30, 0, 15);
            signals.Add($"Learning: {bearishWeights.Count} bearish weights adjusted (avg {avgWeight:F2}x)");
        }

        // Fallback: generic (non-directional) weights contribute equally
        var genericWeights = weights
            .Where(w => w.Key != "calibration_factor"
                && !w.Key.EndsWith("_bullish") && !w.Key.EndsWith("_bearish")
                && Math.Abs(w.Value - 1.0) > 0.15)
            .ToList();
        if (genericWeights.Count > 0)
        {
            var avgWeight = genericWeights.Average(w => w.Value);
            var contribution = Math.Clamp((avgWeight - 1.0) * 15, -7, 7);
            if (contribution > 0) { bull += contribution; }
            else { bear += Math.Abs(contribution); }
            signals.Add($"Learning: {genericWeights.Count} generic weights (avg {avgWeight:F2}x)");
        }

        if (lessons.Count > 0)
            signals.Add($"Learning: {lessons.Count} prior lessons considered");

        return new BucketScores(Math.Clamp(bull, 0, 15), Math.Clamp(bear, 0, 15));
    }

    // -----------------------------------------------------------------------
    // Risk penalty: 0 to -30 (direction-independent)
    // -----------------------------------------------------------------------

    private static double ScoreRiskPenalty(
        TechnicalIndicators ind, BenchmarkContext ctx, MarketSnapshot snapshot, List<string> signals)
    {
        double penalty = 0;

        if (ind.Atr14 is double atr && snapshot.Quote is not null && snapshot.Quote.Price > 0)
        {
            var atrPct = (atr / snapshot.Quote.Price) * 100;
            if (atrPct > 8) { penalty -= 10; signals.Add($"Risk: ATR very high ({atrPct:F1}%)"); }
            else if (atrPct > 5) { penalty -= 5; signals.Add($"Risk: ATR elevated ({atrPct:F1}%)"); }
        }

        if (ctx.SpyTrend == "bearish" && ctx.QqqTrend == "bearish")
        { penalty -= 8; signals.Add("Risk: both SPY and QQQ bearish"); }

        if (!snapshot.DataAvailability.MarketDataAvailable)
        { penalty -= 15; signals.Add("Risk: no market data available"); }

        if (ind.BarsAvailable < 10)
        { penalty -= 5; signals.Add($"Risk: only {ind.BarsAvailable} bars available"); }

        if (ind.Rsi14 is double rsi && (rsi > 80 || rsi < 20))
        { penalty -= 5; signals.Add($"Risk: RSI at extreme ({rsi:F0})"); }

        return Math.Clamp(penalty, -30, 0);
    }

    // -----------------------------------------------------------------------
    // Direction-neutral prediction type determination
    // -----------------------------------------------------------------------

    private static (string WinningDirection, string PredictionType) DeterminePredictionType(
        double bullishScore, double bearishScore, MarketSnapshot snapshot, TechnicalIndicators ind)
    {
        if (!snapshot.DataAvailability.MarketDataAvailable && !snapshot.DataAvailability.NewsAvailable)
            return ("neutral", "unavailable");

        var margin = bullishScore - bearishScore;

        if (bullishScore >= MinScoreForDirection && margin >= MinEdgeMargin)
            return ("bullish", "bullish");

        if (bearishScore >= MinScoreForDirection && -margin >= MinEdgeMargin)
            return ("bearish", "bearish");

        if (Math.Max(bullishScore, bearishScore) < 8)
            return ("neutral", "watch_only");

        if (ind.BollingerBandwidth is double bw && bw > 8)
            return ("neutral", "neutral_high_volatility");

        var hasTrendSignal = ind.Sma5 is not null && ind.Sma20 is not null;
        if (hasTrendSignal && Math.Abs(margin) < 5)
            return ("neutral", "neutral_range_bound");

        return ("neutral", "neutral_no_edge");
    }

    // -----------------------------------------------------------------------
    // Risk score (unchanged logic, adapted for dual scores)
    // -----------------------------------------------------------------------

    private static int ComputeRisk(
        MarketSnapshot snapshot, TechnicalIndicators ind,
        BenchmarkContext ctx, string predType, double riskPenalty)
    {
        int risk = 40;

        if (ind.Atr14 is double atr && snapshot.Quote is not null && snapshot.Quote.Price > 0)
        {
            var atrPct = (atr / snapshot.Quote.Price) * 100;
            if (atrPct > 5) risk += 15;
            else if (atrPct > 3) risk += 5;
        }

        if (predType == "bullish" && !ind.Sma5AboveSma20) risk += 10;
        if (predType == "bearish" && ind.Sma5AboveSma20) risk += 10;

        if (ctx.SpyTrend == "bearish" && predType == "bullish") risk += 10;
        if (ctx.SpyTrend == "bullish" && predType == "bearish") risk += 10;

        if (!snapshot.DataAvailability.MarketDataAvailable) risk += 15;
        if (!snapshot.DataAvailability.NewsAvailable) risk += 5;

        risk += (int)Math.Abs(riskPenalty / 2);

        return Math.Clamp(risk, 0, 100);
    }
}
