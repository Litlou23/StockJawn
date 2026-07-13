using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.OpportunityLearning;

/// <summary>
/// Persistence layer for opportunity learning records.
/// Append-only — records are never updated, only inserted.
/// </summary>
public interface IOpportunityLearningRepository
{
    /// <summary>Persist a single opportunity learning record.</summary>
    Task PersistAsync(OpportunityLearningRecord record);

    /// <summary>Persist a batch of opportunity learning records.</summary>
    Task PersistManyAsync(List<OpportunityLearningRecord> records);

    /// <summary>Get recent records, ordered by scan date descending.</summary>
    Task<List<OpportunityLearningRecord>> GetRecentAsync(int limit = 100);

    /// <summary>Get records for a specific ticker.</summary>
    Task<List<OpportunityLearningRecord>> GetByTickerAsync(string ticker, int limit = 50);

    /// <summary>Get records within a date range.</summary>
    Task<List<OpportunityLearningRecord>> GetByDateRangeAsync(DateTimeOffset from, DateTimeOffset to, int limit = 500);

    /// <summary>Get records by capture status.</summary>
    Task<List<OpportunityLearningRecord>> GetByCaptureStatusAsync(OpportunityCaptureStatus status, int limit = 100);

    /// <summary>Get records by movement tier.</summary>
    Task<List<OpportunityLearningRecord>> GetByTierAsync(MovementTier tier, int limit = 100);

    /// <summary>Check if a record already exists for this ticker + scan date + period
    /// (to avoid duplicates on re-runs).</summary>
    Task<bool> ExistsAsync(string ticker, DateTimeOffset scanDate, string measurementPeriod);

    /// <summary>Get all existing (ticker, measurement_period) keys for a date range.
    /// One HTTP call instead of N ExistsAsync checks.</summary>
    Task<HashSet<string>> GetExistingKeysAsync(DateTimeOffset scanDate);

    /// <summary>Count total records.</summary>
    Task<int> CountAsync(string? filter = null);
}
