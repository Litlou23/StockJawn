using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.MetaLabeling;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// Meta-labeler admin endpoints. Two-step pipeline exposed:
///   1. POST /api/meta-labeler/label   — build training data (triple-barrier)
///   2. POST /api/meta-labeler/train   — train a new model version
///
/// Plus read endpoints for status and the model registry. Both mutation
/// endpoints require the JOB_RUN_SECRET header, matching the backtest
/// admin pattern.
/// </summary>
[ApiController]
[Route("api/meta-labeler")]
public class MetaLabelerController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JobStatusTracker _jobs;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetaLabelerController> _logger;

    public MetaLabelerController(
        IServiceScopeFactory scopeFactory,
        JobStatusTracker jobs,
        IConfiguration configuration,
        ILogger<MetaLabelerController> logger)
    {
        _scopeFactory = scopeFactory;
        _jobs = jobs;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Current model status: is a model loaded, which version, feature count.</summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MetaLabelerService>();
        var features = scope.ServiceProvider.GetRequiredService<MetaLabelerFeatureExtractor>();

        return Ok(new
        {
            isReady = svc.IsReady,
            activeVersion = svc.ActiveVersion,
            featureCount = features.FeatureCount,
            featureExtractorVersion = MetaLabelerFeatureExtractor.FeatureVersion,
            labelingJob = _jobs.GetStatus("meta-labeler-label"),
            trainingJob = _jobs.GetStatus("meta-labeler-train"),
        });
    }

    /// <summary>List all trained model versions with their test metrics.</summary>
    [HttpGet("models")]
    public async Task<IActionResult> ListModels([FromQuery] int limit = 20)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SupabaseClient>();
        var rows = await db.SelectAsync("meta_labeler_models",
            order: "version.desc", limit: limit);
        return Ok(rows);
    }

    /// <summary>Get a preview of the labeled training data (recent rows).</summary>
    [HttpGet("training-data")]
    public async Task<IActionResult> ListTrainingData([FromQuery] int limit = 50)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SupabaseClient>();
        var rows = await db.SelectAsync("meta_labeler_training_data",
            order: "labeled_at.desc", limit: limit);

        // Count wins/losses in a second query to avoid pulling all rows
        var summary = await db.SelectAsync("meta_labeler_training_data",
            select: "label", limit: 10000);
        int wins = 0, losses = 0;
        foreach (var r in summary)
        {
            if (r["label"]?.GetValue<int>() == 1) wins++; else losses++;
        }

        return Ok(new
        {
            totalRows = summary.Count,
            wins,
            losses,
            baseRate = summary.Count > 0 ? (double)wins / summary.Count : 0,
            recent = rows,
        });
    }

    /// <summary>
    /// Kick off triple-barrier labeling of the most-recent predictions. Fire-
    /// and-forget — poll /status for progress. Requires JOB_RUN_SECRET.
    /// </summary>
    [HttpPost("label")]
    public IActionResult StartLabeling(
        [FromQuery] int limit = 2000,
        [FromQuery] string? profileId = null)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        var jobName = "meta-labeler-label";
        if (_jobs.GetStatus(jobName)?.State == "running")
            return Conflict(new { error = "Labeling already in progress" });

        _jobs.MarkStarted(jobName);
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var labeler = scope.ServiceProvider.GetRequiredService<TripleBarrierLabeler>();
            try
            {
                var result = await labeler.LabelRecentAsync(limit, profileId);
                _jobs.MarkCompleted(jobName,
                    $"Labeled {result.Labeled} ({result.Wins} wins / {result.Losses} losses), " +
                    $"skipped {result.Skipped}, failed {result.Failed}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[meta-labeler] Labeling failed");
                _jobs.MarkFailed(jobName, ex.Message);
            }
        });

        return Accepted(new { message = "Meta-labeler labeling started", statusUrl = "/api/meta-labeler/status" });
    }

    /// <summary>
    /// Train a new model version from the labeled training data. Fire-and-
    /// forget — poll /status for progress. Reloads the inference service on
    /// success. Requires JOB_RUN_SECRET.
    /// </summary>
    [HttpPost("train")]
    public IActionResult StartTraining()
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        var jobName = "meta-labeler-train";
        if (_jobs.GetStatus(jobName)?.State == "running")
            return Conflict(new { error = "Training already in progress" });

        _jobs.MarkStarted(jobName);
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var trainer = scope.ServiceProvider.GetRequiredService<MetaLabelerTrainingService>();
            var inference = scope.ServiceProvider.GetRequiredService<MetaLabelerService>();
            try
            {
                var result = await trainer.TrainAsync();
                if (!result.Success)
                {
                    _jobs.MarkFailed(jobName, result.Error ?? "training failed");
                    return;
                }

                await inference.ReloadAsync();
                _jobs.MarkCompleted(jobName,
                    $"Model v{result.Version}: {result.TrainRows}/{result.TestRows} train/test, " +
                    $"AUC={result.Auc:F3}, Acc={result.Accuracy:F3}, F1={result.F1:F3}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[meta-labeler] Training failed");
                _jobs.MarkFailed(jobName, ex.Message);
            }
        });

        return Accepted(new { message = "Meta-labeler training started", statusUrl = "/api/meta-labeler/status" });
    }

    /// <summary>
    /// GET-based dev shortcut for the labeling + training flow. Same auth as
    /// backtest dev hook — the Claude sandbox uses this because its outbound
    /// proxy blocks POST. Requires ?token= matching JOB_RUN_SECRET.
    ///   action=label → runs labeling then returns
    ///   action=train → runs training then returns
    ///   action=full  → labels first, then trains
    /// </summary>
    [HttpGet("dev/run")]
    public IActionResult DevRun(
        [FromQuery] string? token,
        [FromQuery] string action = "full",
        [FromQuery] int limit = 2000)
    {
        var expected = _configuration["JOB_RUN_SECRET"];
        if (string.IsNullOrWhiteSpace(expected) || token != expected)
            return Unauthorized(new { error = "Invalid or missing ?token=" });

        var jobName = "meta-labeler-dev";
        if (_jobs.GetStatus(jobName)?.State == "running")
            return Conflict(new { error = "Dev job already in progress" });

        _jobs.MarkStarted(jobName);
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var labeler = scope.ServiceProvider.GetRequiredService<TripleBarrierLabeler>();
            var trainer = scope.ServiceProvider.GetRequiredService<MetaLabelerTrainingService>();
            var inference = scope.ServiceProvider.GetRequiredService<MetaLabelerService>();
            try
            {
                var summary = new List<string>();

                if (action == "label" || action == "full")
                {
                    var lr = await labeler.LabelRecentAsync(limit);
                    summary.Add($"Labeled {lr.Labeled} ({lr.Wins}W/{lr.Losses}L)");
                }

                if (action == "train" || action == "full")
                {
                    var tr = await trainer.TrainAsync();
                    if (tr.Success)
                    {
                        await inference.ReloadAsync();
                        summary.Add($"Trained v{tr.Version}: AUC={tr.Auc:F3} F1={tr.F1:F3}");
                    }
                    else summary.Add($"Train failed: {tr.Error}");
                }

                _jobs.MarkCompleted(jobName, string.Join(" | ", summary));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[meta-labeler] Dev run failed");
                _jobs.MarkFailed(jobName, ex.Message);
            }
        });

        return Ok(new { message = "Meta-labeler dev run started", action, statusUrl = "/api/meta-labeler/status" });
    }

    /// <summary>
    /// Calibration + rolling accuracy — tells you whether the meta-labeler's
    /// probability actually predicts realized win rate. Buckets recent trades
    /// (backtest + live) by predicted decile and returns the observed win rate
    /// per bucket. A calibrated model has diagonal buckets: predicted 0.7 →
    /// observed ~0.7.
    ///
    /// Also returns the enforcement threshold currently in effect (if any) so
    /// you can see where you're gating vs. where the calibration curve suggests
    /// gating.
    /// </summary>
    [HttpGet("monitoring")]
    public async Task<IActionResult> Monitoring([FromQuery] int lookbackDays = 30)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SupabaseClient>();
        var svc = scope.ServiceProvider.GetRequiredService<MetaLabelerService>();

        var since = DateTimeOffset.UtcNow.AddDays(-Math.Abs(lookbackDays));

        // Pull backtest trades that have a meta_probability set. Backtest is
        // the reliable source because outcomes are guaranteed observed, unlike
        // live paper trades which may still be open.
        var trades = await db.SelectAsync("backtest_trades",
            filter: $"meta_probability=not.is.null&created_at=gte.{since:o}&exit_reason=not.is.null",
            order: "created_at.desc",
            limit: 5000);

        // Decile bucket → (count, wins)
        var buckets = new (int count, int wins)[10];
        int totalTrades = 0, totalWins = 0;
        double sumPredicted = 0, sumObserved = 0;

        foreach (var t in trades)
        {
            var probNode = t["meta_probability"];
            if (probNode is null || probNode.GetValueKind() == System.Text.Json.JsonValueKind.Null)
                continue;
            var prob = probNode.GetValue<double>();
            var pnl = t["pnl_percent"];
            if (pnl is null || pnl.GetValueKind() == System.Text.Json.JsonValueKind.Null)
                continue;
            var pnlV = pnl.GetValue<double>();
            var isWin = pnlV > 0;

            var idx = Math.Min(9, Math.Max(0, (int)(prob * 10)));
            buckets[idx].count++;
            if (isWin) buckets[idx].wins++;

            totalTrades++;
            if (isWin) totalWins++;
            sumPredicted += prob;
            sumObserved += isWin ? 1 : 0;
        }

        var calibration = new List<object>();
        for (int i = 0; i < 10; i++)
        {
            var (count, wins) = buckets[i];
            calibration.Add(new
            {
                bucket = $"{i / 10.0:F1}-{(i + 1) / 10.0:F1}",
                lowerBound = i / 10.0,
                upperBound = (i + 1) / 10.0,
                count,
                wins,
                observedWinRate = count > 0 ? (double)wins / count : 0.0,
                predictedCenter = (i + 0.5) / 10.0,
            });
        }

        var threshold = await svc.GetEnforcementThresholdAsync();

        return Ok(new
        {
            lookbackDays,
            since,
            isReady = svc.IsReady,
            activeVersion = svc.ActiveVersion,
            enforcementThreshold = threshold,
            enforcementActive = threshold is not null,
            summary = new
            {
                totalTrades,
                overallWinRate = totalTrades > 0 ? (double)totalWins / totalTrades : 0.0,
                avgPredictedProbability = totalTrades > 0 ? sumPredicted / totalTrades : 0.0,
                avgObservedWinRate = totalTrades > 0 ? sumObserved / totalTrades : 0.0,
                calibrationGap = totalTrades > 0 ? Math.Abs((sumPredicted / totalTrades) - (sumObserved / totalTrades)) : 0.0,
            },
            calibration,
            hint = "A well-calibrated model has observedWinRate ≈ predictedCenter in every bucket. " +
                   "Large calibrationGap (>0.10) means the probability isn't reliable — retrain.",
        });
    }

    /// <summary>Force a reload of the active model from disk (after a manual swap).</summary>
    [HttpPost("reload")]
    public async Task<IActionResult> Reload()
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MetaLabelerService>();
        var ok = await svc.ReloadAsync();
        return Ok(new { reloaded = ok, activeVersion = svc.ActiveVersion });
    }

    // ── Helpers ──────────────────────────────────────────────────

    private bool ValidateJobSecret()
    {
        var expected = _configuration["JOB_RUN_SECRET"];
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var provided = Request.Headers["x-job-secret"].FirstOrDefault();
        return !string.IsNullOrEmpty(provided) && provided == expected;
    }
}
