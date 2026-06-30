using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.ResearchEngine;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResearchJobsController> _logger;

    public ResearchJobsController(
        DailyResearchRunService researchService,
        ResearchRepository repo,
        JobStatusTracker tracker,
        IConfiguration configuration,
        ILogger<ResearchJobsController> logger)
    {
        _researchService = researchService;
        _repo = repo;
        _tracker = tracker;
        _configuration = configuration;
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

    [HttpPost("run-morning-scan")]
    public async Task<IActionResult> RunMorningScan([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        var traceId = GetTraceId();
        _logger.LogInformation("[jobs] Morning scan triggered by {Trigger} traceId={TraceId}",
            trigger?.Trigger ?? "unknown", traceId ?? "(none)");

        return await AcceptBackgroundJob("morning_scan", "morning scan", traceId,
            async runId =>
            {
                var r = await _researchService.RunMorningScanAsync(runId);
                return (r.Report, $"predictions={r.PredictionsGenerated}");
            });
    }

    // -----------------------------------------------------------------------
    // End-of-Day Review
    // -----------------------------------------------------------------------

    [HttpPost("run-end-of-day-review")]
    public async Task<IActionResult> RunEndOfDayReview([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        var traceId = GetTraceId();
        _logger.LogInformation("[jobs] EOD review triggered by {Trigger} traceId={TraceId}",
            trigger?.Trigger ?? "unknown", traceId ?? "(none)");

        return await AcceptBackgroundJob("end_of_day_review", "EOD review", traceId,
            async runId =>
            {
                var r = await _researchService.RunEndOfDayReviewAsync(runId);
                return (r.Report, $"evaluated={r.PredictionsEvaluated}");
            });
    }

    // -----------------------------------------------------------------------
    // Learning Update
    // -----------------------------------------------------------------------

    [HttpPost("run-learning-update")]
    public async Task<IActionResult> RunLearningUpdate([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        var traceId = GetTraceId();
        _logger.LogInformation("[jobs] Learning update triggered by {Trigger} traceId={TraceId}",
            trigger?.Trigger ?? "unknown", traceId ?? "(none)");

        return await AcceptBackgroundJob("learning_update", "learning update", traceId,
            async runId =>
            {
                var r = await _researchService.RunLearningUpdateAsync(runId);
                return (r.Report, $"insights={r.InsightsGenerated} weights={r.WeightsAdjusted}");
            });
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
