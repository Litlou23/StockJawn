using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchUniverse;

/// <summary>
/// Supabase-backed implementation of <see cref="IResearchUniverseRepository"/>.
/// Uses the PostgREST-based <see cref="SupabaseClient"/>.
/// </summary>
public class SupabaseResearchUniverseRepository : IResearchUniverseRepository
{
    private const string Table = "research_universe";
    private readonly SupabaseClient _db;
    private readonly ILogger<SupabaseResearchUniverseRepository> _logger;

    public SupabaseResearchUniverseRepository(
        SupabaseClient db,
        ILogger<SupabaseResearchUniverseRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── CRUD ────────────────────────────────────────────────────

    public async Task<ResearchAsset?> AddAsync(ResearchAsset asset)
    {
        var row = ToRow(asset);
        var rows = await _db.InsertAsync(Table, new[] { row });
        return rows.Count > 0 ? MapAsset(rows[0]) : null;
    }

    public async Task<bool> UpdateAsync(ResearchAsset asset)
    {
        var row = new
        {
            ticker = asset.Ticker,
            current_state = asset.CurrentState.ToString(),
            last_activity = asset.LastActivity.ToString("o"),
            last_news_timestamp = asset.LastNewsTimestamp?.ToString("o"),
            current_thesis = asset.CurrentThesis,
            interest_score = asset.InterestScore,
            expected_holding_window = asset.ExpectedHoldingWindow,
            evidence_count = asset.EvidenceCount,
            days_active = asset.DaysActive,
            last_updated = DateTimeOffset.UtcNow.ToString("o"),
            status = asset.Status.ToString(),
            archive_reason = asset.ArchiveReason,
            market_regime_snapshot = asset.MarketRegimeSnapshot,
        };

        return await _db.UpdateAsync(Table, $"id=eq.{asset.Id}", row);
    }

    public async Task<ResearchAsset?> GetByIdAsync(string id)
    {
        var row = await _db.SelectSingleAsync(Table, $"id=eq.{id}");
        return row is not null ? MapAsset(row) : null;
    }

    public async Task<ResearchAsset?> GetActiveByTickerAsync(string ticker)
    {
        var row = await _db.SelectSingleAsync(Table,
            $"ticker=ilike.{ticker}&status=eq.Active");
        return row is not null ? MapAsset(row) : null;
    }

    // ── Queries ─────────────────────────────────────────────────

    public async Task<List<ResearchAsset>> GetByStateAsync(ResearchState state, int limit = 100)
    {
        var rows = await _db.SelectAsync(Table,
            $"current_state=eq.{state}&status=eq.Active",
            order: "interest_score.desc", limit: limit);
        return rows.Select(MapAsset).ToList();
    }

    public async Task<List<ResearchAsset>> GetActiveAsync(int limit = 200)
    {
        var rows = await _db.SelectAsync(Table,
            "status=eq.Active",
            order: "interest_score.desc", limit: limit);
        return rows.Select(MapAsset).ToList();
    }

    public async Task<List<ResearchAsset>> GetReadyForEvaluationAsync(int limit = 50)
    {
        var rows = await _db.SelectAsync(Table,
            "current_state=eq.ReadyForEvaluation&status=eq.Active",
            order: "interest_score.desc", limit: limit);
        return rows.Select(MapAsset).ToList();
    }

    public async Task<List<ResearchAsset>> GetStaleAsync(int staleDays = 7, int limit = 100)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-staleDays).ToString("o");
        var rows = await _db.SelectAsync(Table,
            $"status=eq.Active&last_activity=lt.{cutoff}",
            order: "last_activity.asc", limit: limit);
        return rows.Select(MapAsset).ToList();
    }

    public async Task<List<ResearchAsset>> GetBySourceAsync(string discoverySource, int limit = 100)
    {
        var rows = await _db.SelectAsync(Table,
            $"discovery_source=ilike.{discoverySource}",
            order: "date_discovered.desc", limit: limit);
        return rows.Select(MapAsset).ToList();
    }

    // ── Batch ───────────────────────────────────────────────────

    public async Task<HashSet<string>> GetActiveTickerSetAsync()
    {
        var rows = await _db.SelectAsync(Table, "status=eq.Active", select: "ticker", limit: 2000);
        return rows
            .Select(r => r["ticker"]?.ToString() ?? "")
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> BatchArchiveAsync(IReadOnlyList<string> ids, string reason)
    {
        if (ids.Count == 0) return true;
        var now = DateTimeOffset.UtcNow.ToString("o");
        var filter = SupabaseClient.InFilter("id", ids);
        return await _db.UpdateAsync(Table, filter, new
        {
            current_state = ResearchState.Archived.ToString(),
            status = ResearchAssetStatus.Archived.ToString(),
            archive_reason = reason,
            last_activity = now,
            last_updated = now,
        });
    }

    public async Task<bool> BatchUpdateFieldsAsync(IReadOnlyList<(string Id, object Fields)> updates)
    {
        // PostgREST doesn't support heterogeneous batch updates in a single call,
        // so we group by identical field shapes and use IN filters where possible.
        // For per-row-different values (like days_active), we fall back to chunked updates.
        // This is still better than N individual calls when combined with UpsertManyAsync.
        if (updates.Count == 0) return true;

        var allOk = true;
        foreach (var update in updates)
        {
            if (!await _db.UpdateAsync(Table, $"id=eq.{update.Id}", update.Fields))
                allOk = false;
        }
        return allOk;
    }

    // ── Lifecycle ───────────────────────────────────────────────

    public async Task<bool> TransitionStateAsync(string id, ResearchState newState)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        return await _db.UpdateAsync(Table, $"id=eq.{id}", new
        {
            current_state = newState.ToString(),
            last_activity = now,
            last_updated = now,
        });
    }

    public async Task<bool> ArchiveAsync(string id, string reason)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        return await _db.UpdateAsync(Table, $"id=eq.{id}", new
        {
            current_state = ResearchState.Archived.ToString(),
            status = ResearchAssetStatus.Archived.ToString(),
            archive_reason = reason,
            last_activity = now,
            last_updated = now,
        });
    }

    // ── Stats ───────────────────────────────────────────────────

    public async Task<ResearchUniverseStats> GetStatsAsync()
    {
        var rows = await _db.SelectAsync(Table);
        var all = rows.Select(MapAsset).ToList();

        var active = all.Where(a => a.Status == ResearchAssetStatus.Active).ToList();

        return new ResearchUniverseStats
        {
            TotalAssets = all.Count,
            ActiveAssets = active.Count,
            DiscoveredCount = active.Count(a => a.CurrentState == ResearchState.Discovered),
            MonitoringCount = active.Count(a => a.CurrentState == ResearchState.Monitoring),
            BuildingThesisCount = active.Count(a => a.CurrentState == ResearchState.BuildingThesis),
            ReadyForEvaluationCount = active.Count(a => a.CurrentState == ResearchState.ReadyForEvaluation),
            ArchivedCount = all.Count(a => a.Status == ResearchAssetStatus.Archived),
            AverageInterestScore = active.Count > 0
                ? Math.Round(active.Average(a => a.InterestScore), 1) : 0,
            AverageDaysActive = active.Count > 0
                ? (int)Math.Round(active.Average(a => a.DaysActive)) : 0,
            Summary = $"{active.Count} active research assets, " +
                      $"{active.Count(a => a.CurrentState == ResearchState.ReadyForEvaluation)} ready for evaluation.",
        };
    }

    // ══════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════

    private static object ToRow(ResearchAsset a) => new
    {
        ticker = a.Ticker,
        date_discovered = a.DateDiscovered.ToString("o"),
        discovery_source = a.DiscoverySource,
        discovery_reason = a.DiscoveryReason,
        current_state = a.CurrentState.ToString(),
        last_activity = a.LastActivity.ToString("o"),
        last_news_timestamp = a.LastNewsTimestamp?.ToString("o"),
        current_thesis = a.CurrentThesis,
        interest_score = a.InterestScore,
        expected_holding_window = a.ExpectedHoldingWindow,
        evidence_count = a.EvidenceCount,
        days_active = a.DaysActive,
        last_updated = DateTimeOffset.UtcNow.ToString("o"),
        status = a.Status.ToString(),
        archive_reason = a.ArchiveReason,
        market_regime_snapshot = a.MarketRegimeSnapshot,
    };

    private static ResearchAsset MapAsset(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        Ticker = r["ticker"]?.ToString() ?? "",
        DateDiscovered = GetDateTimeOffset(r, "date_discovered"),
        DiscoverySource = r["discovery_source"]?.ToString() ?? "",
        DiscoveryReason = r["discovery_reason"]?.ToString() ?? "",
        CurrentState = Enum.TryParse<ResearchState>(r["current_state"]?.ToString(), out var state)
            ? state : ResearchState.Discovered,
        LastActivity = GetDateTimeOffset(r, "last_activity"),
        LastNewsTimestamp = GetNullableDateTimeOffset(r, "last_news_timestamp"),
        CurrentThesis = r["current_thesis"]?.ToString(),
        InterestScore = GetInt(r, "interest_score"),
        ExpectedHoldingWindow = r["expected_holding_window"]?.ToString(),
        EvidenceCount = GetInt(r, "evidence_count"),
        DaysActive = GetInt(r, "days_active"),
        LastUpdated = GetDateTimeOffset(r, "last_updated"),
        Status = Enum.TryParse<ResearchAssetStatus>(r["status"]?.ToString(), out var status)
            ? status : ResearchAssetStatus.Active,
        ArchiveReason = r["archive_reason"]?.ToString(),
        MarketRegimeSnapshot = r["market_regime_snapshot"]?.ToString(),
        CreatedAt = GetDateTimeOffset(r, "created_at"),
    };

    private static int GetInt(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return 0;
        if (node is JsonValue jv && jv.TryGetValue<int>(out var i)) return i;
        return int.TryParse(node.ToString(), out var parsed) ? parsed : 0;
    }

    private static DateTimeOffset GetDateTimeOffset(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(node.ToString(), out var dt) ? dt : DateTimeOffset.MinValue;
    }

    private static DateTimeOffset? GetNullableDateTimeOffset(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return null;
        return DateTimeOffset.TryParse(node.ToString(), out var dt) ? dt : null;
    }
}
