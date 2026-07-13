using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Pure calculation service for risk/reward metrics.
///
/// Bullish (long):
///   Risk   = EntryPrice − StopLossPrice
///   Reward = TargetPrice − EntryPrice
///
/// Bearish (short):
///   Risk   = StopLossPrice − EntryPrice
///   Reward = EntryPrice − TargetPrice
///
/// RiskRewardRatio = Reward / Risk
/// IsFavorable     = ratio >= 2.0  (threshold will become configurable)
///
/// Never throws — returns a result with ValidationError when inputs are invalid.
/// </summary>
public class RiskRewardAnalyzer : IRiskRewardAnalyzer
{
    private const double FavorableThreshold = 2.0;

    public RiskRewardResult Analyze(RiskRewardRequest request)
    {
        // ── Input validation ──────────────────────────────────────────
        if (request.EntryPrice <= 0)
            return Invalid("EntryPrice must be greater than zero.");
        if (request.TargetPrice <= 0)
            return Invalid("TargetPrice must be greater than zero.");
        if (request.StopLossPrice <= 0)
            return Invalid("StopLossPrice must be greater than zero.");

        // ── Direction-aware calculation ────────────────────────────────
        double riskAmount, rewardAmount;

        if (request.IsBullish)
        {
            riskAmount = request.EntryPrice - request.StopLossPrice;
            rewardAmount = request.TargetPrice - request.EntryPrice;
        }
        else
        {
            riskAmount = request.StopLossPrice - request.EntryPrice;
            rewardAmount = request.EntryPrice - request.TargetPrice;
        }

        // Guard: zero or negative risk means the stop is on the wrong
        // side of entry — return unfavorable rather than crashing.
        if (riskAmount <= 0)
        {
            return new RiskRewardResult
            {
                RiskAmount = riskAmount,
                RewardAmount = rewardAmount,
                RiskRewardRatio = 0,
                IsFavorable = false,
                ValidationError = "RiskAmount is zero or negative — stop loss is at or beyond entry price.",
            };
        }

        var ratio = Math.Round(rewardAmount / riskAmount, 4);

        return new RiskRewardResult
        {
            RiskAmount = Math.Round(riskAmount, 4),
            RewardAmount = Math.Round(rewardAmount, 4),
            RiskRewardRatio = ratio,
            IsFavorable = ratio >= FavorableThreshold,
        };
    }

    private static RiskRewardResult Invalid(string error) => new()
    {
        RiskAmount = 0,
        RewardAmount = 0,
        RiskRewardRatio = 0,
        IsFavorable = false,
        ValidationError = error,
    };
}
