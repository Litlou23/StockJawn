using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.OptionsLab;

/// <summary>
/// Automatically generates theoretical option strategy scenarios from
/// an existing prediction + real underlying market data.
///
/// No manual entry required for the default flow.
/// No fake option contracts, premiums, IV, Greeks, or chain data.
/// All values are theoretical estimates clearly labeled as such.
/// </summary>
public class AutomaticScenarioGenerator
{
    private readonly ResearchRepository _repo;
    private readonly MarketDataService _marketData;
    private readonly ILogger<AutomaticScenarioGenerator> _logger;

    private static readonly (string Key, string Label, int Days)[] Durations =
    [
        ("1_week", "1 Week", 7),
        ("2_week", "2 Weeks", 14),
        ("3_week", "3 Weeks", 21),
    ];

    public AutomaticScenarioGenerator(
        ResearchRepository repo,
        MarketDataService marketData,
        ILogger<AutomaticScenarioGenerator> logger)
    {
        _repo = repo;
        _marketData = marketData;
        _logger = logger;
    }

    public async Task<OptionsScenarioResponse?> GenerateScenariosAsync(OptionsScenarioRequest request)
    {
        // ── 1. Load prediction ───────────────────────────────
        var predictions = await _repo.GetRecentPredictionsAsync(200);
        var pred = predictions.FirstOrDefault(p => p.Id == request.PredictionId);
        if (pred is null)
        {
            _logger.LogWarning("[options-lab] Prediction {Id} not found", request.PredictionId);
            return null;
        }

        // ── 2. Load real market data ─────────────────────────
        var bars = await _marketData.GetRecentBarsAsync(pred.Ticker, 30);
        var quote = await _marketData.GetQuoteAsync(pred.Ticker);

        var currentPrice = quote?.Price ?? pred.EntryReferencePrice ?? 0;
        if (currentPrice <= 0)
        {
            return new OptionsScenarioResponse
            {
                PredictionId = pred.Id,
                Ticker = pred.Ticker,
                PredictionDirection = pred.PredictionType.ToString(),
                Warnings = ["Real underlying stock price unavailable. Cannot generate scenarios."],
            };
        }

        var snapshotBars = bars.Select(b => new MarketSnapshotBar
        {
            Date = b.Date, Open = b.Open, High = b.High,
            Low = b.Low, Close = b.Close, Volume = b.Volume,
        }).ToList();

        // ── 3. Calculate realized volatility ─────────────────
        var realizedVol = request.OverrideIv
            ?? RealizedVolatilityCalculator.Calculate(snapshotBars);
        if (realizedVol <= 0) realizedVol = 0.30; // safe fallback, labeled

        var atr = RealizedVolatilityCalculator.CalculateATR(snapshotBars);
        var avgDailyMove = RealizedVolatilityCalculator.AverageDailyMovePercent(snapshotBars);
        var riskFreeRate = request.OverrideRiskFreeRate ?? 0.05;

        // ── 4. Load outcome if available ─────────────────────
        var outcomes = await _repo.GetRecentOutcomesAsync(200);
        var outcome = outcomes.FirstOrDefault(o => o.PredictionId == pred.Id);
        var endingPrice = outcome?.ClosePrice;

        // ── 5. Load historical signal performance ────────────
        var signalPerf = await _repo.GetAllSignalPerformanceAsync();
        var avgAccuracy = signalPerf.Count > 0
            ? signalPerf.Where(s => s.TotalPredictions > 0).Select(s => s.Accuracy).DefaultIfEmpty(0).Average()
            : 0;

        var marketContext = new ScenarioMarketContext
        {
            RealizedVolatility = Math.Round(realizedVol, 4),
            RealizedVolatilityLabel = request.OverrideIv.HasValue
                ? "User-provided volatility override."
                : "Realized volatility proxy — calculated from recent underlying price bars, not options implied volatility.",
            AverageTrueRange = atr > 0 ? Math.Round(atr, 2) : null,
            AverageDailyMovePercent = avgDailyMove > 0 ? Math.Round(avgDailyMove, 4) : null,
            BarsUsed = snapshotBars.Count,
            AssumedRiskFreeRate = riskFreeRate,
        };

        // ── 6. Generate scenarios for each duration ──────────
        var direction = pred.PredictionType.ToString();
        var scenarios = new List<OptionsScenarioCard>();

        foreach (var (key, label, days) in Durations)
        {
            var expectedMove = request.OverrideExpectedMove
                ?? RealizedVolatilityCalculator.EstimateExpectedMove(currentPrice, realizedVol, days);

            marketContext.EstimatedExpectedMovePercent = Math.Round(expectedMove / currentPrice * 100, 2);

            var generated = GenerateScenariosForDuration(
                pred, currentPrice, endingPrice, expectedMove,
                realizedVol, riskFreeRate, days, key, label,
                direction, avgAccuracy);

            scenarios.AddRange(generated);
        }

        // ── 7. Rank and recommend ────────────────────────────
        var ranked = ScenarioRankingService.RankAndRecommend(
            scenarios, pred, currentPrice, endingPrice, avgAccuracy);

        var response = new OptionsScenarioResponse
        {
            PredictionId = pred.Id,
            Ticker = pred.Ticker,
            PredictionDirection = direction,
            PredictionConfidence = pred.ConfidenceScore,
            PredictionRisk = pred.RiskScore,
            StartingStockPrice = currentPrice,
            EndingStockPrice = endingPrice,
            MarketContext = marketContext,
            Scenarios = ranked,
            RecommendedScenarioId = ranked.FirstOrDefault(s => s.Recommended)?.ScenarioId,
            Warnings =
            [
                "THEORETICAL SIMULATION ONLY — These are not real option quotes.",
                "Real options-chain data, bid/ask, IV, Greeks, open interest, volume, and liquidity are not connected.",
                "Premiums are theoretical estimates using a simplified pricing model with realized volatility proxy.",
            ],
        };

        _logger.LogInformation(
            "[options-lab] Generated {Count} scenarios for {Ticker} ({Direction}), recommended: {Rec}",
            scenarios.Count, pred.Ticker, direction,
            response.RecommendedScenarioId ?? "none");

        return response;
    }

    // -----------------------------------------------------------------------
    // Scenario generation per duration
    // -----------------------------------------------------------------------

    private static List<OptionsScenarioCard> GenerateScenariosForDuration(
        PredictionCandidate pred,
        double currentPrice, double? endingPrice, double expectedMove,
        double vol, double riskFreeRate, int days,
        string durationKey, string durationLabel,
        string direction, double historicalAccuracy)
    {
        var cards = new List<OptionsScenarioCard>();
        var isHighConf = pred.ConfidenceScore >= 60;
        var isStrongMove = expectedMove / currentPrice > 0.03;

        switch (direction)
        {
            case "bullish":
                // Always generate bull call spread
                cards.Add(BuildBullCallSpread(
                    currentPrice, endingPrice, expectedMove, vol, riskFreeRate,
                    days, durationKey, durationLabel, pred, historicalAccuracy, aggressive: false));

                // Generate long call for high confidence + strong expected move
                if (isHighConf && isStrongMove)
                {
                    cards.Add(BuildLongCall(
                        currentPrice, endingPrice, expectedMove, vol, riskFreeRate,
                        days, durationKey, durationLabel, pred, historicalAccuracy));
                }
                else
                {
                    // Still show it but mark it as less suitable
                    var lc = BuildLongCall(
                        currentPrice, endingPrice, expectedMove, vol, riskFreeRate,
                        days, durationKey, durationLabel, pred, historicalAccuracy);
                    lc.RiskWarnings.Add("Long call has higher risk — prediction confidence or expected move may not justify the premium.");
                    cards.Add(lc);
                }
                break;

            case "bearish":
                cards.Add(BuildBearPutSpread(
                    currentPrice, endingPrice, expectedMove, vol, riskFreeRate,
                    days, durationKey, durationLabel, pred, historicalAccuracy, aggressive: false));

                if (isHighConf && isStrongMove)
                {
                    cards.Add(BuildLongPut(
                        currentPrice, endingPrice, expectedMove, vol, riskFreeRate,
                        days, durationKey, durationLabel, pred, historicalAccuracy));
                }
                else
                {
                    var lp = BuildLongPut(
                        currentPrice, endingPrice, expectedMove, vol, riskFreeRate,
                        days, durationKey, durationLabel, pred, historicalAccuracy);
                    lp.RiskWarnings.Add("Long put has higher risk — prediction confidence or expected move may not justify the premium.");
                    cards.Add(lp);
                }
                break;

            case "neutral":
            case "watch_only":
                cards.Add(BuildIronCondor(
                    currentPrice, endingPrice, expectedMove, vol, riskFreeRate,
                    days, durationKey, durationLabel, pred, historicalAccuracy));
                break;
        }

        return cards;
    }

    // -----------------------------------------------------------------------
    // Strategy builders
    // -----------------------------------------------------------------------

    private static OptionsScenarioCard BuildLongCall(
        double price, double? endPrice, double expectedMove,
        double vol, double rfr, int days, string durKey, string durLabel,
        PredictionCandidate pred, double histAcc)
    {
        var strike = StrikeGenerator.AtmStrike(price);
        var premium = StrategyPayoffCalculator.EstimateTheoreticalPremium(
            price, strike, vol, rfr, days, isCall: true);

        var breakeven = strike + premium;
        var targetPrice = endPrice ?? (price + expectedMove);
        var payoffAtTarget = Math.Max(targetPrice - strike, 0) - premium;
        var returnPct = premium > 0 ? payoffAtTarget / premium * 100 : 0;

        return new OptionsScenarioCard
        {
            ScenarioId = $"long_call_{durKey}",
            Duration = durKey,
            DurationLabel = durLabel,
            DaysToExpiration = days,
            StrategyType = OptionsStrategyType.long_call_proxy,
            DirectionBias = "bullish",
            StartingStockPrice = price,
            GeneratedStrikes = new ScenarioStrikes { StrikePrice = strike },
            EstimatedTheoreticalPremium = Math.Round(premium, 2),
            Breakevens = [Math.Round(breakeven, 2)],
            MaxProfit = -1, // unlimited
            MaxLoss = Math.Round(premium, 2),
            EstimatedPayoffIfPredictionHits = Math.Round(payoffAtTarget, 2),
            EstimatedReturnPercent = Math.Round(returnPct, 2),
            RiskRewardSummary = $"Risk ${premium:F2} to profit if {pred.Ticker} rises above ${breakeven:F2}. Target based on {(endPrice.HasValue ? "actual outcome" : "expected move")}.",
            ConfidenceFitScore = CalculateConfidenceFit(pred, OptionsStrategyType.long_call_proxy, histAcc),
            WhyThisScenarioWasGenerated = $"Bullish prediction with {durLabel} horizon. Long call benefits from strong upward moves.",
            Warnings = StandardWarnings(),
        };
    }

    private static OptionsScenarioCard BuildLongPut(
        double price, double? endPrice, double expectedMove,
        double vol, double rfr, int days, string durKey, string durLabel,
        PredictionCandidate pred, double histAcc)
    {
        var strike = StrikeGenerator.AtmStrike(price);
        var premium = StrategyPayoffCalculator.EstimateTheoreticalPremium(
            price, strike, vol, rfr, days, isCall: false);

        var breakeven = strike - premium;
        var targetPrice = endPrice ?? (price - expectedMove);
        var payoffAtTarget = Math.Max(strike - targetPrice, 0) - premium;
        var returnPct = premium > 0 ? payoffAtTarget / premium * 100 : 0;

        return new OptionsScenarioCard
        {
            ScenarioId = $"long_put_{durKey}",
            Duration = durKey,
            DurationLabel = durLabel,
            DaysToExpiration = days,
            StrategyType = OptionsStrategyType.long_put_proxy,
            DirectionBias = "bearish",
            StartingStockPrice = price,
            GeneratedStrikes = new ScenarioStrikes { StrikePrice = strike },
            EstimatedTheoreticalPremium = Math.Round(premium, 2),
            Breakevens = [Math.Round(breakeven, 2)],
            MaxProfit = Math.Round(strike - premium, 2),
            MaxLoss = Math.Round(premium, 2),
            EstimatedPayoffIfPredictionHits = Math.Round(payoffAtTarget, 2),
            EstimatedReturnPercent = Math.Round(returnPct, 2),
            RiskRewardSummary = $"Risk ${premium:F2} to profit if {pred.Ticker} falls below ${breakeven:F2}.",
            ConfidenceFitScore = CalculateConfidenceFit(pred, OptionsStrategyType.long_put_proxy, histAcc),
            WhyThisScenarioWasGenerated = $"Bearish prediction with {durLabel} horizon. Long put benefits from strong downward moves.",
            Warnings = StandardWarnings(),
        };
    }

    private static OptionsScenarioCard BuildBullCallSpread(
        double price, double? endPrice, double expectedMove,
        double vol, double rfr, int days, string durKey, string durLabel,
        PredictionCandidate pred, double histAcc, bool aggressive)
    {
        var (lower, upper) = StrikeGenerator.BullCallSpreadStrikes(price, expectedMove, aggressive);
        var longPremium = StrategyPayoffCalculator.EstimateTheoreticalPremium(price, lower, vol, rfr, days, true);
        var shortPremium = StrategyPayoffCalculator.EstimateTheoreticalPremium(price, upper, vol, rfr, days, true);
        var netDebit = Math.Max(longPremium - shortPremium, 0.01);

        var maxProfit = upper - lower - netDebit;
        var breakeven = lower + netDebit;

        var targetPrice = endPrice ?? (price + expectedMove);
        var payoff = Math.Max(targetPrice - lower, 0) - Math.Max(targetPrice - upper, 0) - netDebit;
        var returnPct = netDebit > 0 ? payoff / netDebit * 100 : 0;

        return new OptionsScenarioCard
        {
            ScenarioId = $"bull_call_spread_{durKey}",
            Duration = durKey,
            DurationLabel = durLabel,
            DaysToExpiration = days,
            StrategyType = OptionsStrategyType.bull_call_spread_proxy,
            DirectionBias = "bullish",
            StartingStockPrice = price,
            GeneratedStrikes = new ScenarioStrikes { LowerCallStrike = lower, UpperCallStrike = upper },
            EstimatedTheoreticalPremium = Math.Round(netDebit, 2),
            NetDebit = Math.Round(netDebit, 2),
            Breakevens = [Math.Round(breakeven, 2)],
            MaxProfit = Math.Round(maxProfit, 2),
            MaxLoss = Math.Round(netDebit, 2),
            EstimatedPayoffIfPredictionHits = Math.Round(payoff, 2),
            EstimatedReturnPercent = Math.Round(returnPct, 2),
            RiskRewardSummary = $"Risk ${netDebit:F2} to make up to ${maxProfit:F2}. Breakeven at ${breakeven:F2}. Capped risk and reward.",
            ConfidenceFitScore = CalculateConfidenceFit(pred, OptionsStrategyType.bull_call_spread_proxy, histAcc),
            WhyThisScenarioWasGenerated = $"Bullish prediction with {durLabel} horizon. Bull call spread limits risk while capturing upside to ${upper:F2}.",
            Warnings = StandardWarnings(),
        };
    }

    private static OptionsScenarioCard BuildBearPutSpread(
        double price, double? endPrice, double expectedMove,
        double vol, double rfr, int days, string durKey, string durLabel,
        PredictionCandidate pred, double histAcc, bool aggressive)
    {
        var (upper, lower) = StrikeGenerator.BearPutSpreadStrikes(price, expectedMove, aggressive);
        var longPremium = StrategyPayoffCalculator.EstimateTheoreticalPremium(price, upper, vol, rfr, days, false);
        var shortPremium = StrategyPayoffCalculator.EstimateTheoreticalPremium(price, lower, vol, rfr, days, false);
        var netDebit = Math.Max(longPremium - shortPremium, 0.01);

        var maxProfit = upper - lower - netDebit;
        var breakeven = upper - netDebit;

        var targetPrice = endPrice ?? (price - expectedMove);
        var payoff = Math.Max(upper - targetPrice, 0) - Math.Max(lower - targetPrice, 0) - netDebit;
        var returnPct = netDebit > 0 ? payoff / netDebit * 100 : 0;

        return new OptionsScenarioCard
        {
            ScenarioId = $"bear_put_spread_{durKey}",
            Duration = durKey,
            DurationLabel = durLabel,
            DaysToExpiration = days,
            StrategyType = OptionsStrategyType.bear_put_spread_proxy,
            DirectionBias = "bearish",
            StartingStockPrice = price,
            GeneratedStrikes = new ScenarioStrikes { UpperPutStrike = upper, LowerPutStrike = lower },
            EstimatedTheoreticalPremium = Math.Round(netDebit, 2),
            NetDebit = Math.Round(netDebit, 2),
            Breakevens = [Math.Round(breakeven, 2)],
            MaxProfit = Math.Round(maxProfit, 2),
            MaxLoss = Math.Round(netDebit, 2),
            EstimatedPayoffIfPredictionHits = Math.Round(payoff, 2),
            EstimatedReturnPercent = Math.Round(returnPct, 2),
            RiskRewardSummary = $"Risk ${netDebit:F2} to make up to ${maxProfit:F2}. Breakeven at ${breakeven:F2}. Capped risk and reward.",
            ConfidenceFitScore = CalculateConfidenceFit(pred, OptionsStrategyType.bear_put_spread_proxy, histAcc),
            WhyThisScenarioWasGenerated = $"Bearish prediction with {durLabel} horizon. Bear put spread limits risk while capturing downside to ${lower:F2}.",
            Warnings = StandardWarnings(),
        };
    }

    private static OptionsScenarioCard BuildIronCondor(
        double price, double? endPrice, double expectedMove,
        double vol, double rfr, int days, string durKey, string durLabel,
        PredictionCandidate pred, double histAcc)
    {
        var (longPut, shortPut, shortCall, longCall) =
            StrikeGenerator.IronCondorStrikes(price, expectedMove);

        var shortPutPrem = StrategyPayoffCalculator.EstimateTheoreticalPremium(price, shortPut, vol, rfr, days, false);
        var longPutPrem = StrategyPayoffCalculator.EstimateTheoreticalPremium(price, longPut, vol, rfr, days, false);
        var shortCallPrem = StrategyPayoffCalculator.EstimateTheoreticalPremium(price, shortCall, vol, rfr, days, true);
        var longCallPrem = StrategyPayoffCalculator.EstimateTheoreticalPremium(price, longCall, vol, rfr, days, true);

        var netCredit = Math.Max((shortPutPrem - longPutPrem) + (shortCallPrem - longCallPrem), 0.01);
        var wingWidth = Math.Max(shortPut - longPut, longCall - shortCall);
        var maxLoss = wingWidth - netCredit;
        var lowerBe = shortPut - netCredit;
        var upperBe = shortCall + netCredit;

        var targetPrice = endPrice ?? price; // neutral = stock stays flat
        // Iron condor payoff
        var putSide = Math.Max(longPut - targetPrice, 0) - Math.Max(shortPut - targetPrice, 0);
        var callSide = Math.Max(targetPrice - longCall, 0) - Math.Max(targetPrice - shortCall, 0);
        var payoff = netCredit + putSide + callSide;
        var returnPct = maxLoss > 0 ? payoff / maxLoss * 100 : 0;

        return new OptionsScenarioCard
        {
            ScenarioId = $"iron_condor_{durKey}",
            Duration = durKey,
            DurationLabel = durLabel,
            DaysToExpiration = days,
            StrategyType = OptionsStrategyType.iron_condor_proxy,
            DirectionBias = "neutral",
            StartingStockPrice = price,
            GeneratedStrikes = new ScenarioStrikes
            {
                LongPutStrike = longPut, ShortPutStrike = shortPut,
                ShortCallStrike = shortCall, LongCallStrike = longCall,
            },
            EstimatedTheoreticalPremium = Math.Round(netCredit, 2),
            NetCredit = Math.Round(netCredit, 2),
            Breakevens = [Math.Round(lowerBe, 2), Math.Round(upperBe, 2)],
            MaxProfit = Math.Round(netCredit, 2),
            MaxLoss = Math.Round(maxLoss, 2),
            EstimatedPayoffIfPredictionHits = Math.Round(payoff, 2),
            EstimatedReturnPercent = Math.Round(returnPct, 2),
            RiskRewardSummary = $"Collect ${netCredit:F2} if {pred.Ticker} stays between ${shortPut:F2} and ${shortCall:F2}. Max risk ${maxLoss:F2}.",
            ConfidenceFitScore = CalculateConfidenceFit(pred, OptionsStrategyType.iron_condor_proxy, histAcc),
            WhyThisScenarioWasGenerated = $"Neutral prediction with {durLabel} horizon. Iron condor profits from low movement within range.",
            Warnings = StandardWarnings(),
        };
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static int CalculateConfidenceFit(
        PredictionCandidate pred, OptionsStrategyType strategy, double histAcc)
    {
        int score = 50; // base

        // Direction match
        if (PredictionStrategyMapper.DirectionMatchesStrategy(pred.PredictionType.ToString(), strategy))
            score += 20;

        // Confidence boost
        score += pred.ConfidenceScore / 5; // 0-20 points

        // Penalize high risk
        score -= pred.RiskScore / 10; // 0-10 penalty

        // Spreads get a safety bonus for moderate confidence
        if (strategy is OptionsStrategyType.bull_call_spread_proxy or OptionsStrategyType.bear_put_spread_proxy)
        {
            if (pred.ConfidenceScore < 60) score += 10; // spreads are better for moderate confidence
        }

        // Historical accuracy bonus
        if (histAcc > 0.6) score += 10;
        else if (histAcc < 0.4 && histAcc > 0) score -= 10;

        return Math.Clamp(score, 0, 100);
    }

    private static List<string> StandardWarnings() =>
    [
        "THEORETICAL SIMULATION ONLY — not a real option quote.",
        "Premiums estimated using simplified model with realized volatility proxy.",
        "Actual option performance may differ due to real IV, time decay, and market conditions.",
    ];
}
