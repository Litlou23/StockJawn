using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Portfolio;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.TradeDecision;

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
    private readonly OptionsDataRepository _optionsRepo;
    private readonly MarketDataService _marketData;
    private readonly MarketStressDetector _stressDetector;
    private readonly TradeStatsProvider _tradeStats;
    private readonly ILogger<PortfolioLifecycleService> _logger;

    public PortfolioLifecycleService(
        PortfolioBalanceEngine portfolio,
        PortfolioChallengeRepository portfolioRepo,
        PaperStockCandidateRepository candidateRepo,
        ResearchRepository researchRepo,
        OptionsDataRepository optionsRepo,
        MarketDataService marketData,
        MarketStressDetector stressDetector,
        TradeStatsProvider tradeStats,
        ILogger<PortfolioLifecycleService> logger)
    {
        _portfolio = portfolio;
        _portfolioRepo = portfolioRepo;
        _candidateRepo = candidateRepo;
        _researchRepo = researchRepo;
        _optionsRepo = optionsRepo;
        _marketData = marketData;
        _stressDetector = stressDetector;
        _tradeStats = tradeStats;
        _logger = logger;
    }

    /// <summary>
    /// Largest single-contract option premium any active challenge can afford.
    /// Returned to the option-generation pipeline as a plain dollar constraint so
    /// candidate selection never proposes contracts the portfolio cannot open
    /// (ADR-008: the budget decision stays in the Portfolio AI layer).
    /// Null means there is no active challenge, so no budget constraint applies.
    /// </summary>
    public async Task<double?> GetMaxOptionContractBudgetAsync()
    {
        var activeChallenges = await _portfolioRepo.GetActiveChallengesAsync();
        if (activeChallenges.Count == 0) return null;

        var overrides = await _researchRepo.GetActiveWeightOverridesAsync();
        var weights = overrides.ToDictionary(o => o.SignalName, o => o.EffectiveWeight);
        var sizingConfig = BuildSizingConfig(weights);

        // A contract is affordable if *any* active challenge could open it.
        return activeChallenges.Max(c => PortfolioBalanceEngine.CalculateMaxContractBudget(
            c.CurrentCash, c.RiskProfile, sizingConfig));
    }

    private static PortfolioBalanceEngine.PositionSizingConfig BuildSizingConfig(
        Dictionary<string, double> weights) => new(
            MinFraction: weights.GetValueOrDefault("sizing_min_fraction", 0.02),
            MaxFraction: weights.GetValueOrDefault("sizing_max_fraction", 0.20),
            ConfidenceFloor: weights.GetValueOrDefault("sizing_confidence_floor", 35),
            ConfidenceCeiling: weights.GetValueOrDefault("sizing_confidence_ceiling", 85),
            EvBonus: weights.GetValueOrDefault("sizing_ev_bonus", 0.03),
            EvPenalty: weights.GetValueOrDefault("sizing_ev_penalty", 0.50),
            VolBaselineAtrPct: weights.GetValueOrDefault("sizing_vol_baseline_atr_pct", 2.5),
            VolMinFactor: weights.GetValueOrDefault("sizing_vol_min_factor", 0.25),
            VolMaxFactor: weights.GetValueOrDefault("sizing_vol_max_factor", 2.0),
            KellyFraction: weights.GetValueOrDefault("sizing_kelly_fraction", 0.25),
            KellyMinSampleSize: weights.GetValueOrDefault("sizing_kelly_min_samples", 30),
            OptionMinFraction: weights.GetValueOrDefault("sizing_option_min_fraction", 0.30)
        );

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

        // ── Load configurable guardrails from scoring_weight_overrides ──
        var overrides = await _researchRepo.GetActiveWeightOverridesAsync();
        var weights = overrides.ToDictionary(o => o.SignalName, o => o.EffectiveWeight);
        var minConfidence = (int)weights.GetValueOrDefault("min_confidence_threshold", 35);
        var maxPositions = (int)weights.GetValueOrDefault("max_open_positions", 8);
        var maxDrawdownPct = weights.GetValueOrDefault("max_drawdown_percent", 25);
        var maxPerSector = (int)weights.GetValueOrDefault("max_positions_per_sector", 3);
        var maxChasePercent = weights.GetValueOrDefault("max_entry_chase_percent", 2.0);
        var minEvPercent = weights.GetValueOrDefault("min_ev_threshold", 0.5);
        var minEntryPrice = weights.GetValueOrDefault("min_entry_price", 2.0);
        var regimeGateEnabled = weights.GetValueOrDefault("regime_gate_enabled", 1.0) >= 1.0;
        var minTargetMovePct = weights.GetValueOrDefault("min_target_move_pct", 3.0);
        var skipWeakQuality = weights.GetValueOrDefault("skip_weak_quality", 1.0) >= 1.0;
        var minBearishConfidence = (int)weights.GetValueOrDefault("min_bearish_confidence", 55);
        var bearishAllowed = weights.GetValueOrDefault("bearish_portfolio_allowed", 0.0) >= 1.0;

        // ── Position sizing config ──
        var sizingConfig = BuildSizingConfig(weights);

        // ── Market regime gate ──
        // Don't open bullish positions in a bearish market or vice versa.
        // Uses SPY price vs EMA26 (proxy for 20-day) — same signal the scoring
        // pipeline uses, but here it's a hard gate rather than a score modifier.
        string? marketRegime = null; // "bullish", "bearish", or null (neutral/unknown)
        if (regimeGateEnabled)
        {
            try
            {
                var spyQuoteTask = _marketData.GetQuoteAsync("SPY");
                var spyEmaTask = _marketData.GetEmaAsync("SPY");
                await Task.WhenAll(spyQuoteTask, spyEmaTask);

                var spyPrice = spyQuoteTask.Result?.Price;
                var spyEma = spyEmaTask.Result.Ema26; // EMA26 used as 20-EMA proxy throughout codebase

                if (spyPrice is > 0 && spyEma is > 0)
                {
                    var spyRatio = spyPrice.Value / spyEma.Value;
                    // Use same 0.3% deviation threshold as BenchmarkContext
                    if (spyRatio > 1.003)
                        marketRegime = "bullish";
                    else if (spyRatio < 0.997)
                        marketRegime = "bearish";
                    // else: neutral — no gate applied

                    _logger.LogInformation(
                        "[portfolio] Regime gate: SPY ${Price:F2}, EMA ${Ema:F2}, ratio {Ratio:F4} → regime={Regime}",
                        spyPrice, spyEma, spyRatio, marketRegime ?? "neutral");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[portfolio] Regime gate SPY fetch failed — proceeding without gate");
            }
        }

        var eligible = actionableCandidates
            .Where(c => c.IsActionable
                && c.Status == PaperStockStatus.open
                && c.EntryPrice is > 0
                && (double)c.EntryPrice.Value >= minEntryPrice // Penny stock filter — skip sub-$2 stocks
                && c.Timeframe != StockTimeframe.one_day // 1-day predictions are 34% accurate — pure noise
                && c.Timeframe != StockTimeframe.one_month // 1-month predictions are 12.5% accurate — catastrophic
                && PredictionCategoryHelper.IsDirectional(c.PredictionType)
                && c.ConfidenceScore >= minConfidence) // EXP-005: filter low-confidence noise
            .ToList();

        // ── Bearish filter ─────────────────────────────────────────────
        // Data shows bearish predictions underperform (45.1% overall, catastrophic
        // in 1-month). When losers hit, they're 10-16% adverse on average.
        // Either block entirely (bearish_portfolio_allowed=0) or require much
        // higher confidence than bullish trades.
        {
            var beforeBearish = eligible.Count;
            eligible = eligible
                .Where(c =>
                {
                    if (!PredictionCategoryHelper.IsBullish(c.PredictionType))
                    {
                        // This is a bearish candidate
                        if (!bearishAllowed) return false;
                        if (c.ConfidenceScore < minBearishConfidence) return false;
                    }
                    return true;
                })
                .ToList();

            var filteredBearish = beforeBearish - eligible.Count;
            if (filteredBearish > 0)
                _logger.LogInformation(
                    "[portfolio] Filtered out {Count} bearish candidates (allowed={Allowed}, minConf={Min})",
                    filteredBearish, bearishAllowed, minBearishConfidence);
        }

        // ── Minimum target move filter ──────────────────────────────────
        // A real trader doesn't enter a trade for 0.5% upside. Skip any
        // candidate whose target price is less than minTargetMovePct from entry.
        var beforeTargetFilter = eligible.Count;
        if (minTargetMovePct > 0)
        {
            eligible = eligible
                .Where(c =>
                {
                    if (c.TargetPrice is not > 0 || c.EntryPrice is not > 0) return true; // no target data → don't block
                    var movePct = Math.Abs(c.TargetPrice.Value - c.EntryPrice.Value) / c.EntryPrice.Value * 100;
                    return movePct >= minTargetMovePct;
                })
                .ToList();

            var filteredByTarget = beforeTargetFilter - eligible.Count;
            if (filteredByTarget > 0)
                _logger.LogInformation(
                    "[portfolio] Filtered out {Count} candidates with target move < {Min}%",
                    filteredByTarget, minTargetMovePct);
        }

        // ── Quality filter — skip "weak" candidates ─────────────────────
        // Data shows weak-quality trades are noise. A trader who cares about
        // P&L only takes setups where the edge is clear.
        if (skipWeakQuality)
        {
            var beforeQuality = eligible.Count;
            eligible = eligible
                .Where(c => c.QualityTier != QualityTier.weak && c.QualityTier != QualityTier.very_weak)
                .ToList();

            var filteredByQuality = beforeQuality - eligible.Count;
            if (filteredByQuality > 0)
                _logger.LogInformation(
                    "[portfolio] Filtered out {Count} weak/very_weak quality candidates — only trading strong setups",
                    filteredByQuality);
        }

        // ── Sort by profit potential — best EV first ────────────────────
        // A trader who cares about P&L opens the highest-edge trades first,
        // not whatever happens to be at the top of the list.
        eligible = eligible
            .OrderByDescending(c => ComputeEvPercent(c))
            .ThenByDescending(c => c.ConfidenceScore)
            .ToList();

        var filteredByConfidence = actionableCandidates.Count(c => c.IsActionable
            && c.Status == PaperStockStatus.open
            && c.EntryPrice is > 0
            && PredictionCategoryHelper.IsDirectional(c.PredictionType)
            && c.ConfidenceScore < minConfidence);

        if (filteredByConfidence > 0)
            _logger.LogInformation(
                "[portfolio] Filtered out {Count} candidates below confidence threshold {Min}",
                filteredByConfidence, minConfidence);

        foreach (var challenge in activeChallenges)
        {
            // ── Drawdown circuit breaker ──
            // If portfolio has dropped more than maxDrawdownPct from its peak,
            // pause all new trades until recovery
            var peakBalance = Math.Max(challenge.StartingBalance, challenge.CurrentBalance);
            // Use highest historical balance if tracked, otherwise max of starting and current
            if (challenge.CurrentBalance > 0 && peakBalance > 0)
            {
                var drawdownPct = (peakBalance - challenge.CurrentBalance) / peakBalance * 100;
                if (drawdownPct >= maxDrawdownPct)
                {
                    _logger.LogWarning(
                        "[portfolio] CIRCUIT BREAKER: challenge {Name} drawdown {Dd:F1}% >= {Max}% limit. " +
                        "Peak ${Peak:F2}, current ${Current:F2}. Pausing new trades.",
                        challenge.Name, drawdownPct, maxDrawdownPct, peakBalance, challenge.CurrentBalance);
                    continue;
                }
            }

            // ── Max open positions check ──
            var openPositions = await _portfolioRepo.GetOpenPositionsAsync(challenge.Id);
            var currentOpenCount = openPositions.Count;

            if (currentOpenCount >= maxPositions)
            {
                _logger.LogInformation(
                    "[portfolio] Challenge {Name} at position limit ({Count}/{Max}). Skipping new entries.",
                    challenge.Name, currentOpenCount, maxPositions);
                continue;
            }

            var slotsAvailable = maxPositions - currentOpenCount;
            var opened = 0;

            // ── Fetch real trade stats for Kelly criterion position sizing ──
            var tradeStats = await _tradeStats.GetStatsAsync();

            // ── Build sector concentration map from existing open positions ──
            // Uses cached fundamentals (no extra API calls — already fetched during morning scan).
            var sectorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var pos in openPositions)
            {
                try
                {
                    var fundamentals = await _marketData.GetFundamentalsAsync(pos.Ticker);
                    var sector = fundamentals?.Sector;
                    if (!string.IsNullOrEmpty(sector))
                        sectorCounts[sector] = sectorCounts.GetValueOrDefault(sector) + 1;
                }
                catch { /* best-effort — unknown sector won't block */ }
            }

            foreach (var c in eligible)
            {
                if (opened >= slotsAvailable) break;

                // ── Filter candidates by challenge PortfolioMode ──
                var (allowed, assetType) = FilterByPortfolioMode(challenge, c);
                if (!allowed)
                {
                    _logger.LogDebug("[portfolio] Skipping {Ticker} for challenge {Challenge} — mode {Mode} rejects this candidate (options={QualifiesForOptions}, tf={Timeframe})",
                        c.Ticker, challenge.Name, challenge.PortfolioMode, c.QualifiesForOptions, c.Timeframe);
                    continue;
                }

                // ── Regime gate — don't trade against the market trend ──
                // Bullish picks in a bearish market get slaughtered (data shows 4-23% accuracy).
                // Bearish picks in a bullish market face a rising tide.
                if (marketRegime is not null)
                {
                    var isBullish = PredictionCategoryHelper.IsBullish(c.PredictionType);
                    var blocked = (isBullish && marketRegime == "bearish")
                               || (!isBullish && marketRegime == "bullish");
                    if (blocked)
                    {
                        _logger.LogInformation(
                            "[portfolio] REGIME GATE: Skipping {Direction} {Ticker} — market regime is {Regime}",
                            c.PredictionType, c.Ticker, marketRegime);
                        continue;
                    }
                }

                // Skip tickers we already hold
                if (openPositions.Any(p => p.Ticker == c.Ticker))
                {
                    _logger.LogDebug("[portfolio] Skipping {Ticker} — already held in challenge {Challenge}",
                        c.Ticker, challenge.Name);
                    continue;
                }

                // ── Sector concentration check ──
                // Don't overload any single sector — one bad sector day shouldn't wipe the portfolio.
                string? candidateSector = null;
                try
                {
                    var fundamentals = await _marketData.GetFundamentalsAsync(c.Ticker);
                    candidateSector = fundamentals?.Sector;
                }
                catch { /* unknown sector won't block entry */ }

                if (!string.IsNullOrEmpty(candidateSector)
                    && sectorCounts.GetValueOrDefault(candidateSector) >= maxPerSector)
                {
                    _logger.LogInformation(
                        "[portfolio] Skipping {Ticker} — sector {Sector} already at concentration limit ({Count}/{Max})",
                        c.Ticker, candidateSector, sectorCounts[candidateSector], maxPerSector);
                    continue;
                }

                // ── Entry timing filter — don't chase ──
                // If the stock already moved significantly in the predicted direction
                // since the prediction was generated, we'd be buying high (or selling low).
                // Compare current price to the prediction's entry price snapshot.
                if (maxChasePercent > 0 && c.EntryPrice is > 0)
                {
                    try
                    {
                        var currentQuote = await _marketData.GetQuoteAsync(c.Ticker);
                        if (currentQuote is not null)
                        {
                            var movePercent = (currentQuote.Price - c.EntryPrice.Value) / c.EntryPrice.Value * 100;
                            var isBullish = PredictionCategoryHelper.IsBullish(c.PredictionType);

                            // Bullish + stock already up > threshold = chasing
                            // Bearish + stock already down > threshold = chasing
                            var isChasing = (isBullish && movePercent >= maxChasePercent)
                                         || (!isBullish && movePercent <= -maxChasePercent);

                            if (isChasing)
                            {
                                _logger.LogInformation(
                                    "[portfolio] Skipping {Ticker} — already moved {Move:F1}% in predicted direction (chase limit {Limit}%)",
                                    c.Ticker, Math.Abs(movePercent), maxChasePercent);
                                continue;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[portfolio] Chase check quote fetch failed for {Ticker} — proceeding", c.Ticker);
                    }
                }

                try
                {
                    // Compute EV% from target/stop/entry if available
                    // Uses Math.Abs so the formula works for both bullish (target>entry) and bearish (target<entry)
                    double? evPercent = null;
                    if (c.TargetPrice is > 0 && c.StopPrice is > 0 && c.EntryPrice is > 0)
                    {
                        var winProb = c.ConfidenceScore / 100.0;
                        var gainPct = Math.Abs(c.TargetPrice.Value - c.EntryPrice.Value) / c.EntryPrice.Value * 100;
                        var lossPct = Math.Abs(c.EntryPrice.Value - c.StopPrice.Value) / c.EntryPrice.Value * 100;
                        evPercent = (winProb * gainPct) - ((1 - winProb) * lossPct);
                    }

                    // ── EV gate — never enter negative-EV trades ──
                    // A real trader would never take a trade where the math says you lose money.
                    // min_ev_threshold defaults to 0.5% — configurable via scoring_weight_overrides.
                    if (evPercent is not null && evPercent < minEvPercent)
                    {
                        _logger.LogInformation(
                            "[portfolio] Skipping {Ticker} — EV {Ev:F1}% below threshold {Min}% (conf={Conf}, gain={Gain:F1}%, loss={Loss:F1}%)",
                            c.Ticker, evPercent, minEvPercent, c.ConfidenceScore,
                            c.TargetPrice is > 0 && c.EntryPrice is > 0
                                ? Math.Abs(c.TargetPrice.Value - c.EntryPrice.Value) / c.EntryPrice.Value * 100 : 0,
                            c.StopPrice is > 0 && c.EntryPrice is > 0
                                ? Math.Abs(c.EntryPrice.Value - c.StopPrice.Value) / c.EntryPrice.Value * 100 : 0);
                        continue;
                    }

                    // ── For options: use real option premium from paper option candidate ──
                    // The stock candidate's EntryPrice is the stock price, not the option
                    // premium. Look up the linked PaperCandidateEnhanced to get the actual
                    // contract mid-price from MarketData.app.
                    var entryPrice = c.EntryPrice!.Value;
                    var optionSymbol = (string?)null;
                    if (assetType == PositionAssetType.option && !string.IsNullOrEmpty(c.Id))
                    {
                        var optionCandidate = await _optionsRepo.GetByStockCandidateIdAsync(c.Id);
                        if (optionCandidate is not null && optionCandidate.EntryMid > 0)
                        {
                            entryPrice = optionCandidate.EntryMid;
                            optionSymbol = optionCandidate.OptionSymbol;
                            _logger.LogInformation(
                                "[portfolio] Using real option premium for {Ticker}: ${Premium:F2} ({Symbol}, strike ${Strike}, DTE {Dte})",
                                c.Ticker, entryPrice, optionCandidate.OptionSymbol, optionCandidate.Strike, optionCandidate.DteAtEntry);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "[portfolio] No option candidate found for stock candidate {Id} ({Ticker}) — skipping option position",
                                c.Id, c.Ticker);
                            continue; // Don't open option positions without real premium data
                        }
                    }

                    // ── Look up prediction's ATR% for volatility-adjusted sizing ──
                    // Stored on PredictionCandidate at scan time — no extra API call.
                    double? atrPct = null;
                    if (!string.IsNullOrEmpty(c.PredictionId))
                    {
                        try
                        {
                            var prediction = await _researchRepo.GetPredictionByIdAsync(c.PredictionId);
                            atrPct = prediction?.AtrPercent;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[portfolio] ATR lookup failed for {Ticker} — sizing without vol adjustment", c.Ticker);
                        }
                    }

                    var pos = await _portfolio.AutoOpenPositionAsync(
                        challenge.Id,
                        c.PredictionId,
                        c.Ticker,
                        entryPrice,
                        assetType,
                        $"Auto from paper stock candidate. Mode={c.CandidateMode}, tier={c.QualityTier}, conf={c.ConfidenceScore}, ev={evPercent:F1}%, atr={atrPct?.ToString("F1") ?? "n/a"}%"
                            + (optionSymbol is not null ? $", contract={optionSymbol}" : "")
                            + (tradeStats.IsReal ? $", kelly(wr={tradeStats.WinRate:P0},n={tradeStats.SampleSize})" : ""),
                        confidence: c.ConfidenceScore,
                        expectedValuePercent: evPercent,
                        sizingConfig: sizingConfig,
                        atrPercent: atrPct,
                        winRate: tradeStats.IsReal ? tradeStats.WinRate : null,
                        avgWinPercent: tradeStats.IsReal ? tradeStats.AverageWinPercent : null,
                        avgLossPercent: tradeStats.IsReal ? tradeStats.AverageLossPercent : null,
                        statsSampleSize: tradeStats.SampleSize);

                    if (pos is not null)
                    {
                        portfolioPositionsOpened++;
                        opened++;

                        // Update sector count so subsequent candidates respect the limit
                        if (!string.IsNullOrEmpty(candidateSector))
                            sectorCounts[candidateSector] = sectorCounts.GetValueOrDefault(candidateSector) + 1;
                    }
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

    /// <summary>
    /// Safety net that closes positions whose holding window has fully elapsed,
    /// keyed off the position itself rather than its paper candidate.
    ///
    /// ClosePositionsForCandidatesAsync can only reach positions whose candidate is
    /// still `open`. A candidate that expires past MaxEvalHours, or whose close fails
    /// after it was already marked evaluated, drops out of that list permanently and
    /// strands its position — holding cash that never returns to the challenge.
    /// This sweep runs after the candidate-driven pass and uses the later
    /// MaxEvalHours boundary, so it only ever picks up genuine strays.
    /// </summary>
    public async Task<int> CloseExpiredPositionsAsync(List<string> errors)
    {
        var activeChallenges = await _portfolioRepo.GetActiveChallengesAsync();
        if (activeChallenges.Count == 0) return 0;

        var overrides = await _researchRepo.GetActiveWeightOverridesAsync();
        var weights = overrides.ToDictionary(o => o.SignalName, o => o.EffectiveWeight);
        // Backstop for positions whose originating candidate can no longer be found.
        var fallbackMaxHours = weights.GetValueOrDefault("max_position_hold_hours", 720);

        var closed = 0;

        foreach (var challenge in activeChallenges)
        {
            var openPositions = await _portfolioRepo.GetOpenPositionsAsync(challenge.Id);
            if (openPositions.Count == 0) continue;

            var predictionIds = openPositions
                .Where(p => !string.IsNullOrEmpty(p.PredictionId))
                .Select(p => p.PredictionId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidates = await _candidateRepo.GetCandidatesByPredictionIdsAsync(predictionIds);
            var timeframeByPrediction = new Dictionary<string, StockTimeframe>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
            {
                if (!string.IsNullOrEmpty(c.PredictionId))
                    timeframeByPrediction.TryAdd(c.PredictionId!, c.Timeframe);
            }

            foreach (var pos in openPositions)
            {
                var ageHours = (DateTimeOffset.UtcNow - pos.EntryDate).TotalHours;

                var maxHours = pos.PredictionId is not null
                    && timeframeByPrediction.TryGetValue(pos.PredictionId, out var tf)
                        ? StockCandidateService.MaxEvalHours.GetValueOrDefault(tf, (int)fallbackMaxHours)
                        : (int)fallbackMaxHours;

                if (ageHours <= maxHours) continue;

                try
                {
                    var quote = await _marketData.GetQuoteWithFallbackAsync(pos.Ticker);
                    if (quote is null || quote.Price <= 0)
                    {
                        _logger.LogWarning(
                            "[portfolio] Cannot close stranded position {Ticker} ({Age:F0}h old) — no quote available.",
                            pos.Ticker, ageHours);
                        continue;
                    }

                    var result = await _portfolio.ClosePositionAsync(new ClosePositionRequest
                    {
                        PositionId = pos.Id,
                        ExitPrice = quote.Price,
                        ReasonExited =
                            $"Holding window elapsed ({ageHours:F0}h > {maxHours}h). Auto-closed at ${quote.Price:F2}.",
                    });

                    if (result is not null)
                    {
                        closed++;
                        _logger.LogInformation(
                            "[portfolio] Closed stranded position {Ticker} after {Age:F0}h (limit {Max}h), ${Invested:F2} returned to cash.",
                            pos.Ticker, ageHours, maxHours, pos.DollarsInvested);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[portfolio] Stranded-position close failed for {Ticker}", pos.Ticker);
                    errors.Add($"portfolio-expire {pos.Ticker}: {ex.Message}");
                }
            }
        }

        if (closed > 0)
            _logger.LogInformation("[portfolio] Released {Count} stranded positions back to cash.", closed);

        return closed;
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

        // Market stress: widen stop-losses during volatile conditions
        // so temporary drops don't trigger premature exits
        var stopMultiplier = 1.0;
        try
        {
            var stress = await _stressDetector.EvaluateAsync();
            if (stress.IsStressed)
            {
                stopMultiplier = stress.StopLossMultiplier;
                _logger.LogInformation(
                    "[risk-mgmt] Market stress {Level} — widening stop-losses by {Mult:F2}x",
                    stress.Level, stopMultiplier);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[risk-mgmt] Market stress check failed, using normal stops");
        }

        var thresholds = new Dictionary<RiskTier, RiskThresholds>
        {
            [RiskTier.Day] = new()
            {
                StopLoss = weights.GetValueOrDefault("risk_sl_day", 0.05) * stopMultiplier,
                TakeProfit = weights.GetValueOrDefault("risk_tp_day", 0.08),
                TrailActivate = weights.GetValueOrDefault("risk_trail_activate_day", 0.04),
                TrailPercent = weights.GetValueOrDefault("risk_trail_pct_day", 0.025),
            },
            [RiskTier.Swing] = new()
            {
                StopLoss = weights.GetValueOrDefault("risk_sl_swing", 0.08) * stopMultiplier,
                TakeProfit = weights.GetValueOrDefault("risk_tp_swing", 0.15),
                TrailActivate = weights.GetValueOrDefault("risk_trail_activate_swing", 0.10),
                TrailPercent = weights.GetValueOrDefault("risk_trail_pct_swing", 0.05),
            },
            [RiskTier.LongTerm] = new()
            {
                StopLoss = weights.GetValueOrDefault("risk_sl_longterm", 0.15) * stopMultiplier,
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
            var candidateMap = await _candidateRepo.GetCandidateMapByPredictionIdsAsync(predictionIds);

            foreach (var pos in openPositions)
            {
                result.PositionsChecked++;

                // ── Skip options: we fetch STOCK quotes, not option quotes.
                // Comparing a $55 stock price against a $1.05 option premium
                // produces absurd P&L (5,000%+) and triggers instant take-profit.
                // Options need their own pricing path (not yet implemented).
                if (pos.AssetType == PositionAssetType.option)
                    continue;

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
                        // Trail floor = high-water mark minus trail percent.
                        // Guard: never let trail floor drop below entry price — if
                        // trail_pct is misconfigured wider than trail_activate, the raw
                        // floor could be below entry, turning a "locked-in gain" into a loss.
                        var trailFloor = Math.Max(
                            hwm * (1 - limits.TrailPercent),
                            pos.EntryPrice * 1.001); // guarantee at least +0.1% gain
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
    /// Pre-compute EV% from a stock candidate's target/stop/entry/confidence
    /// so we can sort candidates by profit potential.
    /// </summary>
    private static double ComputeEvPercent(PaperStockCandidate c)
    {
        if (c.TargetPrice is not > 0 || c.StopPrice is not > 0 || c.EntryPrice is not > 0)
            return 0;
        var winProb = c.ConfidenceScore / 100.0;
        var gainPct = Math.Abs(c.TargetPrice.Value - c.EntryPrice.Value) / c.EntryPrice.Value * 100;
        var lossPct = Math.Abs(c.EntryPrice.Value - c.StopPrice.Value) / c.EntryPrice.Value * 100;
        return (winProb * gainPct) - ((1 - winProb) * lossPct);
    }

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
