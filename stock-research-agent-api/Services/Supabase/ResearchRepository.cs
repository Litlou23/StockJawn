using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Supabase;

/// <summary>
/// Full CRUD for the 8 research engine tables, ported from the Next.js
/// researchRepository.ts. Uses the PostgREST-based SupabaseClient.
/// </summary>
public class ResearchRepository
{
    private readonly SupabaseClient _db;
    private readonly ILogger<ResearchRepository> _logger;

    public ResearchRepository(SupabaseClient db, ILogger<ResearchRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public bool IsConfigured => _db.IsConfigured;

    // -----------------------------------------------------------------------
    // Research Runs
    // -----------------------------------------------------------------------

    public async Task<ResearchRun?> CreateResearchRunAsync(string runType)
    {
        if (!_db.IsConfigured) return null;
        var rows = await _db.InsertAsync("research_runs", new[]
        {
            new { run_type = runType, status = "started" }
        });
        return rows.Count > 0 ? MapResearchRun(rows[0]) : null;
    }

    public async Task<bool> CompleteResearchRunAsync(
        string id, string summary, int predictionsGenerated, int predictionsEvaluated, List<string> errors)
    {
        return await _db.UpdateAsync("research_runs", $"id=eq.{id}", new
        {
            status = errors.Count > 0 ? "failed" : "completed",
            completed_at = DateTimeOffset.UtcNow.ToString("o"),
            summary,
            errors = errors.ToArray(),
            predictions_generated = predictionsGenerated,
            predictions_evaluated = predictionsEvaluated,
        });
    }

    public async Task<ResearchRun?> GetLatestResearchRunAsync(string? runType = null)
    {
        var filter = runType is not null ? $"run_type=eq.{runType}" : null;
        var row = await _db.SelectSingleAsync("research_runs",
            (filter is not null ? filter + "&" : "") + "order=started_at.desc");
        return row is not null ? MapResearchRun(row) : null;
    }

    public async Task<List<ResearchRun>> GetRecentResearchRunsAsync(int limit = 10)
    {
        var rows = await _db.SelectAsync("research_runs", order: "started_at.desc", limit: limit);
        return rows.Select(MapResearchRun).ToList();
    }

    // -----------------------------------------------------------------------
    // Market Snapshots
    // -----------------------------------------------------------------------

    public async Task<bool> SaveMarketSnapshotsAsync(List<object> snapshots)
    {
        if (snapshots.Count == 0) return true;
        var rows = await _db.InsertAsync("market_snapshots", snapshots, returnRows: false);
        return true; // InsertAsync logs failures
    }

    // -----------------------------------------------------------------------
    // Prediction Candidates
    // -----------------------------------------------------------------------

    public async Task<(bool Persisted, List<string> Ids)> SavePredictionsAsync(List<object> predictions)
    {
        if (predictions.Count == 0) return (true, []);
        var rows = await _db.InsertAsync("prediction_candidates", predictions);
        var ids = rows.Select(r => r["id"]?.ToString() ?? "").Where(id => id != "").ToList();
        return (ids.Count > 0, ids);
    }

    public async Task<List<PredictionCandidate>> GetOpenPredictionsAsync()
    {
        var rows = await _db.SelectAsync("prediction_candidates",
            filter: "status=eq.open", order: "created_at.desc");
        return rows.Select(MapPrediction).ToList();
    }

    public async Task<List<PredictionCandidate>> GetRecentPredictionsAsync(int limit = 30, string? status = null)
    {
        var filter = status is not null ? $"status=eq.{status}" : null;
        var rows = await _db.SelectAsync("prediction_candidates",
            filter: filter, order: "created_at.desc", limit: limit);
        return rows.Select(MapPrediction).ToList();
    }

    public async Task<bool> UpdatePredictionStatusAsync(string id, string status)
    {
        return await _db.UpdateAsync("prediction_candidates", $"id=eq.{id}", new { status });
    }

    // -----------------------------------------------------------------------
    // Prediction Inputs
    // -----------------------------------------------------------------------

    public async Task<bool> SavePredictionInputsAsync(List<object> inputs)
    {
        if (inputs.Count == 0) return true;
        await _db.InsertAsync("prediction_inputs", inputs, returnRows: false);
        return true;
    }

    // -----------------------------------------------------------------------
    // Prediction Outcomes
    // -----------------------------------------------------------------------

    public async Task<bool> SaveOutcomeAsync(object outcome)
    {
        var rows = await _db.InsertAsync("prediction_outcomes", new[] { outcome }, returnRows: false);
        return true;
    }

    public async Task<List<PredictionOutcome>> GetRecentOutcomesAsync(int limit = 50)
    {
        var rows = await _db.SelectAsync("prediction_outcomes",
            order: "created_at.desc", limit: limit);
        return rows.Select(MapOutcome).ToList();
    }

    // -----------------------------------------------------------------------
    // Signal Performance
    // -----------------------------------------------------------------------

    public async Task<bool> UpsertSignalPerformanceAsync(object perf)
    {
        return await _db.UpsertAsync("research_signal_performance", perf, "signal_name");
    }

    public async Task<List<ResearchSignalPerformance>> GetAllSignalPerformanceAsync()
    {
        var rows = await _db.SelectAsync("research_signal_performance", order: "accuracy.desc");
        return rows.Select(MapSignalPerf).ToList();
    }

    // -----------------------------------------------------------------------
    // Scoring Weights
    // -----------------------------------------------------------------------

    public async Task<List<ScoringWeight>> GetScoringWeightsAsync()
    {
        var rows = await _db.SelectAsync("research_scoring_weights");
        return rows.Select(r => new ScoringWeight
        {
            Id = r["id"]?.ToString() ?? "",
            SignalName = r["signal_name"]?.ToString() ?? "",
            Weight = GetDouble(r, "weight"),
            Reason = r["reason"]?.ToString() ?? "",
            UpdatedAt = GetDateTimeOffset(r, "updated_at"),
        }).ToList();
    }

    public async Task<bool> UpdateScoringWeightAsync(string signalName, double weight, string reason)
    {
        return await _db.UpsertAsync("research_scoring_weights", new
        {
            signal_name = signalName,
            weight,
            reason,
            updated_at = DateTimeOffset.UtcNow.ToString("o"),
        }, "signal_name");
    }

    // -----------------------------------------------------------------------
    // Learning Insights
    // -----------------------------------------------------------------------

    public async Task<bool> SaveLearningInsightsAsync(List<object> insights)
    {
        if (insights.Count == 0) return true;
        await _db.InsertAsync("learning_insights", insights, returnRows: false);
        return true;
    }

    public async Task<List<LearningInsight>> GetRecentLearningInsightsAsync(int limit = 20)
    {
        var rows = await _db.SelectAsync("learning_insights",
            order: "created_at.desc", limit: limit);
        return rows.Select(r => new LearningInsight
        {
            Id = r["id"]?.ToString() ?? "",
            InsightType = r["insight_type"]?.ToString() ?? "",
            Summary = r["summary"]?.ToString() ?? "",
            Evidence = r["evidence"]?.ToString() ?? "",
            ActionRecommendation = r["action_recommendation"]?.ToString() ?? "",
            Confidence = GetDouble(r, "confidence"),
            CreatedAt = GetDateTimeOffset(r, "created_at"),
        }).ToList();
    }

    // -----------------------------------------------------------------------
    // Row mappers
    // -----------------------------------------------------------------------

    private static ResearchRun MapResearchRun(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        RunType = Enum.TryParse<ResearchRunType>(r["run_type"]?.ToString(), out var rt) ? rt : ResearchRunType.morning_scan,
        Status = Enum.TryParse<ResearchRunStatus>(r["status"]?.ToString(), out var rs) ? rs : ResearchRunStatus.started,
        StartedAt = GetDateTimeOffset(r, "started_at"),
        CompletedAt = r["completed_at"] is not null ? GetDateTimeOffset(r, "completed_at") : null,
        Summary = r["summary"]?.ToString(),
        Errors = GetStringList(r, "errors"),
        PredictionsGenerated = GetInt(r, "predictions_generated"),
        PredictionsEvaluated = GetInt(r, "predictions_evaluated"),
    };

    private static PredictionCandidate MapPrediction(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        RunId = r["run_id"]?.ToString() ?? "",
        Ticker = r["ticker"]?.ToString() ?? "",
        PredictionType = Enum.TryParse<PredictionType>(r["prediction_type"]?.ToString(), out var pt) ? pt : PredictionType.neutral,
        AssetType = Enum.TryParse<PredictionAssetType>(r["asset_type"]?.ToString(), out var at) ? at : PredictionAssetType.stock,
        TimeWindow = r["time_window"]?.ToString() ?? "1_day",
        ConfidenceScore = GetInt(r, "confidence_score"),
        ImportanceScore = GetInt(r, "importance_score"),
        RiskScore = GetInt(r, "risk_score"),
        EntryReferencePrice = r["entry_reference_price"]?.GetValue<double?>(),
        BullishCase = r["bullish_case"]?.ToString() ?? "",
        BearishCase = r["bearish_case"]?.ToString() ?? "",
        PredictionReason = r["prediction_reason"]?.ToString() ?? "",
        InvalidationRule = r["invalidation_rule"]?.ToString() ?? "",
        DataSourcesUsed = GetStringList(r, "data_sources_used"),
        MissingDataWarnings = GetStringList(r, "missing_data_warnings"),
        Status = r["status"]?.ToString() ?? "open",
        CreatedAt = GetDateTimeOffset(r, "created_at"),
    };

    private static PredictionOutcome MapOutcome(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        PredictionId = r["prediction_id"]?.ToString() ?? "",
        EvaluationTime = GetDateTimeOffset(r, "evaluation_time"),
        StartPrice = GetNullableDouble(r, "start_price"),
        ClosePrice = GetNullableDouble(r, "close_price"),
        HighAfterPrediction = GetNullableDouble(r, "high_after_prediction"),
        LowAfterPrediction = GetNullableDouble(r, "low_after_prediction"),
        PercentMove = GetNullableDouble(r, "percent_move"),
        DirectionCorrect = GetNullableBool(r, "direction_correct"),
        InvalidationHit = GetNullableBool(r, "invalidation_hit"),
        OutcomeScore = GetNullableDouble(r, "outcome_score"),
        OutcomeSummary = r["outcome_summary"]?.ToString(),
        Lesson = r["lesson"]?.ToString(),
        CreatedAt = GetDateTimeOffset(r, "created_at"),
    };

    private static ResearchSignalPerformance MapSignalPerf(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        SignalName = r["signal_name"]?.ToString() ?? "",
        SignalType = r["signal_type"]?.ToString() ?? "",
        TotalPredictions = GetInt(r, "total_predictions"),
        CorrectPredictions = GetInt(r, "correct_predictions"),
        Accuracy = GetDouble(r, "accuracy"),
        AverageOutcomeScore = GetDouble(r, "average_outcome_score"),
        LastUpdatedAt = GetDateTimeOffset(r, "last_updated_at"),
    };

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

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

    private static List<string> GetStringList(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return [];
        if (node is JsonArray arr)
            return arr.Select(n => n?.ToString() ?? "").Where(s => s != "").ToList();
        return [];
    }
}
