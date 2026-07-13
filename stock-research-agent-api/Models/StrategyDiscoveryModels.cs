using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

/// <summary>
/// Confidence level for a discovered strategy based on sample size
/// and statistical stability.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StrategyConfidence
{
    Insufficient,
    Low,
    Medium,
    High,
    VeryHigh,
}

/// <summary>
/// One observed outcome for a specific combination of conditions.
/// </summary>
public record PatternObservation
{
    public string PredictionId { get; init; } = "";
    public string? Ticker { get; init; }
    public DateTimeOffset Date { get; init; }
    public bool IsWin { get; init; }
    public double ReturnPercent { get; init; }
    public int HoldingDays { get; init; }
}

/// <summary>
/// A specific combination of features, regimes, evidence, and conditions
/// that has been observed in historical data.
/// </summary>
public record StrategyPattern
{
    /// <summary>Deterministic hash of the sorted condition set.</summary>
    public string PatternId { get; init; } = "";
    /// <summary>The conditions that define this pattern.</summary>
    public List<LearningCondition> Conditions { get; init; } = [];
    /// <summary>Human-readable label (auto-generated from conditions).</summary>
    public string Label { get; init; } = "";
}

/// <summary>
/// A pattern under evaluation — has observations but may not yet
/// meet significance thresholds.
/// </summary>
public record StrategyCandidate
{
    public StrategyPattern Pattern { get; init; } = new();
    public List<PatternObservation> Observations { get; init; } = [];
    public int SampleSize { get; init; }
    public double WinRate { get; init; }
    public double AverageReturn { get; init; }
    public double MedianReturn { get; init; }
    public StrategyConfidence Confidence { get; init; } = StrategyConfidence.Insufficient;
}

/// <summary>
/// A strategy candidate that has passed significance and performance
/// thresholds. Ready to inform decision-making.
/// </summary>
public record DiscoveredStrategy
{
    public string StrategyId { get; init; } = "";
    public StrategyPattern Pattern { get; init; } = new();
    public int SampleSize { get; init; }
    public double WinRate { get; init; }
    public double AverageReturn { get; init; }
    public double MedianReturn { get; init; }
    public StrategyConfidence Confidence { get; init; }
    /// <summary>Auto-generated summary (e.g. "Momentum + Bull Trend: 81% win rate over 127 trades").</summary>
    public string Summary { get; init; } = "";
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Output of <see cref="Services.StrategyDiscovery.IStrategyDiscoveryEngine.Discover"/>.
/// </summary>
public record StrategyDiscoveryResult
{
    public List<DiscoveredStrategy> Strategies { get; init; } = [];
    public int CandidatesEvaluated { get; init; }
    public int StrategiesDiscovered { get; init; }
    public string Summary { get; init; } = "";
}
