using System.Text.Json;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Portfolio;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.TradeDecision;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.UniverseDiscovery;

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
    private readonly IOpenAiCompletionService _ai;
    private readonly FinnhubProvider _finnhub;
    private readonly IBrokerAdapter _broker;
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
        IOpenAiCompletionService ai,
        FinnhubProvider finnhub,
        IBrokerAdapter broker,
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
        _ai = ai;
        _finnhub = finnhub;
        _broker = broker;
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
        List<string> errors,
        bool bypassTimeGate = false)
    {
        var portfolioPositionsOpened = 0;
        var activeChallenges = await _portfolioRepo.GetActiveChallengesAsync();
        if (activeChallenges.Count == 0)
            return 0;

        // ── Time-of-day gate ──
        // A real trader avoids the first 30 min after market open (9:30-10:00 AM ET).
        // The open is chaotic: wide spreads, gap fills, fake breakouts.
        // Afternoon scans bypass this gate since the open volatility has settled.
        if (!bypassTimeGate)
        {
            var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern);
            var marketOpen = new TimeSpan(9, 30, 0);
            var safeEntry = new TimeSpan(10, 0, 0);
            var marketClose = new TimeSpan(16, 0, 0);

            if (nowEt.TimeOfDay >= marketOpen && nowEt.TimeOfDay < safeEntry)
            {
                _logger.LogInformation(
                    "[portfolio] Time-of-day gate: {Time} ET is within first 30 min of open — deferring {Count} candidates to afternoon scan",
                    nowEt.ToString("HH:mm"), actionableCandidates.Count);
                return 0;
            }

            // Also skip entries after market close — no point opening positions after hours
            if (nowEt.TimeOfDay >= marketClose || nowEt.TimeOfDay < marketOpen)
            {
                // Weekend/after-hours: let it through — the morning scan runs pre-market
                // and positions will fill at next open. Only block the chaotic open window.
            }
        }

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
        var dailyLossLimitPct = weights.GetValueOrDefault("daily_loss_limit_pct", 0.03);
        var dailyLossLimitEnabled = weights.GetValueOrDefault("daily_loss_limit_enabled", 1.0) >= 1.0;
        var maxSpreadPct = weights.GetValueOrDefault("max_spread_pct", 0.5);
        var roundTripCostPct = weights.GetValueOrDefault("round_trip_cost_pct", 0.15);
        var minBearishConfidence = (int)weights.GetValueOrDefault("min_bearish_confidence", 55);
        var bearishAllowed = weights.GetValueOrDefault("bearish_portfolio_allowed", 0.0) >= 1.0;
        var aiEntryGateEnabled = weights.GetValueOrDefault("ai_entry_gate_enabled", 1.0) >= 1.0;

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
                && (c.CandidateMode == CandidateMode.live_eligible
                    || c.CandidateMode == CandidateMode.actionable_shadow) // Trade both live-eligible and shadow candidates
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
        // Prioritize 3_day timeframe (86.7% win rate at high confidence) over longer holds.
        // Within each tier, sort by EV then confidence.
        eligible = eligible
            .OrderByDescending(c => c.Timeframe == StockTimeframe.three_day ? 1 : 0) // 3_day first
            .ThenByDescending(c => ComputeEvPercent(c))
            .ThenByDescending(c => c.ConfidenceScore)
            .ToList();

        // ── Same-day earnings guard — fetch once, reuse for all challenges ──
        // A stock reporting earnings today is a coin flip. The post-earnings gap
        // can be 5-20% in either direction regardless of the prediction's thesis.
        // AMAT on Aug 14 is the poster child: entered intraday, reported after close,
        // stock gapped down $44 and took two positions with it.
        var earningsToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var earningsEntries = await _finnhub.GetUpcomingEarningsAsync(daysAhead: 1);
            var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
            foreach (var e in earningsEntries.Where(e => e.Date == todayStr))
                earningsToday.Add(e.Ticker);

            if (earningsToday.Count > 0)
                _logger.LogInformation(
                    "[portfolio] Earnings guard: {Count} tickers reporting today — will block broker entries for: {Sample}",
                    earningsToday.Count,
                    string.Join(", ", earningsToday.Take(20)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[portfolio] Earnings calendar fetch failed — proceeding without earnings guard");
        }

        var filteredByConfidence = actionableCandidates.Count(c => c.IsActionable
            && c.Status == PaperStockStatus.open
            && c.EntryPrice is > 0
            && PredictionCategoryHelper.IsDirectional(c.PredictionType)
            && c.ConfidenceScore < minConfidence);

        if (filteredByConfidence > 0)
            _logger.LogInformation(
                "[portfolio] Filtered out {Count} candidates below confidence threshold {Min}",
                filteredByConfidence, minConfidence);

        // ── Cross-challenge cooldown & blacklist — built once, shared across all challenges ──
        // BX bug: ticker stopped out in Stock Growth (paper) then immediately re-entered in
        // Broker Paper Trading because cooldown only checked the current challenge's history.
        // Fix: aggregate stop-loss exits and repeat losers across ALL challenges.
        var globalCooldownCutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var globalBlacklistDays = (int)weights.GetValueOrDefault("repeat_loser_blacklist_days", 30);
        var globalBlacklistCutoff = DateTimeOffset.UtcNow.AddDays(-globalBlacklistDays);
        var globalStoppedOutTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalRepeatLoserTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allRecentClosedPositions = new List<PortfolioPosition>();

        foreach (var ch in activeChallenges)
        {
            var closed = await _portfolioRepo.GetClosedPositionsAsync(ch.Id, limit: 100);
            allRecentClosedPositions.AddRange(closed);
        }

        foreach (var p in allRecentClosedPositions
            .Where(p => p.ExitDate.HasValue && p.ExitDate.Value >= globalCooldownCutoff)
            .Where(p => p.ReasonExited is not null
                && (p.ReasonExited.StartsWith("STOP-LOSS", StringComparison.OrdinalIgnoreCase)
                 || p.ReasonExited.StartsWith("TRAILING-STOP", StringComparison.OrdinalIgnoreCase))))
        {
            globalStoppedOutTickers.Add(p.Ticker);
        }

        foreach (var ticker in allRecentClosedPositions
            .Where(p => p.ExitDate.HasValue && p.ExitDate.Value >= globalBlacklistCutoff && p.ProfitLoss < 0)
            .GroupBy(p => p.Ticker, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key))
        {
            globalRepeatLoserTickers.Add(ticker);
        }

        if (globalStoppedOutTickers.Count > 0)
            _logger.LogInformation(
                "[portfolio] Cross-challenge stop-loss cooldown: {Tickers}",
                string.Join(", ", globalStoppedOutTickers));
        if (globalRepeatLoserTickers.Count > 0)
            _logger.LogInformation(
                "[portfolio] Cross-challenge repeat loser blacklist: {Tickers} (2+ losses in {Days} days)",
                string.Join(", ", globalRepeatLoserTickers), globalBlacklistDays);

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

            // ── Daily loss limit — stop trading on bad days ──
            // Real scalpers cap daily losses at 2-3% of account equity.
            // If today's realized losses exceed the limit, stop opening new positions
            // for the rest of the day — don't compound losses.
            if (dailyLossLimitEnabled && dailyLossLimitPct > 0)
            {
                var todayStart = DateTime.UtcNow.Date;
                var closedToday = await _portfolioRepo.GetClosedPositionsAsync(challenge.Id, limit: 100);
                var todaysClosedLosses = closedToday
                    .Where(p => p.ExitDate.HasValue && p.ExitDate.Value.UtcDateTime.Date == todayStart)
                    .Where(p => p.ProfitLoss is < 0)
                    .Sum(p => p.ProfitLoss ?? 0);

                var dailyLossLimit = challenge.CurrentBalance * dailyLossLimitPct;
                if (Math.Abs(todaysClosedLosses) >= dailyLossLimit)
                {
                    _logger.LogWarning(
                        "[portfolio] DAILY LOSS LIMIT: challenge {Name} lost ${Loss:F2} today " +
                        "(limit ${Limit:F2} = {Pct:P0} of ${Balance:F2}). No new trades until tomorrow.",
                        challenge.Name, Math.Abs(todaysClosedLosses), dailyLossLimit,
                        dailyLossLimitPct, challenge.CurrentBalance);
                    continue;
                }
            }

            // ── Stop-loss cooldown & repeat loser blacklist ──
            // Now cross-challenge (built above the loop). A ticker stopped out in ANY
            // challenge is blocked from ALL challenges for 24h. Repeat losers (2+ losses
            // across any challenge in the blacklist window) are blocked everywhere.
            var stoppedOutTickers = globalStoppedOutTickers;
            var repeatLoserTickers = globalRepeatLoserTickers;
            var blacklistDays = globalBlacklistDays;

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
            // For broker challenges, track tickers opened this run to prevent
            // duplicate positions on the same stock from different profiles.
            // Paper challenges allow independent profile trades.
            var brokerTickersOpened = challenge.TradingMode is TradingMode.broker_paper or TradingMode.live
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : null;

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

                // ── Broker confidence floor — real money demands higher conviction ──
                // Paper trades can experiment at conf=55, but broker_paper/live trades
                // need conf>=60 to avoid wasting capital on weak setups like CCK (51) or UCB (55).
                if (challenge.TradingMode is TradingMode.broker_paper or TradingMode.live)
                {
                    var brokerMinConf = (int)weights.GetValueOrDefault("broker_min_confidence", 60);
                    if (c.ConfidenceScore < brokerMinConf)
                    {
                        _logger.LogInformation(
                            "[portfolio] BROKER GATE: Skipping {Ticker} for {Challenge} — conf {Conf} < broker minimum {Min}",
                            c.Ticker, challenge.Name, c.ConfidenceScore, brokerMinConf);
                        continue;
                    }
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

                // Skip tickers we already hold (or just opened this run for broker challenges)
                if (openPositions.Any(p => p.Ticker == c.Ticker)
                    || (brokerTickersOpened?.Contains(c.Ticker) == true))
                {
                    _logger.LogDebug("[portfolio] Skipping {Ticker} — already held in challenge {Challenge}",
                        c.Ticker, challenge.Name);
                    continue;
                }

                // ── Stop-loss cooldown — don't re-enter tickers that just got stopped out ──
                if (stoppedOutTickers.Contains(c.Ticker))
                {
                    _logger.LogInformation(
                        "[portfolio] COOLDOWN: Skipping {Ticker} for {Challenge} — stopped out in last 24h. " +
                        "Re-entering a stock that blew through your stop is chasing a loser.",
                        c.Ticker, challenge.Name);
                    continue;
                }

                // ── Repeat loser blacklist — block tickers that keep losing ──
                if (repeatLoserTickers.Contains(c.Ticker))
                {
                    _logger.LogInformation(
                        "[portfolio] BLACKLISTED: Skipping {Ticker} for {Challenge} — 2+ losses in last {Days} days. " +
                        "Stop trading what doesn't work.",
                        c.Ticker, challenge.Name, blacklistDays);
                    continue;
                }

                // ── Same-day earnings guard — don't enter before a binary event ──
                // Only applied to broker challenges — paper challenges can experiment freely.
                if (challenge.TradingMode is TradingMode.broker_paper or TradingMode.live
                    && earningsToday.Contains(c.Ticker))
                {
                    _logger.LogInformation(
                        "[portfolio] EARNINGS GUARD: Skipping {Ticker} for {Challenge} — reports earnings today. " +
                        "Entering before a binary event is gambling, not trading.",
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
                // Also captures the live price for broker limit orders (see below).
                double? livePrice = null;
                if (maxChasePercent > 0 && c.EntryPrice is > 0)
                {
                    try
                    {
                        var currentQuote = await _marketData.GetQuoteAsync(c.Ticker);
                        if (currentQuote is not null)
                        {
                            livePrice = currentQuote.Price > 0 ? currentQuote.Price : null;
                            // ── Liquidity gate — skip illiquid micro-caps ──
                            // A real trader doesn't enter positions in stocks with
                            // no volume — you can't exit when you need to.
                            // Minimum 50K shares traded today (or avg daily volume).
                            if (currentQuote.Volume < 50_000)
                            {
                                _logger.LogInformation(
                                    "[portfolio] Skipping {Ticker} — volume {Vol:N0} < 50K minimum",
                                    c.Ticker, currentQuote.Volume);
                                continue;
                            }

                            // ── Spread estimate filter — skip wide-spread stocks ──
                            // Without L2 data, estimate spread from price and volume.
                            // Empirical formula: tighter spreads for higher-priced, higher-volume stocks.
                            // A stock with a 1% spread on a 2% target eats half the edge.
                            if (maxSpreadPct > 0 && currentQuote.Price > 0 && currentQuote.Volume > 0)
                            {
                                // Rough spread estimate: $0.01 base + inverse of sqrt(volume) scaled by price
                                // For a $50 stock with 1M volume: ~0.03% spread (tight)
                                // For a $10 stock with 50K volume: ~0.50% spread (wide)
                                var estSpreadDollars = 0.01 + (currentQuote.Price / Math.Sqrt(currentQuote.Volume) * 0.5);
                                var estSpreadPct = estSpreadDollars / currentQuote.Price * 100;

                                if (estSpreadPct > maxSpreadPct)
                                {
                                    _logger.LogInformation(
                                        "[portfolio] Skipping {Ticker} — estimated spread {Spread:F2}% > {Max}% limit " +
                                        "(price ${Price:F2}, vol {Vol:N0})",
                                        c.Ticker, estSpreadPct, maxSpreadPct, currentQuote.Price, currentQuote.Volume);
                                    continue;
                                }
                            }

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
                    // Subtracts estimated round-trip trading costs (spread + slippage buffer)
                    // from the gain side — a trade that looks +0.5% EV before costs might be
                    // -0.1% EV after costs. Real scalpers always factor in friction.
                    double? evPercent = null;
                    if (c.TargetPrice is > 0 && c.StopPrice is > 0 && c.EntryPrice is > 0)
                    {
                        var winProb = c.ConfidenceScore / 100.0;
                        var gainPct = Math.Abs(c.TargetPrice.Value - c.EntryPrice.Value) / c.EntryPrice.Value * 100;
                        var lossPct = Math.Abs(c.EntryPrice.Value - c.StopPrice.Value) / c.EntryPrice.Value * 100;
                        // Deduct round-trip costs from gain (costs eat into profit on wins,
                        // and add to losses on losses — net effect is subtracting from EV)
                        gainPct = Math.Max(0, gainPct - roundTripCostPct);
                        lossPct += roundTripCostPct;
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

                    // ── AI Trade Gate — holistic go/no-go before opening ──
                    // AI can veto marginal trades the mechanical system approves.
                    // Uses Terra model for quality. Only for broker/live modes by default,
                    // or when ai_entry_gate_enabled is set.
                    var shouldRunAiGate = aiEntryGateEnabled
                        && challenge.TradingMode != TradingMode.paper; // Skip for pure paper to save API cost
                    var aiPositionScale = 1.0;
                    if (shouldRunAiGate)
                    {
                        // Fetch AI track record for self-awareness
                        string? entryTrackRecord = null;
                        try
                        {
                            var (total, correct, wrong) = await _portfolioRepo.GetAiAccuracyStatsAsync(30);
                            if (total >= 3)
                            {
                                var accuracy = (double)correct / total;
                                entryTrackRecord = $"YOUR TRACK RECORD (last 30 days): {total} decisions, " +
                                    $"{correct} correct ({accuracy:P0}), {wrong} wrong. " +
                                    (accuracy < 0.5
                                        ? "Your recent calls have been poor — be more selective with APPROVE decisions."
                                        : accuracy > 0.7
                                            ? "Your recent calls have been strong — trust your judgment."
                                            : "Your accuracy is moderate — weigh the evidence carefully.");
                            }
                        }
                        catch { /* non-critical */ }

                        // Build lightweight portfolio context for entry gate
                        var entryCtx = $"PORTFOLIO STATE: {challenge.Name} | Cash: ${challenge.CurrentCash:F2} | " +
                                       $"Open positions: {currentOpenCount}/{maxPositions} | " +
                                       $"Recent trades: {recentClosed?.Count ?? 0} closed\n" +
                                       (openPositions.Count > 0
                                           ? "Current holdings: " + string.Join(", ", openPositions.Select(p => p.Ticker))
                                           : "No open positions.") +
                                       (stoppedOutTickers.Count > 0
                                           ? $"\nRecently stopped out (24h cooldown): {string.Join(", ", stoppedOutTickers)}"
                                           : "");
                        var aiGate = await GetAiTradeGateDecisionAsync(
                            c, livePrice ?? entryPrice, evPercent, marketRegime, aiEntryGateEnabled, entryCtx, entryTrackRecord);
                        // Persist AI decision (fire-and-forget)
                        _ = _portfolioRepo.SaveAiDecisionAsync(
                            c.PredictionId ?? c.Ticker, c.Ticker, challenge.Id,
                            "entry_gate", aiGate.Approved ? "APPROVE" : "REJECT",
                            aiGate.Reason,
                            positionScale: aiGate.PositionScale,
                            entryPrice: entryPrice, currentPrice: livePrice ?? entryPrice,
                            marketRegime: marketRegime,
                            portfolioOpenCount: currentOpenCount,
                            portfolioCash: challenge.CurrentCash);

                        if (!aiGate.Approved)
                        {
                            _logger.LogInformation(
                                "[portfolio] AI REJECTED {Ticker}: {Reason}",
                                c.Ticker, aiGate.Reason);
                            continue;
                        }
                        aiPositionScale = aiGate.PositionScale;
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
                        statsSampleSize: tradeStats.SampleSize,
                        currentMarketPrice: livePrice,
                        positionScaleOverride: aiPositionScale);

                    if (pos is not null)
                    {
                        portfolioPositionsOpened++;
                        opened++;
                        brokerTickersOpened?.Add(c.Ticker);

                        // ── Place server-side stop order on Alpaca ──
                        // Eliminates stop-loss overshoot caused by periodic price checks.
                        // Alpaca monitors the price and executes instantly when hit.
                        if (challenge.TradingMode is TradingMode.broker_paper or TradingMode.live
                            && _broker.IsConfigured && pos.BrokerEntryOrderId is not null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var tier = ClassifyTimeframe(c.Timeframe);
                                    var slPct = tier switch
                                    {
                                        RiskTier.Day => weights.GetValueOrDefault("risk_sl_day", 0.05),
                                        RiskTier.Swing => weights.GetValueOrDefault("risk_sl_swing", 0.08),
                                        _ => weights.GetValueOrDefault("risk_sl_longterm", 0.15),
                                    };
                                    var stopPrice = Math.Round(pos.EntryPrice * (1.0 - slPct), 2);

                                    var stopResult = await _broker.PlaceStopOrderAsync(new BrokerOrderRequest
                                    {
                                        Ticker = pos.Ticker,
                                        Quantity = pos.Quantity,
                                        Side = BrokerOrderSide.sell,
                                        TimeInForce = BrokerTimeInForce.gtc,
                                        ClientOrderId = $"sj-sl-{pos.Id[..8]}",
                                    }, stopPrice);

                                    if (stopResult.Success && stopResult.BrokerOrderId is not null)
                                    {
                                        await _portfolioRepo.UpdateBrokerStopOrderIdAsync(pos.Id, stopResult.BrokerOrderId);
                                        _logger.LogInformation(
                                            "[portfolio] BROKER STOP ORDER placed for {Ticker}: stop=${Stop} ({Pct:P0} below ${Entry}), orderId={OrderId}",
                                            pos.Ticker, stopPrice, slPct, pos.EntryPrice, stopResult.BrokerOrderId);
                                    }
                                    else
                                    {
                                        _logger.LogWarning(
                                            "[portfolio] BROKER STOP ORDER FAILED for {Ticker}: {Error}. " +
                                            "Periodic risk check will still enforce the stop, but with possible overshoot.",
                                            pos.Ticker, stopResult.ErrorMessage);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "[portfolio] BROKER STOP ORDER exception for {Ticker}", pos.Ticker);
                                }
                            });
                        }

                        // Backfill entry gate AI decision with actual position ID
                        // (saved with prediction_id before position existed)
                        if (shouldRunAiGate)
                        {
                            _ = _portfolioRepo.BackfillAiDecisionPositionIdAsync(
                                c.PredictionId ?? c.Ticker, pos.Id);
                        }

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
        StockTimeframe.three_day => RiskTier.Swing,
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

        // Partial profit-taking config
        var partialTpEnabled = weights.GetValueOrDefault("partial_tp_enabled", 1.0) >= 1.0;
        var partialTpFraction = Math.Clamp(weights.GetValueOrDefault("partial_tp_fraction", 0.5), 0.1, 0.9);

        // AI exit advisor: consult AI before time-stop decisions
        var aiExitEnabled = weights.GetValueOrDefault("ai_exit_enabled", 1.0) >= 1.0;

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

        // ── Regime-aware time-stop extension ──
        // On neutral/bullish days, extend time-stops so positions get room to recover
        // instead of closing early on what's probably temporary weakness.
        // On confirmed bearish days, use normal (tighter) time-stops.
        var timeStopMultiplier = 1.0;
        string? riskRegime = null;
        try
        {
            var spyQuoteTask = _marketData.GetQuoteAsync("SPY");
            var spyEmaTask = _marketData.GetEmaAsync("SPY");
            await Task.WhenAll(spyQuoteTask, spyEmaTask);

            var spyPrice = spyQuoteTask.Result?.Price;
            var spyEma = spyEmaTask.Result.Ema26;

            if (spyPrice is > 0 && spyEma is > 0)
            {
                var spyRatio = spyPrice.Value / spyEma.Value;
                if (spyRatio > 1.003)
                    riskRegime = "bullish";
                else if (spyRatio < 0.997)
                    riskRegime = "bearish";
                // else neutral

                // Neutral or bullish → extend time-stops by 2x to give positions recovery room
                // Bearish → keep normal time-stops (close losers faster)
                if (riskRegime != "bearish")
                {
                    timeStopMultiplier = weights.GetValueOrDefault("time_stop_neutral_extension", 2.0);
                    _logger.LogInformation(
                        "[risk-mgmt] Regime {Regime} — extending time-stops by {Mult:F1}x. " +
                        "Neutral days recover; let positions breathe.",
                        riskRegime ?? "neutral", timeStopMultiplier);
                }
                else
                {
                    _logger.LogInformation(
                        "[risk-mgmt] Regime bearish — using standard time-stops (close losers faster)");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[risk-mgmt] Regime check for time-stop extension failed — using defaults");
        }

        // ── Macro shock: also widen stops on days with major economic misses ──
        // If consumer sentiment crashes or retail sales misses badly, the whole
        // market dips temporarily. Widening stops avoids getting shaken out by
        // the initial reaction — stocks often recover by next session.
        var isMacroShockDay = false;
        try
        {
            var (isMacroShock, shockEvents) = await _finnhub.DetectMacroShockAsync();
            isMacroShockDay = isMacroShock;
            if (isMacroShock)
            {
                var macroStopWiden = weights.GetValueOrDefault("macro_shock_stop_multiplier", 1.5);
                stopMultiplier = Math.Max(stopMultiplier, macroStopWiden);
                // Also extend time-stops — don't close during the panic
                timeStopMultiplier = Math.Max(timeStopMultiplier, 2.0);
                _logger.LogWarning(
                    "[risk-mgmt] MACRO SHOCK: widening stops to {StopMult:F1}x, time-stops to {TimeMult:F1}x — {Events}",
                    stopMultiplier, timeStopMultiplier, string.Join(" | ", shockEvents));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[risk-mgmt] Macro shock check failed in risk management — proceeding normally");
        }

        var thresholds = new Dictionary<RiskTier, RiskThresholds>
        {
            [RiskTier.Day] = new()
            {
                StopLoss = weights.GetValueOrDefault("risk_sl_day", 0.05) * stopMultiplier,
                TakeProfit = weights.GetValueOrDefault("risk_tp_day", 0.08),
                TrailActivate = weights.GetValueOrDefault("risk_trail_activate_day", 0.04),
                TrailPercent = weights.GetValueOrDefault("risk_trail_pct_day", 0.025),
                TimeStopHours = weights.GetValueOrDefault("risk_time_stop_hours_day", 6) * timeStopMultiplier,
                TimeStopMinMove = weights.GetValueOrDefault("risk_time_stop_min_move_day", 0.005),
            },
            [RiskTier.Swing] = new()
            {
                StopLoss = weights.GetValueOrDefault("risk_sl_swing", 0.08) * stopMultiplier,
                TakeProfit = weights.GetValueOrDefault("risk_tp_swing", 0.15),
                TrailActivate = weights.GetValueOrDefault("risk_trail_activate_swing", 0.10),
                TrailPercent = weights.GetValueOrDefault("risk_trail_pct_swing", 0.05),
                TimeStopHours = weights.GetValueOrDefault("risk_time_stop_hours_swing", 72) * timeStopMultiplier,
                TimeStopMinMove = weights.GetValueOrDefault("risk_time_stop_min_move_swing", 0.008),
            },
            [RiskTier.LongTerm] = new()
            {
                StopLoss = weights.GetValueOrDefault("risk_sl_longterm", 0.15) * stopMultiplier,
                TakeProfit = 0, // no fixed take-profit for long-term — trailing stop handles it
                TrailActivate = weights.GetValueOrDefault("risk_trail_activate_longterm", 0.20),
                TrailPercent = weights.GetValueOrDefault("risk_trail_pct_longterm", 0.10),
                TimeStopHours = weights.GetValueOrDefault("risk_time_stop_hours_longterm", 168) * timeStopMultiplier,
                TimeStopMinMove = weights.GetValueOrDefault("risk_time_stop_min_move_longterm", 0.01),
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

            // ── Build portfolio context for AI decisions ──
            // Fetch recent closed trades so AI can see patterns (churning, streak, etc.)
            // Only for broker/live — don't waste API calls on pure paper
            string? portfolioCtx = null;
            string? aiTrackRecord = null;
            List<PortfolioPosition>? recentClosed = null;
            var isBrokerChallenge = challenge.TradingMode is TradingMode.broker_paper or TradingMode.live;
            var portfolioAllRed = openPositions.Count >= 2
                && openPositions.All(p => quoteMap.GetValueOrDefault(p.Ticker, p.EntryPrice) < p.EntryPrice);
            if (isBrokerChallenge && aiExitEnabled)
            {
                try
                {
                    recentClosed = await _portfolioRepo.GetClosedPositionsAsync(challenge.Id, limit: 10);
                    portfolioCtx = BuildPortfolioContext(openPositions, quoteMap, challenge, recentClosed);

                    // Fetch AI accuracy stats for self-awareness
                    var (total, correct, wrong) = await _portfolioRepo.GetAiAccuracyStatsAsync(30);
                    if (total >= 3)
                    {
                        var accuracy = (double)correct / total;
                        aiTrackRecord = $"YOUR TRACK RECORD (last 30 days): {total} decisions, " +
                                        $"{correct} correct ({accuracy:P0}), {wrong} wrong. " +
                                        (accuracy < 0.5
                                            ? "Your recent calls have been poor — be more cautious with HOLD decisions."
                                            : accuracy > 0.7
                                                ? "Your recent calls have been strong — trust your judgment."
                                                : "Your accuracy is moderate — weigh the evidence carefully.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[risk] Portfolio context build failed — AI will operate without it");
                }
            }

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

                // ── Stop-loss check — AI-gated for broker challenges ──
                // For broker/live: ask AI before closing. AI can override if this is
                // a market-wide dip (all positions red) vs stock-specific breakdown.
                // For paper: mechanical close (save API cost).
                if (limits.StopLoss > 0 && pnlPercent <= -limits.StopLoss)
                {
                    if (isBrokerChallenge && aiExitEnabled)
                    {
                        var aiStop = await GetAiStopLossOverrideAsync(
                            pos, currentPrice, pnlPercent, tier, aiExitEnabled,
                            portfolioCtx ?? "", isMacroShockDay, riskRegime, aiTrackRecord);
                        // Persist AI decision (fire-and-forget)
                        _ = _portfolioRepo.SaveAiDecisionAsync(
                            pos.Id, pos.Ticker, challenge.Id,
                            "stop_loss_override", aiStop.ShouldClose ? "EXIT" : "HOLD",
                            aiStop.AiReason,
                            entryPrice: pos.EntryPrice, currentPrice: currentPrice,
                            pnlPercent: pnlPercent,
                            hoursHeld: (DateTimeOffset.UtcNow - pos.EntryDate).TotalHours,
                            highWaterMark: pos.HighWaterMark,
                            marketRegime: riskRegime, isMacroShock: isMacroShockDay,
                            portfolioOpenCount: openPositions.Count,
                            portfolioAllRed: portfolioAllRed,
                            portfolioCash: challenge.CurrentCash);

                        if (!aiStop.ShouldClose)
                        {
                            _logger.LogWarning(
                                "[risk] AI OVERRODE stop-loss for {Ticker} at {Pnl:P1}: {Reason}",
                                pos.Ticker, pnlPercent, aiStop.AiReason);
                            result.AiOverrides++;
                            continue; // AI says hold — skip the close
                        }
                        // AI agreed to close — include AI reasoning in the exit reason
                        var reason = $"STOP-LOSS ({tier}): {pos.Ticker} down {pnlPercent:P1} " +
                                     $"(limit -{limits.StopLoss:P0}). Entry ${pos.EntryPrice:F2} → ${currentPrice:F2}" +
                                     (aiStop.AiReason is not null ? $". AI: {aiStop.AiReason}" : "");
                        await CloseWithReason(pos, currentPrice, reason);
                        result.StopLossClosed++;
                        result.ClosedTickers.Add(pos.Ticker);
                        _logger.LogWarning("[risk] {Reason}", reason);
                        continue;
                    }

                    // Paper mode — mechanical close, no AI
                    var paperReason = $"STOP-LOSS ({tier}): {pos.Ticker} down {pnlPercent:P1} " +
                                 $"(limit -{limits.StopLoss:P0}). Entry ${pos.EntryPrice:F2} → ${currentPrice:F2}";
                    await CloseWithReason(pos, currentPrice, paperReason);
                    result.StopLossClosed++;
                    result.ClosedTickers.Add(pos.Ticker);
                    _logger.LogWarning("[risk] {Reason}", paperReason);
                    continue;
                }

                // ── Take-profit check (day/swing only) ──
                // After partial TP has been taken, skip fixed TP entirely — the
                // remainder rides on the trailing stop to capture max profit.
                // A real trader takes some off the table then lets the rest run.
                if (limits.TakeProfit > 0 && pnlPercent >= limits.TakeProfit
                    && !pos.PartialProfitTaken) // Don't full-close after partial — let trailing stop manage it
                {
                    if (partialTpEnabled)
                    {
                        var reason = $"PARTIAL-TP ({tier}): {pos.Ticker} up {pnlPercent:P1} " +
                                     $"— closing {partialTpFraction:P0} at ${currentPrice:F2}. " +
                                     $"Entry ${pos.EntryPrice:F2}, remainder runs on trailing stop.";
                        try
                        {
                            await _portfolio.PartialClosePositionAsync(
                                pos, currentPrice, partialTpFraction, reason);
                            result.PartialProfitsTaken++;
                            _logger.LogInformation("[risk] {Reason}", reason);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[risk] Partial TP failed for {Ticker}, falling back to full close",
                                pos.Ticker);
                            await CloseWithReason(pos, currentPrice,
                                $"TAKE-PROFIT ({tier}): {pos.Ticker} up {pnlPercent:P1} (partial failed, full close)");
                            result.TakeProfitClosed++;
                            result.ClosedTickers.Add(pos.Ticker);
                        }
                        continue;
                    }

                    // Partial TP disabled — full close at TP level
                    var fullReason = $"TAKE-PROFIT ({tier}): {pos.Ticker} up {pnlPercent:P1} " +
                                 $"(limit +{limits.TakeProfit:P0}). Entry ${pos.EntryPrice:F2} → ${currentPrice:F2}";
                    await CloseWithReason(pos, currentPrice, fullReason);
                    result.TakeProfitClosed++;
                    result.ClosedTickers.Add(pos.Ticker);
                    _logger.LogInformation("[risk] {Reason}", fullReason);
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

                    // After partial TP, activate trailing stop immediately (already in profit)
                    // and tighten the trail slightly — we've banked half, now protect the rest
                    // but not so tight that normal fluctuations trigger it.
                    var effectiveActivate = pos.PartialProfitTaken ? 0.0 : limits.TrailActivate;
                    var effectiveTrail = pos.PartialProfitTaken
                        ? limits.TrailPercent * 0.90 // 10% tighter trail after partial TP — was 15%, too aggressive
                        : limits.TrailPercent;

                    // Check if trailing stop has been activated (price rose above activation threshold)
                    var hwmGainFromEntry = (hwm - pos.EntryPrice) / pos.EntryPrice;
                    if (hwmGainFromEntry >= effectiveActivate)
                    {
                        // Trail floor = high-water mark minus trail percent.
                        // Guard: never let trail floor drop below entry price — if
                        // trail_pct is misconfigured wider than trail_activate, the raw
                        // floor could be below entry, turning a "locked-in gain" into a loss.
                        var trailFloor = Math.Max(
                            hwm * (1 - effectiveTrail),
                            pos.EntryPrice * 1.001); // guarantee at least +0.1% gain

                        // ── Update broker stop order to match trail floor ──
                        // When the trailing stop activates or the HWM moves up, the broker-side
                        // stop should ratchet up too. This ensures Alpaca executes at the
                        // trailing floor even if our periodic check is late.
                        if (isBrokerChallenge && _broker.IsConfigured
                            && pos.BrokerStopOrderId is not null
                            && currentPrice > trailFloor)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var replaceResult = await _broker.ReplaceStopOrderAsync(
                                        pos.BrokerStopOrderId,
                                        new BrokerOrderRequest
                                        {
                                            Ticker = pos.Ticker,
                                            Quantity = pos.Quantity,
                                            Side = BrokerOrderSide.sell,
                                            TimeInForce = BrokerTimeInForce.gtc,
                                            ClientOrderId = $"sj-ts-{pos.Id[..8]}",
                                        },
                                        trailFloor);

                                    if (replaceResult.Success && replaceResult.BrokerOrderId is not null)
                                    {
                                        await _portfolioRepo.UpdateBrokerStopOrderIdAsync(pos.Id, replaceResult.BrokerOrderId);
                                        _logger.LogInformation(
                                            "[risk] BROKER STOP UPDATED for {Ticker}: new stop=${Stop} (trail floor, peak=${Peak})",
                                            pos.Ticker, trailFloor, hwm);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "[risk] Failed to update broker stop for {Ticker}", pos.Ticker);
                                }
                            });
                        }

                        if (currentPrice <= trailFloor)
                        {
                            var suffix = pos.PartialProfitTaken ? " [post-partial, tightened trail]" : "";
                            var reason = $"TRAILING-STOP ({tier}): {pos.Ticker} fell to ${currentPrice:F2} " +
                                         $"below trail floor ${trailFloor:F2} (peak ${hwm:F2}, trail {effectiveTrail:P1}). " +
                                         $"Locked in {pnlPercent:P1} gain from entry ${pos.EntryPrice:F2}{suffix}";
                            await CloseWithReason(pos, currentPrice, reason);
                            result.TrailingStopClosed++;
                            result.ClosedTickers.Add(pos.Ticker);
                            _logger.LogInformation("[risk] {Reason}", reason);
                            continue;
                        }
                    }
                }

                // ── Time stop — AI-enhanced exit decision ──
                // When a position hits the time stop threshold, ask AI whether
                // to hold or exit. AI sees the P&L, hold duration, entry reason,
                // and makes a holistic judgment. This replaces the rigid
                // mechanical rule with an intelligent exit decision.
                if (limits.TimeStopHours > 0)
                {
                    var hoursHeld = (DateTimeOffset.UtcNow - pos.EntryDate).TotalHours;
                    if (hoursHeld >= limits.TimeStopHours)
                    {
                        if (pnlPercent < limits.TimeStopMinMove)
                        {
                            // Ask AI for exit decision — pass macro context so AI knows
                            // whether this is a bad-day dip (hold) or a broken thesis (exit)
                            var aiDecision = await GetAiExitDecisionAsync(
                                pos, currentPrice, pnlPercent, hoursHeld, tier, aiExitEnabled,
                                isMacroShockDay, riskRegime, portfolioCtx, aiTrackRecord);

                            // Persist AI decision (fire-and-forget)
                            _ = _portfolioRepo.SaveAiDecisionAsync(
                                pos.Id, pos.Ticker, challenge.Id,
                                "exit_advisor", aiDecision.ShouldExit ? "EXIT" : "HOLD",
                                aiDecision.AiReason,
                                entryPrice: pos.EntryPrice, currentPrice: currentPrice,
                                pnlPercent: pnlPercent, hoursHeld: hoursHeld,
                                highWaterMark: pos.HighWaterMark,
                                marketRegime: riskRegime, isMacroShock: isMacroShockDay,
                                portfolioOpenCount: openPositions.Count,
                                portfolioAllRed: portfolioAllRed,
                                portfolioCash: challenge.CurrentCash);

                            if (aiDecision.ShouldExit)
                            {
                                var reason = $"TIME-STOP ({tier}): {pos.Ticker} held {hoursHeld:F0}h (limit {limits.TimeStopHours}h) " +
                                             $"at {pnlPercent:P1} P&L (need +{limits.TimeStopMinMove:P1} to keep). " +
                                             $"Entry ${pos.EntryPrice:F2} → ${currentPrice:F2}. " +
                                             (aiDecision.AiReason is not null
                                                 ? $"AI: {aiDecision.AiReason}"
                                                 : "Capital redeployed.");
                                await CloseWithReason(pos, currentPrice, reason);
                                result.TimeStopClosed++;
                                result.ClosedTickers.Add(pos.Ticker);
                                _logger.LogInformation("[risk] {Reason}", reason);
                                if (aiDecision.AiReason is not null) result.AiExitPositions++;
                                continue;
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "[risk] AI HOLD {Ticker}: time stop triggered but AI says hold — {Reason}",
                                    pos.Ticker, aiDecision.AiReason ?? "no reason given");
                                result.AiHeldPositions++;
                            }
                        }
                    }
                }
            }
        }

        if (result.TotalClosed > 0 || result.PartialProfitsTaken > 0)
        {
            _logger.LogWarning("[risk] Risk check complete: {Checked} positions checked, " +
                "{SL} stop-loss, {TP} take-profit, {TS} trailing-stop, {TmS} time-stop closures, " +
                "{Partial} partial profits taken, {HWM} high-water marks updated",
                result.PositionsChecked, result.StopLossClosed, result.TakeProfitClosed,
                result.TrailingStopClosed, result.TimeStopClosed, result.PartialProfitsTaken, result.HighWaterMarksUpdated);
            if (result.AiHeldPositions > 0 || result.AiExitPositions > 0)
                _logger.LogInformation("[risk] AI decisions: {AiHeld} held (overrode time-stop), {AiExit} exited",
                    result.AiHeldPositions, result.AiExitPositions);
        }
        else
            _logger.LogInformation("[risk] Risk check complete: {Checked} positions checked, no triggers hit",
                result.PositionsChecked);

        return result;
    }

    /// <summary>
    /// After risk management closes positions (stop-loss, take-profit, trailing stop),
    /// redeploy freed capital by opening new positions from available candidates.
    /// This prevents capital from sitting idle until the next morning scan.
    /// Gated by <c>intraday_reopen_enabled</c> config flag.
    /// </summary>
    public async Task<int> ReopenAfterScalpCloseAsync(int positionsClosed, HashSet<string>? closedTickers = null)
    {
        if (positionsClosed <= 0)
            return 0;

        // Check config flag
        var overrides = await _researchRepo.GetActiveWeightOverridesAsync();
        var weights = overrides.ToDictionary(o => o.SignalName, o => o.EffectiveWeight);
        var intradayReopenEnabled = weights.GetValueOrDefault("intraday_reopen_enabled", 1.0) >= 1.0;

        if (!intradayReopenEnabled)
        {
            _logger.LogDebug("[portfolio-reopen] Intraday reopening disabled via config");
            return 0;
        }

        // Fetch all open paper stock candidates (from today's morning scan)
        var openCandidates = await _candidateRepo.GetOpenCandidatesAsync();
        if (openCandidates.Count == 0)
        {
            _logger.LogInformation("[portfolio-reopen] No open candidates available for intraday reopening");
            return 0;
        }

        // Filter to actionable, directional candidates only.
        // Exclude tickers that were just closed by risk management this cycle —
        // re-entering a ticker that just hit a stop-loss is chasing a loser.
        var actionable = openCandidates
            .Where(c => c.IsActionable
                && (c.CandidateMode == CandidateMode.live_eligible
                    || c.CandidateMode == CandidateMode.actionable_shadow)
                && c.Status == PaperStockStatus.open)
            .Where(c => closedTickers is null || !closedTickers.Contains(c.Ticker))
            .ToList();

        if (actionable.Count == 0)
        {
            _logger.LogInformation("[portfolio-reopen] {Total} open candidates but none actionable", openCandidates.Count);
            return 0;
        }

        _logger.LogInformation(
            "[portfolio-reopen] {Closed} positions closed by risk mgmt — attempting reopen from {Available} actionable candidates",
            positionsClosed, actionable.Count);

        var errors = new List<string>();
        var opened = await OpenPositionsForCandidatesAsync(actionable, errors);

        if (errors.Count > 0)
            _logger.LogWarning("[portfolio-reopen] {Count} errors during reopen: {Errors}",
                errors.Count, string.Join("; ", errors.Take(5)));

        if (opened > 0)
            _logger.LogInformation("[portfolio-reopen] Opened {Count} new positions to redeploy capital", opened);
        else
            _logger.LogInformation("[portfolio-reopen] No new positions opened (all candidates filtered by guardrails)");

        return opened;
    }

    /// <summary>
    /// Afternoon opportunity scan — second pass at today's open candidates.
    /// Catches positions that were deferred by the morning open gate, or that
    /// weren't opened because slots were full (positions may have closed since).
    /// Called via pg_cron in the afternoon (~2 PM ET / 18:00 UTC).
    /// Bypasses the time-of-day gate since the chaotic open is long past.
    /// </summary>
    public async Task<int> AfternoonOpportunityScanAsync()
    {
        var openCandidates = await _candidateRepo.GetOpenCandidatesAsync();
        if (openCandidates.Count == 0)
        {
            _logger.LogInformation("[afternoon-scan] No open candidates available");
            return 0;
        }

        // Filter to actionable candidates (same criteria as reopen)
        var actionable = openCandidates
            .Where(c => c.IsActionable
                && (c.CandidateMode == CandidateMode.live_eligible
                    || c.CandidateMode == CandidateMode.actionable_shadow)
                && c.Status == PaperStockStatus.open)
            .ToList();

        if (actionable.Count == 0)
        {
            _logger.LogInformation("[afternoon-scan] {Total} open candidates but none actionable", openCandidates.Count);
            return 0;
        }

        _logger.LogInformation(
            "[afternoon-scan] Found {Actionable} actionable candidates from {Total} open — attempting to open positions",
            actionable.Count, openCandidates.Count);

        var errors = new List<string>();
        var opened = await OpenPositionsForCandidatesAsync(actionable, errors, bypassTimeGate: true);

        if (errors.Count > 0)
            _logger.LogWarning("[afternoon-scan] {Count} errors: {Errors}",
                errors.Count, string.Join("; ", errors.Take(5)));

        _logger.LogInformation("[afternoon-scan] Opened {Count} new positions in afternoon pass", opened);
        return opened;
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

    // ── Portfolio Context ────────────────────────────────────────────────
    // Shared context block that gives AI a holistic portfolio view.
    // Both entry gate, exit advisor, and stop-loss override use this
    // so AI can see "everything is red = market dip" vs "just this stock."

    private string BuildPortfolioContext(
        List<PortfolioPosition> openPositions,
        Dictionary<string, double> quoteMap,
        PortfolioChallenge challenge,
        List<PortfolioPosition>? recentClosedTrades = null)
    {
        var lines = new List<string>
        {
            $"PORTFOLIO STATE: {challenge.Name} | Cash: ${challenge.CurrentCash:F2} | " +
            $"Balance: ${challenge.CurrentBalance:F2} | Mode: {challenge.TradingMode}"
        };

        if (openPositions.Count > 0)
        {
            lines.Add($"Open positions ({openPositions.Count}):");
            var allRed = true;
            var allGreen = true;
            foreach (var p in openPositions)
            {
                var price = quoteMap.GetValueOrDefault(p.Ticker, 0);
                if (price <= 0) continue;
                var pnl = (price - p.EntryPrice) / p.EntryPrice;
                var held = (DateTimeOffset.UtcNow - p.EntryDate).TotalHours;
                lines.Add($"  {p.Ticker}: entry ${p.EntryPrice:F2} → ${price:F2} ({pnl:P1}), held {held:F0}h");
                if (pnl >= 0) allRed = false;
                if (pnl < 0) allGreen = false;
            }
            if (allRed && openPositions.Count >= 2)
                lines.Add("  ⚠ ALL positions are red — this is likely a MARKET-WIDE dip, not stock-specific weakness.");
            else if (allGreen && openPositions.Count >= 2)
                lines.Add("  All positions are green — market conditions favorable.");
        }
        else
        {
            lines.Add("No other open positions.");
        }

        if (recentClosedTrades is { Count: > 0 })
        {
            var recent = recentClosedTrades.Take(5).ToList();
            var wins = recent.Count(t => t.ProfitLoss > 0);
            var losses = recent.Count(t => t.ProfitLoss <= 0);
            lines.Add($"Recent trades (last {recent.Count}): {wins}W-{losses}L");
            foreach (var t in recent)
                lines.Add($"  {t.Ticker}: {t.ReasonExited?.Split(':')[0] ?? "unknown"} → " +
                          $"{(t.ProfitLoss >= 0 ? "+" : "")}${t.ProfitLoss:F2}");
        }

        return string.Join("\n", lines);
    }

    // ── AI Stop-Loss Override ────────────────────────────────────────────
    // Before mechanically closing a stop-loss, ask AI if this is a market-wide
    // dip (hold) or a broken thesis (exit). Only on non-bearish regime days.
    // Falls back to mechanical stop-loss if AI errors or is disabled.

    private record AiStopLossDecision(bool ShouldClose, string? AiReason);

    private async Task<AiStopLossDecision> GetAiStopLossOverrideAsync(
        PortfolioPosition pos, double currentPrice, double pnlPercent,
        RiskTier tier, bool aiEnabled, string portfolioContext,
        bool isMacroShockDay, string? regime,
        string? aiTrackRecord = null)
    {
        // Default to mechanical close if AI is disabled
        if (!aiEnabled || !_ai.IsConfigured)
            return new AiStopLossDecision(true, null);

        // On confirmed bearish days, don't override — cut losses fast
        if (regime == "bearish" && !isMacroShockDay)
            return new AiStopLossDecision(true, "Bearish regime — mechanical stop honored.");

        try
        {
            var macroLine = isMacroShockDay
                ? "A major economic release missed estimates badly today — this is a MACRO SHOCK. " +
                  "The dip is market-wide. Stocks with intact theses recover within 1-2 sessions."
                : regime == "bearish"
                    ? "Market regime is bearish (SPY below EMA)."
                    : $"Market regime is {regime ?? "neutral"}. Temporary dips often recover on neutral/bullish days.";

            var prompt = $$"""
                You are a trader managing a real $1,000 account. Stop-loss hit — EXIT or HOLD?
                The only question: which decision makes us more money going forward?

                {{pos.Ticker}} | Entry: ${{pos.EntryPrice:F2}} → Now: ${{currentPrice:F2}} | P&L: {{pnlPercent:P2}}
                Peak: ${{pos.HighWaterMark ?? pos.EntryPrice:F2}} | Tier: {{tier}}

                {{macroLine}}
                {{portfolioContext}}
                {{(aiTrackRecord ?? "")}}

                Think about P&L — is there a real path back to profit, or are we bleeding?
                - Default to EXIT. The stop triggered for a reason. Take the small loss, redeploy.
                - HOLD only if there's a real reason this bounces back:
                  1. ALL portfolio positions are red (market-wide dip, not stock-specific weakness)
                  2. It's a macro shock day OR regime is not bearish
                  3. Loss is under 4% — still recoverable
                - If this stock dropped alone while others are flat/green → EXIT. That's stock-specific.
                - If loss is over 5% → EXIT. A -$50 hit on $1,000 is huge — stop the bleeding.
                - A small loss today beats a big loss tomorrow. There's always another trade.

                Respond in JSON: {"decision": "EXIT" or "HOLD", "reason": "one sentence about the P&L math"}
                """;

            var result = await _ai.CompleteAsync(new AiCompletionRequest
            {
                Messages = [new AiChatMessageDto { Role = "user", Content = prompt }],
                ResponseFormatJson = true,
                MaxOutputTokens = 100,
                ModelOverride = 5, // Terra — real money decisions
            }, CancellationToken.None);

            if (string.IsNullOrWhiteSpace(result.Text))
                return new AiStopLossDecision(true, null);

            using var doc = JsonDocument.Parse(result.Text);
            var decision = doc.RootElement.GetProperty("decision").GetString() ?? "EXIT";
            var reason = doc.RootElement.GetProperty("reason").GetString() ?? "";

            _logger.LogInformation(
                "[ai-stop-override] {Ticker} at {Pnl:P1} — AI says {Decision}: {Reason}",
                pos.Ticker, pnlPercent, decision, reason);

            return new AiStopLossDecision(
                decision.Equals("EXIT", StringComparison.OrdinalIgnoreCase),
                reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ai-stop-override] AI stop-loss override failed for {Ticker}, defaulting to mechanical close",
                pos.Ticker);
            return new AiStopLossDecision(true, null);
        }
    }

    // ── AI Exit Advisor ──────────────────────────────────────────────────
    // When a position hits the time stop, ask AI whether to hold or exit.
    // Uses Terra model for higher-quality decisions on real money positions.
    // Gated by ai_exit_enabled config flag. Falls back to mechanical exit
    // if AI is unavailable or errors.

    private record AiExitDecision(bool ShouldExit, string? AiReason);

    private async Task<AiExitDecision> GetAiExitDecisionAsync(
        PortfolioPosition pos, double currentPrice, double pnlPercent,
        double hoursHeld, RiskTier tier, bool aiEnabled,
        bool isMacroShockDay = false, string? regime = null,
        string? portfolioContext = null, string? aiTrackRecord = null)
    {
        // Default to mechanical exit if AI is disabled or unavailable
        if (!aiEnabled || !_ai.IsConfigured)
            return new AiExitDecision(true, null);

        try
        {
            var macroContext = isMacroShockDay
                ? "MACRO CONTEXT: A major economic data release missed estimates badly today. " +
                  "The whole market is reacting — this is NOT stock-specific weakness. " +
                  "Stocks with intact theses typically recover within 1-2 sessions after macro shocks."
                : regime == "bearish"
                    ? "MACRO CONTEXT: Market regime is bearish (SPY below EMA). Be cautious about holding losers."
                    : "MACRO CONTEXT: Market regime is neutral/bullish. Temporary dips often recover.";

            var portfolioBlock = portfolioContext is not null
                ? $"\n{portfolioContext}\n"
                : "";

            var prompt = $$"""
                You are a trader managing a real $1,000 account. Time stop hit — EXIT or HOLD?
                The only question: which decision makes us more money?

                {{pos.Ticker}} | Entry: ${{pos.EntryPrice:F2}} → Now: ${{currentPrice:F2}} | P&L: {{pnlPercent:P2}}
                Held: {{hoursHeld:F0}}h | Peak: ${{pos.HighWaterMark ?? pos.EntryPrice:F2}} | Tier: {{tier}}

                {{macroContext}}
                {{portfolioBlock}}
                {{(aiTrackRecord ?? "")}}

                Think about P&L — will holding or exiting make us more money?
                - If RED and no catalyst to reverse → EXIT and redeploy that capital to something better.
                - If we were green and faded back to flat → EXIT. The move happened, take what's left.
                - If barely green (<0.5%) after 20+ hours → EXIT. That capital is doing nothing.
                - HOLD if the stock is still trending in our favor and hasn't peaked.
                - HOLD if ALL positions are red — that's a market dip, not a bad pick. It'll recover.
                - HOLD if there's a clear reason to expect more upside (catalyst, momentum).
                - Dead money is the enemy. Every dollar sitting in a stale trade is a dollar not making profit elsewhere.

                Respond in JSON: {"decision": "EXIT" or "HOLD", "reason": "one sentence about the P&L decision"}
                """;

            var result = await _ai.CompleteAsync(new AiCompletionRequest
            {
                Messages = [new AiChatMessageDto { Role = "user", Content = prompt }],
                ResponseFormatJson = true,
                MaxOutputTokens = 100,
                ModelOverride = 5, // Terra — higher quality for money decisions
            }, CancellationToken.None);

            if (string.IsNullOrWhiteSpace(result.Text))
                return new AiExitDecision(true, null);

            using var doc = JsonDocument.Parse(result.Text);
            var decision = doc.RootElement.GetProperty("decision").GetString() ?? "EXIT";
            var reason = doc.RootElement.GetProperty("reason").GetString() ?? "";

            _logger.LogInformation(
                "[ai-exit] {Ticker} — AI says {Decision}: {Reason}",
                pos.Ticker, decision, reason);

            return new AiExitDecision(
                decision.Equals("EXIT", StringComparison.OrdinalIgnoreCase),
                reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ai-exit] AI exit decision failed for {Ticker}, defaulting to mechanical exit",
                pos.Ticker);
            return new AiExitDecision(true, null);
        }
    }

    // ── AI Trade Gate ─────────────────────────────────────────────────────
    // Before opening a position, ask AI for a holistic go/no-go decision.
    // AI can veto marginal trades that mechanical scoring approves.
    // Uses Terra model for quality. Gated by ai_entry_gate_enabled config.

    private record AiTradeGateResult(bool Approved, string? Reason, double PositionScale = 1.0);

    private async Task<AiTradeGateResult> GetAiTradeGateDecisionAsync(
        PaperStockCandidate candidate, double livePrice, double? evPercent,
        string? marketRegime, bool aiEnabled,
        string? portfolioContext = null, string? aiTrackRecord = null)
    {
        if (!aiEnabled || !_ai.IsConfigured)
            return new AiTradeGateResult(true, null);

        try
        {
            var evDisplay = evPercent is not null ? $"{evPercent:F1}%" : "n/a";
            var portfolioBlock = portfolioContext is not null
                ? $"\n{portfolioContext}\n"
                : "";

            var prompt = $$"""
                You are a trader managing a real $1,000 account. One goal: MAKE MONEY.
                Our system found a trade. Will this make us profit?

                {{candidate.Ticker}} | {{candidate.WinningDirection}} | {{candidate.Timeframe}}
                Entry: ${{candidate.EntryPrice:F2}} → Target: ${{candidate.TargetPrice:F2}} | Stop: ${{candidate.StopPrice:F2}}
                Live price: ${{livePrice:F2}} | Confidence: {{candidate.ConfidenceScore}} | EV: {{evDisplay}}
                Quality: {{candidate.QualityTier}} | Regime: {{marketRegime ?? "unknown"}}

                {{portfolioBlock}}
                {{(aiTrackRecord ?? "")}}

                Think about P&L. Does the math work on this trade?
                - How many dollars can we make vs how many can we lose?
                - Is the stop tight enough that if we're wrong, the loss is small?
                - Is the target realistic — can this stock actually move that much?
                - Has the stock already made its move, or is the move still ahead?
                - Any ticker is fine — small cap, large cap — as long as the P&L makes sense.

                APPROVE if the trade has a clear path to profit with controlled downside.
                REJECT if the math doesn't work: stop too wide relative to target,
                stock already chasing, or we'd risk too much to make too little.

                Set position_scale: 1.5 high conviction, 1.0 normal, 0.5 if you like it but want smaller size.

                Respond in JSON: {"decision": "APPROVE" or "REJECT", "reason": "one sentence about the P&L math", "position_scale": 1.0}
                """;

            var result = await _ai.CompleteAsync(new AiCompletionRequest
            {
                Messages = [new AiChatMessageDto { Role = "user", Content = prompt }],
                ResponseFormatJson = true,
                MaxOutputTokens = 120,
                ModelOverride = 5, // Terra for quality decisions
            }, CancellationToken.None);

            if (string.IsNullOrWhiteSpace(result.Text))
                return new AiTradeGateResult(true, null);

            using var doc = JsonDocument.Parse(result.Text);
            var decision = doc.RootElement.GetProperty("decision").GetString() ?? "APPROVE";
            var reason = doc.RootElement.GetProperty("reason").GetString() ?? "";
            var posScale = 1.0;
            if (doc.RootElement.TryGetProperty("position_scale", out var scaleEl)
                && scaleEl.TryGetDouble(out var scaleVal))
                posScale = Math.Clamp(scaleVal, 0.5, 1.5);

            _logger.LogInformation(
                "[ai-gate] {Ticker} — AI says {Decision} (scale={Scale:F1}x): {Reason}",
                candidate.Ticker, decision, posScale, reason);

            return new AiTradeGateResult(
                decision.Equals("APPROVE", StringComparison.OrdinalIgnoreCase),
                reason, posScale);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ai-gate] AI trade gate failed for {Ticker}, defaulting to approve",
                candidate.Ticker);
            return new AiTradeGateResult(true, null);
        }
    }

    private async Task CloseWithReason(PortfolioPosition pos, double exitPrice, string reason)
    {
        // Cancel any outstanding broker stop order before closing —
        // otherwise Alpaca will try to fill the stop on a position we're
        // already closing via market order.
        if (pos.BrokerStopOrderId is not null && _broker.IsConfigured)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _broker.CancelOrderAsync(pos.BrokerStopOrderId);
                    await _portfolioRepo.UpdateBrokerStopOrderIdAsync(pos.Id, null);
                    _logger.LogInformation("[risk] Cancelled broker stop order {OrderId} for {Ticker} (closing position)",
                        pos.BrokerStopOrderId, pos.Ticker);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[risk] Failed to cancel broker stop order for {Ticker}", pos.Ticker);
                }
            });
        }

        await _portfolio.ClosePositionAsync(new ClosePositionRequest
        {
            PositionId = pos.Id,
            ExitPrice = exitPrice,
            ReasonExited = reason,
        });

        // Evaluate any prior AI decisions for this position (fire-and-forget)
        _ = EvaluateAiDecisionOutcomesAsync(pos.Id, pos.EntryPrice, exitPrice);
    }

    /// <summary>
    /// When a position closes, look up all AI decisions that said HOLD for it
    /// and evaluate whether that was the right call based on the final exit price.
    /// A HOLD decision is "correct" if the position eventually recovered to break-even
    /// or profit before closing. An EXIT decision is always "correct" if it was acted on.
    /// </summary>
    private async Task EvaluateAiDecisionOutcomesAsync(
        string positionId, double entryPrice, double exitPrice)
    {
        try
        {
            var decisions = await _portfolioRepo.GetUnevaluatedDecisionsForPositionAsync(positionId);
            if (decisions.Count == 0) return;

            var exitPnl = (exitPrice - entryPrice) / entryPrice;

            foreach (var d in decisions)
            {
                var id = d["id"]?.ToString();
                if (id is null) continue;

                var decision = d["decision"]?.ToString() ?? "";
                var decisionPrice = double.TryParse(d["current_price"]?.ToString(), out var dp) ? dp : 0;
                var decisionPnl = double.TryParse(d["pnl_percent"]?.ToString(), out var dpnl) ? dpnl : 0;

                bool correct;
                string notes;

                if (decision == "HOLD")
                {
                    // HOLD was correct if the exit price is better than the price at the time of the HOLD decision
                    correct = exitPrice > decisionPrice;
                    notes = correct
                        ? $"HOLD was correct: price recovered from ${decisionPrice:F2} to exit ${exitPrice:F2}"
                        : $"HOLD was wrong: price fell from ${decisionPrice:F2} to exit ${exitPrice:F2}";
                }
                else // EXIT or APPROVE or REJECT
                {
                    // EXIT that was acted on = always correct (it was the decision)
                    // APPROVE/REJECT for entry gate: correct if profit/loss aligns
                    correct = decision == "APPROVE" ? exitPnl > 0 : exitPnl <= 0;
                    notes = $"Entry {decision}: position closed at {exitPnl:P1}";
                }

                await _portfolioRepo.EvaluateAiDecisionAsync(
                    id, correct, exitPrice, exitPnl, notes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ai-decisions] Failed to evaluate AI outcomes for position {PositionId}",
                positionId);
        }
    }

    // ── Timeframes considered "swing" (multi-day holds) ──
    private static readonly HashSet<StockTimeframe> SwingTimeframes =
    [
        StockTimeframe.one_week,
        StockTimeframe.two_day,
        StockTimeframe.three_day,
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
    /// <summary>Max hours to hold a position before time-stop kicks in. 0 = disabled.</summary>
    public double TimeStopHours { get; init; }
    /// <summary>Minimum absolute move (as fraction, e.g. 0.005 = 0.5%) required to avoid time-stop.
    /// If position hasn't moved at least this much in either direction after TimeStopHours, close it.</summary>
    public double TimeStopMinMove { get; init; }
}

public record RiskCheckResult
{
    public int PositionsChecked { get; set; }
    public int StopLossClosed { get; set; }
    public int TakeProfitClosed { get; set; }
    public int TrailingStopClosed { get; set; }
    public int TimeStopClosed { get; set; }
    public int HighWaterMarksUpdated { get; set; }
    public int PartialProfitsTaken { get; set; }
    public int AiHeldPositions { get; set; }
    public int AiExitPositions { get; set; }
    public int AiOverrides { get; set; }
    public int TotalClosed => StopLossClosed + TakeProfitClosed + TrailingStopClosed + TimeStopClosed;
    /// <summary>Tickers closed this cycle — used to prevent immediate same-ticker reentry.</summary>
    public HashSet<string> ClosedTickers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
