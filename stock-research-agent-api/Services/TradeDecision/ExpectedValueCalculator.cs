using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Pure calculation service — no database access, no side effects.
///
/// Formula:
///   EV = (WinRate × AverageWinPercent) − ((1 − WinRate) × AverageLossPercent)
///
/// Example:
///   WinRate=0.65, AvgWin=12%, AvgLoss=6%
///   EV = (0.65 × 12) − (0.35 × 6) = 7.8 − 2.1 = 5.7%
/// </summary>
public class ExpectedValueCalculator : IExpectedValueCalculator
{
    public ExpectedValueResult Calculate(ExpectedValueRequest request)
    {
        var winRate = Math.Clamp(request.WinRate, 0.0, 1.0);
        var lossRate = 1.0 - winRate;
        var avgWin = Math.Max(request.AverageWinPercent, 0.0);
        var avgLoss = Math.Max(request.AverageLossPercent, 0.0);

        var ev = (winRate * avgWin) - (lossRate * avgLoss);

        return new ExpectedValueResult
        {
            ExpectedValue = Math.Round(ev, 4),
            WinRate = winRate,
            AverageWinPercent = avgWin,
            AverageLossPercent = avgLoss,
            PositiveExpectancy = ev > 0,
        };
    }
}
