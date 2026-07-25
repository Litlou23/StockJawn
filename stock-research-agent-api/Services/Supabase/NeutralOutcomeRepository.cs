using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Supabase;

/// <summary>
/// Persistence layer for neutral prediction outcomes.
/// Parallel to PaperStockCandidateRepository's outcome methods but for non-directional predictions.
/// </summary>
public class NeutralOutcomeRepository
{
    private readonly SupabaseClient _db;

    public NeutralOutcomeRepository(SupabaseClient db) => _db = db;

    public async Task<bool> SaveOutcomeAsync(NeutralPredictionOutcome o)
    {
        await _db.InsertAsync("neutral_prediction_outcomes", new[]
        {
            new
            {
                id = o.Id,
                prediction_id = o.PredictionId,
                paper_stock_candidate_id = o.PaperStockCandidateId,
                ticker = o.Ticker,
                prediction_type = o.PredictionType,
                time_window = o.TimeWindow,
                entry_price = o.EntryPrice,
                exit_price = o.ExitPrice,
                high_after = o.HighAfter,
                low_after = o.LowAfter,
                realized_move_percent = o.RealizedMovePercent,
                absolute_move_percent = o.AbsoluteMovePercent,
                max_run_up = o.MaxRunUp,
                max_drawdown = o.MaxDrawdown,
                realized_volatility = o.RealizedVolatility,
                neutral_accuracy_score = o.NeutralAccuracyScore,
                volatility_prediction_accuracy = o.VolatilityPredictionAccuracy,
                range_adherence_percent = o.RangeAdherencePercent,
                support_broken = o.SupportBroken,
                resistance_broken = o.ResistanceBroken,
                max_range_excursion_percent = o.MaxRangeExcursionPercent,
                breakout_occurred = o.BreakoutOccurred,
                directional_persistence = o.DirectionalPersistence,
                counterfactual_direction = o.CounterfactualDirection,
                counterfactual_correct = o.CounterfactualCorrect,
                opportunity_missed_score = o.OpportunityMissedScore,
                original_bull_score = o.OriginalBullScore,
                original_bear_score = o.OriginalBearScore,
                outcome_summary = o.OutcomeSummary,
                lesson = o.Lesson,
                evaluation_time = o.EvaluationTime.ToString("o"),
            }
        }, returnRows: false);
        return true;
    }

    public async Task<List<NeutralPredictionOutcome>> GetRecentOutcomesAsync(int limit = 50)
    {
        var rows = await _db.SelectAsync("neutral_prediction_outcomes",
            order: "evaluation_time.desc", limit: limit);
        return rows.Select(MapOutcome).ToList();
    }

    public async Task<NeutralPredictionOutcome?> GetByPredictionIdAsync(string predictionId)
    {
        var row = await _db.SelectSingleAsync("neutral_prediction_outcomes",
            $"prediction_id=eq.{predictionId}");
        return row is not null ? MapOutcome(row) : null;
    }

    public async Task<List<NeutralPredictionOutcome>> GetForPredictionsAsync(List<string> predictionIds)
    {
        if (predictionIds.Count == 0) return new();

        // Chunk to avoid exceeding PostgREST URL length limits with large in() filters
        const int chunkSize = 100;
        var results = new List<NeutralPredictionOutcome>();
        foreach (var chunk in predictionIds.Chunk(chunkSize))
        {
            var ids = string.Join(",", chunk.Select(id => $"\"{id}\""));
            var rows = await _db.SelectAsync("neutral_prediction_outcomes",
                filter: $"prediction_id=in.({ids})", limit: chunk.Length);
            results.AddRange(rows.Select(MapOutcome));
        }
        return results;
    }

    public async Task<List<NeutralPredictionOutcome>> GetByTypeAsync(string predictionType, int limit = 100)
    {
        var rows = await _db.SelectAsync("neutral_prediction_outcomes",
            filter: $"prediction_type=eq.{predictionType}",
            order: "evaluation_time.desc", limit: limit);
        return rows.Select(MapOutcome).ToList();
    }

    private static NeutralPredictionOutcome MapOutcome(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        PredictionId = r["prediction_id"]?.ToString() ?? "",
        PaperStockCandidateId = r["paper_stock_candidate_id"]?.ToString(),
        Ticker = r["ticker"]?.ToString() ?? "",
        PredictionType = r["prediction_type"]?.ToString() ?? "",
        TimeWindow = r["time_window"]?.ToString() ?? "",
        EntryPrice = GetNullableDouble(r, "entry_price"),
        ExitPrice = GetNullableDouble(r, "exit_price"),
        HighAfter = GetNullableDouble(r, "high_after"),
        LowAfter = GetNullableDouble(r, "low_after"),
        RealizedMovePercent = GetDouble(r, "realized_move_percent"),
        AbsoluteMovePercent = GetDouble(r, "absolute_move_percent"),
        MaxRunUp = GetDouble(r, "max_run_up"),
        MaxDrawdown = GetDouble(r, "max_drawdown"),
        RealizedVolatility = GetDouble(r, "realized_volatility"),
        NeutralAccuracyScore = GetDouble(r, "neutral_accuracy_score"),
        VolatilityPredictionAccuracy = GetNullableDouble(r, "volatility_prediction_accuracy"),
        RangeAdherencePercent = GetNullableDouble(r, "range_adherence_percent"),
        SupportBroken = GetNullableBool(r, "support_broken"),
        ResistanceBroken = GetNullableBool(r, "resistance_broken"),
        MaxRangeExcursionPercent = GetNullableDouble(r, "max_range_excursion_percent"),
        BreakoutOccurred = GetNullableBool(r, "breakout_occurred"),
        DirectionalPersistence = GetNullableDouble(r, "directional_persistence"),
        CounterfactualDirection = r["counterfactual_direction"]?.ToString(),
        CounterfactualCorrect = GetNullableBool(r, "counterfactual_correct"),
        OpportunityMissedScore = GetDouble(r, "opportunity_missed_score"),
        OriginalBullScore = GetNullableDouble(r, "original_bull_score"),
        OriginalBearScore = GetNullableDouble(r, "original_bear_score"),
        OutcomeSummary = r["outcome_summary"]?.ToString() ?? "",
        Lesson = r["lesson"]?.ToString(),
        EvaluationTime = GetDateTimeOffset(r, "evaluation_time"),
        CreatedAt = GetDateTimeOffset(r, "created_at"),
    };

    // Helpers (same pattern as PaperStockCandidateRepository)
    private static double GetDouble(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return 0;
        if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), out var parsed) ? parsed : 0;
    }

    private static double? GetNullableDouble(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return null;
        if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), out var parsed) ? parsed : null;
    }

    private static bool? GetNullableBool(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return null;
        if (node is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        return bool.TryParse(node.ToString(), out var parsed) ? parsed : null;
    }

    private static DateTimeOffset GetDateTimeOffset(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(node.ToString(), out var dt) ? dt : DateTimeOffset.MinValue;
    }
}
