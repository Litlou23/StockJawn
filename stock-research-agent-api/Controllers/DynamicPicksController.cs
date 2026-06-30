using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.ResearchEngine;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// Endpoints for the dynamic pick orchestrator.
///
///   POST /api/jobs/run-dynamic-morning-picks      (fire-and-forget, 202)
///   POST /api/jobs/run-dynamic-eod-review         (fire-and-forget, 202)
///   POST /api/jobs/run-dynamic-learning-update    (fire-and-forget, 202)
///   GET  /api/paper-stock-candidates              — list recent candidates
///   GET  /api/paper-stock-candidates/open         — open only
///   GET  /api/paper-stock-candidates/outcomes     — recent outcomes
///   GET  /api/paper-stock-candidates/stats        — stock_learning_stats
///   GET  /api/paper-stock-candidates/{id}         — candidate + linked options
///   GET  /api/dashboard/dynamic-summary           — summary cards data
///
/// Job routes require x-job-secret. They return 202 Accepted immediately,
/// kick off the actual work on a background Task, and update
/// JobStatusTracker so the UI can poll /api/jobs/status for progress. This
/// is the only pattern that survives Azure App Service's ~230s HTTP idle
/// timeout and Netlify's function limit — see CLAUDE.md.
/// </summary>
[ApiController]
public class DynamicPicksController : ControllerBase
{
    private readonly DynamicPickOrchestrator _orchestrator;
    private readonly PaperStockCandidateRepository _stockRepo;
    private readonly JobStatusTracker _jobStatus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DynamicPicksController> _logger;

    public DynamicPicksController(
        DynamicPickOrchestrator orchestrator,
        PaperStockCandidateRepository stockRepo,
        JobStatusTracker jobStatus,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DynamicPicksController> logger)
    {
        _orchestrator = orchestrator;
        _stockRepo = stockRepo;
        _jobStatus = jobStatus;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Secret validation
    // -----------------------------------------------------------------------

    private bool ValidateJobSecret()
    {
        var expected = _configuration["JOB_RUN_SECRET"];
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var provided = Request.Headers["x-job-secret"].FirstOrDefault();
        return !string.IsNullOrEmpty(provided) && provided == expected;
    }

    // -----------------------------------------------------------------------
    // Fire-and-forget helper. Spawns the work on the threadpool, returns
    // immediately so the HTTP handler isn't holding the request open.
    // The background task updates JobStatusTracker on completion/failure.
    // We resolve the orchestrator from a fresh DI scope because the request
    // scope is gone the moment we return.
    // -----------------------------------------------------------------------

    private IActionResult AcceptAndRun(
        string jobName,
        Func<DynamicPickOrchestrator, Task<(string Summary, IReadOnlyList<string> Errors)>> work,
        string trigger)
    {
        _logger.LogInformation("[dynamic-jobs] {Job} triggered by {Trigger} — running in background", jobName, trigger);
        _jobStatus.MarkStarted(jobName);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<DynamicPickOrchestrator>();
                var (summary, errors) = await work(orchestrator);
                if (errors.Count > 0)
                    _jobStatus.MarkFailed(jobName, string.Join("; ", errors.Take(5)));
                else
                    _jobStatus.MarkCompleted(jobName, summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[dynamic-jobs] {Job} failed", jobName);
                _jobStatus.MarkFailed(jobName, ex.Message);
            }
        });

        return Accepted(new
        {
            status = "started",
            jobName,
            message = $"{jobName} is running in the background. Poll /api/jobs/status for progress.",
            startedAt = DateTimeOffset.UtcNow,
        });
    }

    // -----------------------------------------------------------------------
    // Job endpoints — all return 202 Accepted immediately
    // -----------------------------------------------------------------------

    [HttpPost("api/jobs/run-dynamic-morning-picks")]
    public IActionResult RunMorning([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        return AcceptAndRun(
            "run-dynamic-morning-picks",
            async o =>
            {
                var r = await o.RunDynamicMorningPicksAsync();
                return (r.Report, r.Errors);
            },
            trigger?.Trigger ?? "unknown");
    }

    [HttpPost("api/jobs/run-dynamic-eod-review")]
    public IActionResult RunEod([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        return AcceptAndRun(
            "run-dynamic-eod-review",
            async o =>
            {
                var r = await o.RunDynamicEodReviewAsync();
                return (r.Report, r.Errors);
            },
            trigger?.Trigger ?? "unknown");
    }

    [HttpPost("api/jobs/run-dynamic-learning-update")]
    public IActionResult RunLearning([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        return AcceptAndRun(
            "run-dynamic-learning-update",
            async o =>
            {
                var r = await o.RunDynamicLearningUpdateAsync();
                return (r.Report, r.Errors);
            },
            trigger?.Trigger ?? "unknown");
    }

    // -----------------------------------------------------------------------
    // Read endpoints (cheap, run inline)
    // -----------------------------------------------------------------------

    [HttpGet("api/paper-stock-candidates")]
    public async Task<IActionResult> List([FromQuery] int limit = 50)
    {
        var rows = await _stockRepo.GetRecentCandidatesAsync(limit);
        return Ok(new { count = rows.Count, candidates = rows });
    }

    [HttpGet("api/paper-stock-candidates/open")]
    public async Task<IActionResult> Open()
    {
        var rows = await _stockRepo.GetOpenCandidatesAsync();
        return Ok(new { count = rows.Count, candidates = rows });
    }

    [HttpGet("api/paper-stock-candidates/outcomes")]
    public async Task<IActionResult> Outcomes([FromQuery] int limit = 50)
    {
        var rows = await _stockRepo.GetRecentOutcomesAsync(limit);
        return Ok(new { count = rows.Count, outcomes = rows });
    }

    [HttpGet("api/paper-stock-candidates/stats")]
    public async Task<IActionResult> Stats()
    {
        var rows = await _stockRepo.GetAllLearningStatsAsync();
        return Ok(new { count = rows.Count, stats = rows });
    }

    [HttpGet("api/paper-stock-candidates/{id}")]
    public async Task<IActionResult> Detail(string id)
    {
        var candidate = await _stockRepo.GetCandidateAsync(id);
        if (candidate is null) return NotFound(new { error = "Stock candidate not found" });

        var options = await _stockRepo.GetOptionsForStockCandidateAsync(id);
        return Ok(new { candidate, optionCandidates = options });
    }

    [HttpGet("api/dashboard/dynamic-summary")]
    public async Task<IActionResult> Summary()
    {
        var summary = await _orchestrator.GetDashboardSummaryAsync();
        return Ok(summary);
    }
}
