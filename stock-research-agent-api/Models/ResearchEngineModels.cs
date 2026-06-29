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

// ---------------------------------------------------------------------------
// Prediction Candidate
// ---------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PredictionType { bullish, bearish, neutral, watch_only }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PredictionAssetType { stock, option_watch_candidate }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PredictionStatus { open, evaluated, expired }

/// <summary>
/// Valid time windows: "intraday", "1_day", "3_day", "1_week".
/// Stored as a plain string because C# enum members cannot start with a digit.
/// </summary>
public static class PredictionTimeWindows
{
    public const string Intraday = "intraday";
    public const string OneDay = "1_day";
    public const string ThreeDay = "3_day";
    public const string OneWeek = "1_week";
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
    public string BullishCase { get; init; } = "";
    public string BearishCase { get; init; } = "";
    public string PredictionReason { get; init; } = "";
    public string InvalidationRule { get; init; } = "";
    public List<string> DataSourcesUsed { get; init; } = [];
    public List<string> MissingDataWarnings { get; init; } = [];
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
    public bool? InvalidationHit { get; init; }
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
