using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.OptionsLab;

/// <summary>
/// Pure deterministic payoff calculations for theoretical option strategies.
/// Uses only user-provided assumptions — no invented premiums, IV, or Greeks.
/// All results are theoretical simulations, not real option quotes.
/// </summary>
public static class StrategyPayoffCalculator
{
    // -----------------------------------------------------------------------
    // Long Call Proxy
    // -----------------------------------------------------------------------

    public static TheoreticalOptionSimulationResult CalculateLongCall(
        double startingPrice, double endingPrice, double strikePrice,
        double premiumPaid, SimulationAssumptions assumptions)
    {
        var payoff = Math.Max(endingPrice - strikePrice, 0) - premiumPaid;
        var breakeven = strikePrice + premiumPaid;
        var maxLoss = premiumPaid;

        return BuildResult(
            OptionsStrategyType.long_call_proxy,
            startingPrice, endingPrice, payoff,
            costBasis: premiumPaid,
            maxProfit: double.PositiveInfinity,
            maxLoss: maxLoss,
            breakevens: [breakeven],
            assumptions: assumptions,
            riskReward: $"Max loss: ${maxLoss:F2} (premium paid). Max profit: theoretically unlimited. Breakeven at ${breakeven:F2}.");
    }

    // -----------------------------------------------------------------------
    // Long Put Proxy
    // -----------------------------------------------------------------------

    public static TheoreticalOptionSimulationResult CalculateLongPut(
        double startingPrice, double endingPrice, double strikePrice,
        double premiumPaid, SimulationAssumptions assumptions)
    {
        var payoff = Math.Max(strikePrice - endingPrice, 0) - premiumPaid;
        var breakeven = strikePrice - premiumPaid;
        var maxLoss = premiumPaid;
        var maxProfit = strikePrice - premiumPaid;

        return BuildResult(
            OptionsStrategyType.long_put_proxy,
            startingPrice, endingPrice, payoff,
            costBasis: premiumPaid,
            maxProfit: maxProfit,
            maxLoss: maxLoss,
            breakevens: [breakeven],
            assumptions: assumptions,
            riskReward: $"Max loss: ${maxLoss:F2} (premium paid). Max profit: ${maxProfit:F2} (if stock goes to $0). Breakeven at ${breakeven:F2}.");
    }

    // -----------------------------------------------------------------------
    // Bull Call Spread Proxy
    // -----------------------------------------------------------------------

    public static TheoreticalOptionSimulationResult CalculateBullCallSpread(
        double startingPrice, double endingPrice,
        double lowerCallStrike, double upperCallStrike,
        double netDebit, SimulationAssumptions assumptions)
    {
        var longCallPayoff = Math.Max(endingPrice - lowerCallStrike, 0);
        var shortCallPayoff = Math.Max(endingPrice - upperCallStrike, 0);
        var payoff = longCallPayoff - shortCallPayoff - netDebit;

        var maxProfit = upperCallStrike - lowerCallStrike - netDebit;
        var maxLoss = netDebit;
        var breakeven = lowerCallStrike + netDebit;

        return BuildResult(
            OptionsStrategyType.bull_call_spread_proxy,
            startingPrice, endingPrice, payoff,
            costBasis: netDebit,
            maxProfit: maxProfit,
            maxLoss: maxLoss,
            breakevens: [breakeven],
            assumptions: assumptions,
            riskReward: $"Max loss: ${maxLoss:F2} (net debit). Max profit: ${maxProfit:F2} (at/above ${upperCallStrike:F2}). Breakeven at ${breakeven:F2}.");
    }

    // -----------------------------------------------------------------------
    // Bear Put Spread Proxy
    // -----------------------------------------------------------------------

    public static TheoreticalOptionSimulationResult CalculateBearPutSpread(
        double startingPrice, double endingPrice,
        double upperPutStrike, double lowerPutStrike,
        double netDebit, SimulationAssumptions assumptions)
    {
        var longPutPayoff = Math.Max(upperPutStrike - endingPrice, 0);
        var shortPutPayoff = Math.Max(lowerPutStrike - endingPrice, 0);
        var payoff = longPutPayoff - shortPutPayoff - netDebit;

        var maxProfit = upperPutStrike - lowerPutStrike - netDebit;
        var maxLoss = netDebit;
        var breakeven = upperPutStrike - netDebit;

        return BuildResult(
            OptionsStrategyType.bear_put_spread_proxy,
            startingPrice, endingPrice, payoff,
            costBasis: netDebit,
            maxProfit: maxProfit,
            maxLoss: maxLoss,
            breakevens: [breakeven],
            assumptions: assumptions,
            riskReward: $"Max loss: ${maxLoss:F2} (net debit). Max profit: ${maxProfit:F2} (at/below ${lowerPutStrike:F2}). Breakeven at ${breakeven:F2}.");
    }

    // -----------------------------------------------------------------------
    // Iron Condor Proxy
    // -----------------------------------------------------------------------

    public static TheoreticalOptionSimulationResult CalculateIronCondor(
        double startingPrice, double endingPrice,
        double longPutStrike, double shortPutStrike,
        double shortCallStrike, double longCallStrike,
        double netCredit, SimulationAssumptions assumptions)
    {
        // Put side: short put - long put
        var shortPutPayoff = Math.Max(shortPutStrike - endingPrice, 0);
        var longPutPayoff = Math.Max(longPutStrike - endingPrice, 0);
        var putSide = longPutPayoff - shortPutPayoff; // negative when short put is ITM

        // Call side: short call - long call
        var shortCallPayoff = Math.Max(endingPrice - shortCallStrike, 0);
        var longCallPayoff = Math.Max(endingPrice - longCallStrike, 0);
        var callSide = longCallPayoff - shortCallPayoff; // negative when short call is ITM

        var payoff = netCredit + putSide + callSide;

        var putWidth = shortPutStrike - longPutStrike;
        var callWidth = longCallStrike - shortCallStrike;
        var wingWidth = Math.Max(putWidth, callWidth);
        var maxLoss = wingWidth - netCredit;
        var maxProfit = netCredit;
        var lowerBreakeven = shortPutStrike - netCredit;
        var upperBreakeven = shortCallStrike + netCredit;

        return BuildResult(
            OptionsStrategyType.iron_condor_proxy,
            startingPrice, endingPrice, payoff,
            costBasis: maxLoss, // max risk is the cost basis for return calc
            maxProfit: maxProfit,
            maxLoss: maxLoss,
            breakevens: [lowerBreakeven, upperBreakeven],
            assumptions: assumptions,
            riskReward: $"Max profit: ${maxProfit:F2} (net credit, if stock stays between ${shortPutStrike:F2}-${shortCallStrike:F2}). Max loss: ${maxLoss:F2}. Breakevens at ${lowerBreakeven:F2} and ${upperBreakeven:F2}.");
    }

    // -----------------------------------------------------------------------
    // Theoretical premium estimate (Black-Scholes-ish, clearly labeled)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Rough theoretical premium using a simplified model.
    /// This is NOT a real option price — it requires user-provided IV assumption.
    /// </summary>
    public static double EstimateTheoreticalPremium(
        double stockPrice, double strikePrice, double iv,
        double riskFreeRate, int daysToExpiration, bool isCall)
    {
        var t = daysToExpiration / 365.0;
        if (t <= 0) return Math.Max(isCall ? stockPrice - strikePrice : strikePrice - stockPrice, 0);

        var sqrtT = Math.Sqrt(t);
        var d1 = (Math.Log(stockPrice / strikePrice) + (riskFreeRate + 0.5 * iv * iv) * t) / (iv * sqrtT);
        var d2 = d1 - iv * sqrtT;

        if (isCall)
        {
            return stockPrice * NormalCdf(d1) - strikePrice * Math.Exp(-riskFreeRate * t) * NormalCdf(d2);
        }
        else
        {
            return strikePrice * Math.Exp(-riskFreeRate * t) * NormalCdf(-d2) - stockPrice * NormalCdf(-d1);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static TheoreticalOptionSimulationResult BuildResult(
        OptionsStrategyType strategy,
        double startingPrice, double endingPrice, double payoff,
        double costBasis, double maxProfit, double maxLoss,
        List<double> breakevens, SimulationAssumptions assumptions,
        string riskReward)
    {
        var movePercent = startingPrice > 0 ? (endingPrice - startingPrice) / startingPrice * 100 : 0;
        var returnPercent = costBasis > 0 ? payoff / costBasis * 100 : 0;

        var warnings = new List<string>
        {
            "THEORETICAL SIMULATION ONLY — not a real option quote.",
            "Real options-chain data, bid/ask, IV, Greeks, open interest, volume, and liquidity are not connected.",
            "Actual option performance may differ significantly due to time decay, IV changes, and market conditions.",
        };

        return new TheoreticalOptionSimulationResult
        {
            StrategyType = strategy,
            Label = "THEORETICAL SIMULATION ONLY",
            StartingStockPrice = startingPrice,
            EndingStockPrice = endingPrice,
            StockMovePercent = Math.Round(movePercent, 4),
            EstimatedPayoff = Math.Round(payoff, 2),
            EstimatedReturnPercent = Math.Round(returnPercent, 2),
            MaxProfit = double.IsPositiveInfinity(maxProfit) ? -1 : Math.Round(maxProfit, 2), // -1 signals "unlimited"
            MaxLoss = Math.Round(maxLoss, 2),
            Breakevens = breakevens.Select(b => Math.Round(b, 2)).ToList(),
            RiskRewardSummary = riskReward,
            AssumptionsUsed = assumptions,
            Warnings = warnings,
        };
    }

    /// <summary>Cumulative standard normal distribution (Abramowitz & Stegun approximation).</summary>
    private static double NormalCdf(double x)
    {
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var sign = x < 0 ? -1 : 1;
        x = Math.Abs(x) / Math.Sqrt(2);

        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

        return 0.5 * (1.0 + sign * y);
    }
}
