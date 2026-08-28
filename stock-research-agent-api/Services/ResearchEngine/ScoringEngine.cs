using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchEngine.Evaluation;
using ResearchSignal = StockResearchAgent.Api.Models.ResearchSignal;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Orchestrates the scoring pipeline. Scoring formulas are preserved but moved
/// into independent evaluator, aggregation, confidence, and risk services.
/// </summary>
public class ScoringEngine : IScoringEngine
{
    private readonly ITrendEvaluator _trendEvaluator;
    private readonly IMomentumEvaluator _momentumEvaluator;
    private readonly IVolumeEvaluator _volumeEvaluator;
    private readonly IVolatilityEvaluator _volatilityEvaluator;
    private readonly IMarketContextEvaluator _marketContextEvaluator;
    private readonly ICatalystEvaluator _catalystEvaluator;
    private readonly ILearningAdjustmentEvaluator _learningAdjustmentEvaluator;
    private readonly IResearchSignalEvaluator _researchSignalEvaluator;
    private readonly IScoreAggregator _scoreAggregator;
    private readonly IConfidenceEngine _confidenceEngine;
    private readonly IRiskEngine _riskEngine;

    // Defaults — overridable via scoring_weight_overrides keys:
    //   min_edge_margin, min_score_for_direction, min_ratio_for_direction
    private const double DefaultMinEdgeMargin = 10;
    private const double DefaultMinScoreForDirection = 20;
    private const double DefaultMinRatioForDirection = 1.4;

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
        public List<MarketEvidence> Evidence { get; init; } = [];
        public MarketThesis? Thesis { get; init; }
        public List<EvaluatorReasoning> Reasoning { get; init; } = [];
    }

    public ScoringEngine()
        : this(
            new TrendEvaluator(),
            new MomentumEvaluator(),
            new VolumeEvaluator(),
            new VolatilityEvaluator(),
            new MarketContextEvaluator(),
            new CatalystEvaluator(),
            new LearningAdjustmentEvaluator(),
            new ResearchSignalEvaluator(),
            new ScoreAggregator(),
            new ConfidenceEngine(),
            new RiskEngine())
    {
    }

    public ScoringEngine(
        ITrendEvaluator trendEvaluator,
        IMomentumEvaluator momentumEvaluator,
        IVolumeEvaluator volumeEvaluator,
        IVolatilityEvaluator volatilityEvaluator,
        IMarketContextEvaluator marketContextEvaluator,
        ICatalystEvaluator catalystEvaluator,
        ILearningAdjustmentEvaluator learningAdjustmentEvaluator,
        IResearchSignalEvaluator researchSignalEvaluator,
        IScoreAggregator scoreAggregator,
        IConfidenceEngine confidenceEngine,
        IRiskEngine riskEngine)
    {
        _trendEvaluator = trendEvaluator;
        _momentumEvaluator = momentumEvaluator;
        _volumeEvaluator = volumeEvaluator;
        _volatilityEvaluator = volatilityEvaluator;
        _marketContextEvaluator = marketContextEvaluator;
        _catalystEvaluator = catalystEvaluator;
        _learningAdjustmentEvaluator = learningAdjustmentEvaluator;
        _researchSignalEvaluator = researchSignalEvaluator;
        _scoreAggregator = scoreAggregator;
        _confidenceEngine = confidenceEngine;
        _riskEngine = riskEngine;
    }

    // Compatibility wrapper for older callers and tests.
    public static ScoringResult Score(
        MarketSnapshot snapshot,
        TechnicalIndicators indicators,
        BenchmarkContext benchmark,
        Dictionary<string, double> weights,
        List<string> lessons,
        List<ResearchSignal>? researchSignals = null,
        IReadOnlyList<MarketEvidence>? evidence = null,
        MarketThesis? thesis = null)
    {
        var intelligence = new MarketIntelligenceContext
        {
            Ticker = snapshot.Ticker,
            Evidence = evidence?.ToList() ?? [],
            Thesis = thesis ?? new MarketThesis { Ticker = snapshot.Ticker, Direction = MarketThesisDirection.neutral },
            GeneratedAt = DateTimeOffset.UtcNow,
        };

        return new ScoringEngine().Evaluate(
            snapshot, indicators, benchmark, weights, lessons, researchSignals, intelligence);
    }

    public ScoringResult Evaluate(
        MarketSnapshot snapshot,
        TechnicalIndicators indicators,
        BenchmarkContext benchmark,
        Dictionary<string, double> weights,
        List<string> lessons,
        List<ResearchSignal>? researchSignals = null,
        MarketIntelligenceContext? intelligence = null,
        ResearchUniverseContext? researchUniverse = null,
        VolatilityOpportunityAssessment? volatilityAssessment = null,
        MarketRegimeResult? marketRegimeResult = null,
        int? daysUntilEarnings = null,
        double? estimatedEps = null)
    {
        intelligence ??= new MarketIntelligenceContext
        {
            Ticker = snapshot.Ticker,
            Thesis = new MarketThesis { Ticker = snapshot.Ticker, Direction = MarketThesisDirection.neutral },
            GeneratedAt = DateTimeOffset.UtcNow,
        };

        var context = EvaluationContext.Create(
            snapshot,
            indicators,
            benchmark,
            intelligence,
            weights,
            lessons,
            researchSignals ?? [],
            researchUniverse,
            volatilityAssessment,
            marketRegimeResult,
            daysUntilEarnings,
            estimatedEps);

        var outputs = new List<EvaluatorOutput>
        {
            _trendEvaluator.Evaluate(context),
            _momentumEvaluator.Evaluate(context),
            _volumeEvaluator.Evaluate(context),
            _volatilityEvaluator.Evaluate(context),
            _marketContextEvaluator.Evaluate(context),
            _catalystEvaluator.Evaluate(context),
            _learningAdjustmentEvaluator.Evaluate(context),
            _researchSignalEvaluator.Evaluate(context),
        };

        // Apply profile weight scaling to tentative scores (mirrors ScoreAggregator logic)
        double rawBull = 0, rawBear = 0;
        foreach (var o in outputs)
        {
            double scale = 1.0;
            if (ScoreAggregator.WeightableKinds.TryGetValue(o.Kind, out var wk))
                scale = weights.TryGetValue(wk, out var wv) ? wv : 1.0;
            rawBull += o.BullishContribution * scale;
            rawBear += o.BearishContribution * scale;
        }
        var tentativeBull = Math.Clamp(rawBull, 0, 100);
        var tentativeBear = Math.Clamp(rawBear, 0, 100);
        var (winningDirection, predType) = DeterminePredictionType(tentativeBull, tentativeBear, snapshot, indicators, weights);

        var aggregate = _scoreAggregator.Aggregate(outputs, winningDirection, context);
        var riskAssessment = _riskEngine.Evaluate(context, predType);
        var confidence = _confidenceEngine.Evaluate(context, aggregate, riskAssessment, winningDirection);

        var signals = outputs
            .SelectMany(o => o.DebugSignals)
            .Concat(riskAssessment.DebugSignals)
            .Concat(confidence.DebugSignals)
            .ToList();

        var catalystStrength = _catalystEvaluator.ScoreCatalystStrength(context);
        var catalyst = aggregate.Outputs[EvaluatorKind.catalyst];
        var market = aggregate.Outputs[EvaluatorKind.market_context];
        var trend = aggregate.Outputs[EvaluatorKind.trend];
        var momentum = aggregate.Outputs[EvaluatorKind.momentum];
        var volume = aggregate.Outputs[EvaluatorKind.volume];
        var volatility = aggregate.Outputs[EvaluatorKind.volatility];
        var learning = aggregate.Outputs[EvaluatorKind.learning];
        var research = aggregate.Outputs[EvaluatorKind.research_signal];

        var (actionScore, actionTier, actionReasons) = ComputeActionability(
            confidence.Confidence, riskReward: null,
            catalystScore: catalyst.BullishContribution - catalyst.BearishContribution,
            marketContextScore: market.BullishContribution - market.BearishContribution,
            dataQualityFactor: confidence.DataQualityFactor,
            confidenceCap: confidence.ConfidenceCap,
            weights: context.LearningData.Weights);

        return new ScoringResult
        {
            DirectionalScore = Math.Round(aggregate.DirectionalScore, 2),
            BullishScore = Math.Round(aggregate.BullishScore, 2),
            BearishScore = Math.Round(aggregate.BearishScore, 2),
            WinningDirection = winningDirection,
            DirectionMargin = Math.Round(Math.Abs(aggregate.BullishScore - aggregate.BearishScore), 2),
            Confidence = confidence.Confidence,
            PredictionType = predType,
            Risk = riskAssessment.RiskScore,
            Signals = signals,
            Evidence = intelligence.Evidence.ToList(),
            Thesis = intelligence.Thesis,
            Reasoning = outputs.Select(o => o.DebugInformation).ToList(),
            Breakdown = new ScoringBreakdown
            {
                DirectionalScore = Math.Round(aggregate.DirectionalScore, 2),
                BullishScore = Math.Round(aggregate.BullishScore, 2),
                BearishScore = Math.Round(aggregate.BearishScore, 2),
                WinningDirection = winningDirection,
                DirectionMargin = Math.Round(Math.Abs(aggregate.BullishScore - aggregate.BearishScore), 2),
                Confidence = confidence.Confidence,
                ActionabilityScore = actionScore,
                ActionabilityTier = actionTier,
                DataQualityFactor = Math.Round(confidence.DataQualityFactor, 3),
                ConfirmationMultiplier = Math.Round(confidence.ConfirmationMultiplier, 3),
                AlignedBuckets = aggregate.AlignedBuckets,
                ConflictingBuckets = aggregate.ConflictingBuckets,
                RiskAdjustment = Math.Round(confidence.RiskAdjustment, 3),
                CalibrationFactor = Math.Round(confidence.CalibrationFactor, 3),
                OppositionPenalty = Math.Round(confidence.OppositionPenalty, 3),
                RegimePenalty = Math.Round(confidence.RegimePenalty, 3),
                LiquidityPenalty = Math.Round(confidence.LiquidityPenalty, 3),
                DecisionMargin = Math.Round(confidence.DecisionMargin, 3),
                ClearDirection = confidence.ClearDirection,
                TrendScore = Math.Round(trend.BullishContribution - trend.BearishContribution, 2),
                TrendBullish = Math.Round(trend.BullishContribution, 2),
                TrendBearish = Math.Round(trend.BearishContribution, 2),
                MomentumScore = Math.Round(momentum.BullishContribution - momentum.BearishContribution, 2),
                MomentumBullish = Math.Round(momentum.BullishContribution, 2),
                MomentumBearish = Math.Round(momentum.BearishContribution, 2),
                VolumeScore = Math.Round(volume.BullishContribution - volume.BearishContribution, 2),
                VolumeBullish = Math.Round(volume.BullishContribution, 2),
                VolumeBearish = Math.Round(volume.BearishContribution, 2),
                VolatilitySetupScore = Math.Round(volatility.BullishContribution - volatility.BearishContribution, 2),
                VolatilityBullish = Math.Round(volatility.BullishContribution, 2),
                VolatilityBearish = Math.Round(volatility.BearishContribution, 2),
                MarketContextScore = Math.Round(market.BullishContribution - market.BearishContribution, 2),
                MarketContextBullish = Math.Round(market.BullishContribution, 2),
                MarketContextBearish = Math.Round(market.BearishContribution, 2),
                CatalystScore = Math.Round(catalyst.BullishContribution - catalyst.BearishContribution, 2),
                CatalystBullish = Math.Round(catalyst.BullishContribution, 2),
                CatalystBearish = Math.Round(catalyst.BearishContribution, 2),
                CatalystStrength = Math.Round(catalystStrength, 2),
                LearningScore = Math.Round(learning.BullishContribution - learning.BearishContribution, 2),
                LearningBullish = Math.Round(learning.BullishContribution, 2),
                LearningBearish = Math.Round(learning.BearishContribution, 2),
                ResearchSignalScore = Math.Round(research.BullishContribution - research.BearishContribution, 2),
                ResearchSignalBullish = Math.Round(research.BullishContribution, 2),
                ResearchSignalBearish = Math.Round(research.BearishContribution, 2),
                ResearchSignalCount = context.ResearchSignals.Count,
                RiskPenalty = Math.Round(riskAssessment.RiskPenalty, 2),
                IndicatorsUsed = indicators.IndicatorsComputed,
                IndicatorsSkipped = indicators.IndicatorsSkipped,
                ConfidenceCap = confidence.ConfidenceCap,
                ActionabilityReasons = actionReasons,
                // Research Universe integration
                ResearchUniverseInterestScore = context.ResearchUniverse.InterestScore,
                ResearchUniverseEvidenceCount = context.ResearchUniverse.EvidenceCount,
                ResearchUniverseState = context.ResearchUniverse.ResearchState.ToString(),
                HasResearchAsset = context.ResearchUniverse.HasResearchAsset,
                HistoricalVolatility = context.ResearchUniverse.HistoricalVolatility,
                HistoricalAtrPercent = context.ResearchUniverse.HistoricalAtrPercent,
            },
        };
    }

    public static ScoringResult FinalizeWithRiskReward(ScoringResult initial, double? riskReward)
    {
        var breakdown = initial.Breakdown;
        var reasons = new List<string>(breakdown.ActionabilityReasons);
        int confidence = initial.Confidence;
        string? capReason = breakdown.ConfidenceCap;

        if (riskReward is double rr and > 0)
        {
            if (rr < 0.8)
            {
                confidence = Math.Min(confidence, 35);
                capReason = $"R/R {rr:F2} < 0.8 — poor risk/reward";
                reasons.Add($"Confidence capped at 35 — risk/reward {rr:F2} unacceptable");
            }
            else if (rr < 1.2 && confidence > 55)
            {
                confidence = 55;
                capReason = $"R/R {rr:F2} < 1.2";
                reasons.Add($"Confidence capped at 55 — risk/reward {rr:F2} below 1.2");
            }
            else if (rr < 1.5 && confidence > 70)
            {
                confidence = 70;
                capReason = $"R/R {rr:F2} < 1.5";
                reasons.Add($"Confidence capped at 70 — risk/reward {rr:F2} mediocre");
            }
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

    public static ScoringResult AdjustForSetupHistory(
        ScoringResult initial,
        SetupPerformance? setupPerformance,
        bool isHistoricallyFavorable)
    {
        if (setupPerformance is null || setupPerformance.SampleSize < 5)
            return initial;

        var breakdown = initial.Breakdown;
        var reasons = new List<string>(breakdown.ActionabilityReasons);
        int confidence = initial.Confidence;
        string? capReason = breakdown.ConfidenceCap;

        var ev = setupPerformance.ExpectedValuePercent;
        var wr = setupPerformance.WinRate;
        var trusted = setupPerformance.IsTrusted;

        if (isHistoricallyFavorable && trusted)
        {
            var boost = ev switch
            {
                >= 2.0 => 15,
                >= 1.0 => 10,
                >= 0.5 => 5,
                _ => 0,
            };

            if (boost > 0)
            {
                confidence = Math.Min(confidence + boost, 95);
                reasons.Add($"Setup boost +{boost}: EV={ev:F2}%, WR={wr * 100:F0}%, n={setupPerformance.SampleSize}");
            }
        }
        else if (!trusted && setupPerformance.SampleSize >= 8)
        {
            var penalty = 10;
            confidence = Math.Max(confidence - penalty, 20);
            capReason = "Degraded setup (recent WR dropped >15% from all-time)";
            reasons.Add($"Setup penalty -{penalty}: setup degrading, EV={ev:F2}%");
        }
        else if (ev < -0.5 && setupPerformance.SampleSize >= 8)
        {
            var penalty = 15;
            confidence = Math.Max(confidence - penalty, 15);
            capReason = $"Negative EV setup ({ev:F2}%)";
            reasons.Add($"Setup penalty -{penalty}: negative EV={ev:F2}%, WR={wr * 100:F0}%");
        }

        var catalystNet = breakdown.CatalystBullish - breakdown.CatalystBearish;
        var marketNet = breakdown.MarketContextBullish - breakdown.MarketContextBearish;
        var (actionScore, actionTier, tierReasons) = ComputeActionability(
            confidence, riskReward: null,
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

    /// <summary>
    /// Adjusts confidence for per-ticker historical reliability.
    /// Uses Bayesian-smoothed accuracy to scale confidence.
    /// Returns (adjustedResult, shouldDowngradeToWatchOnly).
    /// </summary>
    public static (ScoringResult Result, bool DowngradeToWatchOnly) AdjustForTickerReliability(
        ScoringResult initial,
        double tickerAccuracy,
        int tickerSampleSize,
        double globalAccuracy)
    {
        if (tickerSampleSize < 5)
            return (initial, false);

        // Bayesian smoothing: weight = n / (n + prior_strength)
        const int priorStrength = 10;
        double sampleWeight = (double)tickerSampleSize / (tickerSampleSize + priorStrength);
        double effectiveAccuracy = sampleWeight * tickerAccuracy + (1 - sampleWeight) * globalAccuracy;

        var breakdown = initial.Breakdown;
        var reasons = new List<string>(breakdown.ActionabilityReasons);
        int confidence = initial.Confidence;
        string? capReason = breakdown.ConfidenceCap;
        bool downgrade = false;

        // Hard cutoff: terrible accuracy → watch_only
        if (effectiveAccuracy < 0.25 && tickerSampleSize >= 10)
        {
            confidence = Math.Min(confidence, 20);
            capReason = $"Ticker effective accuracy {effectiveAccuracy * 100:F0}% < 25%";
            reasons.Add($"Ticker reliability downgrade: accuracy={tickerAccuracy * 100:F0}%, n={tickerSampleSize}, effective={effectiveAccuracy * 100:F0}%");
            downgrade = true;
        }
        else
        {
            double reliabilityFactor = 0.6 + 0.4 * Math.Clamp(effectiveAccuracy / 0.8, 0, 1);

            if (reliabilityFactor < 0.95)
            {
                confidence = (int)Math.Round(confidence * reliabilityFactor);
                confidence = Math.Clamp(confidence, 10, 85);
                reasons.Add($"Ticker reliability {reliabilityFactor:F2}: accuracy={tickerAccuracy * 100:F0}%, n={tickerSampleSize}");
            }
        }

        var catalystNet = breakdown.CatalystBullish - breakdown.CatalystBearish;
        var marketNet = breakdown.MarketContextBullish - breakdown.MarketContextBearish;
        var (actionScore, actionTier, tierReasons) = ComputeActionability(
            confidence, riskReward: null,
            catalystScore: catalystNet,
            marketContextScore: marketNet,
            dataQualityFactor: breakdown.DataQualityFactor,
            confidenceCap: capReason);
        reasons.AddRange(tierReasons.Where(r => !reasons.Contains(r)));

        var adjusted = initial with
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

        return (adjusted, downgrade);
    }

    public static (int Score, ActionabilityTier Tier, List<string> Reasons) ComputeActionability(
        int confidence,
        double? riskReward,
        double catalystScore,
        double marketContextScore,
        double dataQualityFactor,
        string? confidenceCap,
        IReadOnlyDictionary<string, double>? weights = null)
    {
        var reasons = new List<string>();

        // Thresholds DB-configurable to match confidence formula changes.
        // Compressed confidence range (post diminishing-returns fix) means
        // most scores land 25-55 instead of old 20-70+. Adjust tiers to match.
        var w = weights ?? new Dictionary<string, double>();
        var tierScan = (int)w.GetValueOrDefault("actionability_scan_max", 25.0);
        var tierWatch = (int)w.GetValueOrDefault("actionability_watch_max", 38.0);
        var tierActionable = (int)w.GetValueOrDefault("actionability_actionable_max", 48.0);
        var tierStrong = (int)w.GetValueOrDefault("actionability_strong_max", 58.0);

        ActionabilityTier tier;
        if (confidence < tierScan)
            tier = ActionabilityTier.scan;
        else if (confidence < tierWatch)
            tier = ActionabilityTier.watch_only;
        else if (confidence < tierActionable)
            tier = ActionabilityTier.actionable;
        else if (confidence < tierStrong)
            tier = ActionabilityTier.strong;
        else
            tier = ActionabilityTier.strongest;

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

    internal static (string WinningDirection, string PredictionType) DeterminePredictionType(
        double bullishScore, double bearishScore, MarketSnapshot snapshot, TechnicalIndicators ind,
        IReadOnlyDictionary<string, double>? weights = null)
    {
        if (!snapshot.DataAvailability.MarketDataAvailable && !snapshot.DataAvailability.NewsAvailable)
            return ("neutral", "unavailable");

        // Read configurable thresholds from weights (set via scoring_weight_overrides),
        // falling back to compile-time defaults so existing callers are unaffected.
        var minEdgeMargin = weights?.GetValueOrDefault("min_edge_margin", DefaultMinEdgeMargin) ?? DefaultMinEdgeMargin;
        var minScoreForDirection = weights?.GetValueOrDefault("min_score_for_direction", DefaultMinScoreForDirection) ?? DefaultMinScoreForDirection;
        var minRatioForDirection = weights?.GetValueOrDefault("min_ratio_for_direction", DefaultMinRatioForDirection) ?? DefaultMinRatioForDirection;

        var margin = bullishScore - bearishScore;

        if (bullishScore >= minScoreForDirection && margin >= minEdgeMargin)
            return ("bullish", "bullish");

        if (bearishScore >= minScoreForDirection && -margin >= minEdgeMargin)
            return ("bearish", "bearish");

        var minScoreForRatio = minScoreForDirection * 0.75; // scale with the main threshold
        if (bullishScore >= minScoreForRatio && bearishScore >= minScoreForRatio)
        {
            var ratio = bullishScore / bearishScore;
            if (ratio >= minRatioForDirection)
                return ("bullish", "bullish");
            if (1.0 / ratio >= minRatioForDirection)
                return ("bearish", "bearish");
        }

        if (Math.Max(bullishScore, bearishScore) < 8)
            return ("neutral", "watch_only");

        if (ind.BollingerBandwidth is double bw && bw > 8)
            return ("neutral", "neutral_high_volatility");

        var hasTrendSignal = ind.Sma5 is not null && ind.Sma20 is not null;
        if (hasTrendSignal && Math.Abs(margin) < 5)
            return ("neutral", "neutral_range_bound");

        return ("neutral", "neutral_no_edge");
    }
}
