namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// Persists discovery checkpoint timestamps to Supabase so they survive
/// app restarts. Simple key-value store: one row per named checkpoint.
/// </summary>
public interface IDiscoveryCheckpointRepository
{
    /// <summary>Get the checkpoint value for a named checkpoint.
    /// Returns null if no checkpoint has been saved yet.</summary>
    Task<DateTimeOffset?> GetCheckpointAsync(string checkpointName);

    /// <summary>Save or update the checkpoint value.</summary>
    Task SaveCheckpointAsync(string checkpointName, DateTimeOffset value);

    /// <summary>Delete a checkpoint (forces full rescan on next cycle).</summary>
    Task ResetCheckpointAsync(string checkpointName);
}
