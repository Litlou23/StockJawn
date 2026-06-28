using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Supabase;

public class WatchlistRepository
{
    private readonly SupabaseClient _db;
    private readonly ILogger<WatchlistRepository> _logger;

    public WatchlistRepository(SupabaseClient db, ILogger<WatchlistRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Watchlist Items
    // -----------------------------------------------------------------------

    public async Task<List<WatchlistItem>> GetActiveWatchlistAsync(string? userId = null)
    {
        var filter = "status=eq.active";
        if (userId is not null) filter += $"&user_id=eq.{userId}";
        var rows = await _db.SelectAsync("watchlist_items", filter: filter, order: "total_score.desc.nullslast");
        return rows.Select(MapWatchlistItem).ToList();
    }

    public async Task<List<WatchlistItem>> GetWatchlistByStatusAsync(string status, string? userId = null)
    {
        var filter = $"status=eq.{status}";
        if (userId is not null) filter += $"&user_id=eq.{userId}";
        var rows = await _db.SelectAsync("watchlist_items", filter: filter, order: "updated_at.desc");
        return rows.Select(MapWatchlistItem).ToList();
    }

    public async Task<List<WatchlistItem>> GetAllWatchlistItemsAsync(string? userId = null)
    {
        var filter = userId is not null ? $"user_id=eq.{userId}" : null;
        var rows = await _db.SelectAsync("watchlist_items", filter: filter, order: "status.asc,total_score.desc.nullslast");
        return rows.Select(MapWatchlistItem).ToList();
    }

    public async Task<WatchlistItem?> GetWatchlistItemByTickerAsync(string ticker, string? userId = null)
    {
        var filter = $"ticker=eq.{ticker}";
        if (userId is not null) filter += $"&user_id=eq.{userId}";
        var row = await _db.SelectSingleAsync("watchlist_items", filter);
        return row is not null ? MapWatchlistItem(row) : null;
    }

    public async Task<string?> UpsertWatchlistItemAsync(object item)
    {
        var rows = await _db.InsertAsync("watchlist_items", new[] { item });
        return rows.Count > 0 ? rows[0]["id"]?.ToString() : null;
    }

    public async Task<bool> UpdateWatchlistItemAsync(string id, object updates)
    {
        return await _db.UpdateAsync("watchlist_items", $"id=eq.{id}", updates);
    }

    public async Task<bool> ArchiveWatchlistItemAsync(string id, string reason)
    {
        return await _db.UpdateAsync("watchlist_items", $"id=eq.{id}", new
        {
            status = WatchlistStatus.Archived,
            swap_reason = reason,
            archived_at = DateTimeOffset.UtcNow.ToString("o"),
        });
    }

    public async Task<bool> UpdateWatchlistStatusAsync(string id, string status, string? reason = null)
    {
        var update = new Dictionary<string, object?> { ["status"] = status };
        if (reason is not null) update["swap_reason"] = reason;
        if (status == WatchlistStatus.Archived) update["archived_at"] = DateTimeOffset.UtcNow.ToString("o");
        return await _db.UpdateAsync("watchlist_items", $"id=eq.{id}", update);
    }

    // -----------------------------------------------------------------------
    // Change Log
    // -----------------------------------------------------------------------

    public async Task<bool> InsertChangeLogAsync(object entry)
    {
        await _db.InsertAsync("watchlist_change_log", new[] { entry }, returnRows: false);
        return true;
    }

    public async Task<bool> InsertChangeLogsAsync(List<object> entries)
    {
        if (entries.Count == 0) return true;
        await _db.InsertAsync("watchlist_change_log", entries, returnRows: false);
        return true;
    }

    public async Task<List<WatchlistChangeLog>> GetRecentChangeLogsAsync(int limit = 50, string? userId = null)
    {
        var filter = userId is not null ? $"user_id=eq.{userId}" : null;
        var rows = await _db.SelectAsync("watchlist_change_log", filter: filter, order: "created_at.desc", limit: limit);
        return rows.Select(MapChangeLog).ToList();
    }

    // -----------------------------------------------------------------------
    // Candidates
    // -----------------------------------------------------------------------

    public async Task<bool> InsertCandidatesAsync(List<object> candidates)
    {
        if (candidates.Count == 0) return true;
        await _db.InsertAsync("watchlist_candidates", candidates, returnRows: false);
        return true;
    }

    public async Task<List<WatchlistCandidate>> GetRecentCandidatesAsync(int limit = 30, string? userId = null)
    {
        var filter = userId is not null ? $"user_id=eq.{userId}" : null;
        var rows = await _db.SelectAsync("watchlist_candidates", filter: filter, order: "created_at.desc", limit: limit);
        return rows.Select(MapCandidate).ToList();
    }

    // -----------------------------------------------------------------------
    // Row mappers
    // -----------------------------------------------------------------------

    private static WatchlistItem MapWatchlistItem(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        UserId = r["user_id"]?.ToString(),
        Ticker = r["ticker"]?.ToString() ?? "",
        CompanyName = r["company_name"]?.ToString(),
        Status = r["status"]?.ToString() ?? "active",
        Category = r["category"]?.ToString() ?? "general",
        WatchReason = r["watch_reason"]?.ToString(),
        ThesisSummary = r["thesis_summary"]?.ToString(),
        BullishCase = r["bullish_case"]?.ToString(),
        BearishCase = r["bearish_case"]?.ToString(),
        DataConfidence = r["data_confidence"]?.ToString(),
        TotalScore = GetNullableDouble(r, "total_score"),
        CatalystScore = GetNullableDouble(r, "catalyst_score"),
        RiskScore = GetNullableDouble(r, "risk_score"),
        OptionsReadinessScore = GetNullableDouble(r, "options_readiness_score"),
        AddedAt = GetNullableDateTimeOffset(r, "added_at"),
        LastReviewedAt = GetNullableDateTimeOffset(r, "last_reviewed_at"),
        ReviewByDate = r["review_by_date"]?.ToString(),
        InvalidationPoint = r["invalidation_point"]?.ToString(),
        ExitOrRemovalConditions = r["exit_or_removal_conditions"],
        SwapReason = r["swap_reason"]?.ToString(),
        SourcesUsed = r["sources_used"],
        MissingDataWarnings = r["missing_data_warnings"],
        RawContext = r["raw_context"],
        ArchivedAt = GetNullableDateTimeOffset(r, "archived_at"),
        CreatedAt = GetDateTimeOffset(r, "created_at"),
        UpdatedAt = GetDateTimeOffset(r, "updated_at"),
    };

    private static WatchlistChangeLog MapChangeLog(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        UserId = r["user_id"]?.ToString(),
        WatchlistItemId = r["watchlist_item_id"]?.ToString(),
        Ticker = r["ticker"]?.ToString() ?? "",
        ChangeType = r["change_type"]?.ToString() ?? "",
        PreviousStatus = r["previous_status"]?.ToString(),
        NewStatus = r["new_status"]?.ToString(),
        PreviousScore = GetNullableDouble(r, "previous_score"),
        NewScore = GetNullableDouble(r, "new_score"),
        Reason = r["reason"]?.ToString(),
        Metadata = r["metadata"],
        CreatedAt = GetDateTimeOffset(r, "created_at"),
    };

    private static WatchlistCandidate MapCandidate(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        UserId = r["user_id"]?.ToString(),
        Ticker = r["ticker"]?.ToString() ?? "",
        CompanyName = r["company_name"]?.ToString(),
        Source = r["source"]?.ToString() ?? "",
        Category = r["category"]?.ToString(),
        CandidateScore = GetNullableDouble(r, "candidate_score"),
        CatalystScore = GetNullableDouble(r, "catalyst_score"),
        RiskScore = GetNullableDouble(r, "risk_score"),
        OptionsReadinessScore = GetNullableDouble(r, "options_readiness_score"),
        DataConfidence = r["data_confidence"]?.ToString(),
        Reason = r["reason"]?.ToString(),
        SelectedForWatchlist = r["selected_for_watchlist"]?.GetValue<bool>() ?? false,
        RawContext = r["raw_context"],
        CreatedAt = GetDateTimeOffset(r, "created_at"),
    };

    private static double? GetNullableDouble(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return null;
        if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), out var parsed) ? parsed : null;
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
