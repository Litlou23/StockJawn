using static StockResearchAgent.Api.Services.Broker.AlpacaBrokerAdapter;

namespace StockResearchAgent.Api.Services.Scanner;

public record SectorRank(string Etf, string Name, double Ret1w, double Ret1m, double Rs1m, bool AboveSma20, int Rank, string Group);
public record SectorLeader(string Ticker, string Etf, double Price, double Ret1m, double Rs1m);

// Which groups are leading the market (1-month return vs SPY). On 10/2 chips were +14.6% for the month while most of
// the market fell, and our calls were all in falling groups. Calls go with leading groups, puts with lagging ones.
public static class SectorStrength
{
    public const string DefaultEtfs = "XLK,SMH,XLV,XBI,XLF,KRE,XLY,XLE,XLI,XLB,XLRE,XLU,XLC,XLP,ITB,JETS,GLD,SLV,URA,TAN,XME";

    // ETF -> FMP sector, an industry keyword that narrows it (null = whole sector), and a plain name.
    public static readonly Dictionary<string, (string Sector, string? Industry, string Name)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XLK"] = ("Technology", null, "Tech"),
        ["SMH"] = ("Technology", "Semiconductor", "Chips"),
        ["XLV"] = ("Healthcare", null, "Healthcare"),
        ["XBI"] = ("Healthcare", "Biotech", "Biotech"),
        ["XLF"] = ("Financial Services", null, "Financials"),
        ["KRE"] = ("Financial Services", "Regional", "Regional banks"),
        ["XLY"] = ("Consumer Cyclical", null, "Consumer"),
        ["XLE"] = ("Energy", null, "Energy"),
        ["XLI"] = ("Industrials", null, "Industrials"),
        ["XLB"] = ("Basic Materials", null, "Materials"),
        ["XLRE"] = ("Real Estate", null, "Real estate"),
        ["XLU"] = ("Utilities", null, "Utilities"),
        ["XLC"] = ("Communication Services", null, "Communication"),
        ["XLP"] = ("Consumer Defensive", null, "Staples"),
        ["ITB"] = ("Consumer Cyclical", "Residential Construction", "Homebuilders"),
        ["JETS"] = ("Industrials", "Airline", "Airlines"),
        ["GLD"] = ("Basic Materials", "Gold", "Gold"),
        ["SLV"] = ("Basic Materials", "Silver", "Silver"),
        ["URA"] = ("Energy", "Uranium", "Uranium"),
        ["TAN"] = ("Technology", "Solar", "Solar"),
        ["XME"] = ("Basic Materials", "Steel", "Metals & mining"),
    };

    public static List<SectorRank> Rank(IEnumerable<string> etfs, IReadOnlyDictionary<string, List<DailyBar>> bars, DateTime today)
    {
        if (!bars.TryGetValue("SPY", out var spy) || spy.Count < 22 || spy[^1].Date != today) return [];
        var spy1m = Ret(spy, 21);
        var rows = etfs.Where(bars.ContainsKey)
            .Select(e => (Etf: e, Bars: bars[e]))
            .Where(x => x.Bars.Count >= 22 && x.Bars[^1].Date == today)
            .Select(x => (x.Etf, R1w: Ret(x.Bars, 5), R1m: Ret(x.Bars, 21), Above: x.Bars[^1].Close > x.Bars.TakeLast(20).Average(b => b.Close)))
            .OrderByDescending(x => x.R1m - spy1m)
            .ToList();
        return rows.Select((x, i) =>
        {
            var rs = x.R1m - spy1m;
            var group = i < 3 && rs > 0 ? "leading" : i >= rows.Count - 3 && rs < 0 ? "lagging" : "middle";
            return new SectorRank(x.Etf, Map.TryGetValue(x.Etf, out var m) ? m.Name : x.Etf, Math.Round(x.R1w, 2), Math.Round(x.R1m, 2),
                Math.Round(rs, 2), x.Above, i + 1, group);
        }).ToList();
    }

    public static double Ret(List<DailyBar> b, int n) => b.Count > n && b[^(n + 1)].Close > 0 ? (b[^1].Close / b[^(n + 1)].Close - 1) * 100 : 0;

    // A stock's group: the industry-specific ETF when the industry matches (chips, biotech...), else its sector ETF.
    public static string? EtfFor(string? sector, string? industry)
    {
        if (string.IsNullOrWhiteSpace(sector)) return null;
        var byIndustry = Map.FirstOrDefault(kv => kv.Value.Industry is { } kw && kv.Value.Sector.Equals(sector, StringComparison.OrdinalIgnoreCase)
                                                  && industry?.Contains(kw, StringComparison.OrdinalIgnoreCase) == true);
        if (byIndustry.Key is not null) return byIndustry.Key;
        return Map.FirstOrDefault(kv => kv.Value.Industry is null && kv.Value.Sector.Equals(sector, StringComparison.OrdinalIgnoreCase)).Key;
    }
}
