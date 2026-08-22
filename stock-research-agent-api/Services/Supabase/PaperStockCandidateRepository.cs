using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Supabase;

/// <summary>
/// Supabase CRUD for paper_stock_candidates, paper_stock_outcomes, and
/// stock_learning_stats. Parallels OptionsDataRepository — same patterns,
/// same conventions (PostgREST + snake_case + JsonNode helpers).
/// </summary>
public class PaperStockCandidateRepository
{
    private readonly SupabaseClient _db;
    private readonly ILogger<PaperStockCandidateRepository> _logger;

    public PaperStockCandidateRepository(SupabaseClient db, ILogger<PaperStockCandidateRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // paper_stock_candidates
    // -----------------------------------------------------------------------

    public async Task<PaperStockCandidate?> SaveCandidateAsync(PaperStockCandidate c)
    {
        var rows = await _db.InsertAsync("paper_stock_candidates", new[]
        {
            new
            {
                prediction_id = c.PredictionId,
                run_id = c.RunId,
                ticker = c.Ticker,
                prediction_type = c.PredictionType.ToString(),
                timeframe = c.Timeframe.ToString(),
                entry_price = c.EntryPrice,
                reference_price = c.ReferencePrice,
                target_price = c.TargetPrice,
                stop_price = c.StopPrice,
                catalyst_score = c.CatalystScore,
                trend_score = c.TrendScore,
                volume_score = c.VolumeScore,
                market_context_score = c.MarketContextScore,
                historical_accuracy_score = c.HistoricalAccuracyScore,
                risk_penalty = c.RiskPenalty,
                missing_data_penalty = c.MissingDataPenalty,
                total_score = c.TotalScore,
                confidence_score = c.ConfidenceScore,
                risk_score = c.RiskScore,
                catalyst_type = c.CatalystType,
                selection_reason = c.SelectionReason,
                warnings_json = JsonSerializer.SerializeToNode(c.Warnings),
                data_availability = c.DataAvailability,
                candidate_mode = c.CandidateMode.ToString(),
                quality_tier = c.QualityTier.ToString(),
                is_actionable = c.IsActionable,
                threshold_policy_version = c.ThresholdPolicyVersion,
                inclusion_reason = c.InclusionReason,
                exclusion_reason = c.ExclusionReason,
                score_percentile_in_run = c.ScorePercentileInRun,
                bullish_score = c.BullishScore,
                bearish_score = c.BearishScore,
                winning_direction = c.WinningDirection,
                status = c.Status.ToString(),
                qualifies_for_options = c.QualifiesForOptions,
                meta_probability = c.MetaProbability,
                meta_model_version = c.MetaModelVersion,
            }
        });

        if (rows.Count == 0)
        {
            _logger.LogWarning("[stock-repo] Failed to save paper stock candidate {Ticker}", c.Ticker);
            return null;
        }
        return MapCandidate(rows[0]);
    }

    /// <summary>
    /// Batch-insert multiple candidates in chunks of 50. Returns the list of
    /// successfully saved candidates. More efficient than calling SaveCandidateAsync
    /// 430 times individually.
    /// </summary>
    public async Task<List<PaperStockCandidate>> SaveCandidatesBatchAsync(List<PaperStockCandidate> candidates)
    {
        if (candidates.Count == 0) return [];

        var payloads = candidates.Select(c => (object)new
        {
            prediction_id = c.PredictionId,
            run_id = c.RunId,
            ticker = c.Ticker,
            prediction_type = c.PredictionType.ToString(),
            timeframe = c.Timeframe.ToString(),
            entry_price = c.EntryPrice,
            reference_price = c.ReferencePrice,
            target_price = c.TargetPrice,
            stop_price = c.StopPrice,
            catalyst_score = c.CatalystScore,
            trend_score = c.TrendScore,
            volume_score = c.VolumeScore,
            market_context_score = c.MarketContextScore,
            historical_accuracy_score = c.HistoricalAccuracyScore,
            risk_penalty = c.RiskPenalty,
            missing_data_penalty = c.MissingDataPenalty,
            total_score = c.TotalScore,
            confidence_score = c.ConfidenceScore,
            risk_score = c.RiskScore,
            catalyst_type = c.CatalystType,
            selection_reason = c.SelectionReason,
            warnings_json = JsonSerializer.SerializeToNode(c.Warnings),
            data_availability = c.DataAvailability,
            candidate_mode = c.CandidateMode.ToString(),
            quality_tier = c.QualityTier.ToString(),
            is_actionable = c.IsActionable,
            threshold_policy_version = c.ThresholdPolicyVersion,
            inclusion_reason = c.InclusionReason,
            exclusion_reason = c.ExclusionReason,
            score_percentile_in_run = c.ScorePercentileInRun,
            bullish_score = c.BullishScore,
            bearish_score = c.BearishScore,
            winning_direction = c.WinningDirection,
            status = c.Status.ToString(),
            qualifies_for_options = c.QualifiesForOptions,
            meta_probability = c.MetaProbability,
            meta_model_version = c.MetaModelVersion,
        }).ToList();

        var saved = new List<PaperStockCandidate>();
        const int batchSize = 50;
        for (int i = 0; i < payloads.Count; i += batchSize)
        {
            var chunk = payloads.Skip(i).Take(batchSize).ToList();
            var rows = await _db.InsertAsync("paper_stock_candidates", chunk);
            saved.AddRange(rows.Select(MapCandidate));
        }

        _logger.LogInformation("[stock-repo] Batch saved {Saved}/{Total} paper stock candidates",
            saved.Count, candidates.Count);
        return saved;
    }

    public async Task<PaperStockCandidate?> GetCandidateAsync(string id)
    {
        var row = await _db.SelectSingleAsync("paper_stock_candidates", $"id=eq.{id}");
        return row is not null ? MapCandidate(row) : null;
    }

    public async Task<List<PaperStockCandidate>> GetOpenCandidatesAsync()
    {
        var rows = await _db.SelectAsync("paper_stock_candidates",
            filter: "status=eq.open", order: "created_at.desc");
        return rows.Select(MapCandidate).ToList();
    }

    public async Task<List<PaperStockCandidate>> GetRecentCandidatesAsync(int limit = 50)
    {
        var rows = await _db.SelectAsync("paper_stock_candidates",
            order: "created_at.desc", limit: limit);
        return rows.Select(MapCandidate).ToList();
    }

    /// <summary>
    /// Look up candidates by originating prediction, regardless of status.
    /// Used to resolve holding windows for positions whose candidate has already
    /// been evaluated or expired. Chunked to keep the PostgREST URL bounded.
    /// </summary>
    public async Task<List<PaperStockCandidate>> GetCandidatesByPredictionIdsAsync(List<string> predictionIds)
    {
        if (predictionIds.Count == 0) return [];

        var results = new List<PaperStockCandidate>();
        const int chunkSize = 50;
        for (int i = 0; i < predictionIds.Count; i += chunkSize)
        {
            var chunk = predictionIds.Skip(i).Take(chunkSize);
            var rows = await _db.SelectAsync("paper_stock_candidates",
                filter: SupabaseClient.InFilter("prediction_id", chunk));
            results.AddRange(rows.Select(MapCandidate));
        }
        return results;
    }

    public async Task<List<PaperStockCandidate>> GetCandidatesByRunAsync(string runId)
    {
        var rows = await _db.SelectAsync("paper_stock_candidates",
            filter: $"run_id=eq.{runId}", order: "total_score.desc");
        return rows.Select(MapCandidate).ToList();
    }

    public Task<int> CountCandidatesAsync(string? filter = null)
        => _db.CountAsync("paper_stock_candidates", filter);

    /// <summary>
    /// Batch-fetch candidates keyed by prediction ID. Used by risk management
    /// to determine timeframe for each open portfolio position.
    /// Chunked to keep PostgREST URL bounded.
    /// </summary>
    public async Task<Dictionary<string, PaperStockCandidate>> GetCandidateMapByPredictionIdsAsync(List<string> predictionIds)
    {
        if (predictionIds.Count == 0) return new();

        var allRows = new List<PaperStockCandidate>();
        const int chunkSize = 50;
        for (int i = 0; i < predictionIds.Count; i += chunkSize)
        {
            var chunk = predictionIds.Skip(i).Take(chunkSize);
            var rows = await _db.SelectAsync("paper_stock_candidates",
                filter: SupabaseClient.InFilter("prediction_id", chunk));
            allRows.AddRange(rows.Select(MapCandidate));
        }

        // Use GroupBy to avoid crash if duplicate prediction_ids ever appear
        return allRows
            .Where(c => c.PredictionId is not null)
            .GroupBy(c => c.PredictionId!)
            .ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<bool> UpdateCandidateStatusAsync(string id, PaperStockStatus status)
    {
        return await _db.UpdateAsync("paper_stock_candidates", $"id=eq.{id}",
            new { status = status.ToString() });
    }

    // -----------------------------------------------------------------------
    // paper_stock_outcomes
    // -----------------------------------------------------------------------

    public async Task<bool> SaveOutcomeAsync(PaperStockOutcome o)
    {
        await _db.InsertAsync("paper_stock_outcomes", new[]
        {
            new
            {
                paper_stock_candidate_id = o.PaperStockCandidateId,
                prediction_id = o.PredictionId,
                ticker = o.Ticker,
                evaluation_time = o.EvaluationTime.ToString("o"),
                exit_price = o.ExitPrice,
                high_after = o.HighAfter,
                low_after = o.LowAfter,
                percent_move = o.PercentMove,
                direction_correct = o.DirectionCorrect,
                target_hit = o.TargetHit,
                stop_hit = o.StopHit,
                invalidation_hit = o.InvalidationHit,
                outcome_score = o.OutcomeScore,
                outcome_summary = o.OutcomeSummary,
                lesson = o.Lesson,
                failure_reason = o.FailureReason,
                warnings_json = JsonSerializer.SerializeToNode(o.Warnings),
            }
        }, returnRows: false);
        return true;
    }

    public async Task<List<PaperStockOutcome>> GetRecentOutcomesAsync(int limit = 50)
    {
        var rows = await _db.SelectAsync("paper_stock_outcomes",
            order: "evaluation_time.desc", limit: limit);
        return rows.Select(MapOutcome).ToList();
    }

    public Task<int> CountOutcomesAsync(string? filter = null)
        => _db.CountAsync("paper_stock_outcomes", filter);

    // -----------------------------------------------------------------------
    // stock_learning_stats
    // -----------------------------------------------------------------------

    public async Task UpsertLearningStatAsync(
        string statType, string statKey, bool directionCorrect,
        double percentMove, double outcomeScore)
    {
        var existing = await _db.SelectSingleAsync("stock_learning_stats",
            $"stat_type=eq.{statType}&stat_key=eq.{statKey}");

        int total, correct;
        double avgMove, avgScore;

        if (existing is not null)
        {
            total = GetInt(existing, "total_candidates") + 1;
            correct = GetInt(existing, "correct_candidates") + (directionCorrect ? 1 : 0);
            var prevMove = GetDouble(existing, "average_percent_move");
            var prevScore = GetDouble(existing, "average_outcome_score");
            avgMove = (prevMove * (total - 1) + percentMove) / total;
            avgScore = (prevScore * (total - 1) + outcomeScore) / total;
        }
        else
        {
            total = 1;
            correct = directionCorrect ? 1 : 0;
            avgMove = percentMove;
            avgScore = outcomeScore;
        }

        await _db.UpsertAsync("stock_learning_stats", new
        {
            stat_type = statType,
            stat_key = statKey,
            total_candidates = total,
            correct_candidates = correct,
            accuracy = total > 0 ? Math.Round((double)correct / total, 4) : 0,
            average_percent_move = Math.Round(avgMove, 2),
            average_outcome_score = Math.Round(avgScore, 2),
            last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
        }, onConflict: "stat_type,stat_key");
    }

    public async Task<List<StockLearningStat>> GetAllLearningStatsAsync()
    {
        var rows = await _db.SelectAsync("stock_learning_stats",
            order: "last_updated_at.desc", limit: 300);
        return rows.Select(MapLearningStat).ToList();
    }

    public async Task<StockLearningStat?> GetTickerLearningStatAsync(string ticker)
    {
        var row = await _db.SelectSingleAsync("stock_learning_stats",
            $"stat_type=eq.ticker&stat_key=eq.{ticker}");
        return row is not null ? MapLearningStat(row) : null;
    }

    // -----------------------------------------------------------------------
    // Link helper for options
    // -----------------------------------------------------------------------

    public async Task<List<JsonObject>> GetOptionsForStockCandidateAsync(string stockCandidateId)
    {
        return await _db.SelectAsync("paper_option_candidates",
            filter: $"paper_stock_candidate_id=eq.{stockCandidateId}",
            order: "rank.asc");
    }

    // -----------------------------------------------------------------------
    // Mappers
    // -----------------------------------------------------------------------

    private static PaperStockCandidate MapCandidate(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        PredictionId = r["prediction_id"]?.ToString(),
        RunId = r["run_id"]?.ToString(),
        Ticker = r["ticker"]?.ToString() ?? "",
        PredictionType = Enum.TryParse<PredictionType>(r["prediction_type"]?.ToString(), out var pt)
            ? pt : PredictionType.neutral,
        Timeframe = Enum.TryParse<StockTimeframe>(r["timeframe"]?.ToString(), out var tf)
            ? tf : StockTimeframe.one_day,
        EntryPrice = GetNullableDouble(r, "entry_price"),
        ReferencePrice = GetNullableDouble(r, "reference_price"),
        TargetPrice = GetNullableDouble(r, "target_price"),
        StopPrice = GetNullableDouble(r, "stop_price"),
        CatalystScore = GetDouble(r, "catalyst_score"),
        TrendScore = GetDouble(r, "trend_score"),
        VolumeScore = GetDouble(r, "volume_score"),
        MarketContextScore = GetDouble(r, "market_context_score"),
        HistoricalAccuracyScore = GetDouble(r, "historical_accuracy_score"),
        RiskPenalty = GetDouble(r, "risk_penalty"),
        MissingDataPenalty = GetDouble(r, "missing_data_penalty"),
        TotalScore = GetDouble(r, "total_score"),
        ConfidenceScore = GetInt(r, "confidence_score"),
        RiskScore = GetInt(r, "risk_score"),
        CatalystType = r["catalyst_type"]?.ToString(),
        SelectionReason = r["selection_reason"]?.ToString() ?? "",
        Warnings = GetWarnings(r, "warnings_json"),
        DataAvailability = r["data_availability"]?.ToString() ?? "real",
        CandidateMode = Enum.TryParse<CandidateMode>(r["candidate_mode"]?.ToString(), out var cm)
            ? cm : CandidateMode.learning,
        QualityTier = Enum.TryParse<QualityTier>(r["quality_tier"]?.ToString(), out var qt)
            ? qt : QualityTier.very_weak,
        IsActionable = GetBool(r, "is_actionable"),
        ThresholdPolicyVersion = r["threshold_policy_version"]?.ToString() ?? "learning_options_v1",
        InclusionReason = r["inclusion_reason"]?.ToString() ?? "",
        ExclusionReason = r["exclusion_reason"]?.ToString(),
        ScorePercentileInRun = GetDouble(r, "score_percentile_in_run"),
        BullishScore = GetNullableDouble(r, "bullish_score"),
        BearishScore = GetNullableDouble(r, "bearish_score"),
        WinningDirection = r["winning_direction"]?.ToString(),
        Status = Enum.TryParse<PaperStockStatus>(r["status"]?.ToString(), out var s)
            ? s : PaperStockStatus.open,
        QualifiesForOptions = GetBool(r, "qualifies_for_options"),
        MetaProbability = GetNullableDouble(r, "meta_probability"),
        MetaModelVersion = GetNullableInt(r, "meta_model_version"),
        CreatedAt = GetDateTimeOffset(r, "created_at"),
    };

    private static PaperStockOutcome MapOutcome(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        PaperStockCandidateId = r["paper_stock_candidate_id"]?.ToString() ?? "",
        PredictionId = r["prediction_id"]?.ToString(),
        Ticker = r["ticker"]?.ToString() ?? "",
        EvaluationTime = GetDateTimeOffset(r, "evaluation_time"),
        ExitPrice = GetNullableDouble(r, "exit_price"),
        HighAfter = GetNullableDouble(r, "high_after"),
        LowAfter = GetNullableDouble(r, "low_after"),
        PercentMove = GetNullableDouble(r, "percent_move"),
        DirectionCorrect = GetNullableBool(r, "direction_correct"),
        TargetHit = GetNullableBool(r, "target_hit"),
        StopHit = GetNullableBool(r, "stop_hit"),
        InvalidationHit = GetNullableBool(r, "invalidation_hit"),
        OutcomeScore = GetDouble(r, "outcome_score"),
        OutcomeSummary = r["outcome_summary"]?.ToString() ?? "",
        Lesson = r["lesson"]?.ToString(),
        FailureReason = r["failure_reason"]?.ToString(),
        Warnings = GetWarnings(r, "warnings_json"),
        CreatedAt = GetDateTimeOffset(r, "created_at"),
    };

    private static StockLearningStat MapLearningStat(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        StatType = r["stat_type"]?.ToString() ?? "",
        StatKey = r["stat_key"]?.ToString() ?? "",
        TotalCandidates = GetInt(r, "total_candidates"),
        CorrectCandidates = GetInt(r, "correct_candidates"),
        Accuracy = GetDouble(r, "accuracy"),
        AveragePercentMove = GetDouble(r, "average_percent_move"),
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

    private static int? GetNullableInt(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return null;
        if (node is JsonValue jv && jv.TryGetValue<int>(out var i)) return i;
        return int.TryParse(node.ToString(), out var parsed) ? parsed : null;
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

    private static bool GetBool(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return false;
        if (node is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        return bool.TryParse(node.ToString(), out var parsed) && parsed;
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

    private static List<string> GetWarnings(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return [];
        if (node is JsonArray arr) return arr.Select(n => n?.ToString() ?? "").Where(s => s != "").ToList();
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            try { return JsonSerializer.Deserialize<List<string>>(s) ?? []; }
            catch { return []; }
        }
        return [];
    }
}
