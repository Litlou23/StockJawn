using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.Discovery;
using StockResearchAgent.Api.Services.Evidence;
using StockResearchAgent.Api.Services.OpportunityLearning;
using StockResearchAgent.Api.Services.ResearchEngine;
using StockResearchAgent.Api.Services.ResearchUniverse;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// Job endpoints for the research engine. All POST routes require an
/// x-job-secret header matching the JOB_RUN_SECRET env var. Called by
/// Supabase Edge Functions on a pg_cron schedule.
/// </summary>
[ApiController]
[Route("api/jobs")]
public class ResearchJobsController : ControllerBase
{
    private readonly DailyResearchRunService _researchService;
    private readonly ResearchRepository _repo;
    private readonly JobStatusTracker _tracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly DataHygieneService _hygiene;
    private readonly ILogger<ResearchJobsController> _logger;

    public ResearchJobsController(
        DailyResearchRunService researchService,
        ResearchRepository repo,
        JobStatusTracker tracker,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        DataHygieneService hygiene,
        ILogger<ResearchJobsController> logger)
    {
        _researchService = researchService;
        _repo = repo;
        _tracker = tracker;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _hygiene = hygiene;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Secret validation
    // -----------------------------------------------------------------------

    private bool ValidateJobSecret()
    {
        var expected = _configuration["JOB_RUN_SECRET"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            _logger.LogWarning("[jobs] JOB_RUN_SECRET not configured -- rejecting request");
            return false;
        }

        var provided = Request.Headers["x-job-secret"].FirstOrDefault();
        return !string.IsNullOrEmpty(provided) && provided == expected;
    }

    private string? GetTraceId() => Request.Headers["x-trace-id"].FirstOrDefault();

    // -----------------------------------------------------------------------
    // Shared: accepted-background pattern
    // -----------------------------------------------------------------------

    private async Task<IActionResult> AcceptBackgroundJob(
        string runType, string label, string? traceId,
        Func<string, Task<(string Report, string? SummaryDetail)>> work)
    {
        // Reject if a job of this type is already running
        var existing = await _repo.GetRunningJobAsync(runType);
        if (existing is not null)
        {
            _logger.LogWarning("[jobs] {Label} already running: {RunId} traceId={TraceId}",
                label, existing.Id, traceId ?? "(none)");
            return Conflict(new
            {
                ok = false,
                accepted = false,
                jobRunId = existing.Id,
                runType,
                status = "running",
                message = $"A {label} is already running.",
            });
        }

        // Create the research_runs row immediately
        var run = await _repo.CreateResearchRunAsync(runType);
        if (run is null)
            return StatusCode(500, new { ok = false, error = "Failed to create research run row." });

        _tracker.MarkStarted(runType);

        // Fire-and-forget: run in background
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation(
                    "[jobs] Background {Label} starting runId={RunId} traceId={TraceId}",
                    label, run.Id, traceId ?? "(none)");

                var (report, detail) = await work(run.Id);

                _tracker.MarkCompleted(runType, report);

                _logger.LogInformation(
                    "[jobs] Background {Label} completed runId={RunId} {Detail} traceId={TraceId}",
                    label, run.Id, detail ?? "", traceId ?? "(none)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[jobs] Background {Label} failed runId={RunId} traceId={TraceId}",
                    label, run.Id, traceId ?? "(none)");

                _tracker.MarkFailed(runType, ex.Message);

                await _repo.CompleteResearchRunAsync(
                    run.Id, $"{label} failed: {ex.Message}", 0, 0, [ex.Message]);
            }
        });

        return Accepted(new
        {
            ok = true,
            accepted = true,
            jobRunId = run.Id,
            runType,
            status = "running",
            message = $"{label} accepted and running in background.",
        });
    }

    // -----------------------------------------------------------------------
    // Morning Scan
    // -----------------------------------------------------------------------

    /// <summary>
    /// Morning scan now delegates to the DynamicPickOrchestrator so that
    /// predictions are automatically wrapped as paper stock candidates.
    /// Previously this called DailyResearchRunService directly, which only
    /// created predictions — the paper stock candidate step never ran.
    /// </summary>
    [HttpPost("run-morning-scan")]
    public Task<IActionResult> RunMorningScan([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Task.FromResult<IActionResult>(Unauthorized(new { error = "Invalid or missing x-job-secret header" }));

        var traceId = GetTraceId();
        _logger.LogInformation("[jobs] Morning scan triggered by {Trigger} traceId={TraceId} — routing through DynamicPickOrchestrator",
            trigger?.Trigger ?? "unknown", traceId ?? "(none)");

        _tracker.MarkStarted("morning_scan");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<DynamicPickOrchestrator>();
                var result = await orchestrator.RunDynamicMorningPicksAsync();

                if (result.Errors.Count > 0)
                    _tracker.MarkFailed("morning_scan", string.Join("; ", result.Errors.Take(5)));
                else
                    _tracker.MarkCompleted("morning_scan", result.Report);

                _logger.LogInformation("[jobs] Morning scan completed via orchestrator traceId={TraceId}", traceId ?? "(none)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[jobs] Morning scan failed traceId={TraceId}", traceId ?? "(none)");
                _tracker.MarkFailed("morning_scan", ex.Message);
            }
        });

        return Task.FromResult<IActionResult>(Accepted(new
        {
            ok = true,
            accepted = true,
            runType = "morning_scan",
            status = "running",
            message = "Morning scan accepted — running via DynamicPickOrchestrator (predictions + paper stock candidates).",
        }));
    }

    // -----------------------------------------------------------------------
    // End-of-Day Review
    // -----------------------------------------------------------------------

    /// <summary>
    /// EOD review now delegates to the DynamicPickOrchestrator so that
    /// paper stock candidate outcomes are evaluated alongside predictions.
    /// </summary>
    [HttpPost("run-end-of-day-review")]
    public Task<IActionResult> RunEndOfDayReview([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Task.FromResult<IActionResult>(Unauthorized(new { error = "Invalid or missing x-job-secret header" }));

        var traceId = GetTraceId();
        _logger.LogInformation("[jobs] EOD review triggered by {Trigger} traceId={TraceId} — routing through DynamicPickOrchestrator",
            trigger?.Trigger ?? "unknown", traceId ?? "(none)");

        _tracker.MarkStarted("end_of_day_review");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<DynamicPickOrchestrator>();
                var result = await orchestrator.RunDynamicEodReviewAsync();

                if (result.Errors.Count > 0)
                    _tracker.MarkFailed("end_of_day_review", string.Join("; ", result.Errors.Take(5)));
                else
                    _tracker.MarkCompleted("end_of_day_review", result.Report);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[jobs] EOD review failed traceId={TraceId}", traceId ?? "(none)");
                _tracker.MarkFailed("end_of_day_review", ex.Message);
            }
        });

        return Task.FromResult<IActionResult>(Accepted(new
        {
            ok = true,
            accepted = true,
            runType = "end_of_day_review",
            status = "running",
            message = "EOD review accepted — running via DynamicPickOrchestrator.",
        }));
    }

    // -----------------------------------------------------------------------
    // Learning Update
    // -----------------------------------------------------------------------

    /// <summary>
    /// Learning update now delegates to the DynamicPickOrchestrator.
    /// </summary>
    [HttpPost("run-learning-update")]
    public Task<IActionResult> RunLearningUpdate([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Task.FromResult<IActionResult>(Unauthorized(new { error = "Invalid or missing x-job-secret header" }));

        var traceId = GetTraceId();
        _logger.LogInformation("[jobs] Learning update triggered by {Trigger} traceId={TraceId} — routing through DynamicPickOrchestrator",
            trigger?.Trigger ?? "unknown", traceId ?? "(none)");

        _tracker.MarkStarted("learning_update");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<DynamicPickOrchestrator>();
                var result = await orchestrator.RunDynamicLearningUpdateAsync();

                if (result.Errors.Count > 0)
                    _tracker.MarkFailed("learning_update", string.Join("; ", result.Errors.Take(5)));
                else
                    _tracker.MarkCompleted("learning_update", result.Report);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[jobs] Learning update failed traceId={TraceId}", traceId ?? "(none)");
                _tracker.MarkFailed("learning_update", ex.Message);
            }
        });

        return Task.FromResult<IActionResult>(Accepted(new
        {
            ok = true,
            accepted = true,
            runType = "learning_update",
            status = "running",
            message = "Learning update accepted — running via DynamicPickOrchestrator.",
        }));
    }

    // -----------------------------------------------------------------------
    // Data Hygiene
    // -----------------------------------------------------------------------

    [HttpPost("run-data-hygiene")]
    public async Task<IActionResult> RunDataHygiene([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        var traceId = GetTraceId();
        _logger.LogInformation("[jobs] Data hygiene triggered by {Trigger} traceId={TraceId}",
            trigger?.Trigger ?? "unknown", traceId ?? "(none)");

        return await AcceptBackgroundJob("data_hygiene", "Data hygiene", traceId,
            async (runId) =>
            {
                var result = await _hygiene.RunFullHygieneAsync();

                var summary = $"{result.FalseOptionLossesDeleted} false losses, " +
                    $"{result.OptionCandidatesReopened} reopened, " +
                    $"{result.StalePredictionsExpired + result.StaleOptionCandidatesExpired} expired, " +
                    $"{result.ImpossibleValuesFixed} fixed, " +
                    $"{result.LearningStatsReset} stats cleaned";

                var report = string.Join("\n", result.Actions);
                if (result.Warnings.Count > 0)
                    report += "\n\nWARNINGS:\n" + string.Join("\n", result.Warnings);

                await _repo.CompleteResearchRunAsync(runId, report,
                    result.FalseOptionLossesDeleted + result.StalePredictionsExpired,
                    result.OptionCandidatesReopened,
                    result.Warnings);

                return (report, summary);
            });
    }

    // -----------------------------------------------------------------------
    // Discovery Engine — scan all providers for new Research Assets
    // -----------------------------------------------------------------------

    [HttpPost("run-discovery")]
    public Task<IActionResult> RunDiscovery([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Task.FromResult<IActionResult>(Unauthorized(new { error = "Invalid or missing x-job-secret header" }));

        var traceId = GetTraceId();
        _logger.LogInformation("[jobs] Discovery scan triggered by {Trigger} traceId={TraceId}",
            trigger?.Trigger ?? "unknown", traceId ?? "(none)");

        _tracker.MarkStarted("discovery");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<IDiscoveryEngine>();
                var result = await engine.RunDiscoveryAsync();

                _tracker.MarkCompleted("discovery",
                    $"{result.TotalEventsDiscovered} events from {result.ProviderResults.Count} providers, {result.NewAssetsCreated} new assets");

                _logger.LogInformation("[jobs] Discovery completed: {Events} events, {Assets} new assets traceId={TraceId}",
                    result.TotalEventsDiscovered, result.NewAssetsCreated, traceId ?? "(none)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[jobs] Discovery failed traceId={TraceId}", traceId ?? "(none)");
                _tracker.MarkFailed("discovery", ex.Message);
            }
        });

        return Task.FromResult<IActionResult>(Accepted(new
        {
            ok = true,
            accepted = true,
            runType = "discovery",
            status = "running",
            message = "Discovery scan accepted — running all providers.",
        }));
    }

    // -----------------------------------------------------------------------
    // Continuous Discovery — incremental evidence since last checkpoint
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs one lightweight continuous discovery cycle.
    /// Only processes events newer than the last checkpoint.
    /// Does NOT generate predictions, run Morning Scan, or trigger Learning.
    /// Designed to be called hourly (configurable) during market hours.
    /// </summary>
    [HttpPost("run-continuous-discovery")]
    public Task<IActionResult> RunContinuousDiscovery([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Task.FromResult<IActionResult>(Unauthorized(new { error = "Invalid or missing x-job-secret header" }));

        var traceId = GetTraceId();
        _logger.LogInformation(
            "[jobs] Continuous discovery triggered by {Trigger} traceId={TraceId}",
            trigger?.Trigger ?? "unknown", traceId ?? "(none)");

        _tracker.MarkStarted("continuous_discovery");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<IContinuousDiscoveryEngine>();
                var result = await engine.RunCycleAsync();

                if (result.WasSkipped)
                    _tracker.MarkCompleted("continuous_discovery",
                        $"Skipped: {result.SkipReason}");
                else
                    _tracker.MarkCompleted("continuous_discovery",
                        $"{result.NewEventsFound} events, {result.NewAssetsCreated} new + " +
                        $"{result.ExistingAssetsUpdated} updated assets, " +
                        $"{result.TimelineEventsCreated} timeline events, " +
                        $"{result.HistoricalProfilesBuilt} profiles built, " +
                        $"{result.HistoricalProfilesRefreshed} refreshed");

                _logger.LogInformation(
                    "[jobs] Continuous discovery completed traceId={TraceId}: {Summary}",
                    traceId ?? "(none)", result.Summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[jobs] Continuous discovery failed traceId={TraceId}",
                    traceId ?? "(none)");
                _tracker.MarkFailed("continuous_discovery", ex.Message);
            }
        });

        return Task.FromResult<IActionResult>(Accepted(new
        {
            ok = true,
            accepted = true,
            runType = "continuous_discovery",
            status = "running",
            message = "Continuous discovery cycle accepted — scanning for new evidence since last checkpoint.",
        }));
    }

    // -----------------------------------------------------------------------
    // Research Universe Maintenance — decay scores, promote, archive stale
    // -----------------------------------------------------------------------

    [HttpPost("run-universe-maintenance")]
    public Task<IActionResult> RunUniverseMaintenance([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Task.FromResult<IActionResult>(Unauthorized(new { error = "Invalid or missing x-job-secret header" }));

        var traceId = GetTraceId();
        _logger.LogInformation("[jobs] Universe maintenance triggered by {Trigger} traceId={TraceId}",
            trigger?.Trigger ?? "unknown", traceId ?? "(none)");

        _tracker.MarkStarted("universe_maintenance");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<IResearchUniverseEngine>();
                var result = await engine.RunMaintenanceAsync();

                _tracker.MarkCompleted("universe_maintenance", result.Summary);

                _logger.LogInformation("[jobs] Universe maintenance completed traceId={TraceId}: {Summary}",
                    traceId ?? "(none)", result.Summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[jobs] Universe maintenance failed traceId={TraceId}", traceId ?? "(none)");
                _tracker.MarkFailed("universe_maintenance", ex.Message);
            }
        });

        return Task.FromResult<IActionResult>(Accepted(new
        {
            ok = true,
            accepted = true,
            runType = "universe_maintenance",
            status = "running",
            message = "Universe maintenance accepted — evaluating all active research assets.",
        }));
    }

    // -----------------------------------------------------------------------
    // Opportunity Learning — scan for missed opportunities
    // -----------------------------------------------------------------------

    [HttpPost("run-opportunity-scan")]
    public Task<IActionResult> RunOpportunityScan([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Task.FromResult<IActionResult>(Unauthorized(new { error = "Invalid or missing x-job-secret header" }));

        var traceId = GetTraceId();
        _logger.LogInformation("[jobs] Opportunity scan triggered by {Trigger} traceId={TraceId}",
            trigger?.Trigger ?? "unknown", traceId ?? "(none)");

        _tracker.MarkStarted("opportunity_scan");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IOpportunityLearningService>();
                var result = await service.ScanForMissedOpportunitiesAsync();

                if (result.Errors.Count > 0)
                    _tracker.MarkFailed("opportunity_scan", string.Join("; ", result.Errors.Take(5)));
                else
                    _tracker.MarkCompleted("opportunity_scan", result.Summary);

                _logger.LogInformation("[jobs] Opportunity scan completed traceId={TraceId}: {Summary}",
                    traceId ?? "(none)", result.Summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[jobs] Opportunity scan failed traceId={TraceId}", traceId ?? "(none)");
                _tracker.MarkFailed("opportunity_scan", ex.Message);
            }
        });

        return Task.FromResult<IActionResult>(Accepted(new
        {
            ok = true,
            accepted = true,
            runType = "opportunity_scan",
            status = "running",
            message = "Opportunity scan accepted — scanning for missed opportunities.",
        }));
    }

    // -----------------------------------------------------------------------
    // Job status polling (no secret required)
    // -----------------------------------------------------------------------

    /// <summary>GET /api/jobs/latest?runType=morning_scan</summary>
    [HttpGet("latest")]
    public async Task<IActionResult> GetLatestJob([FromQuery] string? runType)
    {
        var run = await _repo.GetLatestResearchRunAsync(runType);
        if (run is null)
            return NotFound(new { error = "No job runs found." });
        return Ok(run);
    }

    /// <summary>GET /api/jobs/{jobRunId}</summary>
    [HttpGet("{jobRunId}")]
    public async Task<IActionResult> GetJobById(string jobRunId)
    {
        var run = await _repo.GetResearchRunByIdAsync(jobRunId);
        if (run is null)
            return NotFound(new { error = $"Job run {jobRunId} not found." });
        return Ok(run);
    }
}
