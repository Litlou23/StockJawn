using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchEngine;

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
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResearchJobsController> _logger;

    public ResearchJobsController(
        DailyResearchRunService researchService,
        IConfiguration configuration,
        ILogger<ResearchJobsController> logger)
    {
        _researchService = researchService;
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

    // -----------------------------------------------------------------------
    // Morning Scan
    // -----------------------------------------------------------------------

    [HttpPost("run-morning-scan")]
    public async Task<IActionResult> RunMorningScan([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        _logger.LogInformation("[jobs] Morning scan triggered by {Trigger}", trigger?.Trigger ?? "unknown");

        var result = await _researchService.RunMorningScanAsync();
        return Ok(result);
    }

    // -----------------------------------------------------------------------
    // End-of-Day Review
    // -----------------------------------------------------------------------

    [HttpPost("run-end-of-day-review")]
    public async Task<IActionResult> RunEndOfDayReview([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        _logger.LogInformation("[jobs] EOD review triggered by {Trigger}", trigger?.Trigger ?? "unknown");

        var result = await _researchService.RunEndOfDayReviewAsync();
        return Ok(result);
    }

    // -----------------------------------------------------------------------
    // Learning Update
    // -----------------------------------------------------------------------

    [HttpPost("run-learning-update")]
    public async Task<IActionResult> RunLearningUpdate([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        _logger.LogInformation("[jobs] Learning update triggered by {Trigger}", trigger?.Trigger ?? "unknown");

        var result = await _researchService.RunLearningUpdateAsync();
        return Ok(result);
    }
}
