using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Evidence;

/// <summary>
/// Persistence layer for evidence records.
/// Evidence is append-only — records are never deleted, only expired.
/// </summary>
public interface IEvidenceRepository
{
    // ── Write ───────────────────────────────────────────────────

    /// <summary>Persist a new evidence record.</summary>
    Task<EvidenceRecord> AddAsync(EvidenceRecord record);

    /// <summary>Persist multiple evidence records in one batch.</summary>
    Task<int> AddManyAsync(List<EvidenceRecord> records);

    /// <summary>Update expiration on an existing record (for decay).</summary>
    Task<bool> ExpireAsync(string recordId, DateTimeOffset expiration);

    // ── Read ────────────────────────────────────────────────────

    /// <summary>Get a single evidence record by ID.</summary>
    Task<EvidenceRecord?> GetByIdAsync(string id);

    /// <summary>Get all evidence for a ticker, ordered by timestamp desc.</summary>
    Task<List<EvidenceRecord>> GetByTickerAsync(string ticker, int limit = 200);

    /// <summary>Get all evidence for multiple tickers in one query. Returns grouped by ticker.</summary>
    Task<Dictionary<string, List<EvidenceRecord>>> GetByTickersAsync(IReadOnlyList<string> tickers);

    /// <summary>Get active (non-expired) evidence for a ticker.</summary>
    Task<List<EvidenceRecord>> GetActiveByTickerAsync(string ticker, int limit = 200);

    /// <summary>Get evidence by ticker and type.</summary>
    Task<List<EvidenceRecord>> GetByTickerAndTypeAsync(string ticker, EvidenceType type, int limit = 100);

    /// <summary>Get evidence within a time range for a ticker.</summary>
    Task<List<EvidenceRecord>> GetByTickerInRangeAsync(string ticker, DateTimeOffset from, DateTimeOffset to);

    /// <summary>Get all expired evidence that can be cleaned up.</summary>
    Task<List<EvidenceRecord>> GetExpiredAsync(int limit = 500);

    /// <summary>Count active evidence records for a ticker.</summary>
    Task<int> CountActiveByTickerAsync(string ticker);

    /// <summary>Get the most recent evidence timestamp for a ticker.</summary>
    Task<DateTimeOffset?> GetLastEvidenceTimestampAsync(string ticker);
}
