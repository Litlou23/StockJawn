using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.MetaLabeling;

/// <summary>
/// Optional context for v2+ features that require data beyond ScoringBreakdown.
/// Callers populate what they can; anything left null becomes 0 in the feature vector.
/// </summary>
public record MetaLabelerContext
{
    /// <summary>SPY daily % change on the prediction date (e.g. -1.2 = SPY fell 1.2%).</summary>
    public float? SpyDailyChangePct { get; init; }

    /// <summary>How many predictions in the same batch share this ticker's sector.</summary>
    public int? SectorBatchCount { get; init; }

    /// <summary>Historical win rate for this ticker from stock_learning_stats (0–1).</summary>
    public float? TickerHistoricalWinRate { get; init; }

    /// <summary>Number of historical samples backing TickerHistoricalWinRate.</summary>
    public int? TickerHistoricalSampleSize { get; init; }
}

/// <summary>
/// Turns a ScoringBreakdown (+ light optional context) into a fixed-length,
/// fixed-order feature vector.
///
/// IMPORTANT: order of features is the ground-truth contract between training
/// and inference. Adding a new feature = new model version (bump the version
/// in FeatureVersion). Never reorder existing features — models trained
/// against an older layout will silently mis-score.
///
/// Kept intentionally simple:
///   • float[] output (ML.NET-native shape)
///   • all values numeric — enums encoded as ints
///   • null-safe (missing values → 0)
/// </summary>
public class MetaLabelerFeatureExtractor
{
    /// <summary>
    /// Bump this when adding, removing, or reordering features. Models
    /// trained against version N cannot be scored with a version-(N+1)
    /// extractor. Used by MetaLabelerService to guard version mismatch.
    /// </summary>
    public const int FeatureVersion = 2;

    /// <summary>
    /// Names in the exact order Extract() emits values. Persisted alongside
    /// the trained model artifact so inference can validate the layout.
    /// </summary>
    public static readonly IReadOnlyList<string> FeatureNames = new[]
    {
        // ── Primary scoring engine output ──
        "directional_score",
        "bullish_score",
        "bearish_score",
        "direction_margin",
        "confidence",
        "actionability_score",
        "data_quality_factor",
        "confirmation_multiplier",
        "aligned_buckets",
        "conflicting_buckets",
        "risk_adjustment",
        "calibration_factor",
        "opposition_penalty",
        "regime_penalty",
        "liquidity_penalty",
        "decision_margin",
        "clear_direction",

        // ── Per-bucket bullish/bearish ──
        "trend_bullish", "trend_bearish", "trend_score",
        "momentum_bullish", "momentum_bearish", "momentum_score",
        "volume_bullish", "volume_bearish", "volume_score",
        "volatility_bullish", "volatility_bearish", "volatility_setup_score",
        "market_context_bullish", "market_context_bearish", "market_context_score",
        "catalyst_bullish", "catalyst_bearish", "catalyst_score",
        "catalyst_strength",
        "learning_bullish", "learning_bearish", "learning_score",
        "research_signal_bullish", "research_signal_bearish", "research_signal_score",
        "research_signal_count",
        "risk_penalty",

        // ── Research universe / historical profile context ──
        "research_universe_interest_score",
        "research_universe_evidence_count",
        "has_research_asset",
        "historical_volatility",
        "historical_atr_percent",

        // ── Prediction-level derived features ──
        "expected_value_percent",
        "risk_reward_ratio",
        "atr_percent",
        "expected_move_percent",
        "days_until_earnings",

        // ── Categorical encodings ──
        "is_bullish_prediction",
        "is_bearish_prediction",
        "is_neutral_prediction",
        "actionability_tier_int",

        // ── v2: Market & ticker context ──
        "prediction_hour_et",
        "spy_daily_change_pct",
        "sector_batch_count",
        "ticker_historical_win_rate",
        "ticker_historical_sample_size",
    };

    /// <summary>Feature vector length — enforced at extract time.</summary>
    public int FeatureCount => FeatureNames.Count;

    /// <summary>
    /// Extract features for training or inference. Any null field becomes 0.
    /// The output length must exactly equal FeatureCount — anything else is a
    /// bug in this class.
    /// </summary>
    public float[] Extract(
        ScoringBreakdown breakdown,
        PredictionCandidate? prediction = null,
        int? daysUntilEarnings = null,
        MetaLabelerContext? context = null)
    {
        var b = breakdown;
        var p = prediction;
        var ctx = context ?? new MetaLabelerContext();

        // Derive prediction hour in ET (Eastern Time) — market behavior
        // differs significantly morning vs afternoon.
        float predictionHourEt = 0f;
        if (p is not null)
        {
            var et = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
                p.CreatedAt, "America/New_York");
            predictionHourEt = et.Hour + (et.Minute / 60f);
        }

        var v = new List<float>(FeatureCount)
        {
            (float)b.DirectionalScore,
            (float)b.BullishScore,
            (float)b.BearishScore,
            (float)b.DirectionMargin,
            b.Confidence,
            b.ActionabilityScore,
            (float)b.DataQualityFactor,
            (float)b.ConfirmationMultiplier,
            b.AlignedBuckets,
            b.ConflictingBuckets,
            (float)b.RiskAdjustment,
            (float)b.CalibrationFactor,
            (float)b.OppositionPenalty,
            (float)b.RegimePenalty,
            (float)b.LiquidityPenalty,
            (float)b.DecisionMargin,
            b.ClearDirection ? 1f : 0f,

            (float)b.TrendBullish, (float)b.TrendBearish, (float)b.TrendScore,
            (float)b.MomentumBullish, (float)b.MomentumBearish, (float)b.MomentumScore,
            (float)b.VolumeBullish, (float)b.VolumeBearish, (float)b.VolumeScore,
            (float)b.VolatilityBullish, (float)b.VolatilityBearish, (float)b.VolatilitySetupScore,
            (float)b.MarketContextBullish, (float)b.MarketContextBearish, (float)b.MarketContextScore,
            (float)b.CatalystBullish, (float)b.CatalystBearish, (float)b.CatalystScore,
            (float)b.CatalystStrength,
            (float)b.LearningBullish, (float)b.LearningBearish, (float)b.LearningScore,
            (float)b.ResearchSignalBullish, (float)b.ResearchSignalBearish, (float)b.ResearchSignalScore,
            b.ResearchSignalCount,
            (float)b.RiskPenalty,

            b.ResearchUniverseInterestScore,
            b.ResearchUniverseEvidenceCount,
            b.HasResearchAsset ? 1f : 0f,
            (float)(b.HistoricalVolatility ?? 0),
            (float)(b.HistoricalAtrPercent ?? 0),

            (float)(p?.ExpectedValuePercent ?? 0),
            (float)(p?.RiskRewardRatio ?? 0),
            (float)(p?.AtrPercent ?? 0),
            (float)(p?.ExpectedMovePercent ?? 0),
            daysUntilEarnings ?? -1,

            p?.PredictionType == PredictionType.bullish ? 1f : 0f,
            p?.PredictionType == PredictionType.bearish ? 1f : 0f,
            p?.PredictionType == PredictionType.neutral ? 1f : 0f,
            (int)(b.ActionabilityTier),

            // ── v2: Market & ticker context ──
            predictionHourEt,
            ctx.SpyDailyChangePct ?? 0f,
            ctx.SectorBatchCount ?? 0,
            ctx.TickerHistoricalWinRate ?? 0f,
            ctx.TickerHistoricalSampleSize ?? 0,
        };

        if (v.Count != FeatureCount)
            throw new InvalidOperationException(
                $"Feature extractor produced {v.Count} values but FeatureCount is {FeatureCount}. " +
                $"FeatureNames and Extract() are out of sync — fix them together.");

        return v.ToArray();
    }
}
