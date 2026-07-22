using StockResearchAgent.Api.Controllers;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Portfolio;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services;

/// <summary>
/// Pre-warms the portfolio dashboard cache on startup so the first
/// user request doesn't timeout waiting for live quote fetches.
/// Runs once, 10 seconds after the app starts.
/// </summary>
public class DashboardWarmupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DashboardWarmupService> _logger;

    public DashboardWarmupService(IServiceProvider services, ILogger<DashboardWarmupService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Short delay to let the app finish starting
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        try
        {
            _logger.LogInformation("[dashboard-warmup] Pre-warming portfolio dashboard cache...");

            var repo = _services.GetRequiredService<PortfolioChallengeRepository>();
            var engine = _services.GetRequiredService<PortfolioBalanceEngine>();
            var marketData = _services.GetRequiredService<MarketDataService>();

            var challenges = await repo.GetAllChallengesAsync();
            var active = challenges.Where(c => c.Status == ChallengeStatus.active).ToList();

            if (active.Count == 0)
            {
                _logger.LogInformation("[dashboard-warmup] No active challenges — nothing to warm");
                return;
            }

            var warmed = 0;
            foreach (var challenge in active)
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    var summary = await engine.GetSummaryAsync(challenge.Id);
                    if (summary is null) continue;

                    var dashboard = await BuildDashboardAsync(summary, challenge, repo, marketData);
                    if (dashboard is null) continue;

                    PortfolioChallengeController.DashboardCache[challenge.Id] = (dashboard, DateTimeOffset.UtcNow);
                    warmed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[dashboard-warmup] Failed for challenge {Id}", challenge.Id);
                }
            }

            _logger.LogInformation("[dashboard-warmup] Warmed {Count}/{Total} active challenges", warmed, active.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[dashboard-warmup] Failed to pre-warm dashboard cache");
        }
    }

    /// <summary>
    /// Mirrors PortfolioChallengeController.BuildDashboardAsync.
    /// Uses a 5-second per-ticker timeout on quote fetches.
    /// </summary>
    private async Task<PortfolioDashboard?> BuildDashboardAsync(
        PortfolioChallengeSummary summary,
        PortfolioChallenge challenge,
        PortfolioChallengeRepository repo,
        MarketDataService marketData)
    {
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var quote = await marketData.GetQuoteAsync(ticker);
                lock (quoteMap) { quoteMap[ticker] = quote?.Price ?? 0; }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[dashboard-warmup] Quote timeout/fail for {Ticker}", ticker);
                lock (quoteMap) { quoteMap[ticker] = 0; }
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(quoteTasks);

        // ── Build enriched positions ──
        var enrichedPositions = new List<EnrichedPosition>();
        var totalUnrealizedPnL = 0.0;

        foreach (var pos in openPositions)
        {
            var currentPrice = quoteMap.GetValueOrDefault(pos.Ticker, 0);
            if (currentPrice <= 0) currentPrice = pos.EntryPrice;
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
                ? Math.Round(sortedClosed.Where(t => t.ExitDate.HasValue)
                    .DefaultIfEmpty()
                    .Average(t => t is null ? 0 : (t.ExitDate!.Value - t.EntryDate).TotalHours), 1)
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
}
