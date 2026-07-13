using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Evidence;

/// <summary>
/// Orchestrates the Evidence Engine.
///
/// Responsibilities:
///   - Convert DiscoveryEvents into EvidenceRecords
///   - Record evidence from any source (discovery, learning, regime, etc.)
///   - Aggregate evidence into snapshots
///   - Update Research Assets with computed scores and thesis
///   - Apply expiration to stale evidence
///
/// This is the main entry point for all evidence operations.
/// Other systems (Discovery Engine, Learning Engine, Market Regime)
/// call RecordEvidenceAsync to feed evidence into the system.
/// </summary>
public interface IEvidenceService
{
    // ── Recording ───────────────────────────────────────────────

    /// <summary>Record a single evidence item for a ticker.</summary>
    Task<EvidenceRecord> RecordAsync(EvidenceRecord record);

    /// <summary>Record multiple evidence items in batch.</summary>
    Task<int> RecordManyAsync(List<EvidenceRecord> records);

    /// <summary>Convert a DiscoveryEvent into evidence and record it.</summary>
    Task<EvidenceRecord> RecordFromDiscoveryAsync(DiscoveryEvent discoveryEvent);

    /// <summary>Convert multiple DiscoveryEvents into evidence and record them.</summary>
    Task<int> RecordFromDiscoveryBatchAsync(List<DiscoveryEvent> events);

    // ── Aggregation ─────────────────────────────────────────────

    /// <summary>Compute an evidence snapshot for a ticker.
    /// Includes interest score, evidence count, timeline, thesis.</summary>
    Task<EvidenceSnapshot> GetSnapshotAsync(string ticker);

    /// <summary>Compute evidence snapshots for multiple tickers in one query.</summary>
    Task<Dictionary<string, EvidenceSnapshot>> GetSnapshotsAsync(IReadOnlyList<string> tickers);

    /// <summary>Compute interest score only (lightweight).</summary>
    Task<int> GetInterestScoreAsync(string ticker);

    /// <summary>Get the current auto-generated thesis for a ticker.</summary>
    Task<string> GetThesisAsync(string ticker);

    /// <summary>Get the evidence timeline for a ticker.</summary>
    Task<List<EvidenceRecord>> GetTimelineAsync(string ticker, int limit = 50);

    // ── Research Asset sync ─────────────────────────────────────

    /// <summary>Recompute evidence snapshot and push updates to the
    /// Research Asset (interest score, evidence count, thesis, last activity).</summary>
    Task SyncToResearchAssetAsync(string ticker);

    /// <summary>Sync all active research assets with current evidence.</summary>
    Task<int> SyncAllResearchAssetsAsync();

    // ── Maintenance ─────────────────────────────────────────────

    /// <summary>Apply default TTL expirations to evidence that has none.
    /// Uses the decay strategy's config to set expiration dates.</summary>
    Task<int> ApplyDefaultExpirationsAsync();

    /// <summary>Get evidence stats for monitoring.</summary>
    Task<EvidenceStats> GetStatsAsync();
}

/// <summary>
/// Summary statistics for the evidence system.
/// </summary>
public record EvidenceStats
{
    public int TotalRecords { get; init; }
    public int ActiveRecords { get; init; }
    public int ExpiredRecords { get; init; }
    public Dictionary<EvidenceType, int> CountByType { get; init; } = new();
    public int TickersWithEvidence { get; init; }
    public DateTimeOffset? OldestEvidence { get; init; }
    public DateTimeOffset? NewestEvidence { get; init; }
}
