using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.Broker;
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
    private readonly IBrokerAdapter _broker;
    private readonly BrokerSyncService _brokerSync;
    private readonly JobStatusTracker _jobStatus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PortfolioChallengeController> _logger;

    public PortfolioChallengeController(
        PortfolioBalanceEngine engine,
        PortfolioChallengeRepository repo,
        PortfolioLifecycleService lifecycle,
        OutcomeEvaluator outcomeEvaluator,
        MarketDataService marketData,
        IBrokerAdapter broker,
        BrokerSyncService brokerSync,
        JobStatusTracker jobStatus,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PortfolioChallengeController> logger)
    {
        _engine = engine;
        _repo = repo;
        _lifecycle = lifecycle;
        _outcomeEvaluator = outcomeEvaluator;
        _marketData = marketData;
        _broker = broker;
        _brokerSync = brokerSync;
        _jobStatus = jobStatus;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
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
    public async Task<IActionResult> CreateChallenge(
        [FromBody] PortfolioChallenge request,
        [FromServices] IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required" });
        if (request.StartingBalance <= 0)
            return BadRequest(new { error = "Starting balance must be positive" });
        if (request.TargetBalance <= request.StartingBalance)
            return BadRequest(new { error = "Target balance must exceed starting balance" });

        // ── Live trading safeguard ──────────────────────────────────
        // Require explicit opt-in via ENABLE_LIVE_TRADING=true config.
        // Without this, live mode cannot be activated even accidentally.
        if (request.TradingMode == TradingMode.live)
        {
            var liveTradingEnabled = config["ENABLE_LIVE_TRADING"]?.ToLowerInvariant() == "true";
            if (!liveTradingEnabled)
                return BadRequest(new { error = "Live trading is not enabled. Set ENABLE_LIVE_TRADING=true in configuration to allow real-money trading." });

            _logger.LogWarning("[portfolio] LIVE TRADING challenge created: {Name}, starting=${Balance:F2}",
                request.Name, request.StartingBalance);
        }

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
    /// Fire-and-forget: returns 202 immediately, runs in background to avoid
    /// Edge Function 150s timeout (116+ unique ticker quotes to fetch).
    /// </summary>
    [HttpPost("dashboard/refresh")]
    public IActionResult RefreshDashboardCache()
    {
        const string jobName = "portfolio-dashboard-refresh";
        _logger.LogInformation("[dashboard-refresh] Triggered — running in background");
        _jobStatus.MarkStarted(jobName);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var lifecycle = scope.ServiceProvider.GetRequiredService<PortfolioLifecycleService>();
                var outcomeEval = scope.ServiceProvider.GetRequiredService<OutcomeEvaluator>();
                var engine = scope.ServiceProvider.GetRequiredService<PortfolioBalanceEngine>();
                var repo = scope.ServiceProvider.GetRequiredService<PortfolioChallengeRepository>();
                var marketData = scope.ServiceProvider.GetRequiredService<MarketDataService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<PortfolioChallengeController>>();

                // ── Run risk management checks first (may close positions) ──
                RiskCheckResult? riskResult = null;
                try { riskResult = await lifecycle.EvaluateRiskLimitsAsync(); }
                catch (Exception ex) { logger.LogError(ex, "[dashboard-refresh] Portfolio risk check failed"); }

                // ── Run prediction pool risk checks ──
                OutcomeEvaluator.PredictionRiskCheckResult? predictionRiskResult = null;
                try { predictionRiskResult = await outcomeEval.EvaluatePredictionRiskLimitsAsync(); }
                catch (Exception ex) { logger.LogError(ex, "[dashboard-refresh] Prediction risk check failed"); }

                // ── Intraday reopen: redeploy capital after scalp closes ──
                int intradayReopened = 0;
                if (riskResult is not null && riskResult.TotalClosed > 0)
                {
                    try { intradayReopened = await lifecycle.ReopenAfterScalpCloseAsync(riskResult.TotalClosed, riskResult.ClosedTickers); }
                    catch (Exception ex) { logger.LogError(ex, "[dashboard-refresh] Intraday reopen failed"); }
                }

                // ── Then refresh cached dashboard data ──
                var challenges = await repo.GetAllChallengesAsync();
                var active = challenges.Where(c => c.Status == ChallengeStatus.active).ToList();
                var refreshed = 0;

                foreach (var challenge in active)
                {
                    try
                    {
                        var summary = await engine.GetSummaryAsync(challenge.Id);
                        if (summary is null) continue;
                        var dashboard = await BuildDashboardCoreAsync(engine, repo, marketData, logger, summary);
                        if (dashboard is null) continue;
                        DashboardCache[challenge.Id] = (dashboard, DateTimeOffset.UtcNow);
                        refreshed++;
                    }
                    catch (Exception ex) { logger.LogWarning(ex, "[dashboard-refresh] Failed for challenge {Id}", challenge.Id); }
                }

                var msg = $"Refreshed {refreshed}/{active.Count} challenges. " +
                    $"Risk: {riskResult?.TotalClosed ?? 0} closed, {riskResult?.PartialProfitsTaken ?? 0} partials. " +
                    $"Predictions: {predictionRiskResult?.TotalEarlyEvaluated ?? 0} early-eval. " +
                    $"Reopened: {intradayReopened}.";
                logger.LogInformation("[dashboard-refresh] {Summary}", msg);
                _jobStatus.MarkCompleted(jobName, msg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[dashboard-refresh] Background job failed");
                _jobStatus.MarkFailed(jobName, ex.Message);
            }
        });

        return Accepted(new
        {
            status = "started",
            jobName,
            message = "Dashboard refresh is running in the background. Poll /api/jobs/status for progress.",
            startedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Lightweight intraday risk check — runs stop-loss, take-profit, and trailing stop
    /// checks on all open portfolio positions and prediction pool.
    /// No dashboard rebuild, no snapshot capture — just risk management.
    /// Designed to be called every 30 minutes during market hours via pg_cron.
    /// Fire-and-forget: returns 202 immediately to avoid Edge Function timeout.
    /// </summary>
    [HttpPost("intraday-risk-check")]
    public IActionResult IntradayRiskCheck()
    {
        const string jobName = "intraday-risk-check";
        _logger.LogInformation("[intraday-risk] Triggered — running in background");
        _jobStatus.MarkStarted(jobName);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var lifecycle = scope.ServiceProvider.GetRequiredService<PortfolioLifecycleService>();
                var outcomeEval = scope.ServiceProvider.GetRequiredService<OutcomeEvaluator>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<PortfolioChallengeController>>();

                // ── Portfolio position risk checks ──
                RiskCheckResult? riskResult = null;
                try { riskResult = await lifecycle.EvaluateRiskLimitsAsync(); }
                catch (Exception ex) { logger.LogError(ex, "[intraday-risk] Portfolio risk check failed"); }

                // ── Prediction pool risk checks ──
                OutcomeEvaluator.PredictionRiskCheckResult? predictionRiskResult = null;
                try { predictionRiskResult = await outcomeEval.EvaluatePredictionRiskLimitsAsync(); }
                catch (Exception ex) { logger.LogError(ex, "[intraday-risk] Prediction risk check failed"); }

                var totalClosed = (riskResult?.TotalClosed ?? 0) + (predictionRiskResult?.TotalEarlyEvaluated ?? 0);

                // ── Intraday reopen: redeploy freed capital immediately ──
                int intradayReopened = 0;
                if (riskResult is not null && riskResult.TotalClosed > 0)
                {
                    try { intradayReopened = await lifecycle.ReopenAfterScalpCloseAsync(riskResult.TotalClosed, riskResult.ClosedTickers); }
                    catch (Exception ex) { logger.LogError(ex, "[intraday-risk] Intraday reopen failed"); }
                }

                var msg = $"Checked {riskResult?.PositionsChecked ?? 0} positions, " +
                    $"{predictionRiskResult?.PredictionsChecked ?? 0} predictions. " +
                    $"Closed: {totalClosed}. Reopened: {intradayReopened}.";

                if (totalClosed > 0)
                    logger.LogWarning("[intraday-risk] {Summary}", msg);
                else
                    logger.LogInformation("[intraday-risk] {Summary}", msg);

                _jobStatus.MarkCompleted(jobName, msg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[intraday-risk] Background job failed");
                _jobStatus.MarkFailed(jobName, ex.Message);
            }
        });

        return Accepted(new
        {
            status = "started",
            jobName,
            message = "Intraday risk check is running in the background. Poll /api/jobs/status for progress.",
            startedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Afternoon opportunity scan — second pass at today's open candidates.
    /// Picks up positions that were deferred by the morning open gate (9:30-10:00 AM),
    /// or that couldn't be opened because slots were full.
    /// Designed to run once in the afternoon via pg_cron (~2 PM ET / 18:00 UTC).
    /// Fire-and-forget: returns 202 immediately to avoid Edge Function timeout.
    /// </summary>
    [HttpPost("afternoon-scan")]
    public IActionResult AfternoonOpportunityScan()
    {
        const string jobName = "afternoon-opportunity-scan";
        _logger.LogInformation("[afternoon-scan] Triggered — running in background");
        _jobStatus.MarkStarted(jobName);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var lifecycle = scope.ServiceProvider.GetRequiredService<PortfolioLifecycleService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<PortfolioChallengeController>>();

                int opened = 0;
                try { opened = await lifecycle.AfternoonOpportunityScanAsync(); }
                catch (Exception ex) { logger.LogError(ex, "[afternoon-scan] Scan failed"); }

                // Also run risk management while we're here
                RiskCheckResult? riskResult = null;
                try { riskResult = await lifecycle.EvaluateRiskLimitsAsync(); }
                catch (Exception ex) { logger.LogError(ex, "[afternoon-scan] Risk check failed"); }

                var msg = $"Opened {opened} positions. Risk: {riskResult?.TotalClosed ?? 0} closed, " +
                    $"{riskResult?.PartialProfitsTaken ?? 0} partials.";
                logger.LogInformation("[afternoon-scan] {Summary}", msg);
                _jobStatus.MarkCompleted(jobName, msg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[afternoon-scan] Background job failed");
                _jobStatus.MarkFailed(jobName, ex.Message);
            }
        });

        return Accepted(new
        {
            status = "started",
            jobName,
            message = "Afternoon scan is running in the background. Poll /api/jobs/status for progress.",
            startedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Builds a full dashboard snapshot: parallel quote fetch, equity curve, stats.
    /// Instance wrapper for request-scoped calls (GET /dashboard).
    /// </summary>
    private async Task<PortfolioDashboard?> BuildDashboardAsync(PortfolioChallengeSummary summary)
        => await BuildDashboardCoreAsync(_engine, _repo, _marketData, _logger, summary);

    /// <summary>
    /// Static core: can be called from background tasks with a fresh DI scope.
    /// </summary>
    private static async Task<PortfolioDashboard?> BuildDashboardCoreAsync(
        PortfolioBalanceEngine engine,
        PortfolioChallengeRepository repo,
        MarketDataService marketData,
        ILogger logger,
        PortfolioChallengeSummary summary)
    {
        var challenge = await repo.GetChallengeAsync(summary.ChallengeId);
        if (challenge is null) return null;

        // ── Fetch quotes for all unique tickers in parallel (capped at 8) ──
        var openPositions = await repo.GetOpenPositionsAsync(challenge.Id);
        var uniqueTickers = openPositions.Select(p => p.Ticker).Distinct().ToList();

        var quoteMap = new Dictionary<string, double>();
        using var semaphore = new SemaphoreSlim(8);
        var quoteTasks = uniqueTickers.Select(async ticker =>
        {
            await semaphore.WaitAsync();
            try
            {
                var quote = await marketData.GetQuoteAsync(ticker);
                lock (quoteMap) { quoteMap[ticker] = quote?.Price ?? 0; }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[dashboard] Failed to fetch quote for {Ticker}", ticker);
                lock (quoteMap) { quoteMap[ticker] = 0; }
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(quoteTasks);

        var enrichedPositions = new List<EnrichedPosition>();
        var totalUnrealizedPnL = 0.0;

        foreach (var pos in openPositions)
        {
            double currentValue;
            double displayPrice;
            if (pos.AssetType == PositionAssetType.option)
            {
                // Options: quoteMap returns the STOCK price, not the option premium.
                // Without live option quotes, value the position at cost (DollarsInvested)
                // to avoid inflating the portfolio by 100× the stock price.
                currentValue = pos.DollarsInvested;
                displayPrice = pos.EntryPrice; // show entry premium as current price
            }
            else
            {
                var currentPrice = quoteMap.GetValueOrDefault(pos.Ticker, 0);
                if (currentPrice <= 0) currentPrice = pos.EntryPrice; // fallback
                currentValue = Math.Round(currentPrice * pos.Quantity, 2);
                displayPrice = currentPrice;
            }
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
                CurrentPrice = displayPrice,
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
        var allClosed = await repo.GetClosedPositionsAsync(challenge.Id, limit: 200);
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

        // ── Persist daily snapshot (upsert — one per day per challenge) ──
        try
        {
            var investedValue = Math.Round(enrichedPositions.Sum(p => p.CurrentValue), 2);
            var snapshot = new PortfolioSnapshot
            {
                ChallengeId = challenge.Id,
                SnapshotDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Cash = Math.Round(challenge.CurrentCash, 2),
                InvestedValue = investedValue,
                UnrealizedPnl = Math.Round(totalUnrealizedPnL, 2),
                TotalEquity = liveEquity,
                OpenPositionCount = enrichedPositions.Count,
                RealizedPnlCumulative = Math.Round(challenge.RealizedProfit, 2),
            };
            await repo.UpsertSnapshotAsync(snapshot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[dashboard] Failed to save portfolio snapshot for {Id}", challenge.Id);
        }

        // ── Build equity curve from snapshots + trade events ──
        var snapshots = await repo.GetSnapshotsAsync(challenge.Id);
        if (snapshots.Count > 0)
        {
            // Use daily snapshots as the primary curve (smooth, daily resolution)
            equityCurve.Clear();

            // Prepend a "Start" point at challenge creation if first snapshot is later
            var firstSnapDate = snapshots[0].SnapshotDate;
            var challengeStartDate = DateOnly.FromDateTime(challenge.CreatedAt.UtcDateTime);
            if (firstSnapDate >= challengeStartDate)
            {
                equityCurve.Add(new EquityPoint
                {
                    Date = challenge.CreatedAt,
                    Balance = challenge.StartingBalance,
                    TradeLabel = "Start",
                });
            }

            foreach (var snap in snapshots)
            {
                equityCurve.Add(new EquityPoint
                {
                    Date = new DateTimeOffset(snap.SnapshotDate.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromHours(21))), TimeSpan.Zero),
                    Balance = snap.TotalEquity,
                    TradeLabel = snap.OpenPositionCount > 0
                        ? $"{snap.OpenPositionCount} positions, unrealized {(snap.UnrealizedPnl >= 0 ? "+" : "")}{snap.UnrealizedPnl:F2}"
                        : null,
                });
            }

            // Add "Now" point if latest snapshot is today (update with live data)
            var lastSnap = snapshots[^1];
            if (lastSnap.SnapshotDate == DateOnly.FromDateTime(DateTime.UtcNow))
            {
                // Replace today's snapshot point with live equity
                equityCurve[^1] = new EquityPoint
                {
                    Date = DateTimeOffset.UtcNow,
                    Balance = liveEquity,
                    TradeLabel = "Now",
                };
            }
            else
            {
                equityCurve.Add(new EquityPoint
                {
                    Date = DateTimeOffset.UtcNow,
                    Balance = liveEquity,
                    TradeLabel = "Now",
                });
            }
        }
        else
        {
            // No snapshots yet — fall back to trade-based curve
            equityCurve.Add(new EquityPoint
            {
                Date = DateTimeOffset.UtcNow,
                Balance = liveEquity,
                TradeLabel = "Now",
            });
        }

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

    // -----------------------------------------------------------------------
    // Broker integration endpoints
    // -----------------------------------------------------------------------

    /// <summary>
    /// Get broker connection status and account info.
    /// </summary>
    [HttpGet("broker/status")]
    public async Task<IActionResult> GetBrokerStatus()
    {
        if (!_broker.IsConfigured)
            return Ok(new
            {
                configured = false,
                message = "Broker not configured. Set ALPACA_API_KEY and ALPACA_API_SECRET.",
            });

        var account = await _broker.GetAccountAsync();
        return Ok(new
        {
            configured = true,
            isPaper = _broker.IsPaperTrading,
            account,
        });
    }

    /// <summary>
    /// Get all positions currently held at the broker.
    /// </summary>
    [HttpGet("broker/positions")]
    public async Task<IActionResult> GetBrokerPositions()
    {
        if (!_broker.IsConfigured)
            return BadRequest(new { error = "Broker not configured" });

        var positions = await _broker.GetPositionsAsync();
        return Ok(positions);
    }

    /// <summary>
    /// Get all open orders at the broker.
    /// </summary>
    [HttpGet("broker/orders")]
    public async Task<IActionResult> GetBrokerOrders()
    {
        if (!_broker.IsConfigured)
            return BadRequest(new { error = "Broker not configured" });

        var orders = await _broker.GetOpenOrdersAsync();
        return Ok(orders);
    }

    /// <summary>
    /// Run a broker sync — reconcile broker state with Supabase records.
    /// </summary>
    [HttpPost("broker/sync")]
    public async Task<IActionResult> RunBrokerSync()
    {
        var result = await _brokerSync.SyncAsync();
        return Ok(result);
    }

    /// <summary>
    /// Manual override: force today's champion predictions through the broker
    /// trading pipeline. Use when the automated morning scan failed to produce
    /// candidates (e.g. DB constraint error, cron miss, etc.).
    ///
    /// 1. Fetches today's champion predictions
    /// 2. Builds paper_stock_candidates for any that don't already have one
    /// 3. Saves them to DB
    /// 4. Runs OpenPositionsForCandidatesAsync with bypassTimeGate=true
    ///
    /// Fire-and-forget: returns 202 immediately.
    /// </summary>
    [HttpPost("force-trade")]
    public IActionResult ForceTrade()
    {
        const string jobName = "force-trade";
        _logger.LogInformation("[force-trade] Manual override triggered — running in background");
        _jobStatus.MarkStarted(jobName);

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var researchRepo = scope.ServiceProvider.GetRequiredService<ResearchRepository>();
                var candidateRepo = scope.ServiceProvider.GetRequiredService<PaperStockCandidateRepository>();
                var candidateService = scope.ServiceProvider.GetRequiredService<StockCandidateService>();
                var lifecycle = scope.ServiceProvider.GetRequiredService<PortfolioLifecycleService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<PortfolioChallengeController>>();

                // 1. Get today's champion predictions
                var championId = await researchRepo.GetChampionProfileIdAsync();
                if (championId is null)
                {
                    logger.LogWarning("[force-trade] No champion profile found");
                    _jobStatus.MarkFailed(jobName, "No champion profile found");
                    return;
                }

                var todayStart = DateTimeOffset.UtcNow.Date;
                var todayEnd = todayStart.AddDays(1);
                var predictions = await researchRepo.GetPredictionsByDateRangeAsync(
                    new DateTimeOffset(todayStart, TimeSpan.Zero),
                    new DateTimeOffset(todayEnd, TimeSpan.Zero),
                    profileId: championId);

                if (predictions.Count == 0)
                {
                    logger.LogWarning("[force-trade] No champion predictions found for today");
                    _jobStatus.MarkCompleted(jobName, "No champion predictions found for today — nothing to trade.");
                    return;
                }

                logger.LogInformation("[force-trade] Found {Count} champion predictions for today", predictions.Count);

                // 2. Check which predictions already have candidates
                var existingCandidates = await candidateRepo.GetOpenCandidatesAsync();
                var existingPredictionIds = new HashSet<string>(
                    existingCandidates
                        .Where(c => !string.IsNullOrEmpty(c.PredictionId))
                        .Select(c => c.PredictionId!),
                    StringComparer.OrdinalIgnoreCase);

                var needsCandidates = predictions
                    .Where(p => !existingPredictionIds.Contains(p.Id))
                    .ToList();

                logger.LogInformation(
                    "[force-trade] {Total} predictions, {Existing} already have candidates, {New} need candidates",
                    predictions.Count, predictions.Count - needsCandidates.Count, needsCandidates.Count);

                // 3. Build candidates from predictions that don't have them yet
                var runId = Guid.NewGuid().ToString();
                var directionalRankings = StockCandidateService.BuildDirectionalRankings(needsCandidates);
                var builtCandidates = new List<PaperStockCandidate>();

                foreach (var pred in needsCandidates)
                {
                    try
                    {
                        directionalRankings.TryGetValue(pred.Id, out var ranking);
                        var candidate = await candidateService.BuildStockCandidateFromPredictionAsync(
                            pred, runId,
                            ranking?.Percentile ?? 0,
                            ranking?.IsTopQuartile ?? false);
                        builtCandidates.Add(candidate);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[force-trade] Failed to build candidate for {Ticker}", pred.Ticker);
                    }
                }

                // 4. Save candidates to DB
                List<PaperStockCandidate> savedCandidates;
                if (builtCandidates.Count > 0)
                {
                    try
                    {
                        savedCandidates = await candidateRepo.SaveCandidatesBatchAsync(builtCandidates);
                        logger.LogInformation("[force-trade] Saved {Count} new candidates", savedCandidates.Count);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[force-trade] Failed to save candidates");
                        _jobStatus.MarkFailed(jobName, $"Failed to save candidates: {ex.Message}");
                        return;
                    }
                }
                else
                {
                    savedCandidates = [];
                }

                // 5. Combine newly saved + already existing open candidates for trading
                // If we built new candidates, trade those.
                // If all predictions already had candidates, trade those existing ones
                // (filtered to today's predictions only — don't trade stale candidates from prior days).
                List<PaperStockCandidate> allTradeable;
                if (savedCandidates.Count > 0)
                {
                    allTradeable = savedCandidates;
                }
                else
                {
                    var todayPredictionIds = new HashSet<string>(
                        predictions.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
                    allTradeable = existingCandidates
                        .Where(c => !string.IsNullOrEmpty(c.PredictionId) && todayPredictionIds.Contains(c.PredictionId))
                        .ToList();
                }

                if (allTradeable.Count == 0)
                {
                    logger.LogWarning("[force-trade] No tradeable candidates available");
                    _jobStatus.MarkCompleted(jobName, "No tradeable candidates available.");
                    return;
                }

                // 6. Open positions via the standard portfolio pipeline (bypasses time gate)
                var errors = new List<string>();
                var opened = await lifecycle.OpenPositionsForCandidatesAsync(
                    allTradeable, errors, bypassTimeGate: true);

                var msg = $"Forced {predictions.Count} predictions → {builtCandidates.Count} new candidates → {opened} positions opened.";
                if (errors.Count > 0)
                    msg += $" Errors: {string.Join("; ", errors)}";

                logger.LogInformation("[force-trade] {Summary}", msg);
                _jobStatus.MarkCompleted(jobName, msg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[force-trade] Background job failed");
                _jobStatus.MarkFailed(jobName, ex.Message);
            }
        });

        return Accepted(new
        {
            status = "started",
            jobName,
            message = "Force trade is running in the background. Poll /api/jobs/status for progress.",
            startedAt = DateTimeOffset.UtcNow,
        });
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
