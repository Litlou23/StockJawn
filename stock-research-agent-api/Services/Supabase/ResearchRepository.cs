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

    public async Task<ResearchRun?> GetResearchRunByIdAsync(string id)
    {
        var row = await _db.SelectSingleAsync("research_runs", $"id=eq.{id}");
        return row is not null ? MapResearchRun(row) : null;
    }

    /// <summary>
    /// Returns a currently-running (status=started) research run of the given type, if any.
    /// </summary>
    public async Task<ResearchRun?> GetRunningJobAsync(string runType)
    {
        var row = await _db.SelectSingleAsync("research_runs",
            $"run_type=eq.{runType}&status=eq.started&order=started_at.desc");
        return row is not null ? MapResearchRun(row) : null;
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

        // Use RPC function to bypass PostgREST schema-cache issues with text[] columns.
        // The SQL function handles jsonb→text[] casting explicitly.
        var rpcResult = await _db.RpcAsync("insert_prediction_candidates",
            new { payload = predictions });

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(rpcResult);
            var ids = parsed?.Select(r => r.GetValueOrDefault("id", "")).Where(id => id != "").ToList() ?? [];
            if (ids.Count > 0)
                return (true, ids);
        }
        catch { /* fall through to legacy path */ }

        // Fallback: try direct INSERT (in case RPC function doesn't exist yet)
        _logger.LogWarning("[research-repo] RPC insert returned no IDs, falling back to direct INSERT");
        var rows = await _db.InsertAsync("prediction_candidates", predictions);
        var fallbackIds = rows.Select(r => r["id"]?.ToString() ?? "").Where(id => id != "").ToList();
        return (fallbackIds.Count > 0, fallbackIds);
    }

    public async Task<List<PredictionCandidate>> GetOpenPredictionsAsync()
    {
        var rows = await _db.SelectAsync("prediction_candidates",
            filter: "status=eq.open", order: "created_at.desc");
        return rows.Select(MapPrediction).ToList();
    }

    public async Task<List<PredictionCandidate>> GetRecentPredictionsAsync(int limit = 30, string? status = null, string? extraFilter = null)
    {
        var parts = new List<string>();
        if (status is not null) parts.Add($"status=eq.{status}");
        if (extraFilter is not null) parts.Add(extraFilter);
        var filter = parts.Count > 0 ? string.Join("&", parts) : null;
        var rows = await _db.SelectAsync("prediction_candidates",
            filter: filter, order: "created_at.desc", limit: limit);
        return rows.Select(MapPrediction).ToList();
    }

    public async Task<PredictionCandidate?> GetPredictionByIdAsync(string id)
    {
        var row = await _db.SelectSingleAsync("prediction_candidates", $"id=eq.{id}");
        return row is not null ? MapPrediction(row) : null;
    }

    public async Task<List<PredictionCandidate>> GetPredictionsByRunAsync(string runId)
    {
        var rows = await _db.SelectAsync("prediction_candidates",
            filter: $"run_id=eq.{runId}", order: "created_at.desc");
        return rows.Select(MapPrediction).ToList();
    }

    public async Task<List<PredictionCandidate>> GetPredictionsByDateRangeAsync(
        DateTimeOffset from, DateTimeOffset to, string? status = null, string? extraFilter = null)
    {
        var filters = new List<string>
        {
            $"created_at=gte.{from.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}",
            $"created_at=lte.{to.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}",
        };
        if (status is not null) filters.Add($"status=eq.{status}");
        if (extraFilter is not null) filters.Add(extraFilter);

        var filter = string.Join("&", filters);
        var rows = await _db.SelectAsync("prediction_candidates",
            filter: filter, order: "created_at.desc");
        return rows.Select(MapPrediction).ToList();
    }

    public async Task<bool> UpdatePredictionStatusAsync(string id, string status)
    {
        return await _db.UpdateAsync("prediction_candidates", $"id=eq.{id}", new { status });
    }

    public async Task<PredictionStatsAggregate> GetPredictionStatsAsync()
    {
        var totalTask = _db.CountAsync("prediction_candidates");
        var outcomesTotalTask = _db.CountAsync("prediction_outcomes");
        var correctTask = _db.CountAsync("prediction_outcomes", "direction_correct=eq.true");
        var incorrectTask = _db.CountAsync("prediction_outcomes", "direction_correct=eq.false");

        await Task.WhenAll(totalTask, outcomesTotalTask, correctTask, incorrectTask);

        var total = totalTask.Result;
        var outcomesTotal = outcomesTotalTask.Result;
        var correct = correctTask.Result;
        var incorrect = incorrectTask.Result;
        var inconclusive = outcomesTotal - correct - incorrect;
        var pending = total - outcomesTotal;
        var denominator = correct + incorrect;

        return new PredictionStatsAggregate
        {
            TotalPredictions = total,
            EvaluatedPredictions = correct + incorrect,
            CorrectPredictions = correct,
            IncorrectPredictions = incorrect,
            InconclusivePredictions = inconclusive > 0 ? inconclusive : 0,
            PendingPredictions = pending > 0 ? pending : 0,
            AccuracyPercent = denominator > 0
                ? Math.Round(100.0 * correct / denominator, 1)
                : null,
        };
    }

    public async Task<List<PredictionWithOutcome>> GetRecentPredictionsWithOutcomesAsync(int limit = 10)
    {
        var predictions = await GetRecentPredictionsAsync(limit);
        if (predictions.Count == 0) return [];

        var predictionIds = predictions.Select(p => p.Id).ToList();
        var filter = $"prediction_id=in.({string.Join(",", predictionIds)})";
        var outcomeRows = await _db.SelectAsync("prediction_outcomes", filter: filter);
        var outcomes = outcomeRows.Select(MapOutcome).ToList();

        var outcomeMap = outcomes
            .GroupBy(o => o.PredictionId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.EvaluationTime).First());

        return predictions.Select(p =>
        {
            outcomeMap.TryGetValue(p.Id, out var outcome);
            return new PredictionWithOutcome
            {
                Prediction = p,
                Outcome = outcome,
            };
        }).ToList();
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

    public async Task<List<PredictionOutcome>> GetOutcomesForPredictionsAsync(List<string> predictionIds)
    {
        if (predictionIds.Count == 0) return [];
        var filter = $"prediction_id=in.({string.Join(",", predictionIds)})";
        var rows = await _db.SelectAsync("prediction_outcomes", filter: filter);
        return rows.Select(MapOutcome).ToList();
    }

    // -----------------------------------------------------------------------
    // Signal Performance
    // -----------------------------------------------------------------------

    public async Task<bool> UpsertSignalPerformanceAsync(object perf)
    {
        return await _db.UpsertAsync("research_signal_performance", perf, "signal_name,direction");
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
    // Category-based stats
    // -----------------------------------------------------------------------

    private static readonly string DirectionalTypes = "prediction_type=in.(bullish,bearish)";
    private static readonly string ShortTermWindows = "time_window=in.(intraday,1_day,3_day,1_week)";
    private static readonly string LongTermWindows = "time_window=in.(1_month,3_month,6_month,1_year)";
    private static readonly string NonDirectionalTypes =
        "prediction_type=in.(neutral_no_edge,neutral_range_bound,neutral_high_volatility,watch_only,rejected,unavailable,neutral)";

    public async Task<CategoryStatsAggregate> GetDirectionalStockStatsAsync()
    {
        var filter = $"{DirectionalTypes}&{ShortTermWindows}";
        return await BuildCategoryStats(PredictionCategory.short_term_stock, filter);
    }

    public async Task<CategoryStatsAggregate> GetLongTermStockStatsAsync()
    {
        var filter = $"{DirectionalTypes}&{LongTermWindows}";
        return await BuildCategoryStats(PredictionCategory.long_term_stock, filter);
    }

    private async Task<CategoryStatsAggregate> BuildCategoryStats(PredictionCategory category, string predFilter)
    {
        var totalTask = _db.CountAsync("prediction_candidates", predFilter);

        var predRows = await _db.SelectAsync("prediction_candidates",
            select: "id", filter: predFilter, limit: 10000);
        var predIds = predRows.Select(r => r["id"]?.ToString() ?? "").Where(id => id != "").ToList();

        var total = await totalTask;

        if (predIds.Count == 0)
            return new CategoryStatsAggregate { Category = category };

        var idsFilter = $"prediction_id=in.({string.Join(",", predIds)})";
        var correctTask = _db.CountAsync("prediction_outcomes", $"{idsFilter}&direction_correct=eq.true");
        var incorrectTask = _db.CountAsync("prediction_outcomes", $"{idsFilter}&direction_correct=eq.false");

        await Task.WhenAll(correctTask, incorrectTask);
        var correct = correctTask.Result;
        var incorrect = incorrectTask.Result;
        var evaluated = correct + incorrect;
        var pending = total - evaluated;
        var denom = correct + incorrect;

        return new CategoryStatsAggregate
        {
            Category = category,
            Total = total,
            Evaluated = evaluated,
            Correct = correct,
            Incorrect = incorrect,
            Pending = pending > 0 ? pending : 0,
            AccuracyPercent = denom > 0 ? Math.Round(100.0 * correct / denom, 1) : null,
        };
    }

    public async Task<ScanResultStats> GetScanResultStatsAsync()
    {
        var totalTask = _db.CountAsync("prediction_candidates", NonDirectionalTypes);
        var noEdgeTask = _db.CountAsync("prediction_candidates", "prediction_type=eq.neutral_no_edge");
        var rangeBoundTask = _db.CountAsync("prediction_candidates", "prediction_type=eq.neutral_range_bound");
        var highVolTask = _db.CountAsync("prediction_candidates", "prediction_type=eq.neutral_high_volatility");
        var watchTask = _db.CountAsync("prediction_candidates", "prediction_type=eq.watch_only");
        var rejectedTask = _db.CountAsync("prediction_candidates", "prediction_type=eq.rejected");
        var unavailTask = _db.CountAsync("prediction_candidates", "prediction_type=eq.unavailable");
        var legacyTask = _db.CountAsync("prediction_candidates", "prediction_type=eq.neutral");

        await Task.WhenAll(totalTask, noEdgeTask, rangeBoundTask, highVolTask,
            watchTask, rejectedTask, unavailTask, legacyTask);

        return new ScanResultStats
        {
            Total = totalTask.Result,
            NeutralNoEdge = noEdgeTask.Result,
            NeutralRangeBound = rangeBoundTask.Result,
            NeutralHighVolatility = highVolTask.Result,
            WatchOnly = watchTask.Result,
            Rejected = rejectedTask.Result,
            Unavailable = unavailTask.Result,
            Legacy = legacyTask.Result,
        };
    }

    public async Task<List<PredictionCandidate>> GetRecentScanResultsAsync(int limit = 20)
    {
        var rows = await _db.SelectAsync("prediction_candidates",
            filter: NonDirectionalTypes,
            order: "created_at.desc",
            limit: limit);
        return rows.Select(MapPrediction).ToList();
    }

    public async Task<List<PredictionCandidate>> GetRecentDirectionalPredictionsAsync(int limit = 10)
    {
        var rows = await _db.SelectAsync("prediction_candidates",
            filter: DirectionalTypes,
            order: "created_at.desc",
            limit: limit);
        return rows.Select(MapPrediction).ToList();
    }

    public async Task<PaperOptionStatsAggregate> GetPaperOptionStatsAsync()
    {
        var totalTask = _db.CountAsync("paper_option_candidates");
        var openTask = _db.CountAsync("paper_option_candidates", "status=eq.open");
        var profitableTask = _db.CountAsync("paper_option_outcomes", "contract_profitable=eq.true");
        var unprofitableTask = _db.CountAsync("paper_option_outcomes", "contract_profitable=eq.false");

        await Task.WhenAll(totalTask, openTask, profitableTask, unprofitableTask);
        var total = totalTask.Result;
        var open = openTask.Result;
        var profitable = profitableTask.Result;
        var unprofitable = unprofitableTask.Result;
        var evaluated = profitable + unprofitable;
        var denom = profitable + unprofitable;

        return new PaperOptionStatsAggregate
        {
            Total = total,
            Evaluated = evaluated,
            Profitable = profitable,
            Unprofitable = unprofitable,
            Open = open,
            WinRatePercent = denom > 0 ? Math.Round(100.0 * profitable / denom, 1) : null,
        };
    }

    // -----------------------------------------------------------------------
    // Generic insert (for options lab and future tables)
    // -----------------------------------------------------------------------

    public async Task InsertGenericAsync(string table, object row)
    {
        if (!_db.IsConfigured) return;
        await _db.InsertAsync(table, new[] { row }, returnRows: false);
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
        PredictionType = Enum.TryParse<PredictionType>(r["prediction_type"]?.ToString(), out var pt) ? pt : PredictionType.neutral_no_edge,
        AssetType = Enum.TryParse<PredictionAssetType>(r["asset_type"]?.ToString(), out var at) ? at : PredictionAssetType.stock,
        TimeWindow = r["time_window"]?.ToString() ?? "1_day",
        ConfidenceScore = GetInt(r, "confidence_score"),
        ImportanceScore = GetInt(r, "importance_score"),
        RiskScore = GetInt(r, "risk_score"),
        EntryReferencePrice = r["entry_reference_price"]?.GetValue<double?>(),
        Atr14 = GetNullableDouble(r, "atr14"),
        AtrPercent = GetNullableDouble(r, "atr_percent"),
        TimeframeMultiplier = GetNullableDouble(r, "timeframe_multiplier"),
        SignalModifier = GetNullableDouble(r, "signal_modifier"),
        ExpectedMoveDollar = GetNullableDouble(r, "expected_move_dollar"),
        ExpectedMovePercent = GetNullableDouble(r, "expected_move_percent"),
        PredictedPrice = GetNullableDouble(r, "predicted_price"),
        PredictedMovePercent = GetNullableDouble(r, "predicted_move_percent"),
        ProjectedPriceLow = GetNullableDouble(r, "projected_price_low"),
        ProjectedPriceHigh = GetNullableDouble(r, "projected_price_high"),
        TargetPrice = GetNullableDouble(r, "target_price"),
        StopPrice = GetNullableDouble(r, "stop_price"),
        InvalidationPrice = GetNullableDouble(r, "invalidation_price"),
        SupportLevel = GetNullableDouble(r, "support_level"),
        ResistanceLevel = GetNullableDouble(r, "resistance_level"),
        RiskRewardRatio = GetNullableDouble(r, "risk_reward_ratio"),
        PricePredictionMethod = r["price_prediction_method"]?.ToString(),
        PricePredictionWarnings = GetStringList(r, "price_prediction_warnings"),
        ScoreDebugJson = r["score_debug_json"]?.ToString(),
        ActionabilityScore = r["actionability_score"] is null ? null : GetInt(r, "actionability_score"),
        ActionabilityTier = Enum.TryParse<ActionabilityTier>(r["actionability_tier"]?.ToString(), out var actTier)
            ? actTier : (ActionabilityTier?)null,
        BullishScore = GetNullableDouble(r, "bullish_score"),
        BearishScore = GetNullableDouble(r, "bearish_score"),
        WinningDirection = r["winning_direction"]?.ToString(),
        DirectionConfidence = GetNullableDouble(r, "direction_confidence"),
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
        PredictedPrice = GetNullableDouble(r, "predicted_price"),
        PredictedMovePercent = GetNullableDouble(r, "predicted_move_percent"),
        ProjectedPriceLow = GetNullableDouble(r, "projected_price_low"),
        ProjectedPriceHigh = GetNullableDouble(r, "projected_price_high"),
        PriceAccuracyPercent = GetNullableDouble(r, "price_accuracy_percent"),
        PricePredictionErrorPercent = GetNullableDouble(r, "price_prediction_error_percent"),
        WasInProjectedZone = GetNullableBool(r, "was_in_projected_zone"),
        TargetHit = GetNullableBool(r, "target_hit"),
        StopHit = GetNullableBool(r, "stop_hit"),
        InvalidationHit = GetNullableBool(r, "invalidation_hit"),
        MaxFavorablePercent = GetNullableDouble(r, "max_favorable_percent"),
        MaxAdversePercent = GetNullableDouble(r, "max_adverse_percent"),
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
        Direction = r["direction"]?.ToString() ?? "all",
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
