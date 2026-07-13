using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Evidence;

public class SupabaseEvidenceRepository : IEvidenceRepository
{
    private const string Table = "evidence_records";
    private readonly SupabaseClient _db;

    public SupabaseEvidenceRepository(SupabaseClient db)
    {
        _db = db;
    }

    // ── Write ───────────────────────────────────────────────────

    public async Task<EvidenceRecord> AddAsync(EvidenceRecord record)
    {
        var id = string.IsNullOrEmpty(record.Id) ? Guid.NewGuid().ToString() : record.Id;
        var row = ToRow(record with { Id = id });
        await _db.InsertAsync(Table, row);
        return record with { Id = id };
    }

    public async Task<int> AddManyAsync(List<EvidenceRecord> records)
    {
        if (records.Count == 0) return 0;

        // Batch insert: one HTTP call instead of N
        var rows = records.Select(r =>
        {
            var id = string.IsNullOrEmpty(r.Id) ? Guid.NewGuid().ToString() : r.Id;
            return ToRow(r with { Id = id });
        }).ToList();

        await _db.InsertAsync(Table, rows, returnRows: false);
        return records.Count;
    }

    public async Task<bool> ExpireAsync(string recordId, DateTimeOffset expiration)
    {
        var update = new JsonObject
        {
            ["expiration"] = expiration.ToString("o"),
        };
        await _db.UpdateAsync(Table, $"id=eq.{recordId}", update);
        return true;
    }

    // ── Read ────────────────────────────────────────────────────

    public async Task<EvidenceRecord?> GetByIdAsync(string id)
    {
        return await _db.SelectSingleAsync(Table, $"id=eq.{id}") is { } row
            ? MapRecord(row) : null;
    }

    public async Task<List<EvidenceRecord>> GetByTickerAsync(string ticker, int limit = 200)
    {
        var rows = await _db.SelectAsync(Table,
            $"ticker=eq.{ticker.ToUpperInvariant()}",
            order: "timestamp.desc", limit: limit);
        return rows.Select(MapRecord).ToList();
    }

    public async Task<Dictionary<string, List<EvidenceRecord>>> GetByTickersAsync(IReadOnlyList<string> tickers)
    {
        if (tickers.Count == 0)
            return new Dictionary<string, List<EvidenceRecord>>(StringComparer.OrdinalIgnoreCase);

        var filter = SupabaseClient.InFilter("ticker", tickers.Select(t => t.ToUpperInvariant()));
        var rows = await _db.SelectAsync(Table, filter, order: "timestamp.desc", limit: tickers.Count * 200);
        return rows.Select(MapRecord)
            .GroupBy(r => r.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<EvidenceRecord>> GetActiveByTickerAsync(string ticker, int limit = 200)
    {
        // Active = expiration is null OR expiration > now
        // PostgREST: or=(expiration.is.null,expiration.gt.{now})
        var now = DateTimeOffset.UtcNow.ToString("o");
        var filter = $"ticker=eq.{ticker.ToUpperInvariant()}&or=(expiration.is.null,expiration.gt.{now})";
        var rows = await _db.SelectAsync(Table, filter, order: "timestamp.desc", limit: limit);
        return rows.Select(MapRecord).ToList();
    }

    public async Task<List<EvidenceRecord>> GetByTickerAndTypeAsync(string ticker, EvidenceType type, int limit = 100)
    {
        var filter = $"ticker=eq.{ticker.ToUpperInvariant()}&evidence_type=eq.{type}";
        var rows = await _db.SelectAsync(Table, filter, order: "timestamp.desc", limit: limit);
        return rows.Select(MapRecord).ToList();
    }

    public async Task<List<EvidenceRecord>> GetByTickerInRangeAsync(string ticker, DateTimeOffset from, DateTimeOffset to)
    {
        var filter = $"ticker=eq.{ticker.ToUpperInvariant()}&timestamp=gte.{from:o}&timestamp=lte.{to:o}";
        var rows = await _db.SelectAsync(Table, filter, order: "timestamp.desc");
        return rows.Select(MapRecord).ToList();
    }

    public async Task<List<EvidenceRecord>> GetExpiredAsync(int limit = 500)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        var filter = $"expiration=lt.{now}";
        var rows = await _db.SelectAsync(Table, filter, order: "expiration.asc", limit: limit);
        return rows.Select(MapRecord).ToList();
    }

    public async Task<int> CountActiveByTickerAsync(string ticker)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        var filter = $"ticker=eq.{ticker.ToUpperInvariant()}&or=(expiration.is.null,expiration.gt.{now})";
        return await _db.CountAsync(Table, filter);
    }

    public async Task<DateTimeOffset?> GetLastEvidenceTimestampAsync(string ticker)
    {
        var rows = await _db.SelectAsync(Table,
            $"ticker=eq.{ticker.ToUpperInvariant()}",
            order: "timestamp.desc", limit: 1, select: "timestamp");

        if (rows.Count == 0) return null;
        return DateTimeOffset.TryParse(rows[0]["timestamp"]?.ToString(), out var ts) ? ts : null;
    }

    // ── Mapping ─────────────────────────────────────────────────

    private static JsonObject ToRow(EvidenceRecord r)
    {
        var row = new JsonObject
        {
            ["id"] = r.Id,
            ["ticker"] = r.Ticker,
            ["timestamp"] = r.Timestamp.ToString("o"),
            ["evidence_type"] = r.EvidenceType.ToString(),
            ["source"] = r.Source,
            ["weight"] = r.Weight,
            ["importance"] = r.Importance,
            ["summary"] = r.Summary.Length > 2000 ? r.Summary[..2000] : r.Summary,
            ["related_event_id"] = r.RelatedEventId,
        };

        if (r.Expiration.HasValue)
            row["expiration"] = r.Expiration.Value.ToString("o");

        return row;
    }

    private static EvidenceRecord MapRecord(JsonObject row)
    {
        _ = Enum.TryParse<EvidenceType>(row["evidence_type"]?.ToString(), out var evType);

        return new EvidenceRecord
        {
            Id = row["id"]?.ToString() ?? "",
            Ticker = row["ticker"]?.ToString() ?? "",
            Timestamp = DateTimeOffset.TryParse(row["timestamp"]?.ToString(), out var ts) ? ts : DateTimeOffset.UtcNow,
            EvidenceType = evType,
            Source = row["source"]?.ToString() ?? "",
            Weight = double.TryParse(row["weight"]?.ToString(), out var w) ? w : 0.0,
            Importance = int.TryParse(row["importance"]?.ToString(), out var imp) ? imp : 0,
            Expiration = DateTimeOffset.TryParse(row["expiration"]?.ToString(), out var exp) ? exp : null,
            Summary = row["summary"]?.ToString() ?? "",
            RelatedEventId = row["related_event_id"]?.ToString(),
            CreatedAt = DateTimeOffset.TryParse(row["created_at"]?.ToString(), out var ca) ? ca : DateTimeOffset.UtcNow,
        };
    }
}
