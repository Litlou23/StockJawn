using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Supabase;

/// <summary>
/// Supabase CRUD for option_learning_stats table.
/// </summary>
public class OptionLearningRepository
{
    private readonly SupabaseClient _db;
    private readonly ILogger<OptionLearningRepository> _logger;

    public OptionLearningRepository(SupabaseClient db, ILogger<OptionLearningRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<OptionLearningStat>> GetAllStatsAsync()
    {
        var rows = await _db.SelectAsync("option_learning_stats",
            order: "stat_type.asc,stat_key.asc");
        return rows.Select(MapStat).ToList();
    }

    public async Task<OptionLearningStat?> GetStatAsync(string statType, string statKey)
    {
        var row = await _db.SelectSingleAsync("option_learning_stats",
            $"stat_type=eq.{statType}&stat_key=eq.{statKey}");
        return row is not null ? MapStat(row) : null;
    }

    public async Task<bool> UpsertStatAsync(string statType, string statKey,
        int totalCandidates, int profitableCandidates, double winRate,
        double avgOptionMove, double avgUnderlyingMove, double avgScore)
    {
        return await _db.UpsertAsync("option_learning_stats", new[]
        {
            new
            {
                stat_type = statType,
                stat_key = statKey,
                total_candidates = totalCandidates,
                profitable_candidates = profitableCandidates,
                win_rate = Math.Round(winRate, 4),
                average_option_move_percent = Math.Round(avgOptionMove, 2),
                average_underlying_move_percent = Math.Round(avgUnderlyingMove, 2),
                average_outcome_score = Math.Round(avgScore, 2),
                last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
            }
        }, onConflict: "stat_type,stat_key");
    }

    private static OptionLearningStat MapStat(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        StatType = r["stat_type"]?.ToString() ?? "",
        StatKey = r["stat_key"]?.ToString() ?? "",
        TotalCandidates = GetInt(r, "total_candidates"),
        ProfitableCandidates = GetInt(r, "profitable_candidates"),
        WinRate = GetDouble(r, "win_rate"),
        AverageOptionMovePercent = GetDouble(r, "average_option_move_percent"),
        AverageUnderlyingMovePercent = GetDouble(r, "average_underlying_move_percent"),
        AverageOutcomeScore = GetDouble(r, "average_outcome_score"),
        LastUpdatedAt = GetDateTimeOffset(r, "last_updated_at"),
    };

    private static int GetInt(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return 0;
        if (node is JsonValue jv && jv.TryGetValue<int>(out var i)) return i;
        return int.TryParse(node.ToString(), out var parsed) ? parsed : 0;
    }

    private static double GetDouble(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return 0;
        if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), out var parsed) ? parsed : 0;
    }

    private static DateTimeOffset GetDateTimeOffset(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(node.ToString(), out var dt) ? dt : DateTimeOffset.MinValue;
    }
}
