using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

/// <summary>
/// A single piece of reusable market knowledge that evolves over time.
/// Examples:
///   "Momentum performs best during Bull Trends."
///   "Breakouts fail frequently during High Volatility."
///   "Low volume reversals have poor expectancy."
///
/// Knowledge entries are stored separately from predictions and
/// are updated as new evidence accumulates.
/// </summary>
public record KnowledgeEntry
{
    public string EntryId { get; init; } = "";
    /// <summary>The insight in natural language.</summary>
    public string Insight { get; init; } = "";
    /// <summary>Category for grouping/filtering.</summary>
    public KnowledgeCategory Category { get; init; }
    /// <summary>Conditions under which this knowledge applies.</summary>
    public List<LearningCondition> ApplicableConditions { get; init; } = [];
    /// <summary>Signal or feature this knowledge relates to.</summary>
    public string? RelatedSignal { get; init; }
    /// <summary>How many observations support this insight.</summary>
    public int SupportingObservations { get; init; }
    /// <summary>Measured win rate under these conditions.</summary>
    public double WinRate { get; init; }
    /// <summary>Average return when conditions are met.</summary>
    public double AverageReturn { get; init; }
    /// <summary>Confidence in this knowledge (0.0–1.0).</summary>
    public double Confidence { get; init; }
    /// <summary>When this entry was first discovered.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>When this entry was last reinforced or revised.</summary>
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>How many times this entry has been reinforced by new data.</summary>
    public int ReinforcementCount { get; init; }
    /// <summary>Set to true when superseded by newer evidence.</summary>
    public bool IsDeprecated { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeCategory
{
    SignalPerformance,
    RegimeBehavior,
    SectorDynamics,
    RiskPattern,
    StrategyRule,
    GeneralObservation,
}

/// <summary>
/// Query for retrieving relevant knowledge entries.
/// </summary>
public record KnowledgeBaseQuery
{
    public KnowledgeCategory? Category { get; init; }
    public string? RelatedSignal { get; init; }
    public List<LearningCondition> Conditions { get; init; } = [];
    public double MinConfidence { get; init; } = 0.5;
    public bool IncludeDeprecated { get; init; } = false;
    public int Limit { get; init; } = 20;
}
