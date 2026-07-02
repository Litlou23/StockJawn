using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Weighted-bucket scoring engine with confirmation multiplier.
/// Each bucket scores independently; confirmation boosts when buckets agree.
/// </summary>
public static class ScoringEngine
{
    public record ScoringResult
    {
        public double DirectionalScore { get; init; }
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

        // --- Bucket scoring ---
        var trend = ScoreTrend(indicators, signals);
        var momentum = ScoreMomentum(indicators, signals);
        var volume = ScoreVolume(indicators, signals);
        var volatility = ScoreVolatilitySetup(indicators, snapshot.Quote, signals);
        var market = ScoreMarketContext(benchmark, signals);
        var catalyst = ScoreCatalyst(snapshot, weights, signals);
        var learning = ScoreLearning(weights, lessons, signals);
        var riskPenalty = ScoreRiskPenalty(indicators, benchmark, snapshot, signals);

        var directionalScore = trend + momentum + volume + volatility + market + catalyst + learning + riskPenalty;

        // --- Prediction type ---
        var predType = DeterminePredictionType(directionalScore, snapshot, indicators);

        // --- Data quality factor ---
        var availableSignals = indicators.IndicatorsComputed.Count;
        var totalPossibleSignals = availableSignals + indicators.IndicatorsSkipped.Count;
        var dataQuality = totalPossibleSignals > 0 ? (double)availableSignals / totalPossibleSignals : 0.5;
        var dataQualityFactor = 0.75 + (0.25 * dataQuality);

        // --- Confirmation multiplier ---
        var buckets = new Dictionary<string, double>
        {
            ["trend"] = trend,
            ["momentum"] = momentum,
            ["volume"] = volume,
            ["marketContext"] = market,
            ["catalyst"] = catalyst,
            ["learningEdge"] = learning,
        };

        bool isBullish = directionalScore > 0;
        int aligned = 0, conflicting = 0;
        foreach (var (_, score) in buckets)
        {
            if (Math.Abs(score) < 1) continue;
            if ((isBullish && score > 0) || (!isBullish && score < 0)) aligned++;
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

        // --- Final confidence ---
        var baseConf = Math.Abs(directionalScore);
        var rawConfidence = baseConf * dataQualityFactor * confirmMult * riskAdj * calFactor;

        // --- Hard caps ---
        string? capReason = null;
        if (indicators.IndicatorsComputed.Count <= 3)
        {
            rawConfidence = Math.Min(rawConfidence, 45);
            capReason = "Only one signal bucket available";
        }
        if (trend * momentum < 0 && Math.Abs(trend) > 5 && Math.Abs(momentum) > 5)
        {
            rawConfidence = Math.Min(rawConfidence, 60);
            capReason = "Trend and momentum conflict";
        }
        if (market < -5)
        {
            rawConfidence = Math.Min(rawConfidence, 65);
            capReason = "Strong market context conflict";
        }

        int confidence = (int)Math.Round(Math.Clamp(rawConfidence, 0, 95));

        // --- Risk score ---
        int risk = ComputeRisk(snapshot, indicators, benchmark, predType, riskPenalty);

        // --- Actionability (initial pass — no R/R yet, may downgrade later
        //     via FinalizeWithRiskReward once price targets are computed).
        var (actionScore, actionTier, actionReasons) = ComputeActionability(
            confidence, riskReward: null, catalystScore: catalyst,
            marketContextScore: market, dataQualityFactor: dataQualityFactor,
            confidenceCap: capReason);

        return new ScoringResult
        {
            DirectionalScore = Math.Round(directionalScore, 2),
            Confidence = confidence,
            PredictionType = predType,
            Risk = risk,
            Signals = signals,
            Breakdown = new ScoringBreakdown
            {
                DirectionalScore = Math.Round(directionalScore, 2),
                Confidence = confidence,
                ActionabilityScore = actionScore,
                ActionabilityTier = actionTier,
                DataQualityFactor = Math.Round(dataQualityFactor, 3),
                ConfirmationMultiplier = Math.Round(confirmMult, 3),
                AlignedBuckets = aligned,
                ConflictingBuckets = conflicting,
                RiskAdjustment = Math.Round(riskAdj, 3),
                CalibrationFactor = Math.Round(calFactor, 3),
                TrendScore = Math.Round(trend, 2),
                MomentumScore = Math.Round(momentum, 2),
                VolumeScore = Math.Round(volume, 2),
                VolatilitySetupScore = Math.Round(volatility, 2),
                MarketContextScore = Math.Round(market, 2),
                CatalystScore = Math.Round(catalyst, 2),
                LearningScore = Math.Round(learning, 2),
                RiskPenalty = Math.Round(riskPenalty, 2),
                IndicatorsUsed = indicators.IndicatorsComputed,
                IndicatorsSkipped = indicators.IndicatorsSkipped,
                ConfidenceCap = capReason,
                ActionabilityReasons = actionReasons,
            },
        };
    }

    // -----------------------------------------------------------------------
    // Second pass: R/R-aware finalization. Called by PredictionGenerator
    // once the price predictor has computed target/stop/RR. Applies:
    //   • Hard cap: R/R < 1.2 → confidence ≤ 55
    //   • Tier gate: R/R < 1.5 blocks "strong"+ unless catalyst ≥ 20
    // Returns a fresh ScoringResult so the caller can persist the final
    // confidence and tier — the pre-R/R values remain in the breakdown as
    // a paper trail via ActionabilityReasons.
    // -----------------------------------------------------------------------

    public static ScoringResult FinalizeWithRiskReward(ScoringResult initial, double? riskReward)
    {
        var breakdown = initial.Breakdown;
        var reasons = new List<string>(breakdown.ActionabilityReasons);
        int confidence = initial.Confidence;
        string? capReason = breakdown.ConfidenceCap;

        // Hard cap for very poor R/R
        if (riskReward is double rr and > 0 && rr < 1.2 && confidence > 55)
        {
            confidence = 55;
            capReason = $"R/R {rr:F2} < 1.2";
            reasons.Add($"Confidence capped at 55 — risk/reward {rr:F2} below 1.2");
        }

        var (actionScore, actionTier, tierReasons) = ComputeActionability(
            confidence,
            riskReward: riskReward,
            catalystScore: breakdown.CatalystScore,
            marketContextScore: breakdown.MarketContextScore,
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
    // Actionability tier computation. Confidence drives the base tier;
    // guardrails (R/R, market context, data quality) can only downgrade —
    // never upgrade. A prediction can be high-confidence but still
    // watch_only when the setup mechanics are wrong.
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

        // Base tier from confidence bands
        ActionabilityTier tier = confidence switch
        {
            < 35 => ActionabilityTier.scan,
            < 55 => ActionabilityTier.watch_only,
            < 70 => ActionabilityTier.actionable,
            < 85 => ActionabilityTier.strong,
            _ => ActionabilityTier.strongest,
        };
        reasons.Add($"Base tier from confidence {confidence}: {tier}");

        // Guardrail: R/R < 1.5 blocks "strong"+ unless catalyst is very strong.
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

        // Guardrail: strong market context conflict shouldn't be actionable+
        if (marketContextScore < -8 && tier >= ActionabilityTier.actionable)
        {
            tier = ActionabilityTier.watch_only;
            reasons.Add($"Downgraded to watch_only — market context {marketContextScore:F0} strongly conflicts");
        }

        // Guardrail: low data quality can never be strong+
        if (dataQualityFactor < 0.85 && (tier == ActionabilityTier.strong || tier == ActionabilityTier.strongest))
        {
            tier = ActionabilityTier.actionable;
            reasons.Add($"Downgraded to actionable — data quality factor {dataQualityFactor:F2} below 0.85");
        }

        // Guardrail: any confidence cap knocks strongest → strong
        if (!string.IsNullOrEmpty(confidenceCap) && tier == ActionabilityTier.strongest)
        {
            tier = ActionabilityTier.strong;
            reasons.Add($"Downgraded to strong — confidence was capped ({confidenceCap})");
        }

        return (confidence, tier, reasons);
    }

    // -----------------------------------------------------------------------
    // Trend bucket: -25 to +25
    // -----------------------------------------------------------------------

    private static double ScoreTrend(TechnicalIndicators ind, List<string> signals)
    {
        double score = 0;

        // SMA5 vs SMA20 (±8)
        if (ind.Sma5 is not null && ind.Sma20 is not null)
        {
            if (ind.Sma5AboveSma20) { score += 8; signals.Add("Trend: SMA5 above SMA20"); }
            else { score -= 8; signals.Add("Trend: SMA5 below SMA20"); }
        }

        // Close vs SMA20 (±6)
        if (ind.Sma20 is not null)
        {
            if (ind.CloseAboveSma20) { score += 6; signals.Add("Trend: close above SMA20"); }
            else { score -= 6; signals.Add("Trend: close below SMA20"); }
        }

        // Linear regression slope (±6)
        if (ind.LinearRegressionSlope is double slope)
        {
            if (slope > 0.5) { score += 6; signals.Add($"Trend: strong upslope ({slope:F2})"); }
            else if (slope > 0.1) { score += 3; signals.Add($"Trend: mild upslope ({slope:F2})"); }
            else if (slope < -0.5) { score -= 6; signals.Add($"Trend: strong downslope ({slope:F2})"); }
            else if (slope < -0.1) { score -= 3; signals.Add($"Trend: mild downslope ({slope:F2})"); }
        }

        // Donchian breakout/breakdown (±5)
        if (ind.DonchianBreakout == true) { score += 5; signals.Add("Trend: Donchian 20 breakout"); }
        else if (ind.DonchianBreakdown == true) { score -= 5; signals.Add("Trend: Donchian 20 breakdown"); }

        return Math.Clamp(score, -25, 25);
    }

    // -----------------------------------------------------------------------
    // Momentum bucket: -20 to +20
    // -----------------------------------------------------------------------

    private static double ScoreMomentum(TechnicalIndicators ind, List<string> signals)
    {
        double score = 0;

        // ROC5 (±5)
        if (ind.Roc5 is double roc5)
        {
            if (roc5 > 2) { score += 5; signals.Add($"Momentum: ROC5 strong up ({roc5:F1}%)"); }
            else if (roc5 > 0.5) { score += 2; signals.Add($"Momentum: ROC5 up ({roc5:F1}%)"); }
            else if (roc5 < -2) { score -= 5; signals.Add($"Momentum: ROC5 strong down ({roc5:F1}%)"); }
            else if (roc5 < -0.5) { score -= 2; signals.Add($"Momentum: ROC5 down ({roc5:F1}%)"); }
        }

        // ROC10 (±4)
        if (ind.Roc10 is double roc10)
        {
            if (roc10 > 3) { score += 4; signals.Add($"Momentum: ROC10 strong up ({roc10:F1}%)"); }
            else if (roc10 > 1) { score += 2; signals.Add($"Momentum: ROC10 up ({roc10:F1}%)"); }
            else if (roc10 < -3) { score -= 4; signals.Add($"Momentum: ROC10 strong down ({roc10:F1}%)"); }
            else if (roc10 < -1) { score -= 2; signals.Add($"Momentum: ROC10 down ({roc10:F1}%)"); }
        }

        // RSI14 (±6)
        if (ind.Rsi14 is double rsi)
        {
            if (rsi > 70) { score -= 3; signals.Add($"Momentum: RSI overbought ({rsi:F0})"); }
            else if (rsi > 55) { score += 4; signals.Add($"Momentum: RSI bullish ({rsi:F0})"); }
            else if (rsi < 30) { score += 3; signals.Add($"Momentum: RSI oversold ({rsi:F0})"); }
            else if (rsi < 45) { score -= 4; signals.Add($"Momentum: RSI bearish ({rsi:F0})"); }
        }

        // Stochastic / close location (±5)
        if (ind.StochasticCloseLocation is double stoch)
        {
            if (stoch > 80) { score += 3; signals.Add($"Momentum: close near highs ({stoch:F0}%)"); }
            else if (stoch < 20) { score -= 3; signals.Add($"Momentum: close near lows ({stoch:F0}%)"); }
        }

        return Math.Clamp(score, -20, 20);
    }

    // -----------------------------------------------------------------------
    // Volume bucket: -15 to +15
    // -----------------------------------------------------------------------

    private static double ScoreVolume(TechnicalIndicators ind, List<string> signals)
    {
        double score = 0;

        // Volume ratio (±6)
        if (ind.VolumeRatio is double vr)
        {
            if (vr > 2.0) { score += 6; signals.Add($"Volume: very elevated ({vr:F1}x avg)"); }
            else if (vr > 1.3) { score += 3; signals.Add($"Volume: above average ({vr:F1}x avg)"); }
            else if (vr < 0.5) { score -= 4; signals.Add($"Volume: very low ({vr:F1}x avg)"); }
            else if (vr < 0.7) { score -= 2; signals.Add($"Volume: below average ({vr:F1}x avg)"); }
        }

        // OBV slope (±5)
        if (ind.ObvSlope is double obvS)
        {
            if (obvS > 0) { score += 5; signals.Add("Volume: OBV trending up"); }
            else if (obvS < 0) { score -= 5; signals.Add("Volume: OBV trending down"); }
        }

        // Price-volume confirmation (±4)
        if (ind.PriceVolumeConfirmation is bool pvc)
        {
            if (pvc) { score += 4; signals.Add("Volume: price-volume confirmed"); }
            else { score -= 4; signals.Add("Volume: price-volume divergence"); }
        }

        return Math.Clamp(score, -15, 15);
    }

    // -----------------------------------------------------------------------
    // Volatility setup bucket: -10 to +10
    // -----------------------------------------------------------------------

    private static double ScoreVolatilitySetup(TechnicalIndicators ind, MarketSnapshotQuote? quote, List<string> signals)
    {
        double score = 0;

        // Bollinger breakout (±5)
        if (ind.BollingerBreakout == true && quote is not null)
        {
            if (quote.Price > (ind.BollingerUpper ?? 0))
            { score += 5; signals.Add("Volatility: Bollinger upper breakout"); }
            else
            { score -= 5; signals.Add("Volatility: Bollinger lower breakdown"); }
        }

        // Bollinger squeeze (setup signal, ±3)
        if (ind.BollingerBandwidth is double bw)
        {
            if (bw < 3) { score += 3; signals.Add($"Volatility: Bollinger squeeze ({bw:F1}%)"); }
            else if (bw > 10) { score -= 3; signals.Add($"Volatility: bands very wide ({bw:F1}%)"); }
        }

        // ATR risk warning (±2)
        if (ind.Atr14 is double atr && quote is not null && quote.Price > 0)
        {
            var atrPct = (atr / quote.Price) * 100;
            if (atrPct > 8) { score -= 2; signals.Add($"Volatility: ATR very high ({atrPct:F1}%)"); }
        }

        return Math.Clamp(score, -10, 10);
    }

    // -----------------------------------------------------------------------
    // Market context bucket: -15 to +15
    // -----------------------------------------------------------------------

    private static double ScoreMarketContext(BenchmarkContext ctx, List<string> signals)
    {
        double score = 0;

        // Relative strength vs SPY (±6)
        if (ctx.RelativeStrengthVsSpy is double relSpy)
        {
            if (relSpy > 1.5) { score += 6; signals.Add($"Market: outperforming SPY (+{relSpy:F1}%)"); }
            else if (relSpy > 0.5) { score += 3; signals.Add($"Market: beating SPY (+{relSpy:F1}%)"); }
            else if (relSpy < -1.5) { score -= 6; signals.Add($"Market: lagging SPY ({relSpy:F1}%)"); }
            else if (relSpy < -0.5) { score -= 3; signals.Add($"Market: trailing SPY ({relSpy:F1}%)"); }
        }

        // Relative strength vs QQQ (±4)
        if (ctx.RelativeStrengthVsQqq is double relQqq)
        {
            if (relQqq > 1.5) { score += 4; signals.Add($"Market: outperforming QQQ (+{relQqq:F1}%)"); }
            else if (relQqq < -1.5) { score -= 4; signals.Add($"Market: lagging QQQ ({relQqq:F1}%)"); }
        }

        // Benchmark trend agreement (±5)
        if (ctx.SpyTrend is not null)
        {
            if (ctx.SpyTrend == "bullish") { score += 3; signals.Add("Market: SPY trend bullish"); }
            else if (ctx.SpyTrend == "bearish") { score -= 3; signals.Add("Market: SPY trend bearish"); }
        }
        if (ctx.QqqTrend is not null)
        {
            if (ctx.QqqTrend == "bullish") { score += 2; signals.Add("Market: QQQ trend bullish"); }
            else if (ctx.QqqTrend == "bearish") { score -= 2; signals.Add("Market: QQQ trend bearish"); }
        }

        return Math.Clamp(score, -15, 15);
    }

    // -----------------------------------------------------------------------
    // Catalyst bucket: -25 to +25
    // -----------------------------------------------------------------------

    private static double ScoreCatalyst(MarketSnapshot snapshot, Dictionary<string, double> weights, List<string> signals)
    {
        var news = snapshot.NewsContext;
        if (news.Count == 0)
        {
            signals.Add("Catalyst: no recent news");
            return 0;
        }

        double score = 0;

        // Multiple source confirmation
        var sources = news.Select(n => n.SourceName).Distinct().Count();
        if (sources >= 3) { score += 5; signals.Add($"Catalyst: {sources} sources confirming"); }
        else if (sources >= 2) { score += 2; signals.Add($"Catalyst: {sources} sources"); }

        foreach (var item in news)
        {
            var catKey = item.CatalystType is not null ? $"catalyst_{item.CatalystType}" : null;
            var catW = catKey is not null ? weights.GetValueOrDefault(catKey, 1.0) : 1.0;

            var impactScore = item.ImportanceScore * catW * 3;
            var sentimentSign = item.Sentiment == "bearish" ? -1 : 1;
            score += impactScore * sentimentSign;

            var preview = item.Title.Length > 50 ? item.Title[..50] : item.Title;
            signals.Add($"Catalyst: \"{preview}\" ({item.Sentiment ?? "neutral"}, imp={item.ImportanceScore:F0})");
        }

        return Math.Clamp(score, -25, 25);
    }

    // -----------------------------------------------------------------------
    // Learning bucket: -15 to +15
    // -----------------------------------------------------------------------

    private static double ScoreLearning(Dictionary<string, double> weights, List<string> lessons, List<string> signals)
    {
        double score = 0;

        var adjustedWeights = weights.Where(w => w.Key != "calibration_factor" && Math.Abs(w.Value - 1.0) > 0.15).ToList();
        if (adjustedWeights.Count > 0)
        {
            var avgWeight = adjustedWeights.Average(w => w.Value);
            score = Math.Clamp((avgWeight - 1.0) * 30, -15, 15);
            signals.Add($"Learning: {adjustedWeights.Count} adjusted weights (avg {avgWeight:F2}x)");
        }

        if (lessons.Count > 0)
            signals.Add($"Learning: {lessons.Count} prior lessons considered");

        return Math.Clamp(score, -15, 15);
    }

    // -----------------------------------------------------------------------
    // Risk penalty: 0 to -30
    // -----------------------------------------------------------------------

    private static double ScoreRiskPenalty(
        TechnicalIndicators ind, BenchmarkContext ctx, MarketSnapshot snapshot, List<string> signals)
    {
        double penalty = 0;

        // High ATR risk
        if (ind.Atr14 is double atr && snapshot.Quote is not null && snapshot.Quote.Price > 0)
        {
            var atrPct = (atr / snapshot.Quote.Price) * 100;
            if (atrPct > 8) { penalty -= 10; signals.Add($"Risk: ATR very high ({atrPct:F1}%)"); }
            else if (atrPct > 5) { penalty -= 5; signals.Add($"Risk: ATR elevated ({atrPct:F1}%)"); }
        }

        // Conflicting market context
        if (ctx.SpyTrend == "bearish" && ctx.QqqTrend == "bearish")
        { penalty -= 8; signals.Add("Risk: both SPY and QQQ bearish"); }

        // Missing critical data
        if (!snapshot.DataAvailability.MarketDataAvailable)
        { penalty -= 15; signals.Add("Risk: no market data available"); }

        if (ind.BarsAvailable < 10)
        { penalty -= 5; signals.Add($"Risk: only {ind.BarsAvailable} bars available"); }

        // RSI extremes
        if (ind.Rsi14 is double rsi && (rsi > 80 || rsi < 20))
        { penalty -= 5; signals.Add($"Risk: RSI at extreme ({rsi:F0})"); }

        return Math.Clamp(penalty, -30, 0);
    }

    // -----------------------------------------------------------------------
    // Prediction type determination
    // -----------------------------------------------------------------------

    private static string DeterminePredictionType(double score, MarketSnapshot snapshot, TechnicalIndicators ind)
    {
        if (!snapshot.DataAvailability.MarketDataAvailable && !snapshot.DataAvailability.NewsAvailable)
            return "unavailable";

        if (score >= 25) return "bullish";
        if (score <= -18) return "bearish";

        if (Math.Abs(score) < 8)
            return "watch_only";

        if (ind.BollingerBandwidth is double bw && bw > 8)
            return "neutral_high_volatility";

        var hasTrendSignal = ind.Sma5 is not null && ind.Sma20 is not null;
        if (hasTrendSignal && ((ind.Sma5AboveSma20 && score < 0) || (!ind.Sma5AboveSma20 && score > 0)))
            return "neutral_range_bound";

        return "neutral_no_edge";
    }

    // -----------------------------------------------------------------------
    // Risk score
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
