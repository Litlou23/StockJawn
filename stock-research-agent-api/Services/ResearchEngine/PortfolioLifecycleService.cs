using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Portfolio;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Manages the lifecycle of portfolio positions that are opened and closed
/// automatically based on paper stock candidate signals. Extracted from
/// DynamicPickOrchestrator to reduce its dependency count.
/// </summary>
public class PortfolioLifecycleService
{
    private readonly PortfolioBalanceEngine _portfolio;
    private readonly PortfolioChallengeRepository _portfolioRepo;
    private readonly MarketDataService _marketData;
    private readonly ILogger<PortfolioLifecycleService> _logger;

    public PortfolioLifecycleService(
        PortfolioBalanceEngine portfolio,
        PortfolioChallengeRepository portfolioRepo,
        MarketDataService marketData,
        ILogger<PortfolioLifecycleService> logger)
    {
        _portfolio = portfolio;
        _portfolioRepo = portfolioRepo;
        _marketData = marketData;
        _logger = logger;
    }

    /// <summary>
    /// Auto-open portfolio positions for actionable candidates that are
    /// directional, open, and have a valid entry price. Returns the number
    /// of positions successfully opened.
    /// </summary>
    public async Task<int> OpenPositionsForCandidatesAsync(
        List<PaperStockCandidate> actionableCandidates,
        List<string> errors)
    {
        var portfolioPositionsOpened = 0;
        var activeChallenges = await _portfolioRepo.GetActiveChallengesAsync();
        if (activeChallenges.Count == 0)
            return 0;

        var eligible = actionableCandidates
            .Where(c => c.IsActionable
                && c.Status == PaperStockStatus.open
                && c.EntryPrice is > 0
                && PredictionCategoryHelper.IsDirectional(c.PredictionType))
            .ToList();

        foreach (var challenge in activeChallenges)
        {
            foreach (var c in eligible)
            {
                try
                {
                    var pos = await _portfolio.AutoOpenPositionAsync(
                        challenge.Id,
                        c.PredictionId,
                        c.Ticker,
                        c.EntryPrice!.Value,
                        PositionAssetType.stock,
                        $"Auto from paper stock candidate. Mode={c.CandidateMode}, tier={c.QualityTier}, conf={c.ConfidenceScore}");

                    if (pos is not null) portfolioPositionsOpened++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[portfolio] Position open failed for {Ticker} in challenge {Challenge}",
                        c.Ticker, challenge.Name);
                    errors.Add($"portfolio-open {c.Ticker} ({challenge.Name}): {ex.Message}");
                }
            }
        }

        if (portfolioPositionsOpened > 0)
            _logger.LogInformation("[portfolio] Opened {Count} portfolio positions across {Challenges} active challenges",
                portfolioPositionsOpened, activeChallenges.Count);

        return portfolioPositionsOpened;
    }

    /// <summary>
    /// Auto-close portfolio positions whose paper stock candidates have
    /// reached their evaluation window. Positions are NOT closed until
    /// the candidate's timeframe has elapsed (e.g. a 1_week prediction
    /// stays open for at least 120 hours / 5 trading days).
    /// Returns the number of positions closed and skipped.
    /// </summary>
    public async Task<(int Closed, int Skipped)> ClosePositionsForCandidatesAsync(
        List<PaperStockCandidate> openCandidates,
        Dictionary<StockTimeframe, int> minEvalHours,
        List<string> errors)
    {
        var portfolioPositionsClosed = 0;
        var portfolioPositionsSkipped = 0;

        foreach (var c in openCandidates)
        {
            if (c.PredictionId is null) continue;

            // ── Timeframe gate: don't close positions before their window ──
            var ageHours = (DateTimeOffset.UtcNow - c.CreatedAt).TotalHours;
            var minHours = minEvalHours.GetValueOrDefault(c.Timeframe, 6);
            if (ageHours < minHours)
            {
                _logger.LogDebug("[portfolio] {Ticker}: position too young to close ({Age:F1}h < {Min}h for {Tf})",
                    c.Ticker, ageHours, minHours, c.Timeframe);
                portfolioPositionsSkipped++;
                continue;
            }

            try
            {
                var portfolioPositions = await _portfolioRepo.GetOpenPositionsByPredictionIdAsync(c.PredictionId);
                if (portfolioPositions.Count == 0) continue;

                var quote = await _marketData.GetQuoteAsync(c.Ticker);
                if (quote is null || quote.Price <= 0) continue;

                foreach (var pos in portfolioPositions)
                {
                    var closed = await _portfolio.ClosePositionAsync(new ClosePositionRequest
                    {
                        PositionId = pos.Id,
                        ExitPrice = quote.Price,
                        ReasonExited = $"EOD auto-close. {c.Ticker} current price ${quote.Price:F2}.",
                    });

                    if (closed is not null) portfolioPositionsClosed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[portfolio] Position close failed for prediction {PredId}", c.PredictionId);
                errors.Add($"portfolio-close {c.Ticker}: {ex.Message}");
            }
        }

        return (portfolioPositionsClosed, portfolioPositionsSkipped);
    }

    public async Task<PortfolioChallengeSummary?> GetSummaryAsync()
    {
        return await _portfolio.GetSummaryAsync();
    }
}
