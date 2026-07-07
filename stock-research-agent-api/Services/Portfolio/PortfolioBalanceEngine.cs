using StockResearchAgent.Api.Models;
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
/// </summary>
public class PortfolioBalanceEngine
{
    private readonly PortfolioChallengeRepository _repo;
    private readonly ILogger<PortfolioBalanceEngine> _logger;

    public PortfolioBalanceEngine(
        PortfolioChallengeRepository repo,
        ILogger<PortfolioBalanceEngine> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Position sizing — simple fixed-fraction approach for Phase 1.
    // Future: Kelly criterion, volatility-based sizing, risk budgeting.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Calculate how many shares/contracts to buy given available cash,
    /// price per unit, and the challenge's risk profile.
    /// Returns 0 if the position cannot be afforded.
    /// </summary>
    public double CalculatePositionSize(
        double cashAvailable,
        double pricePerUnit,
        RiskProfile riskProfile,
        PositionAssetType assetType)
    {
        if (pricePerUnit <= 0 || cashAvailable <= 0) return 0;

        // Max fraction of cash to deploy per position, by risk profile.
        var maxFraction = riskProfile switch
        {
            RiskProfile.conservative => 0.05,  // 5% per position
            RiskProfile.moderate => 0.10,       // 10% per position
            RiskProfile.aggressive => 0.20,     // 20% per position
            _ => 0.10,
        };

        var maxDollars = cashAvailable * maxFraction;

        // Options: buy whole contracts (quantity in contracts, each = 100 shares).
        // Stocks: buy fractional shares if needed (round to 2 decimals).
        if (assetType == PositionAssetType.option)
        {
            // Option premium is per-share, but 1 contract = 100 shares.
            var costPerContract = pricePerUnit * 100;
            if (costPerContract > maxDollars) return 0;
            return Math.Floor(maxDollars / costPerContract); // whole contracts only
        }

        // Stocks: allow fractional shares
        var shares = maxDollars / pricePerUnit;
        if (shares < 0.01) return 0;
        return Math.Round(shares, 2);
    }

    /// <summary>
    /// Open a position with auto-calculated quantity based on risk profile.
    /// Used by the orchestrator for automated portfolio tracking.
    /// </summary>
    public async Task<PortfolioPosition?> AutoOpenPositionAsync(
        string portfolioId,
        string? predictionId,
        string ticker,
        double entryPrice,
        PositionAssetType assetType,
        string? reason)
    {
        var challenge = await _repo.GetChallengeAsync(portfolioId);
        if (challenge is null || challenge.Status != ChallengeStatus.active) return null;

        var quantity = CalculatePositionSize(
            challenge.CurrentCash, entryPrice, challenge.RiskProfile, assetType);

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

        var dollarsInvested = request.EntryPrice * request.Quantity;

        if (dollarsInvested > challenge.CurrentCash)
        {
            _logger.LogWarning("[balance-engine] Insufficient cash. Need {Need}, have {Have}",
                dollarsInvested, challenge.CurrentCash);
            return null;
        }

        if (!Enum.TryParse<PositionAssetType>(request.AssetType, out var assetType))
            assetType = PositionAssetType.stock;

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

        // Calculate P&L
        var dollarsReturned = Math.Round(request.ExitPrice * position.Quantity, 2);
        var profitLoss = Math.Round(dollarsReturned - position.DollarsInvested, 2);
        var percentGain = position.DollarsInvested > 0
            ? Math.Round((profitLoss / position.DollarsInvested) * 100, 2)
            : 0;

        // Close the position in the database
        var closed = await _repo.ClosePositionAsync(
            request.PositionId,
            request.ExitPrice,
            dollarsReturned,
            profitLoss,
            percentGain,
            request.ReasonExited);

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
