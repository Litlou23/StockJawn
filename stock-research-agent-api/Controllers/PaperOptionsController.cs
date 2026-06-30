using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.OptionsData;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// /api/paper-options/* — backs the /paper-options frontend page.
///
/// Real option contract data only (MarketData.app). No invented contracts,
/// no invented prices, no brokerage execution. Outcomes feed the
/// option_learning_stats table for the learning engine.
/// </summary>
[ApiController]
[Route("api/paper-options")]
public class PaperOptionsController : ControllerBase
{
    private readonly PaperOptionsService _service;
    private readonly ILogger<PaperOptionsController> _logger;

    public PaperOptionsController(
        PaperOptionsService service,
        ILogger<PaperOptionsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>GET /api/paper-options/predictions — eligible saved predictions for the selector.</summary>
    [HttpGet("predictions")]
    public async Task<IActionResult> GetPredictions()
    {
        var preds = await _service.GetEligiblePredictionsAsync(50);
        return Ok(new { count = preds.Count, predictions = preds });
    }

    /// <summary>POST /api/paper-options/generate-candidates — score real contracts for a prediction.</summary>
    [HttpPost("generate-candidates")]
    public async Task<IActionResult> Generate([FromBody] GenerateCandidatesRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.PredictionId))
            return BadRequest(new { error = "predictionId is required" });

        var resp = await _service.GenerateCandidatesAsync(req);
        if (resp is null) return NotFound(new { error = "Prediction not found" });

        return Ok(resp);
    }

    /// <summary>POST /api/paper-options/save-candidate — persist a chosen candidate.</summary>
    [HttpPost("save-candidate")]
    public async Task<IActionResult> Save([FromBody] SaveCandidateRequest req)
    {
        if (req is null || req.Candidate is null)
            return BadRequest(new { error = "candidate payload is required" });

        var saved = await _service.SaveCandidateAsync(req);
        if (saved is null) return StatusCode(500, new { error = "Failed to save candidate" });

        return Ok(new { saved });
    }

    /// <summary>GET /api/paper-options/open-candidates — currently open paper candidates.</summary>
    [HttpGet("open-candidates")]
    public async Task<IActionResult> OpenCandidates()
    {
        var open = await _service.GetOpenCandidatesAsync();
        return Ok(new { count = open.Count, candidates = open });
    }

    /// <summary>POST /api/paper-options/evaluate-candidate — pull current data and score outcome.</summary>
    [HttpPost("evaluate-candidate")]
    public async Task<IActionResult> Evaluate([FromBody] EvaluateCandidateRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.PaperCandidateId))
            return BadRequest(new { error = "paperCandidateId is required" });

        var outcome = await _service.EvaluateCandidateAsync(req.PaperCandidateId);
        if (outcome is null) return NotFound(new { error = "Paper candidate not found" });

        return Ok(outcome);
    }

    /// <summary>POST /api/paper-options/evaluate-open-candidates — evaluate every open candidate.</summary>
    [HttpPost("evaluate-open-candidates")]
    public async Task<IActionResult> EvaluateAllOpen()
    {
        var outcomes = await _service.EvaluateAllOpenAsync();
        return Ok(new { count = outcomes.Count, outcomes });
    }

    /// <summary>GET /api/paper-options/outcomes — recent evaluated outcomes.</summary>
    [HttpGet("outcomes")]
    public async Task<IActionResult> Outcomes([FromQuery] int limit = 100)
    {
        var outcomes = await _service.GetOutcomesAsync(limit);
        return Ok(new { count = outcomes.Count, outcomes });
    }

    /// <summary>GET /api/paper-options/debug — counts, learning stats, provider config.</summary>
    [HttpGet("debug")]
    public async Task<IActionResult> Debug()
    {
        return Ok(await _service.GetDebugAsync());
    }
}
