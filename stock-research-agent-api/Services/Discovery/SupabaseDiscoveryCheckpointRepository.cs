using System.Text.Json.Nodes;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// Supabase-backed checkpoint persistence. Uses a simple key-value table
/// <c>discovery_checkpoints</c> with upsert on the <c>checkpoint_name</c> column.
/// </summary>
public class SupabaseDiscoveryCheckpointRepository : IDiscoveryCheckpointRepository
{
    private readonly SupabaseClient _db;
    private readonly ILogger<SupabaseDiscoveryCheckpointRepository> _logger;

    private const string Table = "discovery_checkpoints";

    public SupabaseDiscoveryCheckpointRepository(
        SupabaseClient db,
        ILogger<SupabaseDiscoveryCheckpointRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<DateTimeOffset?> GetCheckpointAsync(string checkpointName)
    {
        try
        {
            var filter = $"checkpoint_name=eq.{checkpointName}";
            var rows = await _db.SelectAsync(Table, filter, limit: 1);

            if (rows.Count == 0)
                return null;

            var value = rows[0]["checkpoint_value"]?.ToString();
            return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[discovery-checkpoint] Failed to get checkpoint '{Name}'",
                checkpointName);
            return null;
        }
    }

    public async Task SaveCheckpointAsync(string checkpointName, DateTimeOffset value)
    {
        try
        {
            var row = new JsonObject
            {
                ["checkpoint_name"] = checkpointName,
                ["checkpoint_value"] = value.ToString("o"),
                ["updated_at"] = DateTimeOffset.UtcNow.ToString("o"),
            };

            await _db.UpsertAsync(Table, row, onConflict: "checkpoint_name");

            _logger.LogDebug(
                "[discovery-checkpoint] Saved checkpoint '{Name}' = {Value}",
                checkpointName, value.ToString("o"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[discovery-checkpoint] Failed to save checkpoint '{Name}'",
                checkpointName);
        }
    }

    public async Task ResetCheckpointAsync(string checkpointName)
    {
        try
        {
            var filter = $"checkpoint_name=eq.{checkpointName}";
            await _db.DeleteAsync(Table, filter);

            _logger.LogInformation(
                "[discovery-checkpoint] Reset checkpoint '{Name}'",
                checkpointName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[discovery-checkpoint] Failed to reset checkpoint '{Name}'",
                checkpointName);
        }
    }
}
