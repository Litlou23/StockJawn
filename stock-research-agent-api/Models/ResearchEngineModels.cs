using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

// ---------------------------------------------------------------------------
// Research Run
// ---------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResearchRunType { morning_scan, end_of_day_review, learning_update }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResearchRunStatus { started, completed, failed }

public record ResearchRun
{
    public string Id { get; init; } = "";
    public ResearchRunType RunType { get; init; }
    public ResearchRunStatus Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Summary { get; init; }
    public List<string> Errors { get; init; } = [];
    public int PredictionsGenerated { get; init; }
    public int PredictionsEvaluated { get; init; }
}

// ---------------------------------------------------------------------------
// Market Snapshot
// ---------------------------------------------------------------------------

public record MarketSnapshot
{
    public string Id { get; init; } = "";
    public string RunId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public MarketSnapshotQuote? Quote { get; init; }
    public List<MarketSnapshotBar> RecentBars { get; init; } = [];
    public MarketSnapshotTechnical? TechnicalContext { get; init; }
    public List<MarketSnapshotNews> NewsContext { get; init; } = [];
    public MarketSnapshotAvailability DataAvailability { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
}

public record MarketSnapshotQuote
{
    public double Price { get; init; }
    public double Change { get; init; }
    public double ChangePercent { get; init; }
    public double Volume { get; init; }
    public double PreviousClose { get; init; }
    public double Open { get; init; }
    public double High { get; init; }
    public double Low { get; init; }
    public string Timestamp { get; init; } = "";
}

public record MarketSnapshotBar
{
    public string Date { get; init; } = "";
    public double Open { get; init; }
    public double High { get; init; }
    public double Low { get; init; }
    public double Close { get; init; }
    public double Volume { get; init; }
}

public record MarketSnapshotTechnical
{
    public string TrendDirection { get; init; } = "";
    public string MovingAverageSummary { get; init; } = "";
    public string MomentumSummary { get; init; } = "";
    public string VolumeSummary { get; init; } = "";
    public string RelativeStrengthNote { get; init; } = "";
}

public record MarketSnapshotNews
{
    public string Title { get; init; } = "";
    public string SourceName { get; init; } = "";
    public string Url { get; init; } = "";
    public string PublishedAt { get; init; } = "";
    public string? CatalystType { get; init; }
    public string? Sentiment { get; init; }
    public double ImportanceScore { get; init; }
}

public record MarketSnapshotAvailability
{
    public bool MarketDataAvailable { get; init; }
    public bool NewsAvailable { get; init; }
    public bool OptionsChainAvailable { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public record TechnicalIndicators
{
    // Moving averages
    public double? Sma5 { get; init; }
    public double? Sma20 { get; init; }
    public bool Sma5AboveSma20 { get; init; }
    public bool CloseAboveSma20 { get; init; }

    // Momentum
    public double? Roc5 { get; init; }
    public double? Roc10 { get; init; }
    public double? Rsi14 { get; init; }
    public double? StochasticCloseLocation { get; init; }

    // Trend
    public double? LinearRegressionSlope { get; init; }
    public double? DonchianHigh20 { get; init; }
    public double? DonchianLow20 { get; init; }
    public bool? DonchianBreakout { get; init; }
    public bool? DonchianBreakdown { get; init; }

    // Volatility
    public double? Atr14 { get; init; }
    public double? BollingerUpper { get; init; }
    public double? BollingerMiddle { get; init; }
    public double? BollingerLower { get; init; }
    public double? BollingerBandwidth { get; init; }
    public bool? BollingerBreakout { get; init; }

    // Volume
    public double? VolumeRatio { get; init; }
    public double? ObvSlope { get; init; }
    public bool? PriceVolumeConfirmation { get; init; }

    // Close location in range
    public double? CloseLocationValue { get; init; }

    // Metadata
    public List<string> IndicatorsComputed { get; init; } = [];
    public List<string> IndicatorsSkipped { get; init; } = [];
    public int BarsAvailable { get; init; }
}

public record BenchmarkContext
{
    public double? SpyChangePercent { get; init; }
    public double? QqqChangePercent { get; init; }
    public string? SpyTrend { get; init; }
    public string? QqqTrend { get; init; }
    public double? RelativeStrengthVsSpy { get; init; }
    public double? RelativeStrengthVsQqq { get; init; }
}

/// <summary>
/// Actionability tier — orthogonal to prediction direction.
/// A prediction can be directionally right but still watch_only if R/R is
/// poor, the market context conflicts, or data quality is low. Confidence
/// bands map to a base tier; guardrails downgrade further.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionabilityTier
{
    scan,          // confidence < 35
    watch_only,    // 35–54 OR any tier downgraded by guardrails
    actionable,    // 55–69
    strong,        // 70–84
    strongest,     // 85+ (rare)
}

public record ScoringBreakdown
{
    public double DirectionalScore { get; init; }
    public double BullishScore { get; init; }
    public double BearishScore { get; init; }
    public string WinningDirection { get; init; } = "neutral";
    public double DirectionMargin { get; init; }
    public int Confidence { get; init; }
    public int ActionabilityScore { get; init; }
    public ActionabilityTier ActionabilityTier { get; init; } = ActionabilityTier.scan;
    public double DataQualityFactor { get; init; }
    public double ConfirmationMultiplier { get; init; }
    public int AlignedBuckets { get; init; }
    public int ConflictingBuckets { get; init; }
    public double RiskAdjustment { get; init; }
    public double CalibrationFactor { get; init; }
    // Legacy net scores (bullish - bearish) for backward compat
    public double TrendScore { get; init; }
    public double MomentumScore { get; init; }
    public double VolumeScore { get; init; }
    public double VolatilitySetupScore { get; init; }
    public double MarketContextScore { get; init; }
    public double CatalystScore { get; init; }
    public double LearningScore { get; init; }
    // Per-bucket independent scores
    public double TrendBullish { get; init; }
    public double TrendBearish { get; init; }
    public double MomentumBullish { get; init; }
    public double MomentumBearish { get; init; }
    public double VolumeBullish { get; init; }
    public double VolumeBearish { get; init; }
    public double VolatilityBullish { get; init; }
    public double VolatilityBearish { get; init; }
    public double MarketContextBullish { get; init; }
    public double MarketContextBearish { get; init; }
    public double CatalystBullish { get; init; }
    public double CatalystBearish { get; init; }
    public double LearningBullish { get; init; }
    public double LearningBearish { get; init; }
    public double RiskPenalty { get; init; }
    public List<string> IndicatorsUsed { get; init; } = [];
    public List<string> IndicatorsSkipped { get; init; } = [];
    public string? ConfidenceCap { get; init; }
    public List<string> ActionabilityReasons { get; init; } = [];
}

// ---------------------------------------------------------------------------
// Prediction Candidate
// ---------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PredictionType
{
    bullish,
    bearish,
    neutral_no_edge,
    neutral_range_bound,
    neutral_high_volatility,
    watch_only,
    rejected,
    unavailable,
    neutral, // legacy — kept for deserialization of old rows only
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PredictionCategory { short_term_stock, long_term_stock, scan_result }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PredictionAssetType { stock, option_watch_candidate }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PredictionStatus { open, evaluated, expired }

/// <summary>
/// Valid time windows. Stored as a plain string because C# enum members cannot start with a digit.
/// </summary>
public static class PredictionTimeWindows
{
    public const string Intraday = "intraday";
    public const string OneDay = "1_day";
    public const string ThreeDay = "3_day";
    public const string OneWeek = "1_week";
    public const string OneMonth = "1_month";
    public const string ThreeMonth = "3_month";
    public const string SixMonth = "6_month";
    public const string OneYear = "1_year";

    public static readonly HashSet<string> ShortTerm = [Intraday, OneDay, ThreeDay, OneWeek];
    public static readonly HashSet<string> LongTerm = [OneMonth, ThreeMonth, SixMonth, OneYear];
    public static readonly HashSet<string> All = [.. ShortTerm, .. LongTerm];
}

public static class PredictionCategoryHelper
{
    private static readonly HashSet<PredictionType> DirectionalTypes =
        [PredictionType.bullish, PredictionType.bearish];

    public static bool IsDirectional(PredictionType type) => DirectionalTypes.Contains(type);

    public static PredictionCategory Categorize(PredictionType type, string timeWindow) =>
        IsDirectional(type)
            ? PredictionTimeWindows.LongTerm.Contains(timeWindow)
                ? PredictionCategory.long_term_stock
                : PredictionCategory.short_term_stock
            : PredictionCategory.scan_result;
}

public record PredictionCandidate
{
    public string Id { get; init; } = "";
    public string RunId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public PredictionType PredictionType { get; init; }
    public PredictionAssetType AssetType { get; init; }
    public string TimeWindow { get; init; } = "1_day";
    public int ConfidenceScore { get; init; }
    public int ImportanceScore { get; init; }
    public int RiskScore { get; init; }
    public double? EntryReferencePrice { get; init; }
    // ATR-based price prediction engine
    public double? Atr14 { get; init; }
    public double? AtrPercent { get; init; }
    public double? TimeframeMultiplier { get; init; }
    public double? SignalModifier { get; init; }
    public double? ExpectedMoveDollar { get; init; }
    public double? ExpectedMovePercent { get; init; }
    public double? PredictedPrice { get; init; }
    public double? PredictedMovePercent { get; init; }
    public double? ProjectedPriceLow { get; init; }
    public double? ProjectedPriceHigh { get; init; }
    public double? TargetPrice { get; init; }
    public double? StopPrice { get; init; }
    public double? InvalidationPrice { get; init; }
    public double? SupportLevel { get; init; }
    public double? ResistanceLevel { get; init; }
    public double? RiskRewardRatio { get; init; }
    public string? PricePredictionMethod { get; init; }
    public List<string> PricePredictionWarnings { get; init; } = [];
    public string BullishCase { get; init; } = "";
    public string BearishCase { get; init; } = "";
    public string PredictionReason { get; init; } = "";
    public string InvalidationRule { get; init; } = "";
    public List<string> DataSourcesUsed { get; init; } = [];
    public List<string> MissingDataWarnings { get; init; } = [];
    public double? BullishScore { get; init; }
    public double? BearishScore { get; init; }
    public string? WinningDirection { get; init; }
    public double? DirectionConfidence { get; init; }
    public string? ScoreDebugJson { get; init; }
    public int? ActionabilityScore { get; init; }
    public ActionabilityTier? ActionabilityTier { get; init; }
    public string Status { get; init; } = "open";
    public DateTimeOffset CreatedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Prediction Input
// ---------------------------------------------------------------------------

public record PredictionInput
{
    public string Id { get; init; } = "";
    public string PredictionId { get; init; } = "";
    public string InputType { get; init; } = "";
    public string SourceName { get; init; } = "";
    public string? SourceUrl { get; init; }
    public string? SourceRecordId { get; init; }
    public string Summary { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Prediction Outcome
// ---------------------------------------------------------------------------

public record PredictionOutcome
{
    public string Id { get; init; } = "";
    public string PredictionId { get; init; } = "";
    public DateTimeOffset EvaluationTime { get; init; }
    public double? StartPrice { get; init; }
    public double? ClosePrice { get; init; }
    public double? HighAfterPrediction { get; init; }
    public double? LowAfterPrediction { get; init; }
    public double? PercentMove { get; init; }
    public bool? DirectionCorrect { get; init; }
    public double? PredictedPrice { get; init; }
    public double? PredictedMovePercent { get; init; }
    public double? ProjectedPriceLow { get; init; }
    public double? ProjectedPriceHigh { get; init; }
    public double? PriceAccuracyPercent { get; init; }
    public double? PricePredictionErrorPercent { get; init; }
    public bool? WasInProjectedZone { get; init; }
    public bool? TargetHit { get; init; }
    public bool? StopHit { get; init; }
    public bool? InvalidationHit { get; init; }
    public double? MaxFavorablePercent { get; init; }
    public double? MaxAdversePercent { get; init; }
    public double? OutcomeScore { get; init; }
    public string? OutcomeSummary { get; init; }
    public string? Lesson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Signal Performance
// ---------------------------------------------------------------------------

public record ResearchSignalPerformance
{
    public string Id { get; init; } = "";
    public string SignalName { get; init; } = "";
    public string SignalType { get; init; } = "";
    public string Direction { get; init; } = "all"; // "bullish", "bearish", or "all"
    public int TotalPredictions { get; init; }
    public int CorrectPredictions { get; init; }
    public double Accuracy { get; init; }
    public double AverageOutcomeScore { get; init; }
    public DateTimeOffset LastUpdatedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Scoring Weight
// ---------------------------------------------------------------------------

public record ScoringWeight
{
    public string Id { get; init; } = "";
    public string SignalName { get; init; } = "";
    public double Weight { get; init; }
    public string Reason { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Learning Insight
// ---------------------------------------------------------------------------

public record LearningInsight
{
    public string Id { get; init; } = "";
    public string InsightType { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string ActionRecommendation { get; init; } = "";
    public double Confidence { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Aggregate stats (no row data — just counts)
// ---------------------------------------------------------------------------

public record PredictionStatsAggregate
{
    public int TotalPredictions { get; init; }
    public int EvaluatedPredictions { get; init; }
    public int CorrectPredictions { get; init; }
    public int IncorrectPredictions { get; init; }
    public int InconclusivePredictions { get; init; }
    public int PendingPredictions { get; init; }
    public double? AccuracyPercent { get; init; }
}

public record CategoryStatsAggregate
{
    public PredictionCategory Category { get; init; }
    public int Total { get; init; }
    public int Evaluated { get; init; }
    public int Correct { get; init; }
    public int Incorrect { get; init; }
    public int Pending { get; init; }
    public double? AccuracyPercent { get; init; }
}

public record ScanResultStats
{
    public int Total { get; init; }
    public int NeutralNoEdge { get; init; }
    public int NeutralRangeBound { get; init; }
    public int NeutralHighVolatility { get; init; }
    public int WatchOnly { get; init; }
    public int Rejected { get; init; }
    public int Unavailable { get; init; }
    public int Legacy { get; init; }
}

public record PaperOptionStatsAggregate
{
    public int Total { get; init; }
    public int Evaluated { get; init; }
    public int Profitable { get; init; }
    public int Unprofitable { get; init; }
    public int Open { get; init; }
    public double? WinRatePercent { get; init; }
    public double? AvgPnlPercent { get; init; }
}

public record PredictionWithOutcome
{
    public PredictionCandidate Prediction { get; init; } = null!;
    public PredictionOutcome? Outcome { get; init; }
}

// DefaultScanUniverse removed — tickers are now discovered dynamically from news/earnings.

// ---------------------------------------------------------------------------
// Job request/response DTOs
// ---------------------------------------------------------------------------

public record JobTriggerRequest
{
    public string Trigger { get; init; } = "manual";
    public string JobName { get; init; } = "";
    public DateTimeOffset? ScheduledAt { get; init; }
}

public record MorningScanResult
{
    public string? RunId { get; init; }
    public int PredictionsGenerated { get; init; }
    public string Report { get; init; } = "";
    public List<string> Errors { get; init; } = [];
}

public record EndOfDayReviewResult
{
    public string? RunId { get; init; }
    public int PredictionsEvaluated { get; init; }
    public string Report { get; init; } = "";
    public List<string> Errors { get; init; } = [];
}

public record LearningUpdateResult
{
    public string? RunId { get; init; }
    public int InsightsGenerated { get; init; }
    public int WeightsAdjusted { get; init; }
    public string Report { get; init; } = "";
    public List<string> Errors { get; init; } = [];
}
