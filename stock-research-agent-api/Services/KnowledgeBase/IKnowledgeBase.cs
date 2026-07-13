using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.KnowledgeBase;

/// <summary>
/// Structured, evolving store of reusable market knowledge.
///
/// Knowledge entries are derived from completed predictions and
/// strategy discovery — they capture durable observations like
/// "Momentum performs best during Bull Trends" that transcend
/// individual trades.
///
/// Separate from predictions and cases: knowledge evolves over time
/// as new evidence confirms or weakens existing entries.
///
/// Does NOT replace the existing <see cref="Knowledge.IKnowledgeRepository"/>
/// — it sits alongside it as a higher-level abstraction focused on
/// actionable, structured insights rather than raw pattern storage.
/// </summary>
public interface IKnowledgeBase
{
    /// <summary>Record or update a knowledge entry. If an entry with the
    /// same key exists, its confidence and sample size are updated.</summary>
    Task RecordAsync(KnowledgeEntry entry);

    /// <summary>Retrieve entries relevant to the given context.</summary>
    Task<List<KnowledgeEntry>> QueryAsync(KnowledgeBaseQuery query);

    /// <summary>Get all entries, optionally filtered by category.</summary>
    Task<List<KnowledgeEntry>> GetAllAsync(KnowledgeCategory? category = null);

    /// <summary>Get the strongest (highest confidence) entries.</summary>
    Task<List<KnowledgeEntry>> GetStrongestAsync(int limit = 20);

    /// <summary>Summary statistics of the knowledge base.</summary>
    Task<KnowledgeBaseStats> GetStatsAsync();
}

/// <summary>
/// Category of market knowledge.
/// </summary>
public enum KnowledgeCategory
{
    /// <summary>"Momentum performs best during Bull Trends"</summary>
    SignalRegimeInteraction,
    /// <summary>"Breakouts fail frequently during High Volatility"</summary>
    PatternBehavior,
    /// <summary>"Technology leadership increases breakout success"</summary>
    SectorInfluence,
    /// <summary>"Low volume reversals have poor expectancy"</summary>
    SetupQuality,
    /// <summary>"Grade A trades outperform Grade C by 12%"</summary>
    GradeInsight,
    /// <summary>Auto-discovered strategy from StrategyDiscoveryEngine</summary>
    DiscoveredStrategy,
    /// <summary>General observation</summary>
    General,
}

/// <summary>
/// One piece of reusable market knowledge.
/// Designed for structured consumption by future AI features.
/// </summary>
public record KnowledgeEntry
{
    /// <summary>Deterministic key for upsert (e.g. "signal:Momentum|regime:BullTrend").</summary>
    public string Key { get; init; } = "";
    public KnowledgeCategory Category { get; init; } = KnowledgeCategory.General;
    /// <summary>Human-readable statement (e.g. "Momentum performs best during Bull Trends.").</summary>
    public string Statement { get; init; } = "";
    /// <summary>Conditions under which this knowledge applies.</summary>
    public List<LearningCondition> Conditions { get; init; } = [];
    public int SampleSize { get; init; }
    public double WinRate { get; init; }
    public double AverageReturn { get; init; }
    /// <summary>0.0–1.0, increases with sample size and effect magnitude.</summary>
    public double Confidence { get; init; }
    public DateTimeOffset FirstObserved { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Number of times this entry has been confirmed by new evidence.</summary>
    public int ConfirmationCount { get; init; }
}

public record KnowledgeBaseQuery
{
    public string? SignalName { get; init; }
    public MarketRegimeType? Regime { get; init; }
    public string? Sector { get; init; }
    public KnowledgeCategory? Category { get; init; }
    public double MinConfidence { get; init; } = 0.0;
    public int Limit { get; init; } = 25;
}

public record KnowledgeBaseStats
{
    public int TotalEntries { get; init; }
    public int HighConfidenceEntries { get; init; }
    public Dictionary<KnowledgeCategory, int> EntriesByCategory { get; init; } = [];
    public string Summary { get; init; } = "";
}
