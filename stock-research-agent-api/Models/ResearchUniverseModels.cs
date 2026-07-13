using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

/// <summary>
/// The lifecycle state of a research asset as it progresses
/// through the Research Universe pipeline.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResearchState
{
    /// <summary>Initial discovery — ticker surfaced but not yet investigated.</summary>
    Discovered,
    /// <summary>Actively tracking news, price action, and catalysts.</summary>
    Monitoring,
    /// <summary>Accumulating evidence toward a directional thesis.</summary>
    BuildingThesis,
    /// <summary>Sufficient evidence exists to run a full prediction evaluation.</summary>
    ReadyForEvaluation,
    /// <summary>No longer under active investigation.</summary>
    Archived,
}

/// <summary>
/// Administrative status of a research asset.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResearchAssetStatus
{
    Active,
    Inactive,
    Archived,
}

/// <summary>
/// A stock currently under investigation in the Research Universe.
///
/// The Research Universe is a persistent collection of every stock
/// the system is currently researching. Unlike the dynamic watchlist
/// (which only holds stocks ready for prediction), this layer tracks
/// stocks from first discovery through thesis formation to evaluation
/// readiness — ensuring multi-day buildups are never missed.
///
/// ResearchAssets are NOT predictions. They are investigation records
/// that may eventually produce predictions if enough evidence accumulates.
/// </summary>
public record ResearchAsset
{
    /// <summary>Unique identifier (UUID).</summary>
    public string Id { get; init; } = "";

    /// <summary>Stock ticker symbol (e.g. "AAPL").</summary>
    public string Ticker { get; init; } = "";

    /// <summary>When this ticker first entered the Research Universe.</summary>
    public DateTimeOffset DateDiscovered { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>What system or signal source surfaced this ticker.
    /// Examples: "news-scanner", "congress-signal", "sector-momentum",
    /// "filing-alert", "analyst-action", "catalyst-accumulation", "manual".</summary>
    public string DiscoverySource { get; init; } = "";

    /// <summary>Human-readable reason this ticker was added.
    /// Example: "3 bullish analyst upgrades in 48 hours".</summary>
    public string DiscoveryReason { get; init; } = "";

    /// <summary>Current investigation lifecycle state.</summary>
    public ResearchState CurrentState { get; init; } = ResearchState.Discovered;

    /// <summary>Timestamp of the most recent activity (news, signal, state transition).</summary>
    public DateTimeOffset LastActivity { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Timestamp of the most recent news article related to this ticker.</summary>
    public DateTimeOffset? LastNewsTimestamp { get; init; }

    /// <summary>Current directional thesis being built (if any).
    /// Example: "Bullish — accumulation pattern with insider buying".</summary>
    public string? CurrentThesis { get; init; }

    /// <summary>Composite score 0–100 indicating research priority.
    /// Higher = more evidence, more catalysts, stronger thesis.</summary>
    public int InterestScore { get; init; }

    /// <summary>Expected holding window if this becomes a trade.
    /// Mirrors prediction time windows: "1_day", "2_5_days", "1_2_weeks".</summary>
    public string? ExpectedHoldingWindow { get; init; }

    /// <summary>Number of distinct evidence items accumulated for this ticker.</summary>
    public int EvidenceCount { get; init; }

    /// <summary>Number of calendar days this ticker has been in the Research Universe.</summary>
    public int DaysActive { get; init; }

    /// <summary>Last time any field on this record was updated.</summary>
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Administrative status.</summary>
    public ResearchAssetStatus Status { get; init; } = ResearchAssetStatus.Active;

    /// <summary>If archived, why. Examples: "stale — no activity 7 days",
    /// "promoted — entered watchlist", "invalidated — thesis disproven".</summary>
    public string? ArchiveReason { get; init; }

    /// <summary>Market regime at the time of discovery or last state transition.
    /// Stored as a summary string (e.g. "BullTrend (82%), RiskOn (74%)").</summary>
    public string? MarketRegimeSnapshot { get; init; }

    /// <summary>Link to the one-time HistoricalResearchProfile built on first discovery.
    /// Null until the profile builder completes.</summary>
    public string? HistoricalProfileId { get; init; }

    /// <summary>When this record was first created in the database.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
