using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.OptionsLab;

/// <summary>
/// Calculates realized volatility from real underlying price bars.
/// This is NOT implied volatility from options — it is a proxy derived
/// from historical stock price movement only.
/// </summary>
public static class RealizedVolatilityCalculator
{
    /// <summary>
    /// Annualized realized volatility from daily log returns.
    /// Returns 0 if insufficient data.
    /// </summary>
    public static double Calculate(List<MarketSnapshotBar> bars)
    {
        if (bars.Count < 3) return 0;

        var logReturns = new List<double>();
        for (int i = 1; i < bars.Count; i++)
        {
            if (bars[i - 1].Close <= 0 || bars[i].Close <= 0) continue;
            logReturns.Add(Math.Log(bars[i].Close / bars[i - 1].Close));
        }

        if (logReturns.Count < 2) return 0;

        var mean = logReturns.Average();
        var variance = logReturns.Sum(r => (r - mean) * (r - mean)) / (logReturns.Count - 1);
        var dailyVol = Math.Sqrt(variance);

        // Annualize: daily vol * sqrt(252 trading days)
        return dailyVol * Math.Sqrt(252);
    }

    /// <summary>
    /// Average True Range from bars (not annualized, in price units).
    /// </summary>
    public static double CalculateATR(List<MarketSnapshotBar> bars)
    {
        if (bars.Count < 2) return 0;

        var trueRanges = new List<double>();
        for (int i = 1; i < bars.Count; i++)
        {
            var high = bars[i].High;
            var low = bars[i].Low;
            var prevClose = bars[i - 1].Close;

            var tr = Math.Max(high - low, Math.Max(
                Math.Abs(high - prevClose),
                Math.Abs(low - prevClose)));
            trueRanges.Add(tr);
        }

        return trueRanges.Count > 0 ? trueRanges.Average() : 0;
    }

    /// <summary>
    /// Average absolute daily move as a percentage.
    /// </summary>
    public static double AverageDailyMovePercent(List<MarketSnapshotBar> bars)
    {
        if (bars.Count < 2) return 0;

        var moves = new List<double>();
        for (int i = 1; i < bars.Count; i++)
        {
            if (bars[i - 1].Close <= 0) continue;
            moves.Add(Math.Abs((bars[i].Close - bars[i - 1].Close) / bars[i - 1].Close * 100));
        }

        return moves.Count > 0 ? moves.Average() : 0;
    }

    /// <summary>
    /// Estimate expected move over N days using realized volatility.
    /// Formula: price * vol * sqrt(days/252)
    /// This is NOT options-implied expected move.
    /// </summary>
    public static double EstimateExpectedMove(double currentPrice, double annualizedVol, int days)
    {
        if (currentPrice <= 0 || annualizedVol <= 0 || days <= 0) return 0;
        return currentPrice * annualizedVol * Math.Sqrt(days / 252.0);
    }
}
