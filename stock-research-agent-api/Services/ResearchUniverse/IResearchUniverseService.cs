using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchUniverse;

/// <summary>
/// Service layer for the Research Universe.
///
/// Encapsulates business logic for managing the lifecycle of research assets:
/// discovery → monitoring → thesis building → evaluation readiness → archival.
///
/// Future implementations will:
///   - Ingest from news scanners, filing alerts, congress signals, sector momentum
///   - Auto-promote assets through lifecycle states based on evidence accumulation
///   - Feed ReadyForEvaluation assets into the prediction pipeline
///   - Archive stale assets that lose momentum
///   - Coordinate with MarketRegimeEngine for regime-aware prioritization
///
/// This interface is defined now to establish the contract.
/// Implementation will be introduced in the next phase.
/// </summary>
public interface IResearchUniverseService
{
    // ── Discovery ───────────────────────────────────────────────

    /// <summary>Add a ticker to the Research Universe from a discovery source.</summary>
    Task<ResearchAsset?> DiscoverAsync(string ticker, string source, string reason);

    /// <summary>Check if a ticker is already under active investigation.</summary>
    Task<bool> IsUnderInvestigationAsync(string ticker);

    /// <summary>Get all active tickers as a HashSet (one HTTP call). For batch pre-fetching.</summary>
    Task<HashSet<string>> GetActiveTickerSetAsync();

    // ── Lifecycle management ────────────────────────────────────

    /// <summary>Promote an asset to Monitoring state.</summary>
    Task<bool> StartMonitoringAsync(string assetId);

    /// <summary>Promote an asset to BuildingThesis with an initial thesis.</summary>
    Task<bool> StartBuildingThesisAsync(string assetId, string thesis);

    /// <summary>Mark an asset as ready for full prediction evaluation.</summary>
    Task<bool> MarkReadyForEvaluationAsync(string assetId);

    /// <summary>Archive an asset with a reason.</summary>
    Task<bool> ArchiveAsync(string assetId, string reason);

    // ── Evidence accumulation ───────────────────────────────────

    /// <summary>Record new evidence for an asset (news, filing, signal, etc.).
    /// Updates evidence count, interest score, and last activity.</summary>
    Task<bool> RecordEvidenceAsync(string assetId, string evidenceType, int scoreImpact);

    /// <summary>Update the thesis for an asset under investigation.</summary>
    Task<bool> UpdateThesisAsync(string assetId, string thesis);

    // ── Queries ─────────────────────────────────────────────────

    /// <summary>Get all active research assets, ordered by priority.</summary>
    Task<List<ResearchAsset>> GetActiveAssetsAsync(int limit = 200);

    /// <summary>Get assets ready to be fed into the prediction pipeline.</summary>
    Task<List<ResearchAsset>> GetEvaluationCandidatesAsync(int limit = 50);

    /// <summary>Get assets that have gone stale (no activity for N days).</summary>
    Task<List<ResearchAsset>> GetStaleAssetsAsync(int staleDays = 7);

    /// <summary>Get the full research asset record for a ticker.</summary>
    Task<ResearchAsset?> GetByTickerAsync(string ticker);

    // ── Maintenance ─────────────────────────────────────────────

    /// <summary>Archive all stale assets. Returns count archived.</summary>
    Task<int> ArchiveStaleAssetsAsync(int staleDays = 7);

    /// <summary>Recalculate days_active for all active assets.</summary>
    Task<int> RefreshDaysActiveAsync();

    // ── Stats ───────────────────────────────────────────────────

    /// <summary>Summary statistics for the Research Universe.</summary>
    Task<ResearchUniverseStats> GetStatsAsync();
}
