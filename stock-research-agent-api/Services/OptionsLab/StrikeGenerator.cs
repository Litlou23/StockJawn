namespace StockResearchAgent.Api.Services.OptionsLab;

/// <summary>
/// Generates theoretical strikes from real underlying stock price and expected move.
/// Does not invent real option contract symbols or chain data.
/// Strikes are rounded to sensible increments based on stock price.
/// </summary>
public static class StrikeGenerator
{
    /// <summary>Round to nearest strike increment (1 for cheap stocks, 5 for expensive).</summary>
    public static double RoundToStrike(double price)
    {
        var increment = price switch
        {
            < 25 => 0.5,
            < 100 => 1.0,
            < 250 => 2.5,
            _ => 5.0,
        };
        return Math.Round(price / increment) * increment;
    }

    /// <summary>ATM strike near current price.</summary>
    public static double AtmStrike(double currentPrice) => RoundToStrike(currentPrice);

    /// <summary>OTM call strike above current price.</summary>
    public static double OtmCallStrike(double currentPrice, double expectedMove)
    {
        var target = currentPrice + Math.Max(expectedMove, currentPrice * 0.02);
        return RoundToStrike(target);
    }

    /// <summary>OTM put strike below current price.</summary>
    public static double OtmPutStrike(double currentPrice, double expectedMove)
    {
        var target = currentPrice - Math.Max(expectedMove, currentPrice * 0.02);
        return RoundToStrike(target);
    }

    /// <summary>Generate strikes for a bull call spread.</summary>
    public static (double Lower, double Upper) BullCallSpreadStrikes(
        double currentPrice, double expectedMove, bool aggressive)
    {
        var lower = aggressive ? AtmStrike(currentPrice) : RoundToStrike(currentPrice - expectedMove * 0.25);
        var upper = RoundToStrike(currentPrice + expectedMove * (aggressive ? 1.5 : 1.0));
        if (upper <= lower) upper = lower + SpreadWidth(currentPrice);
        return (lower, upper);
    }

    /// <summary>Generate strikes for a bear put spread.</summary>
    public static (double Upper, double Lower) BearPutSpreadStrikes(
        double currentPrice, double expectedMove, bool aggressive)
    {
        var upper = aggressive ? AtmStrike(currentPrice) : RoundToStrike(currentPrice + expectedMove * 0.25);
        var lower = RoundToStrike(currentPrice - expectedMove * (aggressive ? 1.5 : 1.0));
        if (lower >= upper) lower = upper - SpreadWidth(currentPrice);
        return (upper, lower);
    }

    /// <summary>Generate strikes for an iron condor.</summary>
    public static (double LongPut, double ShortPut, double ShortCall, double LongCall) IronCondorStrikes(
        double currentPrice, double expectedMove)
    {
        var move = Math.Max(expectedMove, currentPrice * 0.03);
        var shortPut = RoundToStrike(currentPrice - move);
        var shortCall = RoundToStrike(currentPrice + move);
        var wingWidth = SpreadWidth(currentPrice);
        var longPut = RoundToStrike(shortPut - wingWidth);
        var longCall = RoundToStrike(shortCall + wingWidth);
        return (longPut, shortPut, shortCall, longCall);
    }

    private static double SpreadWidth(double price) => price switch
    {
        < 25 => 2.5,
        < 100 => 5.0,
        < 250 => 10.0,
        _ => 25.0,
    };
}
