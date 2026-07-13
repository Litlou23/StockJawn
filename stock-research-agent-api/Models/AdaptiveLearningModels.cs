using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

/// <summary>
/// A single condition axis for conditional performance measurement.
/// Conditions are composable — a query can filter on multiple axes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LearningConditionType
{
    MarketRegime,
    Sector,
    MarketCap,
    Volatility,
    HoldingWindow,
    TradeGrade,
    PatternType,
    Direction,
    ConfidenceBand,
    RiskBand,
}

/// <summary>
/// One condition axis + value pair used to slice performance data.
/// Example: { Type = MarketRegime, Value = "BullTrend" }
/// </summary>
public record LearningCondition
{
    public LearningConditionType Type { get; init; }
    public string Value { get; init; } = "";
}

/// <summary>
/// Shared confidence formula for sample-size-based statistical confidence.
/// Used by AdaptiveLearningEngine, KnowledgeBase, and StrategyDiscoveryEngine.
/// Single source of truth — do not duplicate this formula elsewhere.
/// </summary>
public static class StatisticalConfidence
{
    /// <summary>
    /// Compute sample-size-based confidence: sqrt(n) / 10, clamped 0–1.
    /// Returns a value between 0.0 (no data) and 1.0 (100+ observations).
    /// </summary>
    public static double FromSampleSize(int sampleSize)
        => Math.Round(Math.Min(1.0, Math.Sqrt(Math.Max(0, sampleSize)) / 10.0), 4);
}

/// <summary>
/// Performance of a signal under a specific set of conditions.
/// This is the core learning unit for <see cref="Services.AdaptiveLearning.IAdaptiveLearningEngine"/>.
/// </summary>
public record ConditionalSignalPerformance
{
    /// <summary>Signal bucket name (e.g. "Trend", "Momentum", "Volume").</summary>
    public string SignalName { get; init; } = "";
    /// <summary>Conditions under which this performance was measured.</summary>
    public List<LearningCondition> Conditions { get; init; } = [];
    public int SampleSize { get; init; }
    public double WinRate { get; init; }
    public double AverageReturn { get; init; }
    public double MedianReturn { get; init; }
    public double AverageHoldingDays { get; init; }
    /// <summary>Statistical significance — higher = more reliable.</summary>
    public double Confidence { get; init; }
    /// <summary>Last time this statistic was updated.</summary>
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A completed prediction with its outcome, regime, and metadata.
/// Input to <see cref="Services.AdaptiveLearning.IAdaptiveLearningEngine.RecordOutcome"/>.
/// </summary>
public record AdaptiveLearningObservation
{
    public required PredictionCandidate Prediction { get; init; }
    public required PredictionOutcome Outcome { get; init; }
    public MarketRegimeResult? RegimeAtPrediction { get; init; }
    public TradeGrade TradeGrade { get; init; } = TradeGrade.Unspecified;
    public string? Sector { get; init; }
    public string? MarketCap { get; init; }
}

/// <summary>
/// Query for retrieving conditional performance data.
/// All fields optional — omit a condition to not filter on that axis.
/// </summary>
public record ConditionalPerformanceQuery
{
    public string? SignalName { get; init; }
    public List<LearningCondition> Conditions { get; init; } = [];
    public int MinSampleSize { get; init; } = 10;
}

/// <summary>
/// Aggregated result of a conditional performance query.
/// </summary>
public record ConditionalPerformanceResult
{
    public List<ConditionalSignalPerformance> Performances { get; init; } = [];
    /// <summary>Best-performing condition combination for the queried signal.</summary>
    public ConditionalSignalPerformance? BestCondition { get; init; }
    /// <summary>Worst-performing condition combination for the queried signal.</summary>
    public ConditionalSignalPerformance? WorstCondition { get; init; }
    public string Summary { get; init; } = "";
}
