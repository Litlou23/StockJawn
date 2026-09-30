using System.Globalization;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.UniverseDiscovery;
using static StockResearchAgent.Api.Services.Broker.AlpacaBrokerAdapter;

namespace StockResearchAgent.Api.Services.Scanner;

public record MoverSetup(
    string Ticker, string Direction, double Close, double ChangePct, double RelVolume, string Pattern,
    double Trigger, double Stop, double Target, string? News, double Rank);

public record MoversScanResult(DateTime ScanDate, DateTime PickDate, int Universe, List<MoverSetup> Setups, List<string> Notes);

// StockedUp's nightly routine as code: take the day's biggest movers, keep the ones that closed at an extreme
// (the high/low, a new 20-day high/low, a double top/bottom) on heavy volume, and write "if it breaks X" levels.
// Rows land in claude_daily_picks as 'research' for the nightly/premarket jobs to check.
public class MoversScanner
{
    private const string Table = "claude_daily_picks";
    private const string NotePrefix = "SCANNER";
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly AlpacaBrokerAdapter _alpaca;
    private readonly FmpClient _fmp;
    private readonly SupabaseClient _db;
    private readonly ILogger<MoversScanner> _logger;

    public MoversScanner(AlpacaBrokerAdapter alpaca, FmpClient fmp, SupabaseClient db, ILogger<MoversScanner> logger)
    {
        _alpaca = alpaca;
        _fmp = fmp;
        _db = db;
        _logger = logger;
    }

    public static DateTime TodayEt() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern).Date;

    public static DateTime NextTradingDay(DateTime d)
    {
        var n = d.AddDays(1);
        while (n.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) n = n.AddDays(1);
        return n;
    }

    public async Task<MoversScanResult> ScanAsync(bool write, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var today = TodayEt();
        var pickDate = NextTradingDay(today);

        if (!_alpaca.IsConfigured)
            return new(today, pickDate, 0, [], ["Alpaca not configured (ALPACA_API_KEY / ALPACA_API_SECRET) — no movers data"]);

        var minMove = await NumberAsync("scan_min_move_pct", 3);
        var minRelVol = await NumberAsync("scan_min_rel_volume", 1.5);
        var minPrice = await NumberAsync("risk_min_stock_price", 4);
        var maxPrice = await NumberAsync("scan_max_price", 100);
        var maxSetups = (int)await NumberAsync("scan_max_candidates", 10);
        var stopPct = await NumberAsync("scan_stop_pct", 2);
        var maxTriggerDist = await NumberAsync("scan_max_trigger_distance_pct", 4);
        var maxMove = await NumberAsync("scan_max_move_pct", 25);

        var movers = await _alpaca.GetTopMoversAsync(50);
        var actives = await _alpaca.GetMostActivesAsync(50);
        var universe = movers.Select(m => m.Ticker).Concat(actives.Select(a => a.Ticker))
            .Where(IsPlainTicker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (universe.Count == 0)
            return new(today, pickDate, 0, [], ["Alpaca screener returned no movers"]);

        var bars = await _alpaca.GetDailyBarsAsync(universe, 45);
        var setups = new List<MoverSetup>();
        foreach (var ticker in universe)
        {
            if (!bars.TryGetValue(ticker, out var b) || b.Count < 22) continue;
            var setup = Evaluate(ticker, b, today, minMove, minRelVol, minPrice, maxPrice, stopPct, maxTriggerDist, maxMove);
            if (setup is not null) setups.Add(setup);
        }

        setups = setups.OrderByDescending(s => s.Rank).Take(maxSetups).ToList();
        if (setups.Count > 0) setups = await AttachNewsAsync(setups, today);

        notes.Add($"{universe.Count} movers checked, {setups.Count} setups");
        if (write && setups.Count > 0) await WriteAsync(setups, today, pickDate, notes);
        return new(today, pickDate, universe.Count, setups, notes);
    }

    public static MoverSetup? Evaluate(string ticker, List<DailyBar> b, DateTime today,
        double minMove, double minRelVol, double minPrice, double maxPrice, double stopPct, double maxTriggerDistPct = 4, double maxMovePct = 25)
    {
        var bar = b[^1];
        if (bar.Date != today) return null;
        var prev = b[^2];
        var prior20 = b.Skip(Math.Max(0, b.Count - 21)).Take(20).ToList();
        if (prev.Close <= 0 || bar.High <= bar.Low || prior20.Count < 20) return null;
        if (bar.Close < minPrice || bar.Close > maxPrice) return null;

        var change = (bar.Close / prev.Close - 1) * 100;
        var avgVol = prior20.Average(x => x.Volume);
        var relVol = avgVol > 0 ? bar.Volume / avgVol : 0;
        // Huge one-day moves (a failed trial, a buyout) are events, not clean continuations.
        if (Math.Abs(change) < minMove || Math.Abs(change) > maxMovePct || relVol < minRelVol) return null;

        var closeLoc = (bar.Close - bar.Low) / (bar.High - bar.Low);
        var high20 = prior20.Max(x => x.High);
        var low20 = prior20.Min(x => x.Low);
        var last10 = prior20.Skip(10).ToList();
        var doubleTop = last10.Any(x => Math.Abs(x.High - bar.High) / bar.High <= 0.005) && bar.Close >= bar.High * 0.98;
        var doubleBottom = last10.Any(x => Math.Abs(x.Low - bar.Low) / bar.Low <= 0.005) && bar.Close <= bar.Low * 1.02;

        string dir;
        var tags = new List<string>();
        if (change > 0 && (closeLoc >= 0.7 || bar.High > high20 || doubleTop))
        {
            dir = "bullish";
            if (closeLoc >= 0.7) tags.Add("closed near the high");
            if (bar.High > high20) tags.Add("new 20-day high");
            if (doubleTop) tags.Add("double top");
        }
        else if (change < 0 && (closeLoc <= 0.3 || bar.Low < low20 || doubleBottom))
        {
            dir = "bearish";
            if (closeLoc <= 0.3) tags.Add("closed near the low");
            if (bar.Low < low20) tags.Add("new 20-day low");
            if (doubleBottom) tags.Add("double bottom");
        }
        else return null;

        // Trigger = the day's extreme; stop just back through it; target 2x the risk.
        double trigger, stop, target;
        if (dir == "bullish")
        {
            trigger = Math.Round(bar.High + 0.01, 2);
            stop = Math.Round(trigger * (1 - stopPct / 100), 2);
            target = Math.Round(trigger + 2 * (trigger - stop), 2);
        }
        else
        {
            trigger = Math.Round(bar.Low - 0.01, 2);
            stop = Math.Round(trigger * (1 + stopPct / 100), 2);
            target = Math.Round(trigger - 2 * (stop - trigger), 2);
        }

        // Farther than this and it rarely breaks the next day (same rule as the premarket setup check).
        if (Math.Abs(trigger - bar.Close) / bar.Close * 100 > maxTriggerDistPct) return null;

        // Capped so one -57% crash or 18x volume spike can't bury every normal setup.
        var rank = Math.Min(Math.Abs(change), 15) * Math.Min(relVol, 5) * (1 + 0.25 * (tags.Count - 1)) * (closeLoc is >= 0.85 or <= 0.15 ? 1.2 : 1);
        return new MoverSetup(ticker, dir, bar.Close, Math.Round(change, 2), Math.Round(relVol, 2),
            string.Join(", ", tags), trigger, stop, target, null, Math.Round(rank, 2));
    }

    private async Task<List<MoverSetup>> AttachNewsAsync(List<MoverSetup> setups, DateTime today)
    {
        if (!_fmp.IsConfigured) return setups;
        var since = today.AddDays(-2);
        try
        {
            var grades = await _fmp.GetUpgradesDowngradesAsync(200);
            var news = await _fmp.GetStockNewsAsync(200);
            return setups.Select(s =>
            {
                var g = grades.FirstOrDefault(x => x.Symbol.Equals(s.Ticker, StringComparison.OrdinalIgnoreCase) && x.ParsedDate.Date >= since);
                if (g is not null)
                    return s with { News = $"{g.GradingCompany} {g.Action} to {g.NewGrade} ({g.ParsedDate:M/d})" };
                var n = news.FirstOrDefault(x => x.Symbol.Equals(s.Ticker, StringComparison.OrdinalIgnoreCase) && x.ParsedDate.Date >= since);
                return n is null ? s : s with { News = $"{Trim(n.Title, 110)} ({n.ParsedDate:M/d})" };
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[movers-scan] News lookup failed — setups saved without news");
            return setups;
        }
    }

    private async Task WriteAsync(List<MoverSetup> setups, DateTime today, DateTime pickDate, List<string> notes)
    {
        var pd = pickDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // Re-runs replace the scanner's own rows only; the nightly job's rows stay.
        await _db.DeleteAsync(Table, $"pick_date=eq.{pd}&approval_status=eq.research&notes=like.{NotePrefix}*");

        var rows = setups.Select(s =>
        {
            var side = s.Direction == "bullish" ? "above" : "below";
            var move = s.ChangePct >= 0 ? $"+{s.ChangePct:F1}%" : $"{s.ChangePct:F1}%";
            var line = $"{NotePrefix} {today:M/d}: {s.Ticker} {move} on {s.RelVolume:F1}x volume, {s.Pattern} → " +
                       $"{(s.Direction == "bullish" ? "calls" : "puts")} {side} ${s.Trigger:F2}, target ${s.Target:F2}, out ${s.Stop:F2}" +
                       (s.News is null ? "" : $" · news: {s.News}");
            return (object)new Dictionary<string, object?>
            {
                ["pick_date"] = pd,
                ["ticker"] = s.Ticker,
                ["direction"] = s.Direction,
                ["conviction"] = "medium",
                ["approval_status"] = "research",
                ["order_type"] = "stock",
                ["entry_price"] = s.Close,
                ["trigger_price"] = s.Trigger,
                ["trigger_direction"] = side,
                ["level_target"] = s.Target,
                ["level_stop"] = s.Stop,
                ["target_price"] = s.Target,
                ["stop_price"] = s.Stop,
                ["catalyst"] = s.News ?? $"Momentum: {s.Pattern} on {s.RelVolume:F1}x volume ({today:M/d})",
                ["notes"] = line,
            };
        }).ToList();

        await _db.InsertAsync(Table, rows, returnRows: false);
        notes.Add($"wrote {rows.Count} research rows for {pd}");
        _logger.LogInformation("[movers-scan] Wrote {Count} setups for {PickDate}", rows.Count, pd);
    }

    private static bool IsPlainTicker(string t)
        => t.Length is >= 1 and <= 5 && t.All(char.IsLetter);

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";

    private async Task<double> NumberAsync(string signal, double fallback)
    {
        try
        {
            var row = await _db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signal}&status=eq.active");
            return double.TryParse(row?["effective_weight"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
