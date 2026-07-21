using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Portfolio;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Manages the lifecycle of portfolio positions that are opened and closed
/// automatically based on paper stock candidate signals. Extracted from
/// DynamicPickOrchestrator to reduce its dependency count.
///
/// Includes risk management: stop-loss, take-profit, and trailing stop
/// checks that run during the periodic dashboard refresh cron (~4× daily).
/// Thresholds are timeframe-aware and configurable via scoring_weight_overrides.
/// </summary>
public class PortfolioLifecycleService
{
    private readonly PortfolioBalanceEngine _portfolio;
    private readonly PortfolioChallengeRepository _portfolioRepo;
    private readonly PaperStockCandidateRepository _candidateRepo;
    private readonly ResearchRepository _researchRepo;
    private readonly MarketDataService _marketData;
    private readonly ILogger<PortfolioLifecycleService> _logger;

    public PortfolioLifecycleService(
        PortfolioBalanceEngine portfolio,
        PortfolioChallengeRepository portfolioRepo,
        PaperStockCandidateRepository candidateRepo,
        ResearchRepository researchRepo,
        MarketDataService marketData,
        ILogger<PortfolioLifecycleService> logger)
    {
        _portfolio = portfolio;
        _portfolioRepo = portfolioRepo;
        _candidateRepo = candidateRepo;
        _researchRepo = researchRepo;
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
                // ── Filter candidates by challenge PortfolioMode ──
                var (allowed, assetType) = FilterByPortfolioMode(challenge, c);
                if (!allowed)
                {
                    _logger.LogDebug("[portfolio] Skipping {Ticker} for challenge {Challenge} — mode {Mode} rejects this candidate (options={QualifiesForOptions}, tf={Timeframe})",
                        c.Ticker, challenge.Name, challenge.PortfolioMode, c.QualifiesForOptions, c.Timeframe);
                    continue;
                }

                try
                {
                    var pos = await _portfolio.AutoOpenPositionAsync(
                        challenge.Id,
                        c.PredictionId,
                        c.Ticker,
                        c.EntryPrice!.Value,
                        assetType,
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

                var quote = await _marketData.GetQuoteWithFallbackAsync(c.Ticker);
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

    // -----------------------------------------------------------------------
    // Risk Management — stop-loss, take-profit, trailing stop
    // -----------------------------------------------------------------------

    /// <summary>
    /// Timeframe tiers for risk management thresholds.
    /// Day trades get tight stops, swing trades moderate, long-term wide.
    /// Long-term positions need room to breathe through normal volatility.
    /// </summary>
    private enum RiskTier { Day, Swing, LongTerm }

    private static RiskTier ClassifyTimeframe(StockTimeframe tf) => tf switch
    {
        StockTimeframe.one_day => RiskTier.Day,
        StockTimeframe.two_day => RiskTier.Swing,
        StockTimeframe.one_week => RiskTier.Swing,
        _ => RiskTier.LongTerm, // one_month, three_month, six_month, one_year
    };

    /// <summary>
    /// Evaluate all open positions across all active challenges for risk limits.
    /// Closes positions that hit stop-loss, take-profit, or trailing stop.
    /// Updates high-water marks for trailing stop tracking.
    /// Returns a summary of actions taken.
    /// </summary>
    public async Task<RiskCheckResult> EvaluateRiskLimitsAsync()
    {
        var result = new RiskCheckResult();

        // Load configurable thresholds from scoring_weight_overrides
        var overrides = await _researchRepo.GetActiveWeightOverridesAsync();
        var weights = overrides.ToDictionary(o => o.SignalName, o => o.EffectiveWeight);

        var thresholds = new Dictionary<RiskTier, RiskThresholds>
        {
            [RiskTier.Day] = new()
            {
                StopLoss = weights.GetValueOrDefault("risk_sl_day", 0.05),
                TakeProfit = weights.GetValueOrDefault("risk_tp_day", 0.08),
                TrailActivate = 0, // no trailing stop for day trades
                TrailPercent = 0,
            },
            [RiskTier.Swing] = new()
            {
                StopLoss = weights.GetValueOrDefault("risk_sl_swing", 0.08),
                TakeProfit = weights.GetValueOrDefault("risk_tp_swing", 0.15),
                TrailActivate = weights.GetValueOrDefault("risk_trail_activate_swing", 0.10),
                TrailPercent = weights.GetValueOrDefault("risk_trail_pct_swing", 0.05),
            },
            [RiskTier.LongTerm] = new()
            {
                StopLoss = weights.GetValueOrDefault("risk_sl_longterm", 0.15),
                TakeProfit = 0, // no fixed take-profit for long-term — trailing stop handles it
                TrailActivate = weights.GetValueOrDefault("risk_trail_activate_longterm", 0.20),
                TrailPercent = weights.GetValueOrDefault("risk_trail_pct_longterm", 0.10),
            },
        };

        var activeChallenges = await _portfolioRepo.GetActiveChallengesAsync();
        if (activeChallenges.Count == 0) return result;

        foreach (var challenge in activeChallenges)
        {
            var openPositions = await _portfolioRepo.GetOpenPositionsAsync(challenge.Id);
            if (openPositions.Count == 0) continue;

            // Batch-fetch quotes for unique tickers (parallel, capped at 8)
            var uniqueTickers = openPositions.Select(p => p.Ticker).Distinct().ToList();
            var quoteMap = await FetchQuotesBatchAsync(uniqueTickers);

            // Batch-fetch paper stock candidates for timeframe data
            var predictionIds = openPositions
                .Where(p => p.PredictionId is not null)
                .Select(p => p.PredictionId!)
                .Distinct()
                .ToList();
            var candidateMap = await _candidateRepo.GetCandidatesByPredictionIdsAsync(predictionIds);

            foreach (var pos in openPositions)
            {
                result.PositionsChecked++;

                var currentPrice = quoteMap.GetValueOrDefault(pos.Ticker, 0);
                if (currentPrice <= 0) continue; // no quote available

                // Determine timeframe tier
                var timeframe = StockTimeframe.one_day; // default
                if (pos.PredictionId is not null && candidateMap.TryGetValue(pos.PredictionId, out var candidate))
                    timeframe = candidate.Timeframe;

                var tier = ClassifyTimeframe(timeframe);
                var limits = thresholds[tier];

                // Calculate unrealized P&L
                var pnlPercent = (currentPrice - pos.EntryPrice) / pos.EntryPrice;
                var hwm = pos.HighWaterMark ?? pos.EntryPrice;

                // ── Stop-loss check ──
                if (limits.StopLoss > 0 && pnlPercent <= -limits.StopLoss)
                {
                    var reason = $"STOP-LOSS ({tier}): {pos.Ticker} down {pnlPercent:P1} " +
                                 $"(limit -{limits.StopLoss:P0}). Entry ${pos.EntryPrice:F2} → ${currentPrice:F2}";
                    await CloseWithReason(pos, currentPrice, reason);
                    result.StopLossClosed++;
                    _logger.LogWarning("[risk] {Reason}", reason);
                    continue;
                }

                // ── Take-profit check (day/swing only) ──
                if (limits.TakeProfit > 0 && pnlPercent >= limits.TakeProfit)
                {
                    var reason = $"TAKE-PROFIT ({tier}): {pos.Ticker} up {pnlPercent:P1} " +
                                 $"(limit +{limits.TakeProfit:P0}). Entry ${pos.EntryPrice:F2} → ${currentPrice:F2}";
                    await CloseWithReason(pos, currentPrice, reason);
                    result.TakeProfitClosed++;
                    _logger.LogInformation("[risk] {Reason}", reason);
                    continue;
                }

                // ── Trailing stop ──
                if (limits.TrailActivate > 0 && limits.TrailPercent > 0)
                {
                    // Update high-water mark if new peak
                    if (currentPrice > hwm)
                    {
                        hwm = currentPrice;
                        await _portfolioRepo.UpdateHighWaterMarkAsync(pos.Id, hwm);
                        result.HighWaterMarksUpdated++;
                    }

                    // Check if trailing stop has been activated (price rose above activation threshold)
                    var hwmGainFromEntry = (hwm - pos.EntryPrice) / pos.EntryPrice;
                    if (hwmGainFromEntry >= limits.TrailActivate)
                    {
                        // Trail floor = high-water mark minus trail percent
                        var trailFloor = hwm * (1 - limits.TrailPercent);
                        if (currentPrice <= trailFloor)
                        {
                            var reason = $"TRAILING-STOP ({tier}): {pos.Ticker} fell to ${currentPrice:F2} " +
                                         $"below trail floor ${trailFloor:F2} (peak ${hwm:F2}, trail {limits.TrailPercent:P0}). " +
                                         $"Locked in {pnlPercent:P1} gain from entry ${pos.EntryPrice:F2}";
                            await CloseWithReason(pos, currentPrice, reason);
                            result.TrailingStopClosed++;
                            _logger.LogInformation("[risk] {Reason}", reason);
                            continue;
                        }
                    }
                }
            }
        }

        if (result.TotalClosed > 0)
            _logger.LogWarning("[risk] Risk check complete: {Checked} positions checked, " +
                "{SL} stop-loss, {TP} take-profit, {TS} trailing-stop closures, {HWM} high-water marks updated",
                result.PositionsChecked, result.StopLossClosed, result.TakeProfitClosed,
                result.TrailingStopClosed, result.HighWaterMarksUpdated);
        else
            _logger.LogInformation("[risk] Risk check complete: {Checked} positions checked, no triggers hit",
                result.PositionsChecked);

        return result;
    }

    /// <summary>Fetch quotes for multiple tickers in parallel (capped at 8 concurrent).</summary>
    private async Task<Dictionary<string, double>> FetchQuotesBatchAsync(List<string> tickers)
    {
        var quoteMap = new Dictionary<string, double>();
        using var semaphore = new SemaphoreSlim(8);
        var tasks = tickers.Select(async ticker =>
        {
            await semaphore.WaitAsync();
            try
            {
                var quote = await _marketData.GetQuoteWithFallbackAsync(ticker);
                lock (quoteMap) { quoteMap[ticker] = quote?.Price ?? 0; }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[risk] Failed to fetch quote for {Ticker}", ticker);
                lock (quoteMap) { quoteMap[ticker] = 0; }
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);
        return quoteMap;
    }

    /// <summary>Close a position with a specific risk management reason.</summary>
    private async Task CloseWithReason(PortfolioPosition pos, double exitPrice, string reason)
    {
        await _portfolio.ClosePositionAsync(new ClosePositionRequest
        {
            PositionId = pos.Id,
            ExitPrice = exitPrice,
            ReasonExited = reason,
        });
    }

    // ── Timeframes considered "swing" (multi-day holds) ──
    private static readonly HashSet<StockTimeframe> SwingTimeframes =
    [
        StockTimeframe.one_week,
        StockTimeframe.two_day,
        StockTimeframe.one_month,
        StockTimeframe.three_month,
        StockTimeframe.six_month,
        StockTimeframe.one_year,
    ];

    /// <summary>
    /// Determines whether a candidate is allowed in the given challenge
    /// based on its <see cref="PortfolioMode"/>, and which asset type to use.
    /// </summary>
    private static (bool Allowed, PositionAssetType AssetType) FilterByPortfolioMode(
        PortfolioChallenge challenge,
        PaperStockCandidate candidate)
    {
        return challenge.PortfolioMode switch
        {
            // Options-only: candidate must qualify for options → open as option
            PortfolioMode.options_only =>
                candidate.QualifiesForOptions
                    ? (true, PositionAssetType.option)
                    : (false, default),

            // Stock-only: always open as stock (current default behavior)
            PortfolioMode.stock_only =>
                (true, PositionAssetType.stock),

            // Day trading: only 1-day timeframe, stock positions
            PortfolioMode.day_trading =>
                candidate.Timeframe == StockTimeframe.one_day
                    ? (true, PositionAssetType.stock)
                    : (false, default),

            // Swing trading: multi-day timeframes only, stock positions
            PortfolioMode.swing_trading =>
                SwingTimeframes.Contains(candidate.Timeframe)
                    ? (true, PositionAssetType.stock)
                    : (false, default),

            // Mixed: allow everything; use option type if candidate qualifies
            PortfolioMode.mixed =>
                candidate.QualifiesForOptions
                    ? (true, PositionAssetType.option)
                    : (true, PositionAssetType.stock),

            _ => (true, PositionAssetType.stock),
        };
    }
}

// -----------------------------------------------------------------------
// Risk management models
// -----------------------------------------------------------------------

public record RiskThresholds
{
    /// <summary>Close if unrealized loss exceeds this fraction (e.g. 0.05 = -5%).</summary>
    public double StopLoss { get; init; }
    /// <summary>Close if unrealized gain exceeds this fraction (e.g. 0.08 = +8%). 0 = disabled.</summary>
    public double TakeProfit { get; init; }
    /// <summary>Activate trailing stop once gain exceeds this (e.g. 0.10 = +10%). 0 = disabled.</summary>
    public double TrailActivate { get; init; }
    /// <summary>Trail percent below peak price (e.g. 0.05 = 5% below peak).</summary>
    public double TrailPercent { get; init; }
}

public record RiskCheckResult
{
    public int PositionsChecked { get; set; }
    public int StopLossClosed { get; set; }
    public int TakeProfitClosed { get; set; }
    public int TrailingStopClosed { get; set; }
    public int HighWaterMarksUpdated { get; set; }
    public int TotalClosed => StopLossClosed + TakeProfitClosed + TrailingStopClosed;
}
