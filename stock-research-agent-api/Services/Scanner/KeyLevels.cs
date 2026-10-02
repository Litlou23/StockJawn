using static StockResearchAgent.Api.Services.Broker.AlpacaBrokerAdapter;

namespace StockResearchAgent.Api.Services.Scanner;

public record KeyLevel(double Price, int Touches, string Kind, DateTime LastTouch, string Source);

// Flat support/resistance the way a trader marks it by eye: prices where the stock turned around several times.
// Swing highs and lows are clustered (they flip roles: old resistance becomes support), and a level that price
// reversed at 3+ times is "strong".
public static class KeyLevels
{
    public static List<KeyLevel> Compute(IReadOnlyList<DailyBar> bars, double close, int window = 3, double tolerancePct = 0.75, int minTouches = 2)
    {
        var pivots = new List<(double Price, DateTime Date)>();
        for (var i = window; i < bars.Count - window; i++)
        {
            // Strict on the left so a flat stretch counts once, not once per bar.
            var left = Enumerable.Range(i - window, window).Select(j => bars[j]).ToList();
            var right = Enumerable.Range(i + 1, window).Select(j => bars[j]).ToList();
            if (bars[i].High > left.Max(b => b.High) && bars[i].High >= right.Max(b => b.High)) pivots.Add((bars[i].High, bars[i].Date));
            if (bars[i].Low < left.Min(b => b.Low) && bars[i].Low <= right.Min(b => b.Low)) pivots.Add((bars[i].Low, bars[i].Date));
        }

        var levels = new List<KeyLevel>();
        var cluster = new List<(double Price, DateTime Date)>();
        foreach (var p in pivots.OrderBy(p => p.Price))
        {
            if (cluster.Count > 0 && Math.Abs(p.Price - cluster.Average(c => c.Price)) / p.Price * 100 > tolerancePct)
            {
                Flush();
                cluster.Clear();
            }
            cluster.Add(p);
        }
        Flush();

        return levels.Where(l => l.Touches >= minTouches)
            .OrderBy(l => Math.Abs(l.Price - close))
            .ToList();

        void Flush()
        {
            if (cluster.Count == 0) return;
            var price = Math.Round(cluster.Average(c => c.Price), 2);
            levels.Add(new KeyLevel(price, cluster.Count, price < close ? "support" : "resistance", cluster.Max(c => c.Date), "computed"));
        }
    }

    public static KeyLevel? NearestAbove(IEnumerable<KeyLevel> levels, double price, double minGapPct = 0.5)
        => levels.Where(l => l.Price > price * (1 + minGapPct / 100)).OrderBy(l => l.Price).FirstOrDefault();

    public static KeyLevel? NearestBelow(IEnumerable<KeyLevel> levels, double price, double minGapPct = 0.5)
        => levels.Where(l => l.Price < price * (1 - minGapPct / 100)).OrderByDescending(l => l.Price).FirstOrDefault();

    public static string Describe(KeyLevel l) => $"{l.Price:F2}{(l.Touches > 0 ? $" ({l.Touches}x)" : "")}";
}
