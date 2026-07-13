using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

/// <summary>
/// Type of evidence that supports or weakens a research thesis.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceType
{
    /// <summary>News articles, press mentions, media coverage.</summary>
    News,
    /// <summary>Technical indicator signals (RSI, MACD, breakouts).</summary>
    Technical,
    /// <summary>Congressional or institutional trading activity.</summary>
    Congress,
    /// <summary>SEC filings (10-K, 10-Q, 8-K, S-1, insider forms).</summary>
    SEC,
    /// <summary>Insights from the adaptive learning system.</summary>
    Learning,
    /// <summary>Market regime context (bull, bear, volatile, etc.).</summary>
    MarketRegime,
    /// <summary>Unusual options activity or positioning.</summary>
    Options,
    /// <summary>Abnormal volume patterns.</summary>
    Volume,
    /// <summary>Price or sector momentum signals.</summary>
    Momentum,
    /// <summary>Research pipeline observations (predictions, outcomes).</summary>
    Research,
    /// <summary>Catalyst events (earnings, FDA, product launches).</summary>
    Catalyst,
}

/// <summary>
/// A single piece of evidence attached to a research asset.
///
/// Evidence accumulates over time and decays based on age and type.
/// The Evidence Engine aggregates all active evidence for a ticker
/// to compute InterestScore, thesis, and lifecycle state.
///
/// Evidence is append-only — records are never deleted, only expired.
/// </summary>
public record EvidenceRecord
{
    /// <summary>Unique identifier (UUID, assigned on persist).</summary>
    public string Id { get; init; } = "";

    /// <summary>Stock ticker this evidence applies to.</summary>
    public string Ticker { get; init; } = "";

    /// <summary>When the evidence was recorded.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>What kind of evidence this is.</summary>
    public EvidenceType EvidenceType { get; init; } = EvidenceType.News;

    /// <summary>Which system or provider produced this evidence.
    /// Examples: "finnhub-news", "congress-signals", "learning-engine",
    /// "twelvedata-movers", "market-regime-engine".</summary>
    public string Source { get; init; } = "";

    /// <summary>How much this evidence contributes to the interest score.
    /// Positive = strengthens thesis, negative = weakens thesis.
    /// Range: -1.0 to 1.0. Subject to decay over time.</summary>
    public double Weight { get; init; }

    /// <summary>Urgency/significance of this evidence, 1–100.
    /// Unlike Weight (which can be negative), Importance is always positive
    /// and reflects how noteworthy the event is regardless of direction.</summary>
    public int Importance { get; init; }

    /// <summary>When this evidence expires and should no longer contribute
    /// to aggregations. Null = never expires (e.g. SEC filings).</summary>
    public DateTimeOffset? Expiration { get; init; }

    /// <summary>Human-readable summary of what this evidence represents.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Optional link to the DiscoveryEvent that produced this evidence.
    /// Null if the evidence came from a non-discovery source (e.g. learning engine).</summary>
    public string? RelatedEventId { get; init; }

    /// <summary>When this record was persisted.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Aggregated evidence snapshot for a single ticker.
/// Computed by <see cref="Services.Evidence.IEvidenceAggregator"/>.
/// </summary>
public record EvidenceSnapshot
{
    /// <summary>Ticker this snapshot covers.</summary>
    public string Ticker { get; init; } = "";

    /// <summary>Composite interest score derived from all active evidence.
    /// Range: 0–100. Higher = more compelling research target.</summary>
    public int InterestScore { get; init; }

    /// <summary>Total number of active (non-expired) evidence records.</summary>
    public int EvidenceCount { get; init; }

    /// <summary>Total number of evidence records including expired.</summary>
    public int TotalEvidenceCount { get; init; }

    /// <summary>Breakdown of evidence count by type.</summary>
    public Dictionary<EvidenceType, int> CountByType { get; init; } = new();

    /// <summary>Sum of weights by type (after decay).</summary>
    public Dictionary<EvidenceType, double> WeightByType { get; init; } = new();

    /// <summary>Ordered timeline of evidence — most recent first.</summary>
    public List<EvidenceRecord> Timeline { get; init; } = [];

    /// <summary>Auto-generated thesis based on evidence pattern.</summary>
    public string CurrentThesis { get; init; } = "";

    /// <summary>When the most recent evidence was recorded.</summary>
    public DateTimeOffset? LastEvidenceAt { get; init; }

    /// <summary>When this snapshot was computed.</summary>
    public DateTimeOffset ComputedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Configuration for how a specific evidence type decays over time.
/// Used by <see cref="Services.Evidence.IEvidenceDecayStrategy"/>.
/// </summary>
public record EvidenceDecayConfig
{
    /// <summary>Which evidence type this config applies to.</summary>
    public EvidenceType EvidenceType { get; init; }

    /// <summary>Default time-to-live in days before evidence expires.
    /// Null = evidence never expires by default.</summary>
    public int? DefaultTtlDays { get; init; }

    /// <summary>Half-life in days — after this many days, weight is halved.
    /// Null = no decay (weight stays constant until expiration).</summary>
    public int? HalfLifeDays { get; init; }

    /// <summary>Minimum weight after decay. Below this, evidence is effectively dead.
    /// Default: 0.01.</summary>
    public double MinWeight { get; init; } = 0.01;
}
