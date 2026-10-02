using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Calendar;
using StockResearchAgent.Api.Services.Supabase;
using static StockResearchAgent.Api.Services.Broker.AlpacaBrokerAdapter;

namespace StockResearchAgent.Api.Services.Scanner;

public record IntradaySetup(
    string Ticker, string Direction, double Price, double ChangePct, double RsVsSpy, double RelVolume,
    double Trigger, double Stop, double Target, string Why, double Rank, string Route);

public record IntradayScanResult(DateTime ScanTimeEt, double SpyChangePct, List<string> LeadingThemes,
    List<IntradaySetup> Setups, List<string> OverBudgetLeaders, List<string> Notes);

// The market-hours scan: the night-before picks can't see moves that start after 9:30 (chips and TSLA on 10/2).
// At intraday_scan_times_et it finds the stocks leading the market today (strong vs SPY, heavy volume, holding
// above VWAP and the first 30 minutes' high), stages them with a trigger at the day's high, and pings Lou's phone.
// Shares priced within the per-trade cap go in as 'pending' for the approval page; anything that needs an option
// goes in as 'research' for Lenny to pick a contract. Nothing buys without Lou's approval.
public class IntradayScanner
{
    private const string Table = "claude_daily_picks";
    private const string NotePrefix = "INTRADAY";
    private const string DefaultThemes = "XLE,USO,XBI,SMH,XLF,KRE,GLD,SLV,XLU,XHB,ITB,JETS,TAN,URA,XME,ARKK";
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly AlpacaBrokerAdapter _alpaca;
    private readonly SupabaseClient _db;
    private readonly TradingCalendar _calendar;
    private readonly ILogger<IntradayScanner> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public IntradayScanner(AlpacaBrokerAdapter alpaca, SupabaseClient db, TradingCalendar calendar, ILogger<IntradayScanner> logger)
    {
        _alpaca = alpaca;
        _db = db;
        _calendar = calendar;
        _logger = logger;
    }

    public async Task<IntradayScanResult> ScanAsync(bool write, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var now = TradingCalendar.NowEt();
        var today = now.Date;
        IntradayScanResult Empty(string why) => new(now, 0, [], [], [], [why]);

        if (!_alpaca.IsConfigured) return Empty("Alpaca not configured — no market data");
        if (!await _calendar.IsTradingDayAsync(today)) return Empty("market closed today");
        var open = today.AddHours(9.5);
        if (now < open.AddMinutes(30)) return Empty("too early: the scan needs the first 30 minutes of trading");

        var minMove = await NumberAsync("intraday_min_move_pct", 2);
        var maxMove = await NumberAsync("intraday_max_move_pct", 15);
        var minRs = await NumberAsync("intraday_min_rs_pct", 1.5);
        var minRelVol = await NumberAsync("intraday_min_rel_volume", 1.5);
        var maxPicks = (int)await NumberAsync("intraday_max_picks", 2);
        var minPrice = await NumberAsync("risk_min_stock_price", 4);
        var maxPrice = await NumberAsync("scan_max_price", 100);
        var maxTrade = await NumberAsync("risk_max_trade_dollars", 80);
        var themes = (await StringAsync("scan_theme_etfs", DefaultThemes))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToUpperInvariant()).ToList();

        var movers = await _alpaca.GetTopMoversAsync(50);
        var actives = await _alpaca.GetMostActivesAsync(50);
        var universe = movers.Select(m => m.Ticker).Concat(actives.Select(a => a.Ticker))
            .Where(t => t.Length is >= 1 and <= 5 && t.All(char.IsLetter))
            .Select(t => t.ToUpperInvariant())
            .Where(t => t != "SPY" && !themes.Contains(t))
            .Distinct()
            .ToList();
        if (universe.Count == 0) return Empty("Alpaca screener returned no movers");

        var snaps = await _alpaca.GetSnapshotsAsync(universe.Concat(themes).Append("SPY").ToList());
        if (!snaps.TryGetValue("SPY", out var spy) || spy.PrevClose <= 0 || spy.Last <= 0) return Empty("no SPY quote");
        var spyChg = (spy.Last / spy.PrevClose - 1) * 100;
        var bullishDay = spyChg >= -0.25;

        var leading = themes.Where(snaps.ContainsKey).Select(t => (t, chg: Chg(snaps[t])))
            .Where(x => !double.IsNaN(x.chg))
            .OrderByDescending(x => bullishDay ? x.chg : -x.chg)
            .Where(x => bullishDay ? x.chg - spyChg >= 0.75 : x.chg - spyChg <= -0.75)
            .Take(3)
            .Select(x => $"{x.t} {x.chg:+0.0;-0.0}%")
            .ToList();

        var daily = await _alpaca.GetDailyBarsAsync(universe, 200);
        var openUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(open, DateTimeKind.Unspecified), Eastern);
        var opening = await _alpaca.GetIntradayBarsAsync(universe, "30Min", openUtc);
        var elapsed = Math.Clamp((now - open).TotalMinutes / 390.0, 0.05, 1);

        var setups = new List<IntradaySetup>();
        foreach (var t in universe)
        {
            if (!snaps.TryGetValue(t, out var s) || s.PrevClose <= 0 || s.Last <= 0 || s.High <= s.Low) continue;
            if (s.Last < minPrice) continue;
            var hist = daily.TryGetValue(t, out var d) ? d.Where(b => b.Date < today).ToList() : [];
            if (hist.Count < 20) continue;
            var orBar = opening.TryGetValue(t, out var o) ? o.FirstOrDefault() : null;
            if (orBar is null) continue;

            var setup = Evaluate(t, s, spyChg, hist, orBar, elapsed, bullishDay, minMove, maxMove, minRs, minRelVol);
            if (setup is null) continue;
            var route = setup.Direction == "bullish" && setup.Price <= maxTrade ? "shares"
                : setup.Price <= maxPrice ? "option" : "over_budget";
            setups.Add(setup with { Route = route });
        }

        setups = setups.OrderByDescending(x => x.Rank).ToList();
        var already = (await _db.SelectAsync(Table, filter: $"pick_date=eq.{today:yyyy-MM-dd}", select: "ticker"))
            .Select(r => r["ticker"]?.ToString()?.ToUpperInvariant()).ToHashSet();
        var fresh = setups.Where(x => !already.Contains(x.Ticker)).ToList();
        var staged = fresh.Where(x => x.Route != "over_budget").Take(maxPicks).ToList();
        var overBudget = fresh.Where(x => x.Route == "over_budget").Take(5)
            .Select(x => $"{x.Ticker} ${x.Price:F0} {x.ChangePct:+0.0;-0.0}%").ToList();

        notes.Add($"SPY {spyChg:+0.00;-0.00}%, {universe.Count} movers checked, {setups.Count} leaders, {staged.Count} staged" +
                  (overBudget.Count > 0 ? $", {overBudget.Count} over the ${maxPrice:F0} cap" : ""));

        if (write && staged.Count > 0)
        {
            await WriteAsync(staged, now, today, notes);
            await AlertAsync(now, spyChg, leading, staged, overBudget, notes);
        }
        else if (write && overBudget.Count > 0)
        {
            await AlertAsync(now, spyChg, leading, staged, overBudget, notes);
        }
        return new(now, Math.Round(spyChg, 2), leading, write ? staged : fresh.Take(10).ToList(), overBudget, notes);
    }

    public static IntradaySetup? Evaluate(string ticker, Snapshot s, double spyChg, List<DailyBar> hist, DailyBar orBar,
        double elapsed, bool bullishDay, double minMove, double maxMove, double minRs, double minRelVol)
    {
        var chg = (s.Last / s.PrevClose - 1) * 100;
        var rs = chg - spyChg;
        var avgVol = hist.TakeLast(20).Average(b => b.Volume);
        var relVol = avgVol > 0 ? s.Volume / (avgVol * elapsed) : 0;
        if (relVol < minRelVol || Math.Abs(chg) > maxMove) return null;
        var vwap = s.Vwap > 0 ? s.Vwap : (s.High + s.Low + s.Last) / 3;

        string dir;
        double trigger, guard;
        if (bullishDay && chg >= minMove && rs >= minRs && s.Last >= vwap && s.Last > orBar.High && s.Last >= s.High * 0.985)
        {
            dir = "bullish";
            trigger = Math.Round(s.High + 0.01, 2);
            guard = Math.Max(vwap, orBar.High) - 0.01;
        }
        else if (!bullishDay && chg <= -minMove && rs <= -minRs && s.Last <= vwap && s.Last < orBar.Low && s.Last <= s.Low * 1.015)
        {
            dir = "bearish";
            trigger = Math.Round(s.Low - 0.01, 2);
            guard = Math.Min(vwap, orBar.Low) + 0.01;
        }
        else return null;

        var bull = dir == "bullish";
        // Stop just back through VWAP / the opening-range level, kept between 1.5% and 3% of the trigger.
        var riskPct = Math.Clamp(Math.Abs(trigger - guard) / trigger, 0.015, 0.03);
        var stop = Math.Round(bull ? trigger * (1 - riskPct) : trigger * (1 + riskPct), 2);
        var risk = Math.Abs(trigger - stop);

        var levels = KeyLevels.Compute(hist, s.Last);
        var next = bull ? KeyLevels.NearestAbove(levels, trigger) : KeyLevels.NearestBelow(levels, trigger);
        var target = next is not null && Math.Abs(next.Price - trigger) >= 1.5 * risk
            ? Math.Round(bull ? next.Price - 0.01 : next.Price + 0.01, 2)
            : Math.Round(bull ? trigger + 2 * risk : trigger - 2 * risk, 2);
        var levelNote = next is null ? "" : $", next {(bull ? "resistance" : "support")} {KeyLevels.Describe(next)}";

        var why = $"{chg:+0.0;-0.0}% today vs SPY {spyChg:+0.0;-0.0}% ({rs:+0.0;-0.0}% stronger), {relVol:F1}x normal volume, " +
                  $"{(bull ? "holding above VWAP and the first-30-minute high" : "below VWAP and the first-30-minute low")}{levelNote}";
        var nearExtreme = bull ? s.Last >= s.High * 0.995 : s.Last <= s.Low * 1.005;
        var rank = Math.Abs(rs) * Math.Min(relVol, 5) * (nearExtreme ? 1.2 : 1);

        return new IntradaySetup(ticker, dir, s.Last, Math.Round(chg, 2), Math.Round(rs, 2), Math.Round(relVol, 1),
            trigger, stop, target, why, Math.Round(rank, 2), "");
    }

    private async Task WriteAsync(List<IntradaySetup> setups, DateTime now, DateTime today, List<string> notes)
    {
        var exitBy = await _calendar.AddTradingDaysAsync(today, 2);
        var rows = setups.Select(s =>
        {
            var bull = s.Direction == "bullish";
            var side = bull ? "above" : "below";
            var how = s.Route == "shares" ? "shares" : bull ? "calls (Lenny: pick the contract)" : "puts (Lenny: pick the contract)";
            return (object)new Dictionary<string, object?>
            {
                ["pick_date"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["ticker"] = s.Ticker,
                ["direction"] = s.Direction,
                ["conviction"] = "medium",
                ["approval_status"] = s.Route == "shares" ? "pending" : "research",
                ["order_type"] = "stock",
                ["entry_price"] = s.Price,
                ["current_price"] = s.Price,
                ["trigger_price"] = s.Trigger,
                ["trigger_direction"] = side,
                ["level_target"] = s.Target,
                ["level_stop"] = s.Stop,
                ["target_price"] = s.Target,
                ["stop_price"] = s.Stop,
                ["exit_by_date"] = exitBy.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["total_score"] = 60,
                ["catalyst"] = $"Market-hours leader {now:h:mm}: {s.Why}",
                ["notes"] = $"{NotePrefix} {now:M/d h:mm}: {s.Ticker} {s.Why} → {how} {side} ${s.Trigger:F2}, target ${s.Target:F2}, out ${s.Stop:F2}",
            };
        }).ToList();

        await _db.InsertAsync(Table, rows, returnRows: false);
        notes.Add($"wrote {rows.Count} picks ({setups.Count(x => x.Route == "shares")} pending shares, {setups.Count(x => x.Route != "shares")} research for an option)");

        var forLenny = setups.Where(x => x.Route == "option").ToList();
        if (forLenny.Count > 0)
        {
            try
            {
                await _db.InsertAsync("claude_messages", new Dictionary<string, object?>
                {
                    ["sender"] = "StockJawn",
                    ["recipient"] = "Lenny",
                    ["topic"] = $"Market-hours scan {now:h:mm}: contract needed?",
                    ["body"] = "The market-hours scan staged these as research rows because shares are over the per-trade cap: " +
                               string.Join("; ", forLenny.Select(x => $"{x.Ticker} {(x.Direction == "bullish" ? "above" : "below")} ${x.Trigger:F2} (stop ${x.Stop:F2}, target ${x.Target:F2})")) +
                               ". If one is worth it, pick the option contract (ask $0.20 or more) and INSERT a new pending row with it; " +
                               "don't update the research row, because the 2-hour approval window counts from when a row was created.",
                }, returnRows: false);
            }
            catch (Exception ex)
            {
                notes.Add($"message to Lenny failed: {ex.Message}");
            }
        }
    }

    private async Task AlertAsync(DateTime now, double spyChg, List<string> leading, List<IntradaySetup> staged, List<string> overBudget, List<string> notes)
    {
        var lines = new List<string> { $"SPY {spyChg:+0.0;-0.0}%" + (leading.Count > 0 ? $" · leading: {string.Join(", ", leading)}" : "") };
        foreach (var s in staged)
        {
            var side = s.Direction == "bullish" ? "above" : "below";
            lines.Add(s.Route == "shares"
                ? $"APPROVE: {s.Ticker} shares {side} ${s.Trigger:F2} (stop ${s.Stop:F2}, target ${s.Target:F2}) {s.ChangePct:+0.0;-0.0}%"
                : $"Lenny picks an option: {s.Ticker} {side} ${s.Trigger:F2} {s.ChangePct:+0.0;-0.0}%");
        }
        if (overBudget.Count > 0) lines.Add($"Leading but over the price cap: {string.Join(", ", overBudget)}");

        try
        {
            var topic = await StringAsync("ntfy_topic", "stockjawn-picks-7428");
            var server = (await StringAsync("ntfy_base_url", "https://ntfy.sh")).TrimEnd('/');
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{server}/{Uri.EscapeDataString(topic)}")
            {
                Content = new StringContent(string.Join("\n", lines), Encoding.UTF8, "text/plain"),
            };
            // HTTP headers must be ASCII.
            req.Headers.Add("Title", $"StockJawn - {now:h:mm} market scan");
            req.Headers.Add("Tags", "chart_with_upwards_trend");
            req.Headers.Add("Click", await StringAsync("approval_page_url", "https://yvyofficial.com/approve"));
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) notes.Add($"alert: ntfy returned {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            notes.Add($"alert failed: {ex.Message}");
        }
    }

    private static double Chg(Snapshot s) => s.PrevClose > 0 && s.Last > 0 ? (s.Last / s.PrevClose - 1) * 100 : double.NaN;

    private async Task<string> StringAsync(string signal, string fallback)
    {
        try
        {
            var row = await _db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signal}&status=eq.active");
            return row?["reason"]?.ToString() is { Length: > 0 } v ? v.Trim() : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private async Task<double> NumberAsync(string signal, double fallback)
    {
        try
        {
            var row = await _db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signal}&status=eq.active");
            return double.TryParse(row?["effective_weight"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
