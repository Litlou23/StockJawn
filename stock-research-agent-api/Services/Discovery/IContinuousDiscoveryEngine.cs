using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// Lightweight continuous discovery engine that runs on a configurable interval.
///
/// Unlike the full <see cref="IDiscoveryEngine"/> which rescans everything,
/// this engine only processes NEW evidence since the previous run using
/// checkpoint timestamps. It updates Research Assets incrementally
/// without generating predictions, running the Morning Scan, or triggering Learning.
///
/// Each cycle:
///   1. Checks all configured providers for new events since last checkpoint
///   2. Normalizes events into Evidence
///   3. Updates Research Assets (interest score, thesis, evidence count, last activity)
///   4. Appends immutable ResearchTimelineEvents
///   5. Builds HistoricalResearchProfile for first-time discoveries
///   6. Advances the checkpoint (persisted to Supabase)
/// </summary>
public interface IContinuousDiscoveryEngine
{
    /// <summary>Run one incremental discovery cycle.
    /// Only processes events newer than the last checkpoint.</summary>
    Task<ContinuousDiscoveryResult> RunCycleAsync();

    /// <summary>Get the current checkpoint timestamp.</summary>
    Task<DateTimeOffset> GetLastCheckpointAsync();

    /// <summary>Reset the checkpoint (forces a full rescan on next cycle).
    /// Deletes the persisted checkpoint from Supabase.</summary>
    Task ResetCheckpointAsync();

    /// <summary>Check if a cycle should run right now based on schedule config.</summary>
    bool ShouldRunNow();

    /// <summary>Get current configuration.</summary>
    ContinuousDiscoveryConfig GetConfig();
}
