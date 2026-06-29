using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.OptionsLab;

/// <summary>
/// Validates that all required assumptions are present before simulation.
/// Rejects requests with missing data rather than inventing values.
/// </summary>
public static class StrategyAssumptionValidator
{
    public static AssumptionValidationResult Validate(TheoreticalOptionSimulationRequest req)
    {
        var result = new AssumptionValidationResult { IsValid = true };

        // ── Common fields ────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(req.Ticker))
            result.Errors.Add("ticker is required.");

        if (req.StartingStockPrice <= 0)
            result.Errors.Add("startingStockPrice must be positive.");

        if (req.EndingStockPrice <= 0)
            result.Errors.Add("endingStockPrice must be positive.");

        if (req.DaysToExpiration <= 0)
            result.Errors.Add("daysToExpiration must be a positive integer.");

        if (req.AssumedImpliedVolatility <= 0 || req.AssumedImpliedVolatility > 5)
            result.Errors.Add("assumedImpliedVolatility must be between 0 and 5 (e.g., 0.30 for 30%).");

        if (req.AssumedRiskFreeRate < 0 || req.AssumedRiskFreeRate > 1)
            result.Warnings.Add("assumedRiskFreeRate looks unusual — using provided value. Default is 0.05 (5%).");

        // ── Strategy-specific fields ─────────────────────────────
        switch (req.StrategyType)
        {
            case OptionsStrategyType.long_call_proxy:
                ValidateSingleLeg(req, "call", result);
                break;

            case OptionsStrategyType.long_put_proxy:
                ValidateSingleLeg(req, "put", result);
                break;

            case OptionsStrategyType.bull_call_spread_proxy:
                ValidateBullCallSpread(req, result);
                break;

            case OptionsStrategyType.bear_put_spread_proxy:
                ValidateBearPutSpread(req, result);
                break;

            case OptionsStrategyType.iron_condor_proxy:
                ValidateIronCondor(req, result);
                break;

            default:
                result.Errors.Add($"Unknown strategy type: {req.StrategyType}");
                break;
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    private static void ValidateSingleLeg(
        TheoreticalOptionSimulationRequest req, string legType,
        AssumptionValidationResult result)
    {
        if (req.StrikePrice is null or <= 0)
            result.Errors.Add("strikePrice is required and must be positive.");

        if (req.PremiumMode == PremiumMode.manual)
        {
            if (req.ManualPremium is null or <= 0)
                result.Errors.Add($"manualPremium is required when premiumMode is 'manual'. Provide the {legType} premium you want to assume.");
        }
        else if (req.PremiumMode == PremiumMode.theoretical)
        {
            // Theoretical premium will be calculated from IV and other assumptions
            if (req.AssumedImpliedVolatility <= 0)
                result.Errors.Add("assumedImpliedVolatility is required for theoretical premium calculation.");
        }
    }

    private static void ValidateBullCallSpread(
        TheoreticalOptionSimulationRequest req,
        AssumptionValidationResult result)
    {
        if (req.LowerCallStrike is null or <= 0)
            result.Errors.Add("lowerCallStrike is required for Bull Call Spread.");

        if (req.UpperCallStrike is null or <= 0)
            result.Errors.Add("upperCallStrike is required for Bull Call Spread.");

        if (req.LowerCallStrike.HasValue && req.UpperCallStrike.HasValue
            && req.LowerCallStrike >= req.UpperCallStrike)
            result.Errors.Add("lowerCallStrike must be less than upperCallStrike.");

        if (req.NetDebit is null or <= 0)
            result.Errors.Add("netDebit is required for Bull Call Spread.");

        if (req.NetDebit.HasValue && req.LowerCallStrike.HasValue && req.UpperCallStrike.HasValue)
        {
            var maxSpreadWidth = req.UpperCallStrike.Value - req.LowerCallStrike.Value;
            if (req.NetDebit.Value >= maxSpreadWidth)
                result.Errors.Add("netDebit must be less than the spread width (upperCallStrike - lowerCallStrike).");
        }
    }

    private static void ValidateBearPutSpread(
        TheoreticalOptionSimulationRequest req,
        AssumptionValidationResult result)
    {
        if (req.UpperPutStrike is null or <= 0)
            result.Errors.Add("upperPutStrike is required for Bear Put Spread.");

        if (req.LowerPutStrike is null or <= 0)
            result.Errors.Add("lowerPutStrike is required for Bear Put Spread.");

        if (req.UpperPutStrike.HasValue && req.LowerPutStrike.HasValue
            && req.LowerPutStrike >= req.UpperPutStrike)
            result.Errors.Add("lowerPutStrike must be less than upperPutStrike.");

        if (req.NetDebit is null or <= 0)
            result.Errors.Add("netDebit is required for Bear Put Spread.");

        if (req.NetDebit.HasValue && req.UpperPutStrike.HasValue && req.LowerPutStrike.HasValue)
        {
            var maxSpreadWidth = req.UpperPutStrike.Value - req.LowerPutStrike.Value;
            if (req.NetDebit.Value >= maxSpreadWidth)
                result.Errors.Add("netDebit must be less than the spread width (upperPutStrike - lowerPutStrike).");
        }
    }

    private static void ValidateIronCondor(
        TheoreticalOptionSimulationRequest req,
        AssumptionValidationResult result)
    {
        if (req.LongPutStrike is null or <= 0)
            result.Errors.Add("longPutStrike is required for Iron Condor.");
        if (req.ShortPutStrike is null or <= 0)
            result.Errors.Add("shortPutStrike is required for Iron Condor.");
        if (req.ShortCallStrike is null or <= 0)
            result.Errors.Add("shortCallStrike is required for Iron Condor.");
        if (req.LongCallStrike is null or <= 0)
            result.Errors.Add("longCallStrike is required for Iron Condor.");

        if (req.LongPutStrike.HasValue && req.ShortPutStrike.HasValue
            && req.LongPutStrike >= req.ShortPutStrike)
            result.Errors.Add("longPutStrike must be less than shortPutStrike.");

        if (req.ShortPutStrike.HasValue && req.ShortCallStrike.HasValue
            && req.ShortPutStrike >= req.ShortCallStrike)
            result.Errors.Add("shortPutStrike must be less than shortCallStrike.");

        if (req.ShortCallStrike.HasValue && req.LongCallStrike.HasValue
            && req.ShortCallStrike >= req.LongCallStrike)
            result.Errors.Add("shortCallStrike must be less than longCallStrike.");

        if (req.NetCredit is null or <= 0)
            result.Errors.Add("netCredit is required for Iron Condor.");
    }
}
