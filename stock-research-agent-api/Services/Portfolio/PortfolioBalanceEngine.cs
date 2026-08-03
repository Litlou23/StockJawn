using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Portfolio;

/// <summary>
/// Manages portfolio balance state. When positions are opened or closed,
/// this engine updates cash, balance, realized P&L, and aggregate stats
/// on the portfolio challenge. The portfolio always knows exactly how
/// much cash is available.
///
/// This service is intentionally separate from the Prediction Engine.
/// The Prediction Engine finds opportunities; Portfolio AI decides
/// whether and how much to invest.
///
/// When a challenge has TradingMode = broker_paper or live, opens/closes
/// are also routed through IBrokerAdapter to place real broker orders.
/// </summary>
public class PortfolioBalanceEngine
{
    private readonly PortfolioChallengeRepository _repo;
    private readonly IBrokerAdapter _broker;
    private readonly ILogger<PortfolioBalanceEngine> _logger;

    public PortfolioBalanceEngine(
        PortfolioChallengeRepository repo,
        IBrokerAdapter broker,
        ILogger<PortfolioBalanceEngine> logger)
    {
        _repo = repo;
        _broker = broker;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Position sizing — confidence & EV-scaled approach.
    // Higher confidence and positive EV → larger position (up to risk cap).
    // Negative EV → reduced position. Risk profile sets the ceiling.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Position sizing config loaded from scoring_weight_overrides.
    /// Passed in by callers so PortfolioBalanceEngine doesn't need repo access.
    /// </summary>
    public record PositionSizingConfig(
        double MinFraction = 0.02,     // 2% floor — minimum meaningful position
        double MaxFraction = 0.20,     // 20% ceiling — never exceed even at max confidence
        double ConfidenceFloor = 35,   // confidence below this gets minFraction
        double ConfidenceCeiling = 85, // confidence at/above this gets maxFraction (for the risk profile)
        double EvBonus = 0.03,         // extra 3% allocation when EV is strongly positive (>5%)
        double EvPenalty = 0.50,       // multiply fraction by 0.5 when EV is negative
        // Volatility-adjusted sizing: scale position inversely with ATR%
        // so high-vol stocks get smaller positions and dollar-risk is consistent.
        double VolBaselineAtrPct = 2.5,  // "average" stock ATR% — positions at this level get no adjustment
        double VolMinFactor = 0.25,      // floor — even the wildest stock gets at least 25% of base size
        double VolMaxFactor = 2.0,       // ceiling — low-vol stocks get at most 2x base size
        // Fractional Kelly criterion: use real win rate + avg win/loss to compute
        // mathematically optimal position size, then scale down for safety.
        double KellyFraction = 0.25,     // quarter-Kelly — industry standard conservative fraction
        double KellyMinSampleSize = 30,  // need at least 30 outcomes for Kelly to kick in
        // Options get a higher minimum fraction — a $500 options account should
        // be willing to put $100-200 into a single position, like a real trader would.
        double OptionMinFraction = 0.30  // 30% floor for options — $150 on a $500 account
    );

    // Risk profile sets the absolute ceiling on any single position.
    private static double ProfileCap(RiskProfile riskProfile, PositionSizingConfig config) => riskProfile switch
    {
        RiskProfile.conservative => Math.Min(0.08, config.MaxFraction),
        RiskProfile.moderate => Math.Min(0.15, config.MaxFraction),
        RiskProfile.aggressive => config.MaxFraction,
        _ => Math.Min(0.10, config.MaxFraction),
    };

    /// <summary>
    /// Maximum total premium this challenge can commit to a single option
    /// contract. Uses the same risk-profile ceiling as CalculatePositionSize,
    /// so a contract that passes this budget is one the portfolio can actually
    /// open. Returns 0 when there is no cash to deploy.
    /// </summary>
    public static double CalculateMaxContractBudget(
        double cashAvailable,
        RiskProfile riskProfile,
        PositionSizingConfig? config = null)
    {
        if (cashAvailable <= 0) return 0;
        config ??= new PositionSizingConfig();
        return cashAvailable * ProfileCap(riskProfile, config);
    }

    /// <summary>
    /// Calculate how many shares/contracts to buy using fractional Kelly criterion
    /// when real stats are available, falling back to confidence-scaled sizing otherwise.
    /// Risk profile caps the maximum. ATR adjusts for volatility.
    /// Returns 0 if the position cannot be afforded.
    /// </summary>
    public double CalculatePositionSize(
        double cashAvailable,
        double pricePerUnit,
        RiskProfile riskProfile,
        PositionAssetType assetType,
        int confidence = 50,
        double? expectedValuePercent = null,
        PositionSizingConfig? config = null,
        double? atrPercent = null,
        double? winRate = null,
        double? avgWinPercent = null,
        double? avgLossPercent = null,
        int statsSampleSize = 0)
    {
        if (pricePerUnit <= 0 || cashAvailable <= 0) return 0;

        config ??= new PositionSizingConfig();

        var profileCap = ProfileCap(riskProfile, config);

        // Options get a higher minimum fraction — a real trader with $500 would put
        // $100-200 into a single option position, not $75.
        var baseMin = assetType == PositionAssetType.option
            ? Math.Max(config.MinFraction, config.OptionMinFraction)
            : config.MinFraction;

        // Ensure minFraction doesn't exceed profileCap (could happen via config override)
        var effectiveMin = Math.Min(baseMin, profileCap);

        double fraction;
        string sizingMethod;

        // ── Fractional Kelly criterion (when real stats available) ──────
        // Kelly formula: f* = (p × b - q) / b
        //   p = win probability, q = 1-p, b = avg win / avg loss (odds)
        // Then scale by KellyFraction (default 0.25 = quarter-Kelly) for safety.
        // Confidence modulates Kelly: high confidence → full fractional Kelly,
        // low confidence → scaled down toward minFraction.
        if (winRate is > 0 && avgWinPercent is > 0 && avgLossPercent is > 0
            && statsSampleSize >= config.KellyMinSampleSize)
        {
            var p = winRate.Value;
            var q = 1.0 - p;
            var b = avgWinPercent.Value / avgLossPercent.Value; // payoff ratio

            var fullKelly = (p * b - q) / b;

            // Kelly can go negative when edge is negative — clamp to 0
            var fractionalKelly = Math.Max(0, fullKelly * config.KellyFraction);

            // Confidence modulates: at ConfidenceCeiling → full fractional Kelly,
            // at ConfidenceFloor → half of fractional Kelly
            var clampedConf = Math.Clamp(confidence, config.ConfidenceFloor, config.ConfidenceCeiling);
            var confRange = config.ConfidenceCeiling - config.ConfidenceFloor;
            var confT = confRange > 0 ? (clampedConf - config.ConfidenceFloor) / confRange : 0.5;
            var confScale = 0.5 + 0.5 * confT; // range: 0.5 to 1.0

            fraction = fractionalKelly * confScale;

            // Ensure at least minFraction for any trade that passes filters
            fraction = Math.Max(fraction, effectiveMin);

            sizingMethod = $"Kelly(f*={fullKelly:F3}, frac={config.KellyFraction:F2}, confScale={confScale:F2}, n={statsSampleSize})";
        }
        else
        {
            // ── Fallback: linear confidence scaling (pre-Kelly behavior) ──
            var clampedConf = Math.Clamp(confidence, config.ConfidenceFloor, config.ConfidenceCeiling);
            var confRange = config.ConfidenceCeiling - config.ConfidenceFloor;
            var confT = confRange > 0 ? (clampedConf - config.ConfidenceFloor) / confRange : 0.5;
            fraction = effectiveMin + confT * (profileCap - effectiveMin);
            sizingMethod = "linear-confidence";
        }

        // ── Adjust for expected value ──
        var ev = expectedValuePercent ?? 0;
        if (ev > 5.0)
        {
            // Strong positive EV — bonus allocation (capped at profileCap)
            fraction = Math.Min(fraction + config.EvBonus, profileCap);
        }
        else if (ev < 0)
        {
            // Negative EV — shrink position
            fraction *= config.EvPenalty;
        }

        // ── Volatility adjustment — scale position inversely with ATR% ──
        // A stock with 5% ATR should get a smaller position than one with 2% ATR,
        // so the dollar-risk-per-trade stays roughly constant across volatility levels.
        double volFactor = 1.0;
        if (atrPercent is > 0 && config.VolBaselineAtrPct > 0)
        {
            volFactor = config.VolBaselineAtrPct / atrPercent.Value;
            volFactor = Math.Clamp(volFactor, config.VolMinFactor, config.VolMaxFactor);
            fraction *= volFactor;
        }

        // Final clamp
        fraction = Math.Clamp(fraction, effectiveMin, profileCap);

        var maxDollars = cashAvailable * fraction;

        _logger.LogInformation(
            "[sizing] {Method} conf={Confidence}, EV={EV:F1}%, ATR%={AtrPct}, volFactor={VolFactor:F2}, profile={Profile}, fraction={Fraction:P1} → ${MaxDollars:F2} of ${Cash:F2}",
            sizingMethod, confidence, ev, atrPercent?.ToString("F1") ?? "n/a", volFactor, riskProfile, fraction, maxDollars, cashAvailable);

        // Options: buy whole contracts (quantity in contracts, each = 100 shares).
        // Stocks: buy fractional shares if needed (round to 2 decimals).
        if (assetType == PositionAssetType.option)
        {
            var costPerContract = pricePerUnit * 100;
            if (costPerContract > maxDollars) return 0;
            return Math.Floor(maxDollars / costPerContract);
        }

        var shares = maxDollars / pricePerUnit;
        if (shares < 0.01) return 0;
        return Math.Round(shares, 2);
    }

    /// <summary>
    /// Open a position with auto-calculated quantity based on confidence, EV, and risk profile.
    /// When real trade stats are provided, uses fractional Kelly criterion for sizing.
    /// Used by the orchestrator for automated portfolio tracking.
    /// </summary>
    public async Task<PortfolioPosition?> AutoOpenPositionAsync(
        string portfolioId,
        string? predictionId,
        string ticker,
        double entryPrice,
        PositionAssetType assetType,
        string? reason,
        int confidence = 50,
        double? expectedValuePercent = null,
        PositionSizingConfig? sizingConfig = null,
        double? atrPercent = null,
        double? winRate = null,
        double? avgWinPercent = null,
        double? avgLossPercent = null,
        int statsSampleSize = 0)
    {
        var challenge = await _repo.GetChallengeAsync(portfolioId);
        if (challenge is null || challenge.Status != ChallengeStatus.active) return null;

        var quantity = CalculatePositionSize(
            challenge.CurrentCash, entryPrice, challenge.RiskProfile, assetType,
            confidence, expectedValuePercent, sizingConfig, atrPercent,
            winRate, avgWinPercent, avgLossPercent, statsSampleSize);

        if (quantity <= 0)
        {
            _logger.LogInformation(
                "[balance-engine] Cannot auto-open {Ticker}: price ${Price} exceeds sizing limit for ${Cash} cash",
                ticker, entryPrice, challenge.CurrentCash);
            return null;
        }

        return await OpenPositionAsync(new OpenPositionRequest
        {
            PortfolioId = portfolioId,
            PredictionId = predictionId,
            Ticker = ticker,
            AssetType = assetType.ToString(),
            EntryPrice = entryPrice,
            Quantity = quantity,
            ReasonEntered = reason,
        });
    }

    /// <summary>
    /// Open a new position: deduct dollars invested from cash, persist
    /// the position, and update the challenge balance.
    /// </summary>
    public async Task<PortfolioPosition?> OpenPositionAsync(OpenPositionRequest request)
    {
        var challenge = await _repo.GetChallengeAsync(request.PortfolioId);
        if (challenge is null)
        {
            _logger.LogWarning("[balance-engine] Challenge {Id} not found", request.PortfolioId);
            return null;
        }

        if (challenge.Status != ChallengeStatus.active)
        {
            _logger.LogWarning("[balance-engine] Challenge {Id} is not active (status={Status})",
                request.PortfolioId, challenge.Status);
            return null;
        }

        if (!Enum.TryParse<PositionAssetType>(request.AssetType, out var assetType))
            assetType = PositionAssetType.stock;

        // Options: 1 contract = 100 shares, so dollars invested = price × quantity × 100
        var contractMultiplier = assetType == PositionAssetType.option ? 100.0 : 1.0;
        var dollarsInvested = request.EntryPrice * request.Quantity * contractMultiplier;

        if (dollarsInvested > challenge.CurrentCash)
        {
            _logger.LogWarning("[balance-engine] Insufficient cash. Need {Need}, have {Have}",
                dollarsInvested, challenge.CurrentCash);
            return null;
        }

        // ── Broker execution FIRST (broker_paper or live mode) ──────
        // Place broker order before persisting to Supabase so we never
        // record a position that doesn't exist at the broker.
        // Options are not supported in broker mode — only stocks.
        string? brokerEntryOrderId = null;
        var isBrokerMode = challenge.TradingMode != TradingMode.paper && _broker.IsConfigured;

        if (isBrokerMode)
        {
            if (assetType == PositionAssetType.option)
            {
                _logger.LogWarning(
                    "[balance-engine] Broker mode does not support options — skipping {Ticker}",
                    request.Ticker);
                return null;
            }

            try
            {
                var brokerResult = await _broker.PlaceMarketOrderAsync(new BrokerOrderRequest
                {
                    Ticker = request.Ticker,
                    Quantity = request.Quantity,
                    Side = BrokerOrderSide.buy,
                    TimeInForce = BrokerTimeInForce.day,
                    ClientOrderId = $"sj-{Guid.NewGuid():N}"[..36], // unique client ID for idempotency
                });

                if (brokerResult.Success && brokerResult.BrokerOrderId is not null)
                {
                    brokerEntryOrderId = brokerResult.BrokerOrderId;
                    _logger.LogInformation(
                        "[balance-engine] Broker BUY order placed for {Ticker}: orderId={OrderId}, status={Status}",
                        request.Ticker, brokerResult.BrokerOrderId, brokerResult.Status);
                }
                else
                {
                    _logger.LogError(
                        "[balance-engine] Broker BUY order FAILED for {Ticker}: {Error}. " +
                        "Aborting position open — no Supabase record created.",
                        request.Ticker, brokerResult.ErrorMessage);
                    return null; // Don't create a phantom position
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[balance-engine] Broker order exception for {Ticker}. " +
                    "Aborting position open.", request.Ticker);
                return null; // Don't create a phantom position
            }
        }

        var position = new PortfolioPosition
        {
            PortfolioId = request.PortfolioId,
            PredictionId = request.PredictionId,
            Ticker = request.Ticker,
            AssetType = assetType,
            EntryPrice = request.EntryPrice,
            Quantity = request.Quantity,
            DollarsInvested = Math.Round(dollarsInvested, 2),
            ReasonEntered = request.ReasonEntered,
            BrokerEntryOrderId = brokerEntryOrderId,
        };

        var saved = await _repo.OpenPositionAsync(position);
        if (saved is null) return null;

        // Deduct from cash
        var newCash = Math.Round(challenge.CurrentCash - dollarsInvested, 2);
        var newBuyingPower = newCash; // Phase 1: buying power = cash (no margin)

        await _repo.UpdateChallengeBalanceAsync(
            challenge.Id,
            currentBalance: challenge.CurrentBalance, // balance unchanged until trade closes
            currentCash: newCash,
            buyingPower: newBuyingPower,
            realizedProfit: challenge.RealizedProfit,
            unrealizedProfit: challenge.UnrealizedProfit,
            totalReturn: challenge.TotalReturn,
            percentReturn: challenge.PercentReturn,
            numberOfTrades: challenge.NumberOfTrades,
            winningTrades: challenge.WinningTrades,
            losingTrades: challenge.LosingTrades,
            winRate: challenge.WinRate);

        _logger.LogInformation(
            "[balance-engine] Opened {Ticker} position: ${Invested} at ${Price} x {Qty}. Cash: ${Cash}",
            saved.Ticker, dollarsInvested, request.EntryPrice, request.Quantity, newCash);

        return saved;
    }

    /// <summary>
    /// Close a position: calculate P&L, return dollars to cash,
    /// update realized gains, and recalculate portfolio stats.
    /// </summary>
    public async Task<PortfolioPosition?> ClosePositionAsync(ClosePositionRequest request)
    {
        var position = await _repo.GetPositionAsync(request.PositionId);
        if (position is null)
        {
            _logger.LogWarning("[balance-engine] Position {Id} not found", request.PositionId);
            return null;
        }

        if (position.Status != PositionStatus.open)
        {
            _logger.LogWarning("[balance-engine] Position {Id} is not open (status={Status})",
                request.PositionId, position.Status);
            return null;
        }

        var challenge = await _repo.GetChallengeAsync(position.PortfolioId);
        if (challenge is null)
        {
            _logger.LogWarning("[balance-engine] Challenge {Id} not found for position",
                position.PortfolioId);
            return null;
        }

        // Calculate P&L — options use 100x contract multiplier
        var closeMultiplier = position.AssetType == PositionAssetType.option ? 100.0 : 1.0;
        var dollarsReturned = Math.Round(request.ExitPrice * position.Quantity * closeMultiplier, 2);
        var profitLoss = Math.Round(dollarsReturned - position.DollarsInvested, 2);
        var percentGain = position.DollarsInvested > 0
            ? Math.Round((profitLoss / position.DollarsInvested) * 100, 2)
            : 0;

        // ── Broker close FIRST (broker_paper or live mode) ───────────
        // Close at broker before updating Supabase. If broker close fails,
        // abort — never record a sale that didn't happen.
        string? brokerExitOrderId = null;
        var isBrokerMode = challenge.TradingMode != TradingMode.paper && _broker.IsConfigured;

        if (isBrokerMode)
        {
            try
            {
                // Don't pass quantity — close the full position at broker.
                // This is more reliable when broker qty drifts from ours.
                var brokerResult = await _broker.ClosePositionAsync(position.Ticker);

                if (brokerResult.Success)
                {
                    brokerExitOrderId = brokerResult.BrokerOrderId;
                    _logger.LogInformation(
                        "[balance-engine] Broker SELL order placed for {Ticker}: orderId={OrderId}",
                        position.Ticker, brokerResult.BrokerOrderId);
                }
                else
                {
                    // Check if position simply doesn't exist at broker (already closed)
                    var brokerPos = await _broker.GetPositionAsync(position.Ticker);
                    if (brokerPos is null)
                    {
                        _logger.LogWarning(
                            "[balance-engine] Broker position for {Ticker} already gone — " +
                            "proceeding with Supabase close",
                            position.Ticker);
                        // Position was already closed at broker — safe to close in Supabase
                    }
                    else
                    {
                        _logger.LogError(
                            "[balance-engine] Broker SELL FAILED for {Ticker}: {Error}. " +
                            "Aborting Supabase close — position remains open.",
                            position.Ticker, brokerResult.ErrorMessage);
                        return null; // Don't record phantom P&L
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[balance-engine] Broker close exception for {Ticker}. " +
                    "Aborting Supabase close.", position.Ticker);
                return null; // Don't record phantom P&L
            }
        }

        // Close the position in the database
        var closed = await _repo.ClosePositionAsync(
            request.PositionId,
            request.ExitPrice,
            dollarsReturned,
            profitLoss,
            percentGain,
            request.ReasonExited,
            brokerExitOrderId);

        if (!closed) return null;

        // Update portfolio stats
        var newCash = Math.Round(challenge.CurrentCash + dollarsReturned, 2);
        var newRealizedProfit = Math.Round(challenge.RealizedProfit + profitLoss, 2);
        var newNumberOfTrades = challenge.NumberOfTrades + 1;
        var isWin = profitLoss > 0;
        var newWinningTrades = challenge.WinningTrades + (isWin ? 1 : 0);
        var newLosingTrades = challenge.LosingTrades + (isWin ? 0 : 1);
        var newWinRate = newNumberOfTrades > 0
            ? Math.Round((double)newWinningTrades / newNumberOfTrades * 100, 2)
            : 0;

        // Current balance = cash + value of open positions (at entry price for now)
        // Phase 1: we use entry price for open positions since we don't have live quotes yet
        var openPositions = await _repo.GetOpenPositionsAsync(challenge.Id);
        var openPositionValue = openPositions.Sum(p => p.DollarsInvested);
        var newBalance = Math.Round(newCash + openPositionValue, 2);

        var newTotalReturn = Math.Round(newBalance - challenge.StartingBalance, 2);
        var newPercentReturn = challenge.StartingBalance > 0
            ? Math.Round((newTotalReturn / challenge.StartingBalance) * 100, 2)
            : 0;

        // Check if target was reached
        string? newStatus = null;
        if (newBalance >= challenge.TargetBalance)
        {
            newStatus = ChallengeStatus.completed.ToString();
            _logger.LogInformation(
                "[balance-engine] Challenge {Name} COMPLETED! Balance ${Balance} >= target ${Target}",
                challenge.Name, newBalance, challenge.TargetBalance);
        }

        await _repo.UpdateChallengeBalanceAsync(
            challenge.Id,
            currentBalance: newBalance,
            currentCash: newCash,
            buyingPower: newCash, // Phase 1: buying power = cash
            realizedProfit: newRealizedProfit,
            unrealizedProfit: 0, // Phase 1: not tracking unrealized yet
            totalReturn: newTotalReturn,
            percentReturn: newPercentReturn,
            numberOfTrades: newNumberOfTrades,
            winningTrades: newWinningTrades,
            losingTrades: newLosingTrades,
            winRate: newWinRate,
            status: newStatus);

        _logger.LogInformation(
            "[balance-engine] Closed {Ticker}: P&L ${PnL} ({Pct}%). Balance: ${Balance}, Cash: ${Cash}, Win rate: {WinRate}%",
            position.Ticker, profitLoss, percentGain, newBalance, newCash, newWinRate);

        // Return the updated position
        return await _repo.GetPositionAsync(request.PositionId);
    }

    /// <summary>
    /// Build a dashboard summary for a challenge.
    /// </summary>
    public async Task<PortfolioChallengeSummary?> GetSummaryAsync(string? challengeId = null)
    {
        PortfolioChallenge? challenge;
        if (challengeId is not null)
            challenge = await _repo.GetChallengeAsync(challengeId);
        else
            challenge = await _repo.GetActiveChallengeAsync();

        if (challenge is null) return null;

        var openPositions = await _repo.GetOpenPositionsAsync(challenge.Id);
        var closedPositions = await _repo.GetClosedPositionsAsync(challenge.Id, limit: 10);

        var progressPercent = challenge.TargetBalance > challenge.StartingBalance
            ? Math.Round(
                (challenge.CurrentBalance - challenge.StartingBalance) /
                (challenge.TargetBalance - challenge.StartingBalance) * 100, 2)
            : 0;

        // Clamp to 0..100
        progressPercent = Math.Max(0, Math.Min(100, progressPercent));

        var currentGoal = challenge.Status == ChallengeStatus.completed
            ? $"Challenge completed! Reached ${challenge.CurrentBalance:F2}"
            : $"Grow ${challenge.CurrentBalance:F2} → ${challenge.TargetBalance:F2}";

        return new PortfolioChallengeSummary
        {
            ChallengeId = challenge.Id,
            ChallengeName = challenge.Name,
            CurrentBalance = challenge.CurrentBalance,
            TargetBalance = challenge.TargetBalance,
            ProgressPercent = progressPercent,
            CashAvailable = challenge.CurrentCash,
            OpenPositions = openPositions.Count,
            ClosedPositions = challenge.NumberOfTrades,
            CurrentReturn = challenge.TotalReturn,
            PercentReturn = challenge.PercentReturn,
            Trades = challenge.NumberOfTrades,
            WinRate = challenge.WinRate,
            CurrentGoal = currentGoal,
            Status = challenge.Status,
            PortfolioMode = challenge.PortfolioMode,
            RiskProfile = challenge.RiskProfile,
            RecentOpenPositions = openPositions,
            RecentClosedPositions = closedPositions,
        };
    }
}
