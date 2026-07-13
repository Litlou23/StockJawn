using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.StrategyDiscovery;

/// <summary>
/// Supabase-backed persistence for strategy observations and discovered strategies.
/// </summary>
public class SupabaseStrategyDiscoveryRepository : IStrategyDiscoveryRepository
{
    private const string ObservationsTable = "strategy_observations";
    private const string StrategiesTable = "discovered_strategies";

    private readonly SupabaseClient _db;
    private readonly ILogger<SupabaseStrategyDiscoveryRepository> _logger;

    public SupabaseStrategyDiscoveryRepository(
        SupabaseClient db,
        ILogger<SupabaseStrategyDiscoveryRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task StoreObservationAsync(StrategyObservationInput observation)
    {
        var row = new
        {
            prediction_id = observation.PredictionId,
            ticker = observation.Ticker,
            observation_date = observation.Date.ToString("o"),
            is_win = observation.IsWin,
            return_percent = observation.ReturnPercent,
            holding_days = observation.HoldingDays,
            conditions = observation.Conditions
                .Select(c => new { type = c.Type.ToString(), value = c.Value }).ToArray(),
        };

        var rows = await _db.InsertAsync(ObservationsTable, new[] { row }, returnRows: false);
    }

    public async Task<List<StrategyObservationInput>> GetAllObservationsAsync()
    {
        var rows = await _db.SelectAsync(ObservationsTable, order: "observation_date.desc");
        return rows.Select(MapObservation).ToList();
    }

    public async Task StoreStrategyAsync(DiscoveredStrategy strategy)
    {
        var row = new
        {
            strategy_id = strategy.StrategyId,
            pattern_id = strategy.Pattern.PatternId,
            conditions = strategy.Pattern.Conditions
                .Select(c => new { type = c.Type.ToString(), value = c.Value }).ToArray(),
            label = strategy.Pattern.Label,
            sample_size = strategy.SampleSize,
            win_rate = strategy.WinRate,
            average_return = strategy.AverageReturn,
            median_return = strategy.MedianReturn,
            confidence = strategy.Confidence.ToString(),
            summary = strategy.Summary,
            discovered_at = strategy.DiscoveredAt.ToString("o"),
        };

        var ok = await _db.UpsertAsync(StrategiesTable, row, "strategy_id");
        if (!ok)
            _logger.LogWarning("[strategy-discovery] Upsert failed for strategy {Id}", strategy.StrategyId);
    }

    public async Task<List<DiscoveredStrategy>> GetAllStrategiesAsync()
    {
        var rows = await _db.SelectAsync(StrategiesTable, order: "win_rate.desc");
        return rows.Select(MapStrategy).ToList();
    }

    // ══════════════════════════════════════════════════════════════
    // Mapping
    // ══════════════════════════════════════════════════════════════

    private static StrategyObservationInput MapObservation(JsonObject r) => new()
    {
        PredictionId = r["prediction_id"]?.ToString() ?? "",
        Ticker = r["ticker"]?.ToString(),
        Date = GetDateTimeOffset(r, "observation_date"),
        IsWin = r["is_win"]?.GetValue<bool>() ?? false,
        ReturnPercent = GetDouble(r, "return_percent"),
        HoldingDays = GetInt(r, "holding_days"),
        Conditions = ParseConditions(r["conditions"]),
    };

    private static DiscoveredStrategy MapStrategy(JsonObject r) => new()
    {
        StrategyId = r["strategy_id"]?.ToString() ?? "",
        Pattern = new StrategyPattern
        {
            PatternId = r["pattern_id"]?.ToString() ?? "",
            Conditions = ParseConditions(r["conditions"]),
            Label = r["label"]?.ToString() ?? "",
        },
        SampleSize = GetInt(r, "sample_size"),
        WinRate = GetDouble(r, "win_rate"),
        AverageReturn = GetDouble(r, "average_return"),
        MedianReturn = GetDouble(r, "median_return"),
        Confidence = Enum.TryParse<StrategyConfidence>(r["confidence"]?.ToString(), out var c)
            ? c : StrategyConfidence.Insufficient,
        Summary = r["summary"]?.ToString() ?? "",
        DiscoveredAt = GetDateTimeOffset(r, "discovered_at"),
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
