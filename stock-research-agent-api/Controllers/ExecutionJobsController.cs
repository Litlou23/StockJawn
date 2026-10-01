using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.Execution;
using StockResearchAgent.Api.Services.Scanner;
using StockResearchAgent.Api.Services.Calendar;

namespace StockResearchAgent.Api.Controllers;

[ApiController]
public class ExecutionJobsController : ControllerBase
{
    private const string JobName = "execute-approved-picks";

    private readonly JobStatusTracker _jobStatus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExecutionJobsController> _logger;

    public ExecutionJobsController(
        JobStatusTracker jobStatus,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ExecutionJobsController> logger)
    {
        _jobStatus = jobStatus;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("api/jobs/execute-approved-picks")]
    public IActionResult ExecuteApprovedPicks([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        var triggeredBy = trigger?.Trigger ?? "unknown";
        _logger.LogInformation("[execution-jobs] {Job} triggered by {Trigger} — running in background", JobName, triggeredBy);
        var ct = _jobStatus.MarkStarted(JobName);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var executor = scope.ServiceProvider.GetRequiredService<ClaudePickExecutor>();
                var result = await executor.ExecuteApprovedPicksAsync(ct);
                if (result.Errors.Count > 0)
                    _jobStatus.MarkFailed(JobName, string.Join("; ", result.Errors.Take(5)));
                else
                    _jobStatus.MarkCompleted(JobName, result.Summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[execution-jobs] {Job} failed", JobName);
                _jobStatus.MarkFailed(JobName, ex.Message);
            }
        });

        return Accepted(new
        {
            status = "started",
            jobName = JobName,
            message = $"{JobName} is running in the background. Poll /api/jobs/status for progress.",
            startedAt = DateTimeOffset.UtcNow,
        });
    }

    // Read-only: logs in, lists Robinhood's tools and checks our argument templates against their schemas.
    [HttpGet("api/jobs/execute-approved-picks/readiness")]
    public async Task<IActionResult> Readiness(CancellationToken ct)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        using var scope = _scopeFactory.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ClaudePickExecutor>();
        var r = await executor.CheckReadinessAsync(ct);
        return Ok(new { ready = r.Ready, problems = r.Problems, tools = r.Tools });
    }

    // StockedUp-style movers scan. ?write=false previews without saving rows.
    [HttpPost("api/jobs/scan-movers")]
    public async Task<IActionResult> ScanMovers([FromQuery] bool write = true, CancellationToken ct = default)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        using var scope = _scopeFactory.CreateScope();
        var scanner = scope.ServiceProvider.GetRequiredService<MoversScanner>();
        var r = await scanner.ScanAsync(write, ct);
        return Ok(new { scanDate = r.ScanDate.ToString("yyyy-MM-dd"), pickDate = r.PickDate.ToString("yyyy-MM-dd"), r.Universe, r.Setups, r.Notes });
    }

    // Refresh market_events now. ?alert=true also sends the next session's events to the ntfy topic.
    [HttpPost("api/jobs/refresh-calendar")]
    public async Task<IActionResult> RefreshCalendar([FromQuery] bool alert = false, CancellationToken ct = default)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        using var scope = _scopeFactory.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<EventsCalendarService>().RefreshAsync(alert, ct);
        return Ok(r);
    }

    private bool ValidateJobSecret()
    {
        var expected = _configuration["JOB_RUN_SECRET"];
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var provided = Request.Headers["x-job-secret"].FirstOrDefault();
        return !string.IsNullOrEmpty(provided) && provided == expected;
    }
}
