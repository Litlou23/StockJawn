using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchEngine;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// Endpoints for the dynamic pick orchestrator.
///
///   POST /api/jobs/run-dynamic-morning-picks
///   POST /api/jobs/run-dynamic-eod-review
///   POST /api/jobs/run-dynamic-learning-update
///   GET  /api/paper-stock-candidates           — list recent candidates
///   GET  /api/paper-stock-candidates/open      — open only
///   GET  /api/paper-stock-candidates/outcomes  — recent outcomes
///   GET  /api/paper-stock-candidates/stats     — stock_learning_stats
///   GET  /api/paper-stock-candidates/{id}      — candidate + linked options
///   GET  /api/dashboard/dynamic-summary        — summary cards data
///
/// Job routes require x-job-secret. Read routes do not.
/// </summary>
[ApiController]
public class DynamicPicksController : ControllerBase
{
    private readonly DynamicPickOrchestrator _orchestrator;
    private readonly PaperStockCandidateRepository _stockRepo;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DynamicPicksController> _logger;

    public DynamicPicksController(
        DynamicPickOrchestrator orchestrator,
        PaperStockCandidateRepository stockRepo,
        IConfiguration configuration,
        ILogger<DynamicPicksController> logger)
    {
        _orchestrator = orchestrator;
        _stockRepo = stockRepo;
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
    // Job endpoints
    // -----------------------------------------------------------------------

    [HttpPost("api/jobs/run-dynamic-morning-picks")]
    public async Task<IActionResult> RunMorning([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        _logger.LogInformation("[dynamic-jobs] morning triggered by {Trigger}", trigger?.Trigger ?? "unknown");
        var result = await _orchestrator.RunDynamicMorningPicksAsync();
        return Ok(result);
    }

    [HttpPost("api/jobs/run-dynamic-eod-review")]
    public async Task<IActionResult> RunEod([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        _logger.LogInformation("[dynamic-jobs] eod triggered by {Trigger}", trigger?.Trigger ?? "unknown");
        var result = await _orchestrator.RunDynamicEodReviewAsync();
        return Ok(result);
    }

    [HttpPost("api/jobs/run-dynamic-learning-update")]
    public async Task<IActionResult> RunLearning([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        _logger.LogInformation("[dynamic-jobs] learning triggered by {Trigger}", trigger?.Trigger ?? "unknown");
        var result = await _orchestrator.RunDynamicLearningUpdateAsync();
        return Ok(result);
    }

    // -----------------------------------------------------------------------
    // Read endpoints
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
