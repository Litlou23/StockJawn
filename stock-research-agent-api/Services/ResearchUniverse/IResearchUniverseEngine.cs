using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchUniverse;

/// <summary>
/// Engine that maintains every Research Asset through its lifecycle.
///
/// Unlike <see cref="IResearchUniverseService"/> (which provides basic CRUD),
/// the engine applies configurable rules to:
///   - Promote assets through state flow (Discovered → Monitoring → BuildingThesis → ReadyForEvaluation)
///   - Decay interest scores on stale assets
///   - Boost interest scores on repeated catalysts
///   - Archive assets that go stale or drop below minimum interest
///   - Update expected holding windows based on evidence patterns
///   - Maintain thesis from accumulated evidence
///   - Refresh days-active counters
///
/// Designed to run as a periodic maintenance job.
/// </summary>
public interface IResearchUniverseEngine
{
    /// <summary>Run a full maintenance cycle on all active research assets.
    /// Returns a summary of actions taken.</summary>
    Task<UniverseMaintenanceResult> RunMaintenanceAsync();

    /// <summary>Evaluate a single asset and apply state transitions, score updates, etc.</summary>
    Task<AssetMaintenanceAction> EvaluateAssetAsync(ResearchAsset asset);

    /// <summary>Apply interest score decay to stale assets.</summary>
    Task<int> DecayStaleScoresAsync();

    /// <summary>Promote assets that meet state transition thresholds.</summary>
    Task<int> PromoteEligibleAssetsAsync();

    /// <summary>Archive assets past their staleness threshold.</summary>
    Task<int> ArchiveStaleAssetsAsync();

    /// <summary>Update holding windows based on dominant evidence types.</summary>
    Task<int> UpdateHoldingWindowsAsync();

    /// <summary>Refresh days-active and sync evidence-driven fields.</summary>
    Task<int> RefreshAllAssetsAsync();

    /// <summary>Get the current configuration.</summary>
    ResearchUniverseConfig GetConfig();
}

/// <summary>
/// Result of a full maintenance cycle.
/// </summary>
public record UniverseMaintenanceResult
{
    public int AssetsEvaluated { get; init; }
    public int Promoted { get; init; }
    public int ScoresDecayed { get; init; }
    public int Archived { get; init; }
    public int HoldingWindowsUpdated { get; init; }
    public int DaysActiveRefreshed { get; init; }
    public int ThesesUpdated { get; init; }
    public TimeSpan Duration { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// What happened to a single asset during evaluation.
/// </summary>
public record AssetMaintenanceAction
{
    public string AssetId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public ResearchState PreviousState { get; init; }
    public ResearchState NewState { get; init; }
    public int PreviousScore { get; init; }
    public int NewScore { get; init; }
    public string? Action { get; init; }
    public bool WasArchived { get; init; }
    public bool WasPromoted { get; init; }
    public bool ScoreChanged { get; init; }
    public bool HoldingWindowChanged { get; init; }
    public bool ThesisUpdated { get; init; }
}
