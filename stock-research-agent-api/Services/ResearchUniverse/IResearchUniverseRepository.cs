using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchUniverse;

/// <summary>
/// Persistence abstraction for the Research Universe.
/// All methods are async — backed by Supabase PostgREST.
/// </summary>
public interface IResearchUniverseRepository
{
    // ── CRUD ────────────────────────────────────────────────────

    /// <summary>Add a new asset to the Research Universe.</summary>
    Task<ResearchAsset?> AddAsync(ResearchAsset asset);

    /// <summary>Update an existing asset. Returns false if not found.</summary>
    Task<bool> UpdateAsync(ResearchAsset asset);

    /// <summary>Get a single asset by its ID.</summary>
    Task<ResearchAsset?> GetByIdAsync(string id);

    /// <summary>Get the active research asset for a ticker (at most one due to unique constraint).</summary>
    Task<ResearchAsset?> GetActiveByTickerAsync(string ticker);

    // ── Queries ─────────────────────────────────────────────────

    /// <summary>Get all assets in a given lifecycle state.</summary>
    Task<List<ResearchAsset>> GetByStateAsync(ResearchState state, int limit = 100);

    /// <summary>Get all active assets, ordered by interest score descending.</summary>
    Task<List<ResearchAsset>> GetActiveAsync(int limit = 200);

    /// <summary>Get assets ready for evaluation (state = ReadyForEvaluation, status = Active).</summary>
    Task<List<ResearchAsset>> GetReadyForEvaluationAsync(int limit = 50);

    /// <summary>Get stale assets — active but no activity for the given number of days.</summary>
    Task<List<ResearchAsset>> GetStaleAsync(int staleDays = 7, int limit = 100);

    /// <summary>Get assets by discovery source.</summary>
    Task<List<ResearchAsset>> GetBySourceAsync(string discoverySource, int limit = 100);

    // ── Batch ───────────────────────────────────────────────────

    /// <summary>Get the set of active tickers in one query.</summary>
    Task<HashSet<string>> GetActiveTickerSetAsync();

    /// <summary>Batch-archive multiple assets with the same reason. One HTTP call.</summary>
    Task<bool> BatchArchiveAsync(IReadOnlyList<string> ids, string reason);

    /// <summary>Batch-update multiple assets. One HTTP call via upsert.</summary>
    Task<bool> BatchUpdateFieldsAsync(IReadOnlyList<(string Id, object Fields)> updates);

    // ── Lifecycle ───────────────────────────────────────────────

    /// <summary>Transition an asset to a new state. Updates last_activity and last_updated.</summary>
    Task<bool> TransitionStateAsync(string id, ResearchState newState);

    /// <summary>Archive an asset with a reason.</summary>
    Task<bool> ArchiveAsync(string id, string reason);

    // ── Stats ───────────────────────────────────────────────────

    /// <summary>Summary statistics for the Research Universe.</summary>
    Task<ResearchUniverseStats> GetStatsAsync();
}

/// <summary>
/// Summary statistics for the Research Universe.
/// </summary>
public record ResearchUniverseStats
{
    public int TotalAssets { get; init; }
    public int ActiveAssets { get; init; }
    public int DiscoveredCount { get; init; }
    public int MonitoringCount { get; init; }
    public int BuildingThesisCount { get; init; }
    public int ReadyForEvaluationCount { get; init; }
    public int ArchivedCount { get; init; }
    public double AverageInterestScore { get; init; }
    public int AverageDaysActive { get; init; }
    public string Summary { get; init; } = "";
}
