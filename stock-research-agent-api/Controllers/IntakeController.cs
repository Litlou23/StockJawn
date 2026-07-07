using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Services.ResearchEngine;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// RSS/news intake analysis endpoints. Replaces the Next.js
/// /api/jobs/analyze-learning route for intake-specific functionality.
/// </summary>
[ApiController]
[Route("api/intake")]
public class IntakeController : ControllerBase
{
    private readonly IntakeAnalysisService _intake;

    public IntakeController(IntakeAnalysisService intake)
    {
        _intake = intake;
    }

    /// <summary>
    /// Full intake analysis: RSS scan + analytics + auto-picks + AI briefing.
    /// </summary>
    [HttpPost("analyze")]
    public async Task<IActionResult> RunAnalysis()
    {
        var result = await _intake.RunIntakeAnalysisAsync();
        return Ok(result);
    }

    /// <summary>
    /// GET version for dashboard polling.
    /// </summary>
    [HttpGet("latest")]
    public async Task<IActionResult> GetLatestAnalysis()
    {
        var result = await _intake.RunIntakeAnalysisAsync();
        return Ok(result);
    }
}
