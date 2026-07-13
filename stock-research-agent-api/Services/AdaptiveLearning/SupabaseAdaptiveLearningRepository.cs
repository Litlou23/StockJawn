using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.AdaptiveLearning;

/// <summary>
/// Supabase-backed implementation of <see cref="IAdaptiveLearningRepository"/>.
/// Persists conditional signal performance data across restarts.
/// Uses the PostgREST-based <see cref="SupabaseClient"/>.
/// </summary>
public class SupabaseAdaptiveLearningRepository : IAdaptiveLearningRepository
{
    private const string Table = "conditional_signal_performance";
    private readonly SupabaseClient _db;
    private readonly ILogger<SupabaseAdaptiveLearningRepository> _logger;

    public SupabaseAdaptiveLearningRepository(
        SupabaseClient db,
        ILogger<SupabaseAdaptiveLearningRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task UpsertPerformanceAsync(ConditionalSignalPerformance performance)
    {
        var conditionsKey = BuildKey(performance.SignalName, performance.Conditions);

        var row = new
        {
            signal_name = performance.SignalName,
            conditions_key = conditionsKey,
            conditions = performance.Conditions.Select(c => new { type = c.Type.ToString(), value = c.Value }).ToArray(),
            sample_size = performance.SampleSize,
            win_rate = performance.WinRate,
            average_return = performance.AverageReturn,
            median_return = performance.MedianReturn,
            average_holding_days = performance.AverageHoldingDays,
            confidence = performance.Confidence,
            last_updated = DateTimeOffset.UtcNow.ToString("o"),
        };

        var ok = await _db.UpsertAsync(Table, row, "signal_name,conditions_key");
        if (!ok)
            _logger.LogWarning("[adaptive-learning] Upsert failed for {Signal} :: {Key}", performance.SignalName, conditionsKey);
    }

    public async Task<List<ConditionalSignalPerformance>> QueryAsync(ConditionalPerformanceQuery query)
    {
        var filters = new List<string>();

        if (query.SignalName is not null)
            filters.Add($"signal_name=ilike.{query.SignalName}");

        if (query.MinSampleSize > 0)
            filters.Add($"sample_size=gte.{query.MinSampleSize}");

        var filter = filters.Count > 0 ? string.Join("&", filters) : null;
        var rows = await _db.SelectAsync(Table, filter);

        var results = rows.Select(MapPerformance).ToList();

        // Apply in-memory condition filtering (JSONB condition matching is complex in PostgREST)
        if (query.Conditions.Count > 0)
        {
            results = results.Where(p =>
                query.Conditions.All(qc =>
                    p.Conditions.Any(pc =>
                        pc.Type == qc.Type &&
                        pc.Value.Equals(qc.Value, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }

        return results;
    }

    public async Task<List<ConditionalSignalPerformance>> GetBySignalAsync(string signalName)
    {
        var rows = await _db.SelectAsync(Table, $"signal_name=ilike.{signalName}");
        return rows.Select(MapPerformance).ToList();
    }

    // ══════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════

    private static string BuildKey(string signal, List<LearningCondition> conditions)
    {
        var condPart = string.Join("|",
            conditions.OrderBy(c => c.Type).ThenBy(c => c.Value)
                .Select(c => $"{c.Type}:{c.Value}"));
        return $"{signal.ToLowerInvariant()}::{condPart}";
    }

    private static ConditionalSignalPerformance MapPerformance(JsonObject r) => new()
    {
        SignalName = r["signal_name"]?.ToString() ?? "",
        Conditions = ParseConditions(r["conditions"]),
        SampleSize = GetInt(r, "sample_size"),
        WinRate = GetDouble(r, "win_rate"),
        AverageReturn = GetDouble(r, "average_return"),
        MedianReturn = GetDouble(r, "median_return"),
        AverageHoldingDays = GetDouble(r, "average_holding_days"),
        Confidence = GetDouble(r, "confidence"),
        LastUpdated = GetDateTimeOffset(r, "last_updated"),
    };

    private static List<LearningCondition> ParseConditions(JsonNode? node)
    {
        if (node is not JsonArray arr) return [];
        var results = new List<LearningCondition>();
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            var typeStr = obj["type"]?.ToString() ?? "";
            if (Enum.TryParse<LearningConditionType>(typeStr, out var condType))
            {
                results.Add(new LearningCondition
                {
                    Type = condType,
                    Value = obj["value"]?.ToString() ?? "",
                });
            }
        }
        return results;
    }

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
