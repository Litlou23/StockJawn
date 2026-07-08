using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Portfolio;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// Portfolio Challenge endpoints.
///
///   GET  /api/portfolio/summary             — dashboard summary for the active challenge
///   GET  /api/portfolio/summary/{id}        — dashboard summary for a specific challenge
///   GET  /api/portfolio/challenges           — list all challenges
///   POST /api/portfolio/challenges           — create a new challenge
///   GET  /api/portfolio/positions/open       — open positions for active challenge
///   GET  /api/portfolio/positions/closed     — closed positions for active challenge
///   POST /api/portfolio/positions/open       — open a new position
///   POST /api/portfolio/positions/close      — close an existing position
///
/// These endpoints are for the Portfolio AI layer — separate from the
/// Prediction Engine. The Prediction Engine finds opportunities;
/// Portfolio AI decides whether and how much to invest.
/// </summary>
[ApiController]
[Route("api/portfolio")]
public class PortfolioChallengeController : ControllerBase
{
    private readonly PortfolioBalanceEngine _engine;
    private readonly PortfolioChallengeRepository _repo;
    private readonly ILogger<PortfolioChallengeController> _logger;

    public PortfolioChallengeController(
        PortfolioBalanceEngine engine,
        PortfolioChallengeRepository repo,
        ILogger<PortfolioChallengeController> logger)
    {
        _engine = engine;
        _repo = repo;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Dashboard summary
    // -----------------------------------------------------------------------

    /// <summary>
    /// Summary for the active portfolio challenge. This is the primary
    /// endpoint for future UI dashboard work.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        var summary = await _engine.GetSummaryAsync();
        if (summary is null)
            return NotFound(new { error = "No active portfolio challenge found" });
        return Ok(summary);
    }

    /// <summary>
    /// Summary for a specific challenge by ID.
    /// </summary>
    [HttpGet("summary/{id}")]
    public async Task<IActionResult> GetSummaryById(string id)
    {
        var summary = await _engine.GetSummaryAsync(id);
        if (summary is null)
            return NotFound(new { error = "Portfolio challenge not found", id });
        return Ok(summary);
    }

    // -----------------------------------------------------------------------
    // Challenge management
    // -----------------------------------------------------------------------

    [HttpGet("challenges")]
    public async Task<IActionResult> GetChallenges()
    {
        var challenges = await _repo.GetAllChallengesAsync();
        return Ok(challenges);
    }

    [HttpPost("challenges")]
    public async Task<IActionResult> CreateChallenge([FromBody] PortfolioChallenge request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required" });
        if (request.StartingBalance <= 0)
            return BadRequest(new { error = "Starting balance must be positive" });
        if (request.TargetBalance <= request.StartingBalance)
            return BadRequest(new { error = "Target balance must exceed starting balance" });

        var created = await _repo.CreateChallengeAsync(request);
        if (created is null)
            return StatusCode(500, new { error = "Failed to create challenge" });
        return Created($"/api/portfolio/summary/{created.Id}", created);
    }

    [HttpPatch("challenges/{id}/status")]
    public async Task<IActionResult> UpdateChallengeStatus(string id, [FromBody] UpdateStatusRequest request)
    {
        if (!Enum.TryParse<ChallengeStatus>(request.Status, out var status))
            return BadRequest(new { error = "Invalid status. Valid: active, completed, paused, abandoned" });

        var ok = await _repo.UpdateChallengeStatusAsync(id, status);
        if (!ok)
            return NotFound(new { error = "Challenge not found or update failed", id });
        return Ok(new { id, status = status.ToString() });
    }

    [HttpPatch("challenges/{id}/settings")]
    public async Task<IActionResult> UpdateChallengeSettings(string id, [FromBody] UpdateSettingsRequest request)
    {
        RiskProfile? riskProfile = null;
        PortfolioMode? portfolioMode = null;

        if (request.RiskProfile is not null)
        {
            if (!Enum.TryParse<RiskProfile>(request.RiskProfile, out var rp))
                return BadRequest(new { error = "Invalid risk profile. Valid: conservative, moderate, aggressive" });
            riskProfile = rp;
        }

        if (request.PortfolioMode is not null)
        {
            if (!Enum.TryParse<PortfolioMode>(request.PortfolioMode, out var pm))
                return BadRequest(new { error = "Invalid portfolio mode. Valid: swing_trading, day_trading, options_only, stock_only, mixed" });
            portfolioMode = pm;
        }

        var ok = await _repo.UpdateChallengeSettingsAsync(id, riskProfile, portfolioMode, request.Notes);
        if (!ok)
            return NotFound(new { error = "Challenge not found or update failed", id });

        var updated = await _repo.GetChallengeAsync(id);
        return Ok(updated);
    }

    // -----------------------------------------------------------------------
    // Decision log — full audit trail
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the full decision audit trail for every trade: entry reasoning,
    /// exit reasoning, candidate scores, and prediction outcomes.
    /// Backed by the portfolio_decision_log view.
    /// </summary>
    [HttpGet("decision-log")]
    public async Task<IActionResult> GetDecisionLog(
        [FromQuery] string? challengeId = null,
        [FromQuery] int limit = 50)
    {
        var challenge = challengeId is not null
            ? await _repo.GetChallengeAsync(challengeId)
            : await _repo.GetActiveChallengeAsync();

        if (challenge is null)
            return NotFound(new { error = "No active portfolio challenge found" });

        var rows = await _repo.GetDecisionLogAsync(challenge.Id, limit);
        return Ok(rows);
    }

    // -----------------------------------------------------------------------
    // Position management
    // -----------------------------------------------------------------------

    [HttpGet("positions/open")]
    public async Task<IActionResult> GetOpenPositions([FromQuery] string? challengeId = null)
    {
        var challenge = challengeId is not null
            ? await _repo.GetChallengeAsync(challengeId)
            : await _repo.GetActiveChallengeAsync();

        if (challenge is null)
            return NotFound(new { error = "No active portfolio challenge found" });

        var positions = await _repo.GetOpenPositionsAsync(challenge.Id);
        return Ok(positions);
    }

    [HttpGet("positions/closed")]
    public async Task<IActionResult> GetClosedPositions(
        [FromQuery] string? challengeId = null,
        [FromQuery] int limit = 50)
    {
        var challenge = challengeId is not null
            ? await _repo.GetChallengeAsync(challengeId)
            : await _repo.GetActiveChallengeAsync();

        if (challenge is null)
            return NotFound(new { error = "No active portfolio challenge found" });

        var positions = await _repo.GetClosedPositionsAsync(challenge.Id, limit);
        return Ok(positions);
    }

    [HttpPost("positions/open")]
    public async Task<IActionResult> OpenPosition([FromBody] OpenPositionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Ticker))
            return BadRequest(new { error = "Ticker is required" });
        if (request.EntryPrice <= 0)
            return BadRequest(new { error = "Entry price must be positive" });
        if (request.Quantity <= 0)
            return BadRequest(new { error = "Quantity must be positive" });

        // If no portfolio ID specified, use the active challenge
        var portfolioId = request.PortfolioId;
        if (string.IsNullOrWhiteSpace(portfolioId))
        {
            var active = await _repo.GetActiveChallengeAsync();
            if (active is null)
                return NotFound(new { error = "No active portfolio challenge found" });
            portfolioId = active.Id;
        }

        var adjusted = request with { PortfolioId = portfolioId };
        var position = await _engine.OpenPositionAsync(adjusted);

        if (position is null)
            return BadRequest(new { error = "Failed to open position. Check cash availability and challenge status." });

        return Created($"/api/portfolio/positions/{position.Id}", position);
    }

    [HttpPost("positions/close")]
    public async Task<IActionResult> ClosePosition([FromBody] ClosePositionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PositionId))
            return BadRequest(new { error = "PositionId is required" });
        if (request.ExitPrice <= 0)
            return BadRequest(new { error = "Exit price must be positive" });

        var position = await _engine.ClosePositionAsync(request);
        if (position is null)
            return BadRequest(new { error = "Failed to close position. Check position exists and is open." });

        return Ok(position);
    }
}

public record UpdateStatusRequest
{
    public string Status { get; init; } = "";
}

public record UpdateSettingsRequest
{
    public string? RiskProfile { get; init; }
    public string? PortfolioMode { get; init; }
    public string? Notes { get; init; }
}
