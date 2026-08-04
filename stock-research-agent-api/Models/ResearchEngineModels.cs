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
    public FundamentalsContext? Fundamentals { get; init; }
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
    public string? Summary { get; init; }
    public string SourceName { get; init; } = "";
    public string Url { get; init; } = "";
    public string PublishedAt { get; init; } = "";
    public string? CatalystType { get; init; }
    public string? Sentiment { get; init; }
    public double ImportanceScore { get; init; }

    // ── LLM-classified catalyst quality (set by NewsCatalystClassifier) ──
    /// <summary>
    /// fundamental_catalyst | technical_momentum | noise | null (unclassified).
    /// fundamental_catalyst = real event driving the move (earnings, FDA, merger, etc.)
    /// technical_momentum = price action only, no fundamental driver
    /// noise = irrelevant or low-quality article
    /// </summary>
    public string? CatalystQuality { get; set; }
    /// <summary>LLM confidence in the classification (0-100). Null if unclassified.</summary>
    public int? CatalystConfidence { get; set; }
    /// <summary>Brief LLM reasoning for the classification.</summary>
    public string? CatalystReasoning { get; set; }
}

public record MarketSnapshotAvailability
{
    public bool MarketDataAvailable { get; init; }
    public bool NewsAvailable { get; init; }
    public bool FundamentalsAvailable { get; init; }
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

    // Moving averages (exponential)
    public double? Ema12 { get; init; }
    public double? Ema26 { get; init; }
    public double? Ema50 { get; init; }

    // Momentum
    public double? Roc5 { get; init; }
    public double? Roc10 { get; init; }
    public double? Rsi14 { get; init; }
    public double? StochasticCloseLocation { get; init; }

    // MACD
    public double? MacdLine { get; init; }
    public double? MacdSignal { get; init; }
    public double? MacdHistogram { get; init; }
    public bool? MacdBullishCrossover { get; init; }

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

    /// <summary>SPY price / EMA(20). &gt;1.0 = above EMA (bullish), &lt;1.0 = below EMA (bearish).</summary>
    public double? SpyEmaRatio { get; init; }
    /// <summary>Multi-day SPY trend: "bullish" (above EMA20), "bearish" (below), or "neutral" (within 0.3%).</summary>
    public string? SpyMultiDayTrend { get; init; }

    /// <summary>Sector ETF ticker for this stock's sector (e.g., "XLK" for Technology). Null if sector unknown.</summary>
    public string? SectorEtf { get; init; }
    /// <summary>Sector ETF price / EMA26 ratio. &gt;1.0 = above EMA (sector bullish). Null if unavailable.</summary>
    public double? SectorEtfEmaRatio { get; init; }
    /// <summary>Sector ETF multi-day trend: "bullish", "bearish", or "neutral" based on EMA ratio.</summary>
    public string? SectorEtfTrend { get; init; }
}

/// <summary>
/// Fundamental data for a ticker from TwelveData /profile and /statistics.
/// Enhances prediction quality by factoring in financial health, valuation,
/// and upcoming catalysts.
/// </summary>
public record FundamentalsContext
{
    // Company profile
    public string? Sector { get; init; }
    public string? Industry { get; init; }
    public string? Exchange { get; init; }
    public long? MarketCap { get; init; }
    public int? Employees { get; init; }

    // Valuation
    public double? PeRatio { get; init; }
    public double? ForwardPe { get; init; }
    public double? PbRatio { get; init; }
    public double? PsRatio { get; init; }
    public double? EvToEbitda { get; init; }

    // Dividends
    public double? DividendYield { get; init; }
    public double? PayoutRatio { get; init; }

    // Financial health
    public double? ProfitMargin { get; init; }
    public double? OperatingMargin { get; init; }
    public double? ReturnOnEquity { get; init; }
    public double? DebtToEquity { get; init; }
    public double? CurrentRatio { get; init; }

    // Growth
    public double? RevenueGrowthYoy { get; init; }
    public double? EarningsGrowthYoy { get; init; }
    public double? QuarterlyRevenueGrowth { get; init; }
    public double? QuarterlyEarningsGrowth { get; init; }

    // Short interest
    public double? ShortPercentOfFloat { get; init; }

    // Beta
    public double? Beta { get; init; }

    // 52-week range
    public double? FiftyTwoWeekHigh { get; init; }
    public double? FiftyTwoWeekLow { get; init; }

    // Metadata
    public List<string> DataPoints { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
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

/// <summary>
/// Wrapper for score_debug_json which stores {"Breakdown": {...}}.
/// Use ScoringBreakdownEnvelope.Parse() to safely deserialize.
/// </summary>
public record ScoringBreakdownEnvelope
{
    public ScoringBreakdown? Breakdown { get; init; }

    public static ScoringBreakdown? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        // Try envelope format first: {"Breakdown": {...}}
        try
        {
            var envelope = System.Text.Json.JsonSerializer.Deserialize<ScoringBreakdownEnvelope>(json, opts);
            if (envelope?.Breakdown is not null) return envelope.Breakdown;
        }
        catch { /* fall through */ }
        // Fallback: direct ScoringBreakdown
        try { return System.Text.Json.JsonSerializer.Deserialize<ScoringBreakdown>(json, opts); }
        catch { return null; }
    }
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
    public double OppositionPenalty { get; init; }
    public double RegimePenalty { get; init; } = 1.0;
    public double LiquidityPenalty { get; init; } = 1.0;
    public double DecisionMargin { get; init; }
    public bool ClearDirection { get; init; }
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
    /// <summary>
    /// Direction-independent catalyst intensity score (0-25).
    /// Measures "how likely is rapid repricing?" based on news volume,
    /// importance, recency, and catalyst type — regardless of bull/bear direction.
    /// Used by the velocity formula to determine time windows.
    /// </summary>
    public double CatalystStrength { get; init; }
    public double LearningBullish { get; init; }
    public double LearningBearish { get; init; }
    public double ResearchSignalScore { get; init; }
    public double ResearchSignalBullish { get; init; }
    public double ResearchSignalBearish { get; init; }
    public int ResearchSignalCount { get; init; }
    public double RiskPenalty { get; init; }
    public List<string> IndicatorsUsed { get; init; } = [];
    public List<string> IndicatorsSkipped { get; init; } = [];
    public string? ConfidenceCap { get; init; }
    public List<string> ActionabilityReasons { get; init; } = [];

    // ── Research Universe integration fields ──────────────────────
    /// <summary>Interest score from Research Universe (0-100). 0 when no ResearchAsset.</summary>
    public int ResearchUniverseInterestScore { get; init; }
    /// <summary>Evidence count from Research Universe. 0 when no ResearchAsset.</summary>
    public int ResearchUniverseEvidenceCount { get; init; }
    /// <summary>Research lifecycle state. "Discovered" when no ResearchAsset.</summary>
    public string ResearchUniverseState { get; init; } = "Discovered";
    /// <summary>Whether a real ResearchAsset was available for this prediction.</summary>
    public bool HasResearchAsset { get; init; }
    /// <summary>Historical volatility from profile (annualized %). Null if no profile.</summary>
    public double? HistoricalVolatility { get; init; }
    /// <summary>Historical ATR% from profile. Null if no profile.</summary>
    public double? HistoricalAtrPercent { get; init; }
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
public enum PredictionStatus { open, evaluated, expired, superseded }

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

    /// <summary>Neutral types that the NeutralOutcomeEvaluator should evaluate.</summary>
    private static readonly HashSet<PredictionType> NeutralEvaluableTypes =
    [
        PredictionType.neutral_high_volatility,
        PredictionType.neutral_no_edge,
        PredictionType.neutral_range_bound,
        PredictionType.neutral,
        PredictionType.watch_only,
    ];

    public static bool IsDirectional(PredictionType type) => DirectionalTypes.Contains(type);

    /// <summary>True if the prediction expects upward price movement.</summary>
    public static bool IsBullish(PredictionType type) => type == PredictionType.bullish;

    /// <summary>True for neutral_* types that need evaluation by NeutralOutcomeEvaluator.</summary>
    public static bool IsNeutralEvaluable(PredictionType type) => NeutralEvaluableTypes.Contains(type);

    /// <summary>True for types that need no evaluation at all (unavailable, rejected).</summary>
    public static bool IsPassThrough(PredictionType type) => !IsDirectional(type) && !IsNeutralEvaluable(type);

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
    /// <summary>
    /// Expected Value = (winProb × potentialGain%) - (lossProb × potentialLoss%).
    /// Positive EV means the trade is worth taking over many repetitions.
    /// Computed from confidence, target price, and stop price.
    /// </summary>
    public double? ExpectedValuePercent { get; init; }
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
    public string? IndicatorsJson { get; init; }
    public string? WeightsSnapshotJson { get; init; }
    public int? ActionabilityScore { get; init; }
    public ActionabilityTier? ActionabilityTier { get; init; }
    public List<string> DowngradeReasons { get; init; } = [];
    public string Status { get; init; } = "open";
    public string? SupersededBy { get; init; }
    public string? SupersessionReason { get; init; }
    public string? ProfileId { get; init; }
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
    /// <summary>"win", "loss", or "pending".</summary>
    public string? Outcome { get; init; }
    public double? ReturnPercent { get; init; }
    public int? HoldingPeriodDays { get; init; }
    // Watch-only / abstention evaluation fields
    public bool? AbstentionCorrect { get; init; }
    public double? MissedAlphaPercent { get; init; }
    public bool? GuardrailJustified { get; init; }
    public string? OriginalDirection { get; init; }
    public List<string>? DowngradeReasonsEvaluated { get; init; }
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
    public int ObservationsCreated { get; init; }
    public int SupersessionRecordsCreated { get; init; }
    public int KnowledgeCasesIndexed { get; init; }
    public int KnowledgePatternsDetected { get; init; }
    public int KnowledgeRulesGenerated { get; init; }
    public string Report { get; init; } = "";
    public string? AiSummary { get; init; }
    public SupersessionAnalytics? RevisionAnalytics { get; init; }
    public List<string> Errors { get; init; } = [];
}

// ---------------------------------------------------------------------------
// Signal Observation (per-bucket per-prediction learning data)
// ---------------------------------------------------------------------------

public record SignalObservation
{
    public string? Id { get; init; }
    public string PredictionId { get; init; } = "";
    public string? OutcomeId { get; init; }
    public string SignalName { get; init; } = "";
    public double BullScore { get; init; }
    public double BearScore { get; init; }
    public string PredictedDirection { get; init; } = "";
    public bool? Correct { get; init; }
    public double RawWeight { get; init; } = 1.0;
    public double EffectiveWeight { get; init; } = 1.0;
    public double WeightedContribution { get; init; }
    public double? ContributionPercent { get; init; }
    public double? ActualReturnPercent { get; init; }
    public double? Confidence { get; init; }
    public double? OutcomeScore { get; init; }
    public string? MarketRegime { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Scoring Weight Override (three-layer weight system)
// ---------------------------------------------------------------------------

public record ScoringWeightOverride
{
    public string? Id { get; init; }
    public string SignalName { get; init; } = "";
    public double BaseWeight { get; init; } = 1.0;
    public double AdjustmentPercent { get; init; }
    public double EffectiveWeight { get; init; } = 1.0;
    public double Confidence { get; init; }
    public int SampleSize { get; init; }
    public string Status { get; init; } = "active";
    public string? Reason { get; init; }
    public DateTimeOffset LastUpdated { get; init; }
}

// ---------------------------------------------------------------------------
// Enhanced Learning Report
// ---------------------------------------------------------------------------

public record EnhancedLearningReport
{
    public string? Id { get; init; }
    public DateTimeOffset ReportDate { get; init; }
    public int EvaluationWindowDays { get; init; } = 30;
    public int PredictionCount { get; init; }
    public double? OverallAccuracy { get; init; }
    public double? BullAccuracy { get; init; }
    public double? BearAccuracy { get; init; }
    public string? MarketRegime { get; init; }
    public List<SignalPerformanceSummary> TopSignals { get; init; } = [];
    public List<SignalPerformanceSummary> WeakSignals { get; init; } = [];
    public List<WeightChangeSummary> WeightChanges { get; init; } = [];
    public ConfidenceAnalysis? ConfidenceCalibration { get; init; }
    public string? AiSummary { get; init; }
}

public record SignalPerformanceSummary
{
    public string SignalName { get; init; } = "";
    public double Accuracy { get; init; }
    public int SampleSize { get; init; }
    public double? BullAccuracy { get; init; }
    public double? BearAccuracy { get; init; }
    public double AverageContribution { get; init; }
}

public record WeightChangeSummary
{
    public string SignalName { get; init; } = "";
    public double PreviousWeight { get; init; }
    public double NewWeight { get; init; }
    public double ChangePercent { get; init; }
    public string? Reason { get; init; }
}

public record ConfidenceAnalysis
{
    public List<ConfidenceBucket> Buckets { get; init; } = [];
    public bool IsOverconfident { get; init; }
    public string? Summary { get; init; }
}

public record ConfidenceBucket
{
    public string Range { get; init; } = "";
    public int Count { get; init; }
    public double ActualAccuracy { get; init; }
    public double ExpectedAccuracy { get; init; }
    public double CalibrationError { get; init; }
}

// ---------------------------------------------------------------------------
// Supersession Learning — tracks prediction revisions for pattern analysis
// ---------------------------------------------------------------------------

/// <summary>
/// A single supersession event: one prediction replaced another.
/// Captures the before/after state for learning.
/// </summary>
public record SupersessionLearningRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string OriginalPredictionId { get; init; } = "";
    public string ReplacementPredictionId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public string TimeWindow { get; init; } = "";

    // Transition type (e.g., "neutral→bullish", "bearish→bullish")
    public string OriginalType { get; init; } = "";
    public string ReplacementType { get; init; } = "";
    public string TransitionLabel { get; init; } = "";

    // Timing
    public double HoursBetween { get; init; }
    public DateTimeOffset OriginalCreatedAt { get; init; }
    public DateTimeOffset ReplacementCreatedAt { get; init; }

    // Score deltas
    public int ConfidenceDelta { get; init; }
    public int RiskDelta { get; init; }
    public double BullScoreDelta { get; init; }
    public double BearScoreDelta { get; init; }

    // Context
    public string? OriginalMarketRegime { get; init; }
    public string? ReplacementMarketRegime { get; init; }
    public bool RegimeChanged { get; init; }
    public double? OriginalCatalystStrength { get; init; }
    public double? ReplacementCatalystStrength { get; init; }

    // Outcome (populated after the replacement is evaluated)
    public bool? ReplacementCorrect { get; init; }
    public double? ReplacementReturnPercent { get; init; }
    public double? ReplacementOutcomeScore { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Aggregated analytics across all supersession events.
/// </summary>
public record SupersessionAnalytics
{
    public int TotalSupersessions { get; init; }
    public Dictionary<string, TransitionStats> ByTransition { get; init; } = new();
    public double OverallImprovementRate { get; init; }
    public string Summary { get; init; } = "";

    // Ranked lists
    public List<RankedTransition> MostCommonTransitions { get; init; } = [];
    public List<RankedTransition> MostSuccessfulTransitions { get; init; } = [];
    public List<RankedTransition> LeastSuccessfulTransitions { get; init; } = [];

    // Context breakdowns
    public Dictionary<string, int> ByMarketRegime { get; init; } = new();
    public Dictionary<string, RegimeTransitionStats> RegimeBreakdown { get; init; } = new();
    public Dictionary<string, NeutralTypeStats> NeutralTypeBreakdown { get; init; } = new();

    // Timing
    public double AvgHoursBeforeSupersession { get; init; }
    public double MedianHoursBeforeSupersession { get; init; }
}

/// <summary>
/// Performance stats for a specific transition type (e.g., "neutral→bullish").
/// </summary>
public record TransitionStats
{
    public int Count { get; init; }
    public double AvgHoursBetween { get; init; }
    public double AvgConfidenceDelta { get; init; }
    public double AvgRiskDelta { get; init; }
    public int EvaluatedCount { get; init; }
    public int CorrectCount { get; init; }
    public double Accuracy { get; init; }
    public double AvgReturnPercent { get; init; }
    public bool IsImprovement { get; init; }
    public double AvgBullScoreDelta { get; init; }
    public double AvgBearScoreDelta { get; init; }
}

public record RankedTransition
{
    public string TransitionLabel { get; init; } = "";
    public int Count { get; init; }
    public double Accuracy { get; init; }
    public int EvaluatedCount { get; init; }
}

public record RegimeTransitionStats
{
    public int Count { get; init; }
    public int EvaluatedCount { get; init; }
    public int CorrectCount { get; init; }
    public double Accuracy { get; init; }
    public double AvgHoursBetween { get; init; }
    public bool RegimeChangedDuringTransition { get; init; }
}

public record NeutralTypeStats
{
    public string NeutralType { get; init; } = "";
    public int TimesSuperseded { get; init; }
    public double AvgHoursBeforeSupersession { get; init; }
    public Dictionary<string, int> SupersededTo { get; init; } = new();
    public int EvaluatedCount { get; init; }
    public int CorrectCount { get; init; }
    public double ReplacementAccuracy { get; init; }
}

// ---------------------------------------------------------------------------
// Volatility Opportunity Engine
// ---------------------------------------------------------------------------

/// <summary>Per-stock volatility regime classification.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StockVolatilityRegime
{
    /// <summary>ATR pctile &lt; 20 AND bandwidth pctile &lt; 20 — compressed range.</summary>
    Squeeze,
    /// <summary>Both percentiles between 20–80 — typical volatility.</summary>
    Normal,
    /// <summary>Either percentile &gt; 80 — volatility increasing.</summary>
    Expanding,
    /// <summary>ATR pctile &gt; 90 — unusually wide moves.</summary>
    Extreme,
    /// <summary>Insufficient history to classify.</summary>
    Unknown,
}

/// <summary>Gap size classification.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapType
{
    NoGap,        // |gap| < 1%
    Small,        // 1–3%
    Significant,  // 3–5%
    Large,        // 5–10%
    Extreme,      // > 10%
}

/// <summary>Gap direction.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GapDirection
{
    None,
    Up,
    Down,
}

/// <summary>
/// Volatility opportunity type. Placeholder for Phase 2 classification.
/// Phase 1 always sets <see cref="OpportunityType.None"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpportunityType
{
    None,
    DipAfterPanic,
    SqueezeBreakout,
    ExhaustionReversal,
    MomentumContinuation,
    FailedBounce,
    VolatilityTrap,
    MeanReversion,
}

/// <summary>
/// Structured output of <see cref="Services.ResearchEngine.VolatilityOpportunityEngine"/>.
/// Contains all computed volatility features for a single ticker at a point in time.
/// Designed to be consumed by the VolatilityEvaluator in Phase 2.
/// </summary>
public record VolatilityOpportunityAssessment
{
    public string Ticker { get; init; } = "";
    public DateTimeOffset AssessedAt { get; init; } = DateTimeOffset.UtcNow;

    // ── Volatility context ──────────────────────────────────────
    /// <summary>Current ATR14 ranked against trailing 60-day ATR history (0–100). Null if insufficient history.</summary>
    public double? AtrPercentile { get; init; }
    /// <summary>ATR14 rate of change over 5 days. Positive = expanding, negative = contracting.</summary>
    public double? AtrAcceleration { get; init; }
    /// <summary>Current Bollinger Bandwidth ranked against trailing 60-day history (0–100).</summary>
    public double? BandwidthPercentile { get; init; }
    /// <summary>Linear regression slope of last 5 bandwidth values. Positive = widening.</summary>
    public double? BandwidthDirection { get; init; }
    /// <summary>Per-stock volatility regime derived from ATR and bandwidth percentiles.</summary>
    public StockVolatilityRegime StockVolRegime { get; init; } = StockVolatilityRegime.Unknown;

    // ── Gap features ────────────────────────────────────────────
    /// <summary>(Today open − yesterday close) / yesterday close × 100.</summary>
    public double? GapPercent { get; init; }
    public GapDirection GapDir { get; init; } = GapDirection.None;
    public GapType GapClassification { get; init; } = GapType.NoGap;
    /// <summary>True when gap is accompanied by volume &gt; 1.5× average.</summary>
    public bool GapWithVolume { get; init; }

    // ── Support / resistance ────────────────────────────────────
    /// <summary>% distance from DonchianLow20 (positive = above support).</summary>
    public double? DistanceFromSupport { get; init; }
    /// <summary>% distance from DonchianHigh20 (negative = below resistance).</summary>
    public double? DistanceFromResistance { get; init; }

    // ── Volume ──────────────────────────────────────────────────
    /// <summary>Average VolumeRatio over the last 3 bars.</summary>
    public double? VolumeRatioPersistence { get; init; }

    // ── Catalyst ────────────────────────────────────────────────
    /// <summary>Hours since the most recent catalyst event. Null if no catalysts.</summary>
    public double? CatalystAgeHours { get; init; }

    // ── Classification (Phase 2 placeholders) ───────────────────
    /// <summary>Opportunity type. Always <see cref="OpportunityType.None"/> in Phase 1.</summary>
    public OpportunityType Opportunity { get; init; } = OpportunityType.None;
    /// <summary>Composite opportunity score (0–100). Placeholder — always 0 in Phase 1.</summary>
    public double OpportunityScore { get; init; }
    /// <summary>Risk modifier from volatility context. Placeholder — always 0 in Phase 1.</summary>
    public double RiskModifier { get; init; }

    // ── Metadata ────────────────────────────────────────────────
    /// <summary>Features that could not be computed due to insufficient data.</summary>
    public List<string> FeaturesSkipped { get; init; } = [];
    /// <summary>Number of historical bars used for percentile calculations.</summary>
    public int BarsUsedForHistory { get; init; }
}

// ---------------------------------------------------------------------------
// Volatility Learning Record — one per evaluated prediction
// ---------------------------------------------------------------------------

public record VolatilityLearningRecord
{
    public string PredictionId { get; init; } = "";
    public string RunId { get; init; } = "";
    public string Ticker { get; init; } = "";

    // Opportunity context (snapshot from VOE assessment)
    public string? OpportunityType { get; init; }
    public double? OpportunityScore { get; init; }
    public string? StockVolatilityRegime { get; init; }
    public double? AtrPercentile { get; init; }
    public double? AtrAcceleration { get; init; }
    public double? BandwidthPercentile { get; init; }
    public string? GapType { get; init; }
    public double? GapPercent { get; init; }
    public double? CatalystAgeHours { get; init; }

    // Prediction context
    public int Confidence { get; init; }
    public int Risk { get; init; }
    public string? PredictionType { get; init; }
    public string? TimeWindow { get; init; }

    // Movement outcome
    public bool? DirectionCorrect { get; init; }
    public double? OutcomeScore { get; init; }
    public double? HoldingPeriodHours { get; init; }
    public double? MaxFavorableExcursion { get; init; }
    public double? MaxAdverseExcursion { get; init; }

    // Time-to-move (in trading days)
    public int? TimeTo3Pct { get; init; }
    public int? TimeTo5Pct { get; init; }
    public int? TimeToTarget { get; init; }

    // Recovery
    public double? RecoverySpeed { get; init; }
    public string? BounceQualityRealized { get; init; }

    // Opportunity success
    public bool? OpportunitySuccess { get; init; }
    public string? OpportunitySuccessReason { get; init; }

    // Profile linkage
    public string? ProfileId { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BounceQuality
{
    None,
    Poor,
    Fair,
    Good,
    Excellent,
}
