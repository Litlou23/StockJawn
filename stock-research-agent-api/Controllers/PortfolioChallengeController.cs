using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Portfolio;
using StockResearchAgent.Api.Services.ResearchEngine;
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
    // ── Dashboard cache: serves cached data, refreshes ~4× during trading day ──
    // Internal so DashboardWarmupService can pre-warm on startup.
    internal static readonly ConcurrentDictionary<string, (PortfolioDashboard Data, DateTimeOffset FetchedAt)> DashboardCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(90);

    private readonly PortfolioBalanceEngine _engine;
    private readonly PortfolioChallengeRepository _repo;
    private readonly PortfolioLifecycleService _lifecycle;
    private readonly OutcomeEvaluator _outcomeEvaluator;
    private readonly MarketDataService _marketData;
    private readonly ILogger<PortfolioChallengeController> _logger;

    public PortfolioChallengeController(
        PortfolioBalanceEngine engine,
        PortfolioChallengeRepository repo,
        PortfolioLifecycleService lifecycle,
        OutcomeEvaluator outcomeEvaluator,
        MarketDataService marketData,
        ILogger<PortfolioChallengeController> logger)
    {
        _engine = engine;
        _repo = repo;
        _lifecycle = lifecycle;
        _outcomeEvaluator = outcomeEvaluator;
        _marketData = marketData;
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
    // Enriched dashboard — live P&L, equity curve, AI quality stats
    // -----------------------------------------------------------------------

    /// <summary>
    /// Full dashboard with live positions (current prices + unrealized P&L),
    /// equity curve from trade history, and aggregate AI quality stats.
    /// Serves from cache (90-min TTL) for fast page loads.
    /// </summary>
    [HttpGet("dashboard/{id?}")]
    public async Task<IActionResult> GetDashboard(string? id = null)
    {
        var summary = await _engine.GetSummaryAsync(id);
        if (summary is null)
            return NotFound(new { error = "No active portfolio challenge found" });

        var cacheKey = summary.ChallengeId;

        // Serve from cache if fresh
        if (DashboardCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < CacheTtl)
        {
            _logger.LogInformation("[dashboard] Serving cached data for {Id} (age: {Age:F0}s)",
                cacheKey, (DateTimeOffset.UtcNow - cached.FetchedAt).TotalSeconds);
            return Ok(cached.Data);
        }

        // Cache miss or stale — rebuild
        var dashboard = await BuildDashboardAsync(summary);
        if (dashboard is null)
            return NotFound(new { error = "Challenge not found" });

        DashboardCache[cacheKey] = (dashboard, DateTimeOffset.UtcNow);
        return Ok(dashboard);
    }

    /// <summary>
    /// Force-refresh the dashboard cache for all active challenges.
    /// Also runs risk management checks (stop-loss, take-profit, trailing stop).
    /// Called by a scheduled cron ~4× during trading hours.
    /// </summary>
    [HttpPost("dashboard/refresh")]
    public async Task<IActionResult> RefreshDashboardCache()
    {
        // ── Run risk management checks first (may close positions) ──
        RiskCheckResult? riskResult = null;
        try
        {
            riskResult = await _lifecycle.EvaluateRiskLimitsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[dashboard-refresh] Portfolio risk check failed");
        }

        // ── Run prediction pool risk checks (stop/target/invalidation on all open predictions) ──
        OutcomeEvaluator.PredictionRiskCheckResult? predictionRiskResult = null;
        try
        {
            predictionRiskResult = await _outcomeEvaluator.EvaluatePredictionRiskLimitsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[dashboard-refresh] Prediction risk check failed");
        }

        // ── Then refresh cached dashboard data ──
        var challenges = await _repo.GetAllChallengesAsync();
        var active = challenges.Where(c => c.Status == ChallengeStatus.active).ToList();
        var refreshed = 0;

        foreach (var challenge in active)
        {
            try
            {
                var summary = await _engine.GetSummaryAsync(challenge.Id);
                if (summary is null) continue;

                var dashboard = await BuildDashboardAsync(summary);
                if (dashboard is null) continue;

                DashboardCache[challenge.Id] = (dashboard, DateTimeOffset.UtcNow);
                refreshed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[dashboard-refresh] Failed for challenge {Id}", challenge.Id);
            }
        }

        _logger.LogInformation("[dashboard-refresh] Refreshed {Count}/{Total} active challenges", refreshed, active.Count);
        return Ok(new
        {
            refreshed,
            total = active.Count,
            riskCheck = riskResult is not null ? new
            {
                riskResult.PositionsChecked,
                riskResult.StopLossClosed,
                riskResult.TakeProfitClosed,
                riskResult.TrailingStopClosed,
                riskResult.HighWaterMarksUpdated,
                riskResult.TotalClosed,
            } : null,
            predictionRiskCheck = predictionRiskResult is not null ? new
            {
                predictionRiskResult.PredictionsChecked,
                predictionRiskResult.StopLossEvaluated,
                predictionRiskResult.TargetHitEvaluated,
                predictionRiskResult.InvalidationEvaluated,
                predictionRiskResult.TotalEarlyEvaluated,
                predictionRiskResult.QuotesFailed,
            } : null,
        });
    }

    /// <summary>
    /// Builds a full dashboard snapshot: parallel quote fetch, equity curve, stats.
    /// </summary>
    private async Task<PortfolioDashboard?> BuildDashboardAsync(PortfolioChallengeSummary summary)
    {
        var challenge = await _repo.GetChallengeAsync(summary.ChallengeId);
        if (challenge is null) return null;

        // ── Fetch quotes for all unique tickers in parallel (capped at 8) ──
        var openPositions = await _repo.GetOpenPositionsAsync(challenge.Id);
        var uniqueTickers = openPositions.Select(p => p.Ticker).Distinct().ToList();

        var quoteMap = new Dictionary<string, double>();
        using var semaphore = new SemaphoreSlim(8);
        var quoteTasks = uniqueTickers.Select(async ticker =>
        {
            await semaphore.WaitAsync();
            try
            {
                var quote = await _marketData.GetQuoteAsync(ticker);
                lock (quoteMap) { quoteMap[ticker] = quote?.Price ?? 0; }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[dashboard] Failed to fetch quote for {Ticker}", ticker);
                lock (quoteMap) { quoteMap[ticker] = 0; }
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(quoteTasks);

        var enrichedPositions = new List<EnrichedPosition>();
        var totalUnrealizedPnL = 0.0;

        foreach (var pos in openPositions)
        {
            var currentPrice = quoteMap.GetValueOrDefault(pos.Ticker, 0);
            if (currentPrice <= 0) currentPrice = pos.EntryPrice; // fallback
            var multiplier = pos.AssetType == PositionAssetType.option ? 100.0 : 1.0;
            var currentValue = Math.Round(currentPrice * pos.Quantity * multiplier, 2);
            var unrealizedPnL = Math.Round(currentValue - pos.DollarsInvested, 2);
            var unrealizedPct = pos.DollarsInvested > 0
                ? Math.Round(unrealizedPnL / pos.DollarsInvested * 100, 2) : 0;
            totalUnrealizedPnL += unrealizedPnL;

            enrichedPositions.Add(new EnrichedPosition
            {
                Id = pos.Id,
                Ticker = pos.Ticker,
                AssetType = pos.AssetType,
                EntryPrice = pos.EntryPrice,
                CurrentPrice = currentPrice,
                Quantity = pos.Quantity,
                DollarsInvested = pos.DollarsInvested,
                CurrentValue = currentValue,
                UnrealizedPnL = unrealizedPnL,
                UnrealizedPnLPercent = unrealizedPct,
                PredictionId = pos.PredictionId,
                ReasonEntered = pos.ReasonEntered,
                HoursHeld = Math.Round((DateTimeOffset.UtcNow - pos.EntryDate).TotalHours, 1),
                EntryDate = pos.EntryDate,
            });
        }

        // ── Build equity curve from closed trades ──
        var allClosed = await _repo.GetClosedPositionsAsync(challenge.Id, limit: 200);
        var sortedClosed = allClosed.OrderBy(p => p.ExitDate ?? p.CreatedAt).ToList();

        var equityCurve = new List<EquityPoint>
        {
            new() { Date = challenge.CreatedAt, Balance = challenge.StartingBalance, TradeLabel = "Start" },
        };

        var runningBalance = challenge.StartingBalance;
        foreach (var trade in sortedClosed)
        {
            runningBalance += trade.ProfitLoss ?? 0;
            equityCurve.Add(new EquityPoint
            {
                Date = trade.ExitDate ?? trade.CreatedAt,
                Balance = Math.Round(runningBalance, 2),
                TradeLabel = $"{trade.Ticker} {(trade.ProfitLoss >= 0 ? "+" : "")}{trade.ProfitLoss:F2}",
            });
        }

        var liveEquity = Math.Round(challenge.CurrentCash + enrichedPositions.Sum(p => p.CurrentValue), 2);
        equityCurve.Add(new EquityPoint
        {
            Date = DateTimeOffset.UtcNow,
            Balance = liveEquity,
            TradeLabel = "Now",
        });

        // ── Compute AI quality stats from closed trades ──
        var winners = sortedClosed.Where(t => t.ProfitLoss > 0).ToList();
        var losers = sortedClosed.Where(t => t.ProfitLoss <= 0).ToList();
        var bestTrade = sortedClosed.MaxBy(t => t.ProfitLoss ?? 0);
        var worstTrade = sortedClosed.MinBy(t => t.ProfitLoss ?? 0);
        var totalGross = winners.Sum(w => w.ProfitLoss ?? 0);
        var totalLoss = Math.Abs(losers.Sum(l => l.ProfitLoss ?? 0));

        var stats = new PortfolioQualityStats
        {
            TotalTrades = sortedClosed.Count,
            Winners = winners.Count,
            Losers = losers.Count,
            WinRate = sortedClosed.Count > 0 ? Math.Round((double)winners.Count / sortedClosed.Count * 100, 1) : 0,
            AvgWinPercent = winners.Count > 0 ? Math.Round(winners.Average(w => w.PercentGain ?? 0), 2) : 0,
            AvgLossPercent = losers.Count > 0 ? Math.Round(losers.Average(l => l.PercentGain ?? 0), 2) : 0,
            AvgWinDollars = winners.Count > 0 ? Math.Round(winners.Average(w => w.ProfitLoss ?? 0), 2) : 0,
            AvgLossDollars = losers.Count > 0 ? Math.Round(losers.Average(l => l.ProfitLoss ?? 0), 2) : 0,
            LargestWinDollars = bestTrade?.ProfitLoss ?? 0,
            LargestLossDollars = worstTrade?.ProfitLoss ?? 0,
            LargestWinTicker = bestTrade?.Ticker,
            LargestLossTicker = worstTrade?.Ticker,
            TotalRealizedPnL = Math.Round(challenge.RealizedProfit, 2),
            ProfitFactor = totalLoss > 0 ? Math.Round(totalGross / totalLoss, 2) : totalGross > 0 ? 999 : 0,
            AvgHoldHours = sortedClosed.Count > 0
                ? Math.Round(sortedClosed.Where(t => t.ExitDate.HasValue).Average(t => (t.ExitDate!.Value - t.EntryDate).TotalHours), 1)
                : 0,
        };

        return new PortfolioDashboard
        {
            Summary = summary,
            LivePositions = enrichedPositions,
            RecentClosedTrades = sortedClosed.OrderByDescending(t => t.ExitDate).Take(20).ToList(),
            EquityCurve = equityCurve,
            Stats = stats,
            TotalUnrealizedPnL = Math.Round(totalUnrealizedPnL, 2),
            LiveEquity = liveEquity,
        };
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
