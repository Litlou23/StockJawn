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
    private string? _cachedChampionProfileId;

    public ResearchRepository(SupabaseClient db, ILogger<ResearchRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public bool IsConfigured => _db.IsConfigured;

    public async Task<string?> GetChampionProfileIdAsync()
    {
        if (_cachedChampionProfileId != null) return _cachedChampionProfileId;
        var row = await _db.SelectSingleAsync("prediction_profiles", "role=eq.champion");
        _cachedChampionProfileId = row?["id"]?.ToString();
        return _cachedChampionProfileId;
    }

    public async Task<Dictionary<string, (string Name, string Role)>> GetAllProfilesAsync()
    {
        var rows = await _db.SelectAsync("prediction_profiles");
        return rows.ToDictionary(
            r => r["id"]?.ToString() ?? "",
            r => (r["profile_name"]?.ToString() ?? "unknown", r["role"]?.ToString() ?? "unknown"));
    }

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

    /// <summary>
    /// Marks any research runs stuck in 'started' for longer than the given
    /// threshold as 'failed'. Returns the number of runs cleaned up.
    /// Prevents ghost runs from accumulating when the process dies mid-execution.
    /// </summary>
    public async Task<int> CleanupStuckRunsAsync(TimeSpan staleThreshold)
    {
        if (!_db.IsConfigured) return 0;
        var cutoff = DateTimeOffset.UtcNow.Subtract(staleThreshold);
        var stuckRuns = await _db.SelectAsync("research_runs",
            $"status=eq.started&started_at=lt.{cutoff:o}");
        var cleaned = 0;
        foreach (var run in stuckRuns)
        {
            var id = run["id"]?.ToString();
            if (id is null) continue;
            await _db.UpdateAsync("research_runs", $"id=eq.{id}", new
            {
                status = "failed",
                completed_at = DateTimeOffset.UtcNow.ToString("o"),
                summary = "Auto-cleaned: run was stuck in 'started' and presumed killed by process recycle",
                errors = new[] { "stuck_run_auto_cleanup" },
            });
            cleaned++;
        }
        return cleaned;
    }

    /// <summary>
    /// Append a progress entry to the research_runs.progress_log jsonb array.
    /// This gives real-time visibility into where a scan is at (or where it died).
    /// Non-blocking — swallows errors so it never kills the scan.
    /// </summary>
    public async Task LogProgressAsync(string runId, string step, string message, object? data = null)
    {
        if (!_db.IsConfigured) return;
        try
        {
            var entry = new
            {
                step,
                message,
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data
            };
            var entryJson = System.Text.Json.JsonSerializer.Serialize(entry);
            // Use raw SQL via RPC to append to the jsonb array atomically
            await _db.RpcAsync("append_progress_log", new { run_id = runId, entry = entryJson });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[repo] Failed to log progress for run {RunId} step {Step} (non-blocking)", runId, step);
        }
    }

    // -----------------------------------------------------------------------
    // Market Snapshots
    // -----------------------------------------------------------------------

    public async Task<bool> SaveMarketSnapshotsAsync(List<object> snapshots)
    {
        if (snapshots.Count == 0) return true;

        // Chunk into batches of 50 to avoid oversized PostgREST payloads.
        // Each snapshot carries full JSON (bars, news, technicals) so 430
        // snapshots in one INSERT can exceed body size limits.
        const int batchSize = 50;
        for (int i = 0; i < snapshots.Count; i += batchSize)
        {
            var chunk = snapshots.Skip(i).Take(batchSize).ToList();
            await _db.InsertAsync("market_snapshots", chunk, returnRows: false);
        }
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
        try
        {
            var rpcResult = await _db.RpcAsync("insert_prediction_candidates",
                new { payload = predictions });

            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(rpcResult);
            var ids = parsed?.Select(r => r.GetValueOrDefault("id", "")).Where(id => id != "").ToList() ?? [];
            if (ids.Count > 0)
            {
                _logger.LogInformation("[research-repo] RPC saved {Count}/{Total} predictions", ids.Count, predictions.Count);
                return (true, ids);
            }

            _logger.LogWarning("[research-repo] RPC insert returned 0 IDs for {Count} predictions — response was empty or unparseable",
                predictions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[research-repo] RPC insert_prediction_candidates failed for {Count} predictions. Falling back to direct INSERT",
                predictions.Count);
        }

        // Fallback: try direct INSERT (in case RPC function doesn't exist yet or failed)
        try
        {
            _logger.LogWarning("[research-repo] Attempting direct INSERT fallback for {Count} predictions", predictions.Count);
            var rows = await _db.InsertAsync("prediction_candidates", predictions);
            var fallbackIds = rows.Select(r => r["id"]?.ToString() ?? "").Where(id => id != "").ToList();
            _logger.LogInformation("[research-repo] Direct INSERT saved {Count}/{Total} predictions", fallbackIds.Count, predictions.Count);
            return (fallbackIds.Count > 0, fallbackIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[research-repo] CRITICAL: Both RPC and direct INSERT failed for {Count} predictions. Zero predictions persisted!",
                predictions.Count);
            return (false, []);
        }
    }

    public async Task<List<PredictionCandidate>> GetOpenPredictionsAsync(string? profileId = null)
    {
        var filter = "status=eq.open";
        if (profileId is not null) filter += $"&profile_id=eq.{profileId}";
        var rows = await _db.SelectAsync("prediction_candidates",
            filter: filter, order: "created_at.desc");
        return rows.Select(MapPrediction).ToList();
    }

    public async Task<List<PredictionCandidate>> GetRecentPredictionsAsync(int limit = 30, string? status = null, string? extraFilter = null, string? profileId = null)
    {
        var parts = new List<string>();
        if (status is not null) parts.Add($"status=eq.{status}");
        if (extraFilter is not null) parts.Add(extraFilter);
        if (profileId is not null) parts.Add($"profile_id=eq.{profileId}");
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
        DateTimeOffset from, DateTimeOffset to, string? status = null, string? extraFilter = null, string? profileId = null)
    {
        var filters = new List<string>
        {
            $"created_at=gte.{from.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}",
            $"created_at=lte.{to.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}",
        };
        if (status is not null) filters.Add($"status=eq.{status}");
        if (extraFilter is not null) filters.Add(extraFilter);
        if (profileId is not null) filters.Add($"profile_id=eq.{profileId}");

        var filter = string.Join("&", filters);
        var rows = await _db.SelectAsync("prediction_candidates",
            filter: filter, order: "created_at.desc");
        return rows.Select(MapPrediction).ToList();
    }

    public async Task<bool> UpdatePredictionStatusAsync(string id, string status)
    {
        return await _db.UpdateAsync("prediction_candidates", $"id=eq.{id}", new { status });
    }

    public async Task<bool> UpdatePeakFavorablePriceAsync(string id, double peakPrice)
    {
        return await _db.UpdateAsync("prediction_candidates", $"id=eq.{id}",
            new { peak_favorable_price = peakPrice });
    }

    public async Task<bool> SupersedePredictionAsync(string id, string supersededBy, string reason)
    {
        return await _db.UpdateAsync("prediction_candidates", $"id=eq.{id}",
            new { status = "superseded", superseded_by = supersededBy, supersession_reason = reason });
    }

    public async Task<PredictionStatsAggregate> GetPredictionStatsAsync(string? profileId = null)
    {
        var predFilter = profileId is not null ? $"profile_id=eq.{profileId}" : null;

        var totalTask = _db.CountAsync("prediction_candidates", predFilter);

        int outcomesTotal, correct, incorrect;

        if (profileId is not null)
        {
            // Profile-scoped: fetch prediction IDs, then count outcomes in chunks
            // to avoid PostgREST URL length limits with large in() filters
            var predRows = await _db.SelectAsync("prediction_candidates",
                filter: predFilter, select: "id", limit: 5000);
            var ids = predRows.Select(r => r["id"]?.ToString()).Where(id => id is not null).ToList();
            if (ids.Count == 0)
                return new PredictionStatsAggregate { TotalPredictions = await totalTask };

            outcomesTotal = 0; correct = 0; incorrect = 0;
            const int chunkSize = 100;
            foreach (var chunk in ids.Chunk(chunkSize))
            {
                var inFilter = $"prediction_id=in.({string.Join(",", chunk)})";
                var tasks = new[]
                {
                    _db.CountAsync("prediction_outcomes", inFilter),
                    _db.CountAsync("prediction_outcomes", $"{inFilter}&direction_correct=eq.true"),
                    _db.CountAsync("prediction_outcomes", $"{inFilter}&direction_correct=eq.false"),
                };
                await Task.WhenAll(tasks);
                outcomesTotal += tasks[0].Result;
                correct += tasks[1].Result;
                incorrect += tasks[2].Result;
            }
        }
        else
        {
            // Unscoped: no in() filter needed
            var outcomesTotalTask = _db.CountAsync("prediction_outcomes");
            var correctTask = _db.CountAsync("prediction_outcomes", "direction_correct=eq.true");
            var incorrectTask = _db.CountAsync("prediction_outcomes", "direction_correct=eq.false");
            await Task.WhenAll(outcomesTotalTask, correctTask, incorrectTask);
            outcomesTotal = outcomesTotalTask.Result;
            correct = correctTask.Result;
            incorrect = incorrectTask.Result;
        }

        var total = await totalTask;
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

    public async Task<List<PredictionWithOutcome>> GetRecentPredictionsWithOutcomesAsync(int limit = 10, string? profileId = null)
    {
        var predictions = await GetRecentPredictionsAsync(limit, profileId: profileId);
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

        // Chunk into batches of 100 to avoid oversized payloads.
        // With 430 tickers × ~5 inputs each = 2000+ rows.
        const int batchSize = 100;
        for (int i = 0; i < inputs.Count; i += batchSize)
        {
            var chunk = inputs.Skip(i).Take(batchSize).ToList();
            await _db.InsertAsync("prediction_inputs", chunk, returnRows: false);
        }
        return true;
    }

    public async Task<List<PredictionInput>> GetPredictionInputsAsync(List<string> predictionIds)
    {
        if (predictionIds.Count == 0) return [];
        var filter = $"prediction_id=in.({string.Join(",", predictionIds)})";
        var rows = await _db.SelectAsync("prediction_inputs", filter: filter, order: "created_at.asc");
        return rows.Select(r => new PredictionInput
        {
            Id = r["id"]?.ToString() ?? "",
            PredictionId = r["prediction_id"]?.ToString() ?? "",
            InputType = r["input_type"]?.ToString() ?? "",
            SourceName = r["source_name"]?.ToString() ?? "",
            SourceUrl = r["source_url"]?.ToString(),
            SourceRecordId = r["source_record_id"]?.ToString(),
            Summary = r["summary"]?.ToString() ?? "",
            CreatedAt = GetDateTimeOffset(r, "created_at"),
        }).ToList();
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

    /// <summary>
    /// Get outcomes only for predictions belonging to a specific profile.
    /// Fetches evaluated predictions first (not just recent by creation date),
    /// then looks up their outcomes.
    /// </summary>
    public async Task<List<PredictionOutcome>> GetOutcomesForProfileAsync(string profileId, int limit = 500)
    {
        var predictions = await GetRecentPredictionsAsync(limit, status: "evaluated", profileId: profileId);
        if (predictions.Count == 0) return [];
        return await GetOutcomesForPredictionsAsync(predictions.Select(p => p.Id).ToList());
    }

    public async Task<List<PredictionOutcome>> GetOutcomesSinceAsync(DateTimeOffset since, int limit = 500)
    {
        var rows = await _db.SelectAsync("prediction_outcomes",
            filter: $"evaluation_time=gte.{since:o}",
            order: "evaluation_time.desc", limit: limit);
        return rows.Select(MapOutcome).ToList();
    }

    public async Task<List<PredictionOutcome>> GetOutcomesForPredictionsAsync(List<string> predictionIds)
    {
        if (predictionIds.Count == 0) return [];

        // Chunk to avoid exceeding PostgREST URL length limits with large in() filters
        const int chunkSize = 100;
        var results = new List<PredictionOutcome>();
        foreach (var chunk in predictionIds.Chunk(chunkSize))
        {
            var filter = $"prediction_id=in.({string.Join(",", chunk)})";
            var rows = await _db.SelectAsync("prediction_outcomes", filter: filter);
            results.AddRange(rows.Select(MapOutcome));
        }
        return results;
    }

    /// <summary>
    /// Get ticker accuracy from both prediction_outcomes and paper_stock_outcomes,
    /// deduplicated by prediction_id. Returns (total, correct) or null if no outcomes.
    /// </summary>
    public async Task<(int Total, int Correct)?> GetTickerAccuracyFromOutcomesAsync(string ticker)
    {
        // Collect outcomes from both sources, keyed by prediction_id to deduplicate
        var seen = new Dictionary<string, bool>(); // prediction_id → direction_correct

        // 1. prediction_outcomes (joined via prediction_candidates for ticker)
        var predictions = await _db.SelectAsync("prediction_candidates",
            filter: $"ticker=eq.{ticker}", select: "id", limit: 500);
        if (predictions.Count > 0)
        {
            var predIds = predictions.Select(p => p["id"]?.ToString()).Where(id => id is not null).Select(id => id!).ToList();
            // Chunk to avoid PostgREST URL length limits with large in() filters
            const int chunkSize = 100;
            foreach (var chunk in predIds.Chunk(chunkSize))
            {
                var idsFilter = $"prediction_id=in.({string.Join(",", chunk)})";
                var outcomeRows = await _db.SelectAsync("prediction_outcomes",
                    filter: idsFilter, select: "prediction_id,direction_correct", limit: chunk.Length);
                foreach (var row in outcomeRows)
                {
                    var pid = row["prediction_id"]?.ToString();
                    if (pid is null) continue;
                    var dc = row["direction_correct"];
                    if (dc is null || dc.GetValueKind() == System.Text.Json.JsonValueKind.Null) continue;
                    seen[pid] = row["direction_correct"]?.GetValue<bool>() == true;
                }
            }
        }

        // 2. paper_stock_outcomes (has ticker column directly)
        var paperRows = await _db.SelectAsync("paper_stock_outcomes",
            filter: $"ticker=eq.{ticker}", select: "prediction_id,direction_correct", limit: 500);
        foreach (var row in paperRows)
        {
            var pid = row["prediction_id"]?.ToString();
            if (pid is null) continue;
            if (seen.ContainsKey(pid)) continue; // already counted from prediction_outcomes
            var dc = row["direction_correct"];
            if (dc is null || dc.GetValueKind() == System.Text.Json.JsonValueKind.Null) continue;
            seen[pid] = dc.GetValue<bool>();
        }

        if (seen.Count == 0) return null;
        return (seen.Count, seen.Values.Count(v => v));
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
    // Signal Observations (prediction_signal_observations)
    // -----------------------------------------------------------------------

    public async Task<bool> InsertSignalObservationsAsync(List<object> observations)
    {
        if (observations.Count == 0) return true;
        await _db.InsertAsync("prediction_signal_observations", observations, returnRows: false);
        return true;
    }

    public async Task<List<SignalObservation>> GetSignalObservationsAsync(
        int limit = 500, int? windowDays = null, string? profileId = null)
    {
        var parts = new List<string>();
        if (windowDays.HasValue)
            parts.Add($"created_at=gte.{DateTimeOffset.UtcNow.AddDays(-windowDays.Value):yyyy-MM-dd}");
        if (profileId is not null)
            parts.Add($"profile_id=eq.{profileId}");
        var filter = parts.Count > 0 ? string.Join("&", parts) : null;
        var rows = await _db.SelectAsync("prediction_signal_observations",
            filter: filter, order: "created_at.desc", limit: limit);
        return rows.Select(r => new SignalObservation
        {
            Id = r["id"]?.ToString(),
            PredictionId = r["prediction_id"]?.ToString() ?? "",
            OutcomeId = r["outcome_id"]?.ToString(),
            SignalName = r["signal_name"]?.ToString() ?? "",
            BullScore = GetDouble(r, "bull_score"),
            BearScore = GetDouble(r, "bear_score"),
            PredictedDirection = r["predicted_direction"]?.ToString() ?? "",
            Correct = GetNullableBool(r, "correct"),
            RawWeight = GetDouble(r, "raw_weight"),
            EffectiveWeight = GetDouble(r, "effective_weight"),
            WeightedContribution = GetDouble(r, "weighted_contribution"),
            ContributionPercent = GetNullableDouble(r, "contribution_percent"),
            ActualReturnPercent = GetNullableDouble(r, "actual_return_percent"),
            Confidence = GetNullableDouble(r, "confidence"),
            OutcomeScore = GetNullableDouble(r, "outcome_score"),
            MarketRegime = r["market_regime"]?.ToString(),
            CreatedAt = GetDateTimeOffset(r, "created_at"),
        }).ToList();
    }

    public async Task<bool> HasObservationsForPredictionAsync(string predictionId)
    {
        var rows = await _db.SelectAsync("prediction_signal_observations",
            filter: $"prediction_id=eq.{predictionId}", limit: 1);
        return rows.Count > 0;
    }

    // -----------------------------------------------------------------------
    // Scoring Weight Overrides
    // -----------------------------------------------------------------------

    public async Task<List<ScoringWeightOverride>> GetActiveWeightOverridesAsync()
    {
        var rows = await _db.SelectAsync("scoring_weight_overrides",
            filter: "status=eq.active", order: "signal_name.asc");
        return rows.Select(r => new ScoringWeightOverride
        {
            Id = r["id"]?.ToString(),
            SignalName = r["signal_name"]?.ToString() ?? "",
            BaseWeight = GetDouble(r, "base_weight"),
            AdjustmentPercent = GetDouble(r, "adjustment_percent"),
            EffectiveWeight = GetDouble(r, "effective_weight"),
            Confidence = GetDouble(r, "confidence"),
            SampleSize = (int)GetDouble(r, "sample_size"),
            Status = r["status"]?.ToString() ?? "active",
            Reason = r["reason"]?.ToString(),
            LastUpdated = GetDateTimeOffset(r, "last_updated"),
        }).ToList();
    }

    public async Task<bool> UpsertWeightOverrideAsync(ScoringWeightOverride wt)
    {
        return await _db.UpsertAsync("scoring_weight_overrides", new
        {
            signal_name = wt.SignalName,
            base_weight = wt.BaseWeight,
            adjustment_percent = wt.AdjustmentPercent,
            effective_weight = wt.EffectiveWeight,
            confidence = wt.Confidence,
            sample_size = wt.SampleSize,
            status = wt.Status,
            reason = wt.Reason,
            last_updated = DateTimeOffset.UtcNow.ToString("o"),
        }, "signal_name");
    }

    // -----------------------------------------------------------------------
    // Enhanced Learning Reports
    // -----------------------------------------------------------------------

    public async Task<bool> SaveEnhancedLearningReportAsync(object report)
    {
        await _db.InsertAsync("learning_reports", report, returnRows: false);
        return true;
    }

    // -----------------------------------------------------------------------
    // Supersession Learning
    // -----------------------------------------------------------------------

    public async Task<List<PredictionCandidate>> GetSupersededPredictionsAsync(int limit = 200, string? profileId = null)
    {
        var parts = new List<string> { "status=eq.superseded", "superseded_by=not.is.null" };
        if (profileId is not null) parts.Add($"profile_id=eq.{profileId}");
        var rows = await _db.SelectAsync("prediction_candidates",
            filter: string.Join("&", parts),
            order: "created_at.desc", limit: limit);
        return rows.Select(MapPrediction).ToList();
    }

    public async Task<bool> SaveSupersessionLearningRecordsAsync(List<object> records)
    {
        if (records.Count == 0) return true;
        await _db.InsertAsync("supersession_learning", records, returnRows: false);
        return true;
    }

    public async Task<List<JsonObject>> GetSupersessionLearningRecordsAsync(int limit = 500)
    {
        return await _db.SelectAsync("supersession_learning",
            order: "created_at.desc", limit: limit);
    }

    public async Task<bool> HasSupersessionRecordAsync(string originalId, string replacementId)
    {
        var rows = await _db.SelectAsync("supersession_learning",
            filter: $"original_prediction_id=eq.{originalId}&replacement_prediction_id=eq.{replacementId}",
            select: "id", limit: 1);
        return rows.Count > 0;
    }

    // -----------------------------------------------------------------------
    // Volatility Assessments
    // -----------------------------------------------------------------------

    public async Task<bool> SaveVolatilityAssessmentAsync(VolatilityOpportunityAssessment a, string runId)
    {
        try
        {
            await _db.UpsertAsync("volatility_assessments", new
            {
                run_id = runId,
                ticker = a.Ticker,
                atr_percentile = a.AtrPercentile,
                atr_acceleration = a.AtrAcceleration,
                bandwidth_percentile = a.BandwidthPercentile,
                bandwidth_direction = a.BandwidthDirection,
                stock_volatility_regime = a.StockVolRegime.ToString(),
                gap_percent = a.GapPercent,
                gap_direction = a.GapDir.ToString(),
                gap_type = a.GapClassification.ToString(),
                gap_with_volume = a.GapWithVolume,
                distance_from_support = a.DistanceFromSupport,
                distance_from_resistance = a.DistanceFromResistance,
                volume_ratio_persistence = a.VolumeRatioPersistence,
                catalyst_age_hours = a.CatalystAgeHours,
                opportunity_type = a.Opportunity.ToString(),
                opportunity_score = a.OpportunityScore,
                volatility_risk_modifier = a.RiskModifier,
                features_skipped = a.FeaturesSkipped.ToArray(),
                bars_used_for_history = a.BarsUsedForHistory,
            }, onConflict: "ticker,run_id");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[research-repo] Failed to save volatility assessment for {Ticker}", a.Ticker);
            return false;
        }
    }

    public async Task<VolatilityOpportunityAssessment?> GetAssessmentAsync(string ticker, string runId)
    {
        var row = await _db.SelectSingleAsync("volatility_assessments",
            filter: $"ticker=eq.{ticker}&run_id=eq.{runId}");
        return row is not null ? MapVolatilityAssessment(row) : null;
    }

    public async Task<List<VolatilityOpportunityAssessment>> GetAssessmentsByTickerAsync(string ticker, int limit = 60)
    {
        var rows = await _db.SelectAsync("volatility_assessments",
            filter: $"ticker=eq.{ticker}", order: "created_at.desc", limit: limit);
        return rows.Select(MapVolatilityAssessment).ToList();
    }

    public async Task<List<VolatilityOpportunityAssessment>> GetAssessmentsByRunAsync(string runId)
    {
        var rows = await _db.SelectAsync("volatility_assessments",
            filter: $"run_id=eq.{runId}", order: "ticker.asc");
        return rows.Select(MapVolatilityAssessment).ToList();
    }

    private static VolatilityOpportunityAssessment MapVolatilityAssessment(JsonObject r)
    {
        return new VolatilityOpportunityAssessment
        {
            Ticker = r["ticker"]?.ToString() ?? "",
            AssessedAt = r["created_at"] is JsonValue v && DateTimeOffset.TryParse(v.ToString(), out var dt)
                ? dt : DateTimeOffset.UtcNow,
            AtrPercentile = r["atr_percentile"]?.GetValue<double?>(),
            AtrAcceleration = r["atr_acceleration"]?.GetValue<double?>(),
            BandwidthPercentile = r["bandwidth_percentile"]?.GetValue<double?>(),
            BandwidthDirection = r["bandwidth_direction"]?.GetValue<double?>(),
            StockVolRegime = Enum.TryParse<StockVolatilityRegime>(r["stock_volatility_regime"]?.ToString(), out var regime)
                ? regime : StockVolatilityRegime.Unknown,
            GapPercent = r["gap_percent"]?.GetValue<double?>(),
            GapDir = Enum.TryParse<GapDirection>(r["gap_direction"]?.ToString(), out var gd)
                ? gd : GapDirection.None,
            GapClassification = Enum.TryParse<GapType>(r["gap_type"]?.ToString(), out var gt)
                ? gt : GapType.NoGap,
            GapWithVolume = r["gap_with_volume"]?.GetValue<bool>() ?? false,
            DistanceFromSupport = r["distance_from_support"]?.GetValue<double?>(),
            DistanceFromResistance = r["distance_from_resistance"]?.GetValue<double?>(),
            VolumeRatioPersistence = r["volume_ratio_persistence"]?.GetValue<double?>(),
            CatalystAgeHours = r["catalyst_age_hours"]?.GetValue<double?>(),
            Opportunity = Enum.TryParse<OpportunityType>(r["opportunity_type"]?.ToString(), out var ot)
                ? ot : OpportunityType.None,
            OpportunityScore = r["opportunity_score"]?.GetValue<double>() ?? 0,
            RiskModifier = r["volatility_risk_modifier"]?.GetValue<double>() ?? 0,
            FeaturesSkipped = r["features_skipped"] is JsonArray arr
                ? arr.Select(x => x?.ToString() ?? "").ToList()
                : [],
            BarsUsedForHistory = r["bars_used_for_history"]?.GetValue<int>() ?? 0,
        };
    }

    // -----------------------------------------------------------------------
    // Volatility Learning Stats
    // -----------------------------------------------------------------------

    public async Task<bool> SaveVolatilityLearningRecordAsync(VolatilityLearningRecord rec)
    {
        try
        {
            await _db.InsertAsync("volatility_learning_stats", new
            {
                prediction_id = rec.PredictionId,
                run_id = rec.RunId,
                ticker = rec.Ticker,
                profile_id = rec.ProfileId,
                opportunity_type = rec.OpportunityType,
                opportunity_score = rec.OpportunityScore,
                stock_volatility_regime = rec.StockVolatilityRegime,
                atr_percentile = rec.AtrPercentile,
                atr_acceleration = rec.AtrAcceleration,
                bandwidth_percentile = rec.BandwidthPercentile,
                gap_type = rec.GapType,
                gap_percent = rec.GapPercent,
                catalyst_age_hours = rec.CatalystAgeHours,
                confidence = rec.Confidence,
                risk = rec.Risk,
                prediction_type = rec.PredictionType,
                time_window = rec.TimeWindow,
                direction_correct = rec.DirectionCorrect,
                outcome_score = rec.OutcomeScore,
                holding_period_hours = rec.HoldingPeriodHours,
                max_favorable_excursion = rec.MaxFavorableExcursion,
                max_adverse_excursion = rec.MaxAdverseExcursion,
                time_to_3pct = rec.TimeTo3Pct,
                time_to_5pct = rec.TimeTo5Pct,
                time_to_target = rec.TimeToTarget,
                recovery_speed = rec.RecoverySpeed,
                bounce_quality_realized = rec.BounceQualityRealized,
                opportunity_success = rec.OpportunitySuccess,
                opportunity_success_reason = rec.OpportunitySuccessReason,
            }, returnRows: false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[research-repo] Failed to save volatility learning record for {Ticker}", rec.Ticker);
            return false;
        }
    }

    public async Task<List<VolatilityLearningRecord>> GetLearningByTickerAsync(string ticker, int limit = 60)
    {
        var rows = await _db.SelectAsync("volatility_learning_stats",
            filter: $"ticker=eq.{ticker}", order: "created_at.desc", limit: limit);
        return rows.Select(MapLearningRecord).ToList();
    }

    public async Task<List<VolatilityLearningRecord>> GetLearningByOpportunityTypeAsync(string opportunityType, int limit = 100)
    {
        var rows = await _db.SelectAsync("volatility_learning_stats",
            filter: $"opportunity_type=eq.{opportunityType}", order: "created_at.desc", limit: limit);
        return rows.Select(MapLearningRecord).ToList();
    }

    public async Task<List<VolatilityLearningRecord>> GetAllVolatilityLearningStatsAsync(int limit = 1000, int? windowDays = null, string? profileId = null)
    {
        var parts = new List<string>();
        if (windowDays.HasValue)
            parts.Add($"created_at=gte.{DateTimeOffset.UtcNow.AddDays(-windowDays.Value):yyyy-MM-dd}");
        if (profileId is not null)
            parts.Add($"profile_id=eq.{profileId}");
        var filter = parts.Count > 0 ? string.Join("&", parts) : null;
        var rows = await _db.SelectAsync("volatility_learning_stats",
            filter: filter, order: "created_at.desc", limit: limit);
        return rows.Select(MapLearningRecord).ToList();
    }

    private static VolatilityLearningRecord MapLearningRecord(JsonObject r)
    {
        return new VolatilityLearningRecord
        {
            PredictionId = r["prediction_id"]?.ToString() ?? "",
            RunId = r["run_id"]?.ToString() ?? "",
            Ticker = r["ticker"]?.ToString() ?? "",
            OpportunityType = r["opportunity_type"]?.ToString(),
            OpportunityScore = r["opportunity_score"]?.GetValue<double?>(),
            StockVolatilityRegime = r["stock_volatility_regime"]?.ToString(),
            AtrPercentile = r["atr_percentile"]?.GetValue<double?>(),
            AtrAcceleration = r["atr_acceleration"]?.GetValue<double?>(),
            BandwidthPercentile = r["bandwidth_percentile"]?.GetValue<double?>(),
            GapType = r["gap_type"]?.ToString(),
            GapPercent = r["gap_percent"]?.GetValue<double?>(),
            CatalystAgeHours = r["catalyst_age_hours"]?.GetValue<double?>(),
            Confidence = r["confidence"]?.GetValue<int>() ?? 0,
            Risk = r["risk"]?.GetValue<int>() ?? 0,
            PredictionType = r["prediction_type"]?.ToString(),
            TimeWindow = r["time_window"]?.ToString(),
            DirectionCorrect = r["direction_correct"]?.GetValue<bool?>(),
            OutcomeScore = r["outcome_score"]?.GetValue<double?>(),
            HoldingPeriodHours = r["holding_period_hours"]?.GetValue<double?>(),
            MaxFavorableExcursion = r["max_favorable_excursion"]?.GetValue<double?>(),
            MaxAdverseExcursion = r["max_adverse_excursion"]?.GetValue<double?>(),
            TimeTo3Pct = r["time_to_3pct"]?.GetValue<int?>(),
            TimeTo5Pct = r["time_to_5pct"]?.GetValue<int?>(),
            TimeToTarget = r["time_to_target"]?.GetValue<int?>(),
            RecoverySpeed = r["recovery_speed"]?.GetValue<double?>(),
            BounceQualityRealized = r["bounce_quality_realized"]?.ToString(),
            OpportunitySuccess = r["opportunity_success"]?.GetValue<bool?>(),
            OpportunitySuccessReason = r["opportunity_success_reason"]?.ToString(),
            ProfileId = r["profile_id"]?.ToString(),
        };
    }

    public async Task<EnhancedLearningReport?> GetLatestLearningReportAsync()
    {
        var rows = await _db.SelectAsync("learning_reports",
            order: "created_at.desc", limit: 1);
        if (rows.Count == 0) return null;
        var r = rows[0];
        return new EnhancedLearningReport
        {
            Id = r["id"]?.ToString(),
            ReportDate = GetDateTimeOffset(r, "created_at"),
            EvaluationWindowDays = (int)GetDouble(r, "evaluation_window_days"),
            PredictionCount = (int)GetDouble(r, "prediction_count"),
            OverallAccuracy = GetNullableDouble(r, "overall_accuracy"),
            BullAccuracy = GetNullableDouble(r, "bull_accuracy"),
            BearAccuracy = GetNullableDouble(r, "bear_accuracy"),
            MarketRegime = r["market_regime"]?.ToString(),
            TopSignals = DeserializeJsonColumn<List<SignalPerformanceSummary>>(r, "top_signals_json") ?? [],
            WeakSignals = DeserializeJsonColumn<List<SignalPerformanceSummary>>(r, "weak_signals_json") ?? [],
            WeightChanges = DeserializeJsonColumn<List<WeightChangeSummary>>(r, "weight_changes_json") ?? [],
            ConfidenceCalibration = DeserializeJsonColumn<ConfidenceAnalysis>(r, "confidence_analysis_json"),
            AiSummary = r["ai_summary"]?.ToString(),
        };
    }

    private static T? DeserializeJsonColumn<T>(JsonObject row, string column) where T : class
    {
        var val = row[column]?.ToString();
        if (string.IsNullOrWhiteSpace(val)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(val, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch { return null; }
    }

    // -----------------------------------------------------------------------
    // Category-based stats
    // -----------------------------------------------------------------------

    private static readonly string DirectionalTypes = "prediction_type=in.(bullish,bearish)";
    private static readonly string ShortTermWindows = "time_window=in.(intraday,1_day,3_day,1_week)";
    private static readonly string LongTermWindows = "time_window=in.(swing,1_month,3_month,6_month,1_year)";
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

        // Chunk to avoid PostgREST URL length limits with large in() filters
        int correct = 0, incorrect = 0;
        const int chunkSize = 100;
        foreach (var chunk in predIds.Chunk(chunkSize))
        {
            var inFilter = $"prediction_id=in.({string.Join(",", chunk)})";
            var correctTask = _db.CountAsync("prediction_outcomes", $"{inFilter}&direction_correct=eq.true");
            var incorrectTask = _db.CountAsync("prediction_outcomes", $"{inFilter}&direction_correct=eq.false");
            await Task.WhenAll(correctTask, incorrectTask);
            correct += correctTask.Result;
            incorrect += incorrectTask.Result;
        }

        var evaluated = correct + incorrect;
        var pending = total - evaluated;

        return new CategoryStatsAggregate
        {
            Category = category,
            Total = total,
            Evaluated = evaluated,
            Correct = correct,
            Incorrect = incorrect,
            Pending = pending > 0 ? pending : 0,
            AccuracyPercent = evaluated > 0 ? Math.Round(100.0 * correct / evaluated, 1) : null,
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
    // Trade Setups — setup detection engine persistence
    // -----------------------------------------------------------------------

    public async Task<bool> SaveTradeSetupAsync(object setup)
    {
        if (!_db.IsConfigured) return false;
        try
        {
            await _db.InsertAsync("trade_setups", new[] { setup }, returnRows: false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[repo] Failed to save trade setup");
            return false;
        }
    }

    public async Task<List<JsonObject>> GetActiveTradeSetupsAsync()
    {
        if (!_db.IsConfigured) return [];
        return await _db.SelectAsync("trade_setups", filter: "status=eq.active", order: "created_at.desc", limit: 200);
    }

    public async Task<bool> UpdateTradeSetupStatusAsync(string id, string status, object? outcomeData = null)
    {
        if (!_db.IsConfigured) return false;
        var data = new Dictionary<string, object?> { ["status"] = status };
        if (outcomeData is not null) data["outcome_json"] = System.Text.Json.JsonSerializer.Serialize(outcomeData);
        return await _db.UpdateAsync("trade_setups", $"id=eq.{id}", data);
    }

    public async Task<SetupLearningStat?> GetSetupLearningStatAsync(string fingerprint)
    {
        if (!_db.IsConfigured) return null;
        var row = await _db.SelectSingleAsync("setup_learning_stats",
            $"setup_fingerprint=eq.{Uri.EscapeDataString(fingerprint)}");
        return row is not null ? MapSetupLearningStat(row) : null;
    }

    public async Task<List<SetupLearningStat>> GetAllSetupLearningStatsAsync()
    {
        if (!_db.IsConfigured) return [];
        var rows = await _db.SelectAsync("setup_learning_stats", order: "expected_value_percent.desc", limit: 200);
        return rows.Select(MapSetupLearningStat).ToList();
    }

    public async Task<bool> UpsertSetupLearningStatAsync(object stat)
    {
        if (!_db.IsConfigured) return false;
        try
        {
            await _db.UpsertAsync("setup_learning_stats", stat, "setup_fingerprint");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[repo] Failed to upsert setup learning stat");
            return false;
        }
    }

    private static SetupLearningStat MapSetupLearningStat(JsonObject r) => new()
    {
        Id = r["id"]?.ToString() ?? "",
        SetupFingerprint = r["setup_fingerprint"]?.ToString() ?? "",
        Description = r["description"]?.ToString() ?? "",
        Direction = r["direction"]?.ToString() ?? "",
        TotalOccurrences = GetInt(r, "total_occurrences"),
        Wins = GetInt(r, "wins"),
        Losses = GetInt(r, "losses"),
        WinRate = GetDouble(r, "win_rate"),
        AverageWinPercent = GetDouble(r, "average_win_percent"),
        AverageLossPercent = GetDouble(r, "average_loss_percent"),
        ExpectedValuePercent = GetDouble(r, "expected_value_percent"),
        AverageHoldingDays = GetDouble(r, "average_holding_days"),
        AverageConfirmationCount = GetInt(r, "average_confirmation_count"),
        Confidence = GetDouble(r, "confidence"),
        RiskRating = GetInt(r, "risk_rating"),
        IsTrusted = r["is_trusted"]?.GetValue<bool>() ?? true,
        MarketRegimeBreakdownJson = r["market_regime_breakdown_json"]?.ToString(),
        LastUpdatedAt = GetDateTimeOffset(r, "last_updated_at"),
    };

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
        ExpectedValuePercent = GetNullableDouble(r, "expected_value_percent"),
        PricePredictionMethod = r["price_prediction_method"]?.ToString(),
        PricePredictionWarnings = GetStringList(r, "price_prediction_warnings"),
        ScoreDebugJson = r["score_debug_json"]?.ToString(),
        IndicatorsJson = r["indicators_json"]?.ToString(),
        WeightsSnapshotJson = r["weights_snapshot_json"]?.ToString(),
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
        DowngradeReasons = GetStringList(r, "downgrade_reasons"),
        Status = r["status"]?.ToString() ?? "open",
        SupersededBy = r["superseded_by"]?.ToString(),
        SupersessionReason = r["supersession_reason"]?.ToString(),
        ProfileId = r["profile_id"]?.ToString(),
        SetupType = r["setup_type"]?.ToString() ?? "prediction",
        SetupDetailsJson = r["setup_details"]?.ToJsonString(),
        PeakFavorablePrice = GetNullableDouble(r, "peak_favorable_price"),
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
        AbstentionCorrect = GetNullableBool(r, "abstention_correct"),
        MissedAlphaPercent = GetNullableDouble(r, "missed_alpha_percent"),
        GuardrailJustified = GetNullableBool(r, "guardrail_justified"),
        OriginalDirection = r["original_direction"]?.ToString(),
        DowngradeReasonsEvaluated = GetStringList(r, "downgrade_reasons_evaluated"),
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

    // -----------------------------------------------------------------------
    // Signal Analytics (calibration, correlation, influence, interactions)
    // -----------------------------------------------------------------------

    public async Task<bool> UpsertCalibrationBucketAsync(object bucket)
    {
        return await _db.UpsertAsync("signal_calibration_buckets", bucket,
            onConflict: "signal_name,direction,score_bucket");
    }

    public async Task<List<JsonObject>> GetCalibrationBucketsAsync()
    {
        return await _db.SelectAsync("signal_calibration_buckets",
            filter: "direction=eq.all&sample_count=gte.3",
            order: "signal_name.asc,score_bucket.asc", limit: 200);
    }

    public async Task<bool> UpsertSignalCorrelationAsync(object correlation)
    {
        return await _db.UpsertAsync("signal_correlations", correlation,
            onConflict: "signal_name,direction");
    }

    public async Task<List<JsonObject>> GetSignalCorrelationsAsync()
    {
        return await _db.SelectAsync("signal_correlations",
            filter: "direction=eq.all",
            order: "correlation_r.desc", limit: 50);
    }

    public async Task<bool> UpsertSignalInfluenceAsync(object influence)
    {
        return await _db.UpsertAsync("signal_influence", influence,
            onConflict: "signal_name,direction");
    }

    public async Task<List<JsonObject>> GetSignalInfluenceAsync()
    {
        return await _db.SelectAsync("signal_influence",
            filter: "direction=eq.all",
            order: "decisive_count.desc", limit: 50);
    }

    public async Task<bool> UpsertSignalInteractionAsync(object interaction)
    {
        return await _db.UpsertAsync("signal_interactions", interaction,
            onConflict: "signal_a,signal_b,direction");
    }

    public async Task<List<JsonObject>> GetSignalInteractionsAsync()
    {
        return await _db.SelectAsync("signal_interactions",
            filter: "direction=eq.all&both_strong_count=gte.3",
            order: "synergy_score.desc", limit: 50);
    }

    // -----------------------------------------------------------------------
    // Cap Tuning Stats (self-tuning confidence caps)
    // -----------------------------------------------------------------------

    public async Task<bool> UpsertCapTuningStatAsync(object stat)
    {
        return await _db.UpsertAsync("cap_tuning_stats", stat,
            onConflict: "cap_reason");
    }

    public async Task<List<JsonObject>> GetCapTuningStatsAsync()
    {
        return await _db.SelectAsync("cap_tuning_stats",
            order: "computed_at.desc", limit: 50);
    }
}
