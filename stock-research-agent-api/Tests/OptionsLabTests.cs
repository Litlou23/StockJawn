using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.OptionsLab;

namespace StockResearchAgent.Api.Tests;

/// <summary>
/// Validation tests for the theoretical options payoff calculator.
/// Run with: dotnet test or dotnet run and inspect output.
/// These are inline assertions — not a test framework dependency.
/// </summary>
public static class OptionsLabTests
{
    public static (int Passed, int Failed, List<string> Failures) RunAll()
    {
        var failures = new List<string>();
        int passed = 0;

        void Assert(string name, bool condition)
        {
            if (condition) passed++;
            else failures.Add(name);
        }

        var assumptions = new SimulationAssumptions
        {
            AssumedImpliedVolatility = 0.30,
            AssumedRiskFreeRate = 0.05,
            DaysToExpiration = 30,
            PremiumMode = PremiumMode.manual,
        };

        // ── Long Call: ITM at expiration ──────────────────────
        {
            var r = StrategyPayoffCalculator.CalculateLongCall(150, 160, 150, 5, assumptions);
            Assert("LongCall ITM payoff = 5", Math.Abs(r.EstimatedPayoff - 5) < 0.01);
            Assert("LongCall ITM breakeven = 155", Math.Abs(r.Breakevens[0] - 155) < 0.01);
            Assert("LongCall maxLoss = 5", Math.Abs(r.MaxLoss - 5) < 0.01);
            Assert("LongCall label", r.Label == "THEORETICAL SIMULATION ONLY");
        }

        // ── Long Call: OTM at expiration ─────────────────────
        {
            var r = StrategyPayoffCalculator.CalculateLongCall(150, 148, 150, 5, assumptions);
            Assert("LongCall OTM payoff = -5", Math.Abs(r.EstimatedPayoff - (-5)) < 0.01);
        }

        // ── Long Call: exactly at strike ─────────────────────
        {
            var r = StrategyPayoffCalculator.CalculateLongCall(150, 150, 150, 5, assumptions);
            Assert("LongCall ATM payoff = -5", Math.Abs(r.EstimatedPayoff - (-5)) < 0.01);
        }

        // ── Long Put: ITM at expiration ──────────────────────
        {
            var r = StrategyPayoffCalculator.CalculateLongPut(150, 140, 150, 5, assumptions);
            Assert("LongPut ITM payoff = 5", Math.Abs(r.EstimatedPayoff - 5) < 0.01);
            Assert("LongPut breakeven = 145", Math.Abs(r.Breakevens[0] - 145) < 0.01);
            Assert("LongPut maxLoss = 5", Math.Abs(r.MaxLoss - 5) < 0.01);
            Assert("LongPut maxProfit = 145", Math.Abs(r.MaxProfit - 145) < 0.01);
        }

        // ── Long Put: OTM at expiration ──────────────────────
        {
            var r = StrategyPayoffCalculator.CalculateLongPut(150, 155, 150, 5, assumptions);
            Assert("LongPut OTM payoff = -5", Math.Abs(r.EstimatedPayoff - (-5)) < 0.01);
        }

        // ── Bull Call Spread: max profit ─────────────────────
        {
            var r = StrategyPayoffCalculator.CalculateBullCallSpread(150, 165, 150, 160, 3, assumptions);
            Assert("BullCallSpread maxProfit = 7", Math.Abs(r.MaxProfit - 7) < 0.01);
            Assert("BullCallSpread maxLoss = 3", Math.Abs(r.MaxLoss - 3) < 0.01);
            Assert("BullCallSpread breakeven = 153", Math.Abs(r.Breakevens[0] - 153) < 0.01);
            Assert("BullCallSpread payoff at 165 = 7", Math.Abs(r.EstimatedPayoff - 7) < 0.01);
        }

        // ── Bull Call Spread: max loss ───────────────────────
        {
            var r = StrategyPayoffCalculator.CalculateBullCallSpread(150, 145, 150, 160, 3, assumptions);
            Assert("BullCallSpread payoff below lower = -3", Math.Abs(r.EstimatedPayoff - (-3)) < 0.01);
        }

        // ── Bear Put Spread: max profit ──────────────────────
        {
            var r = StrategyPayoffCalculator.CalculateBearPutSpread(150, 135, 150, 140, 3, assumptions);
            Assert("BearPutSpread maxProfit = 7", Math.Abs(r.MaxProfit - 7) < 0.01);
            Assert("BearPutSpread maxLoss = 3", Math.Abs(r.MaxLoss - 3) < 0.01);
            Assert("BearPutSpread breakeven = 147", Math.Abs(r.Breakevens[0] - 147) < 0.01);
            Assert("BearPutSpread payoff at 135 = 7", Math.Abs(r.EstimatedPayoff - 7) < 0.01);
        }

        // ── Bear Put Spread: max loss ────────────────────────
        {
            var r = StrategyPayoffCalculator.CalculateBearPutSpread(150, 155, 150, 140, 3, assumptions);
            Assert("BearPutSpread payoff above upper = -3", Math.Abs(r.EstimatedPayoff - (-3)) < 0.01);
        }

        // ── Iron Condor: max profit (inside wings) ───────────
        {
            var r = StrategyPayoffCalculator.CalculateIronCondor(150, 150, 140, 145, 155, 160, 2, assumptions);
            Assert("IronCondor maxProfit = 2", Math.Abs(r.MaxProfit - 2) < 0.01);
            Assert("IronCondor payoff at 150 = 2", Math.Abs(r.EstimatedPayoff - 2) < 0.01);
            Assert("IronCondor maxLoss = 3", Math.Abs(r.MaxLoss - 3) < 0.01);
            Assert("IronCondor lowerBreakeven = 143", Math.Abs(r.Breakevens[0] - 143) < 0.01);
            Assert("IronCondor upperBreakeven = 157", Math.Abs(r.Breakevens[1] - 157) < 0.01);
        }

        // ── Iron Condor: max loss (outside wings) ────────────
        {
            var r = StrategyPayoffCalculator.CalculateIronCondor(150, 135, 140, 145, 155, 160, 2, assumptions);
            Assert("IronCondor payoff below long put = -3", Math.Abs(r.EstimatedPayoff - (-3)) < 0.01);
        }

        // ── Validation: missing ticker ───────────────────────
        {
            var req = new TheoreticalOptionSimulationRequest
            {
                Ticker = "",
                StrategyType = OptionsStrategyType.long_call_proxy,
                StartingStockPrice = 150,
                EndingStockPrice = 160,
                DaysToExpiration = 30,
                AssumedImpliedVolatility = 0.30,
                PremiumMode = PremiumMode.manual,
                ManualPremium = 5,
                StrikePrice = 150,
            };
            var v = StrategyAssumptionValidator.Validate(req);
            Assert("Validation: missing ticker is invalid", !v.IsValid);
            Assert("Validation: ticker error present", v.Errors.Any(e => e.Contains("ticker")));
        }

        // ── Validation: missing strike ───────────────────────
        {
            var req = new TheoreticalOptionSimulationRequest
            {
                Ticker = "AAPL",
                StrategyType = OptionsStrategyType.long_call_proxy,
                StartingStockPrice = 150,
                EndingStockPrice = 160,
                DaysToExpiration = 30,
                AssumedImpliedVolatility = 0.30,
                PremiumMode = PremiumMode.manual,
                ManualPremium = 5,
                StrikePrice = null,
            };
            var v = StrategyAssumptionValidator.Validate(req);
            Assert("Validation: missing strike is invalid", !v.IsValid);
        }

        // ── Validation: missing manual premium ───────────────
        {
            var req = new TheoreticalOptionSimulationRequest
            {
                Ticker = "AAPL",
                StrategyType = OptionsStrategyType.long_put_proxy,
                StartingStockPrice = 150,
                EndingStockPrice = 140,
                DaysToExpiration = 30,
                AssumedImpliedVolatility = 0.30,
                PremiumMode = PremiumMode.manual,
                ManualPremium = null,
                StrikePrice = 150,
            };
            var v = StrategyAssumptionValidator.Validate(req);
            Assert("Validation: missing manual premium is invalid", !v.IsValid);
        }

        // ── Validation: zero starting price ──────────────────
        {
            var req = new TheoreticalOptionSimulationRequest
            {
                Ticker = "AAPL",
                StrategyType = OptionsStrategyType.long_call_proxy,
                StartingStockPrice = 0,
                EndingStockPrice = 160,
                DaysToExpiration = 30,
                AssumedImpliedVolatility = 0.30,
                PremiumMode = PremiumMode.manual,
                ManualPremium = 5,
                StrikePrice = 150,
            };
            var v = StrategyAssumptionValidator.Validate(req);
            Assert("Validation: zero starting price is invalid", !v.IsValid);
        }

        // ── Validation: valid request passes ─────────────────
        {
            var req = new TheoreticalOptionSimulationRequest
            {
                Ticker = "AAPL",
                StrategyType = OptionsStrategyType.long_call_proxy,
                StartingStockPrice = 150,
                EndingStockPrice = 160,
                DaysToExpiration = 30,
                AssumedImpliedVolatility = 0.30,
                PremiumMode = PremiumMode.manual,
                ManualPremium = 5,
                StrikePrice = 150,
            };
            var v = StrategyAssumptionValidator.Validate(req);
            Assert("Validation: valid request passes", v.IsValid);
        }

        // ── Theoretical premium estimate is positive ─────────
        {
            var premium = StrategyPayoffCalculator.EstimateTheoreticalPremium(
                150, 150, 0.30, 0.05, 30, isCall: true);
            Assert("TheoreticalPremium ATM call > 0", premium > 0);
            Assert("TheoreticalPremium ATM call reasonable", premium < 20);

            var putPremium = StrategyPayoffCalculator.EstimateTheoreticalPremium(
                150, 150, 0.30, 0.05, 30, isCall: false);
            Assert("TheoreticalPremium ATM put > 0", putPremium > 0);
        }

        // ── Direction matching ───────────────────────────────
        {
            Assert("BullishMatchesLongCall", PredictionStrategyMapper.DirectionMatchesStrategy("bullish", OptionsStrategyType.long_call_proxy));
            Assert("BearishMatchesLongPut", PredictionStrategyMapper.DirectionMatchesStrategy("bearish", OptionsStrategyType.long_put_proxy));
            Assert("NeutralMatchesIronCondor", PredictionStrategyMapper.DirectionMatchesStrategy("neutral", OptionsStrategyType.iron_condor_proxy));
            Assert("BullishDoesNotMatchPut", !PredictionStrategyMapper.DirectionMatchesStrategy("bullish", OptionsStrategyType.long_put_proxy));
        }

        // ── Warnings always present ──────────────────────────
        {
            var r = StrategyPayoffCalculator.CalculateLongCall(150, 160, 150, 5, assumptions);
            Assert("Warnings not empty", r.Warnings.Count >= 3);
            Assert("Warnings contain THEORETICAL", r.Warnings[0].Contains("THEORETICAL"));
        }

        return (passed, failures.Count, failures);
    }
}
