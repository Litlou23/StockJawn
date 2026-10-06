using StockResearchAgent.Api.Services.Supabase;
using static StockResearchAgent.Api.Services.Broker.AlpacaBrokerAdapter;

namespace StockResearchAgent.Api.Services.Scanner;

// Trade with the trend (O'Neil: 3 of 4 stocks follow the market; buy leaders in leading groups). Our year-long
// backtest agreed: breakouts against the trend broke even, weakness in weak groups paid.
public static class TrendRules
{
    public record Group(string Etf, string Name, string Grp, bool AboveSma20);

    // SPY against its 20- and 50-day averages: above both = up, below both = down.
    public static string Market(IReadOnlyList<DailyBar> spy)
    {
        if (spy.Count < 50) return "unknown";
        var close = spy[^1].Close;
        var sma20 = spy.Skip(spy.Count - 20).Average(b => b.Close);
        var sma50 = spy.Skip(spy.Count - 50).Average(b => b.Close);
        return close > sma20 && close > sma50 ? "up" : close < sma20 && close < sma50 ? "down" : "mixed";
    }

    // Null = fine; otherwise the reason the setup fights the trend.
    public static string? Check(string direction, string market, string? group, bool groupAboveSma20, bool relativeWeakness)
    {
        if (direction != "bearish")
        {
            if (market == "down") return "market in a downtrend";
            if (group == "lagging") return "its group is lagging";
            if (group == "middle" && !groupAboveSma20) return "its group is below its 20-day average";
            if (group is null && market != "up") return "group unknown and the market isn't in an uptrend";
            return null;
        }
        return market == "down" || group == "lagging" || relativeWeakness
            ? null
            : "puts need a weak market, a lagging group or a stock weaker than SPY";
    }

    // Minervini: buy only stocks in their own uptrend, short only ones in a downtrend. Null = fine.
    // Fewer than 20 bars (a brand-new listing) can't be judged; between 20 and 50 uses what there is.
    public static string? OwnTrend(string direction, IReadOnlyList<DailyBar> bars, double price, int days = 50)
    {
        var n = Math.Min(days, bars.Count);
        if (n < 20 || price <= 0) return null;
        var sma = bars.Skip(bars.Count - n).Average(b => b.Close);
        var pct = (price / sma - 1) * 100;
        if (direction != "bearish") return price > sma ? null : $"the stock is {-pct:F0}% below its {n}-day average (its own downtrend)";
        return price < sma ? null : $"the stock is {pct:F0}% above its {n}-day average (its own uptrend)";
    }

    // The FRSH pattern (10/6): near its highest price in months and above its 20- and 50-day averages,
    // so there's nobody above waiting to sell at breakeven. Needs ~3 months of bars.
    public static bool Leader(IReadOnlyList<DailyBar> bars, double price)
    {
        if (bars.Count < 60 || price <= 0) return false;
        var sma20 = bars.Skip(bars.Count - 20).Average(b => b.Close);
        var sma50 = bars.Skip(bars.Count - 50).Average(b => b.Close);
        return price >= bars.Max(b => b.High) * 0.97 && price > sma20 && price > sma50;
    }

    // Latest sector ranking from the 4:30 PM scan.
    public static async Task<List<Group>> LoadGroupsAsync(SupabaseClient db)
    {
        try
        {
            var rows = await db.SelectAsync("sector_strength", order: "trade_date.desc", limit: 40, select: "trade_date,etf,name,grp,above_sma20");
            var latest = rows.FirstOrDefault()?["trade_date"]?.ToString();
            return rows.Where(r => r["trade_date"]?.ToString() == latest)
                .Select(r => new Group(r["etf"]?.ToString() ?? "", r["name"]?.ToString() ?? "", r["grp"]?.ToString() ?? "middle",
                    r["above_sma20"]?.ToString()?.ToLowerInvariant() == "true"))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
