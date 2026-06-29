using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.OptionsLab;

/// <summary>
/// Orchestrates the theoretical options simulation:
///   1. Validate assumptions
///   2. Resolve premium (manual or theoretical estimate)
///   3. Calculate deterministic payoff
///   4. Optionally persist to Supabase
///   5. Return result labeled as THEORETICAL SIMULATION ONLY
///
/// Does not invent option contracts, premiums, IV, Greeks, bid/ask, OI, or volume.
/// </summary>
public class TheoreticalOptionsSimulator
{
    private readonly ResearchRepository _repo;
    private readonly ILogger<TheoreticalOptionsSimulator> _logger;

    public TheoreticalOptionsSimulator(
        ResearchRepository repo,
        ILogger<TheoreticalOptionsSimulator> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<(TheoreticalOptionSimulationResult? Result, AssumptionValidationResult Validation)>
        SimulateAsync(TheoreticalOptionSimulationRequest request)
    {
        // ── 1. Validate ──────────────────────────────────────────
        var validation = StrategyAssumptionValidator.Validate(request);
        if (!validation.IsValid)
            return (null, validation);

        // ── 2. Build assumptions record ──────────────────────────
        var assumptions = new SimulationAssumptions
        {
            AssumedImpliedVolatility = request.AssumedImpliedVolatility,
            AssumedRiskFreeRate = request.AssumedRiskFreeRate,
            DaysToExpiration = request.DaysToExpiration,
            PremiumMode = request.PremiumMode,
            ManualPremium = request.ManualPremium,
            StrikePrice = request.StrikePrice,
            LowerCallStrike = request.LowerCallStrike,
            UpperCallStrike = request.UpperCallStrike,
            UpperPutStrike = request.UpperPutStrike,
            LowerPutStrike = request.LowerPutStrike,
            ShortPutStrike = request.ShortPutStrike,
            LongPutStrike = request.LongPutStrike,
            ShortCallStrike = request.ShortCallStrike,
            LongCallStrike = request.LongCallStrike,
            NetDebit = request.NetDebit,
            NetCredit = request.NetCredit,
        };

        // ── 3. Calculate payoff ──────────────────────────────────
        TheoreticalOptionSimulationResult result;

        switch (request.StrategyType)
        {
            case OptionsStrategyType.long_call_proxy:
            {
                var premium = ResolvePremium(request, isCall: true);
                assumptions.TheoreticalPremium = request.PremiumMode == PremiumMode.theoretical ? premium : null;
                result = StrategyPayoffCalculator.CalculateLongCall(
                    request.StartingStockPrice, request.EndingStockPrice,
                    request.StrikePrice!.Value, premium, assumptions);
                break;
            }

            case OptionsStrategyType.long_put_proxy:
            {
                var premium = ResolvePremium(request, isCall: false);
                assumptions.TheoreticalPremium = request.PremiumMode == PremiumMode.theoretical ? premium : null;
                result = StrategyPayoffCalculator.CalculateLongPut(
                    request.StartingStockPrice, request.EndingStockPrice,
                    request.StrikePrice!.Value, premium, assumptions);
                break;
            }

            case OptionsStrategyType.bull_call_spread_proxy:
                result = StrategyPayoffCalculator.CalculateBullCallSpread(
                    request.StartingStockPrice, request.EndingStockPrice,
                    request.LowerCallStrike!.Value, request.UpperCallStrike!.Value,
                    request.NetDebit!.Value, assumptions);
                break;

            case OptionsStrategyType.bear_put_spread_proxy:
                result = StrategyPayoffCalculator.CalculateBearPutSpread(
                    request.StartingStockPrice, request.EndingStockPrice,
                    request.UpperPutStrike!.Value, request.LowerPutStrike!.Value,
                    request.NetDebit!.Value, assumptions);
                break;

            case OptionsStrategyType.iron_condor_proxy:
                result = StrategyPayoffCalculator.CalculateIronCondor(
                    request.StartingStockPrice, request.EndingStockPrice,
                    request.LongPutStrike!.Value, request.ShortPutStrike!.Value,
                    request.ShortCallStrike!.Value, request.LongCallStrike!.Value,
                    request.NetCredit!.Value, assumptions);
                break;

            default:
                validation.Errors.Add($"Unsupported strategy: {request.StrategyType}");
                validation.IsValid = false;
                return (null, validation);
        }

        // ── 4. Attach prediction metadata ────────────────────────
        result.PredictionId = request.PredictionId;
        result.Ticker = request.Ticker;

        if (!string.IsNullOrWhiteSpace(request.PredictionId))
        {
            try
            {
                var predictions = await _repo.GetRecentPredictionsAsync(200);
                var pred = predictions.FirstOrDefault(p => p.Id == request.PredictionId);
                if (pred is not null)
                {
                    result.DirectionMatchedPrediction = PredictionStrategyMapper
                        .DirectionMatchesStrategy(pred.PredictionType.ToString(), request.StrategyType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[options-lab] Could not look up prediction {Id}", request.PredictionId);
            }
        }

        // ── 5. Add theoretical premium warning ───────────────────
        if (request.PremiumMode == PremiumMode.theoretical)
        {
            result.Warnings.Add(
                "Premium was estimated using a simplified Black-Scholes model with your assumed IV. " +
                "This is a theoretical estimate — actual premiums depend on real market conditions.");
        }

        // ── 6. Persist if Supabase is available ──────────────────
        await TrySaveSimulationAsync(result);

        _logger.LogInformation(
            "[options-lab] {Ticker} {Strategy}: payoff=${Payoff:F2}, return={Return:F2}%",
            result.Ticker, result.StrategyType, result.EstimatedPayoff, result.EstimatedReturnPercent);

        return (result, validation);
    }

    // -----------------------------------------------------------------------
    // Premium resolution
    // -----------------------------------------------------------------------

    private static double ResolvePremium(TheoreticalOptionSimulationRequest req, bool isCall)
    {
        if (req.PremiumMode == PremiumMode.manual && req.ManualPremium.HasValue)
            return req.ManualPremium.Value;

        // Theoretical estimate from user-provided IV assumption
        return StrategyPayoffCalculator.EstimateTheoreticalPremium(
            req.StartingStockPrice,
            req.StrikePrice!.Value,
            req.AssumedImpliedVolatility,
            req.AssumedRiskFreeRate,
            req.DaysToExpiration,
            isCall);
    }

    // -----------------------------------------------------------------------
    // Persistence (best-effort)
    // -----------------------------------------------------------------------

    private async Task TrySaveSimulationAsync(TheoreticalOptionSimulationResult result)
    {
        if (!_repo.IsConfigured) return;

        try
        {
            var row = new
            {
                prediction_id = result.PredictionId,
                ticker = result.Ticker,
                strategy_type = result.StrategyType.ToString(),
                starting_stock_price = result.StartingStockPrice,
                ending_stock_price = result.EndingStockPrice,
                stock_move_percent = result.StockMovePercent,
                assumptions_json = result.AssumptionsUsed,
                estimated_payoff = result.EstimatedPayoff,
                estimated_return_percent = result.EstimatedReturnPercent,
                max_profit = result.MaxProfit,
                max_loss = result.MaxLoss,
                breakevens_json = result.Breakevens,
                direction_matched_prediction = result.DirectionMatchedPrediction,
                warnings_json = result.Warnings,
            };

            await _repo.InsertGenericAsync("theoretical_option_simulations", row);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[options-lab] Failed to save simulation — table may not exist yet");
        }
    }
}
