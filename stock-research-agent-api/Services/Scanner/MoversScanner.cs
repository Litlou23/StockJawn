using System.Globalization;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Calendar;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.UniverseDiscovery;
using static StockResearchAgent.Api.Services.Broker.AlpacaBrokerAdapter;

namespace StockResearchAgent.Api.Services.Scanner;

public record MoverSetup(
    string Ticker, string Direction, double Close, double ChangePct, double RelVolume, string Pattern,
    double Trigger, double Stop, double Target, string? News, double Rank,
    string? Levels = null, JsonObject? KeyLevelsJson = null, bool IsTheme = false);

public record MoversScanResult(DateTime ScanDate, DateTime PickDate, int Universe, List<MoverSetup> Setups, List<string> Notes);

// StockedUp's nightly routine as code: take the day's biggest movers, keep the ones that closed at an extreme
// (the high/low, a new 20-day high/low, a double top/bottom) on heavy volume, and write "if it breaks X" levels.
// Also: relative strength vs SPY (names that held up while SPY faded), sector/commodity ETF themes that keep
// running for days (StockedUp's oil/biotech calls), and key support/resistance levels for every setup.
// Rows land in claude_daily_picks as 'research' for the nightly/premarket jobs to check.
public class MoversScanner
{
    private const string Table = "claude_daily_picks";
    private const string NotePrefix = "SCANNER";
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly AlpacaBrokerAdapter _alpaca;
    private readonly FmpClient _fmp;
    private readonly SupabaseClient _db;
    private readonly TradingCalendar _calendar;
    private readonly FinnhubProvider _finnhub;
    private readonly ILogger<MoversScanner> _logger;

    private const string DefaultThemes = "XLE,USO,XBI,SMH,XLF,KRE,GLD,SLV,XLU,XHB,ITB,JETS,TAN,URA,XME,ARKK";

    public MoversScanner(AlpacaBrokerAdapter alpaca, FmpClient fmp, SupabaseClient db, TradingCalendar calendar,
        FinnhubProvider finnhub, ILogger<MoversScanner> logger)
    {
        _alpaca = alpaca;
        _fmp = fmp;
        _db = db;
        _calendar = calendar;
        _finnhub = finnhub;
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
        var pickDate = await _calendar.NextTradingDayAsync(today);

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

        var themes = (await StringAsync("scan_theme_etfs", DefaultThemes))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToUpperInvariant()).ToList();
        var etfMinMove = await NumberAsync("scan_etf_min_move_pct", 1.5);
        var maxThemes = (int)await NumberAsync("scan_max_themes", 4);

        var sectorEtfs = (await StringAsync("sector_etfs", SectorStrength.DefaultEtfs))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToUpperInvariant()).ToList();

        // ~6 months of bars: enough history for key levels, not just the 20-day stats.
        var bars = await _alpaca.GetDailyBarsAsync(universe.Concat(themes).Concat(sectorEtfs).Append("SPY").Distinct().ToList(), 200);
        var spy = bars.TryGetValue("SPY", out var spyBars) && spyBars.Count > 1 && spyBars[^1].Date == today ? spyBars : null;
        if (spy is null) notes.Add("no SPY bar for today — relative strength skipped");

        var sectors = SectorStrength.Rank(sectorEtfs, bars, today);
        var leaders = await FindAffordableLeadersAsync(sectors, bars, minPrice, notes);
        foreach (var l in leaders.Where(l => !universe.Contains(l.Ticker))) universe.Add(l.Ticker);
        if (sectors.Count > 0)
            notes.Add("leading: " + string.Join(", ", sectors.Where(s => s.Group == "leading").Select(s => $"{s.Name} {s.Ret1m:+0.0;-0.0}%")) +
                      " · lagging: " + string.Join(", ", sectors.Where(s => s.Group == "lagging").Select(s => $"{s.Name} {s.Ret1m:+0.0;-0.0}%")) + " (1 month)");

        var setups = new List<MoverSetup>();
        foreach (var ticker in universe)
        {
            if (!bars.TryGetValue(ticker, out var b) || b.Count < 22) continue;
            var setup = Evaluate(ticker, b, today, minMove, minRelVol, minPrice, maxPrice, stopPct, maxTriggerDist, maxMove);
            if (setup is not null) setups.Add(spy is null ? setup : ApplyRelativeStrength(setup, b[^1], spy));
        }
        setups = await ApplySectorAsync(setups.OrderByDescending(s => s.Rank).Take(maxSetups * 2).ToList(), sectors, leaders);
        setups = setups.OrderByDescending(s => s.Rank).Take(maxSetups).ToList();
        if (setups.Count > 0) setups = await AttachNewsAsync(setups, today);

        var themeSetups = new List<MoverSetup>();
        foreach (var ticker in themes)
        {
            if (setups.Any(x => x.Ticker == ticker) || !bars.TryGetValue(ticker, out var b) || b.Count < 22) continue;
            if (EvaluateTheme(ticker, b, today, etfMinMove, stopPct, maxTriggerDist) is not { } t) continue;
            themeSetups.Add(t.Close > maxPrice ? t with { Pattern = $"{t.Pattern} (over ${maxPrice:F0}: use options or cheaper names in the theme)" } : t);
        }
        setups.AddRange(themeSetups.OrderByDescending(s => s.Rank).Take(maxThemes));

        if (setups.Count > 0) setups = await AttachLevelsAsync(setups, bars, notes);
        notes.Add($"{universe.Count} movers + {themes.Count} theme ETFs checked, {setups.Count(x => !x.IsTheme)} setups, {setups.Count(x => x.IsTheme)} themes");
        if (write && setups.Count > 0) await WriteAsync(setups, today, pickDate, notes);
        if (write && sectors.Count > 0) await WriteSectorsAsync(today, sectors, leaders, notes);
        return new(today, pickDate, universe.Count, setups, notes);
    }

    // For the top 2 leading groups: liquid stocks a single buy can afford, beating SPY over the month and above their
    // 20-day average, so the trend can be owned with shares instead of a $0.10 option.
    private async Task<List<SectorLeader>> FindAffordableLeadersAsync(List<SectorRank> sectors, Dictionary<string, List<DailyBar>> bars, double minPrice, List<string> notes)
    {
        var result = new List<SectorLeader>();
        if (!_fmp.IsConfigured || !bars.TryGetValue("SPY", out var spy) || spy.Count < 22) return result;
        var maxPx = (await ScanBudget.LoadAsync(_db)).MaxSharePrice;
        var spy1m = SectorStrength.Ret(spy, 21);
        foreach (var s in sectors.Where(x => x.Group == "leading").Take(2))
        {
            if (!SectorStrength.Map.TryGetValue(s.Etf, out var m)) continue;
            var rows = await _fmp.ScreenAsync(m.Sector, minPrice, maxPx, 1_000_000);
            if (rows is null)
            {
                notes.Add("FMP screener unavailable on this plan — no affordable-leader list");
                break;
            }
            var picks = rows.Where(r => m.Industry is null || r.Industry.Contains(m.Industry, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Volume).Take(40).Select(r => r.Symbol).ToList();
            var missing = picks.Where(t => !bars.ContainsKey(t)).ToList();
            if (missing.Count > 0)
                foreach (var (k, v) in await _alpaca.GetDailyBarsAsync(missing, 200)) bars[k] = v;
            result.AddRange(picks.Where(bars.ContainsKey).Select(t => (t, b: bars[t]))
                .Where(x => x.b.Count >= 22 && x.b[^1].Close > x.b.TakeLast(20).Average(b => b.Close))
                .Select(x => new SectorLeader(x.t, s.Etf, x.b[^1].Close, Math.Round(SectorStrength.Ret(x.b, 21), 2), Math.Round(SectorStrength.Ret(x.b, 21) - spy1m, 2)))
                .Where(l => l.Rs1m > 0 && result.All(r => r.Ticker != l.Ticker))
                .OrderByDescending(l => l.Rs1m).Take(5).ToList());
        }
        return result;
    }

    // Bullish setups in leading groups rank higher, in lagging groups lower (mirror for bearish).
    private async Task<List<MoverSetup>> ApplySectorAsync(List<MoverSetup> setups, List<SectorRank> sectors, List<SectorLeader> leaders)
    {
        if (sectors.Count == 0) return setups;
        var result = new List<MoverSetup>();
        foreach (var s in setups)
        {
            var etf = leaders.FirstOrDefault(l => l.Ticker == s.Ticker)?.Etf;
            if (etf is null && await _fmp.GetProfileAsync(s.Ticker) is { } p) etf = SectorStrength.EtfFor(p.Sector, p.Industry);
            var sec = etf is null ? null : sectors.FirstOrDefault(x => x.Etf == etf);
            if (sec is null || sec.Group == "middle")
            {
                result.Add(sec is null ? s : s with { Pattern = $"{s.Pattern}, group {sec.Name} {sec.Ret1m:+0.0;-0.0}% 1m" });
                continue;
            }
            var withTrend = (s.Direction == "bullish") == (sec.Group == "leading");
            result.Add(s with
            {
                Rank = Math.Round(s.Rank * (withTrend ? 1.25 : 0.75), 2),
                Pattern = $"{s.Pattern}, in a {sec.Group} group ({sec.Name} {sec.Ret1m:+0.0;-0.0}% 1m){(withTrend ? "" : " — against the trend")}",
            });
        }
        return result;
    }

    private async Task WriteSectorsAsync(DateTime today, List<SectorRank> sectors, List<SectorLeader> leaders, List<string> notes)
    {
        try
        {
            var d = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await _db.DeleteAsync("sector_strength", $"trade_date=eq.{d}");
            await _db.InsertAsync("sector_strength", sectors.Select(s => (object)new Dictionary<string, object?>
            {
                ["trade_date"] = d,
                ["etf"] = s.Etf,
                ["name"] = s.Name,
                ["ret_1w"] = s.Ret1w,
                ["ret_1m"] = s.Ret1m,
                ["rs_1m"] = s.Rs1m,
                ["above_sma20"] = s.AboveSma20,
                ["rank"] = s.Rank,
                ["grp"] = s.Group,
                ["leaders"] = new JsonArray(leaders.Where(l => l.Etf == s.Etf).Select(l => (JsonNode)new JsonObject
                    { ["ticker"] = l.Ticker, ["price"] = l.Price, ["ret_1m"] = l.Ret1m, ["rs_1m"] = l.Rs1m }).ToArray()),
            }).ToList(), returnRows: false);
            notes.Add($"sector ranking saved ({sectors.Count} groups, {leaders.Count} affordable leaders)");
        }
        catch (Exception ex)
        {
            notes.Add($"sector ranking save failed: {ex.Message}");
        }
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

    // StockedUp picks names "not too affected by the end-of-day pullback": strong closes on a day SPY closed weak.
    public static MoverSetup ApplyRelativeStrength(MoverSetup s, DailyBar bar, List<DailyBar> spy)
    {
        var spyBar = spy[^1];
        var spyChange = (spyBar.Close / spy[^2].Close - 1) * 100;
        var spyLoc = spyBar.High > spyBar.Low ? (spyBar.Close - spyBar.Low) / (spyBar.High - spyBar.Low) : 0.5;
        var loc = bar.High > bar.Low ? (bar.Close - bar.Low) / (bar.High - bar.Low) : 0.5;
        var rs = s.ChangePct - spyChange;

        if (s.Direction == "bullish" && loc >= 0.7 && (spyLoc <= 0.4 || spyChange < 0))
            return s with { Pattern = $"{s.Pattern}, held up while SPY faded ({rs:+0.0;-0.0}% vs SPY)", Rank = Math.Round(s.Rank * 1.3, 2) };
        if (s.Direction == "bearish" && loc <= 0.3 && (spyLoc >= 0.6 || spyChange > 0))
            return s with { Pattern = $"{s.Pattern}, weak while SPY held up ({rs:+0.0;-0.0}% vs SPY)", Rank = Math.Round(s.Rank * 1.3, 2) };
        return s;
    }

    // Sector/commodity ETFs: a strong close today, or a theme that's run 3 of the last 4 days (oil, biotech...).
    public static MoverSetup? EvaluateTheme(string ticker, List<DailyBar> b, DateTime today, double minMove, double stopPct, double maxTriggerDistPct)
    {
        var bar = b[^1];
        if (bar.Date != today || b.Count < 6 || bar.High <= bar.Low) return null;
        var change = (bar.Close / b[^2].Close - 1) * 100;
        var loc = (bar.Close - bar.Low) / (bar.High - bar.Low);
        var last4 = Enumerable.Range(1, 4).Select(i => b[^i].Close / b[^(i + 1)].Close - 1).ToList();
        var fourDay = (bar.Close / b[^5].Close - 1) * 100;
        var ups = last4.Count(x => x > 0);
        var downs = last4.Count(x => x < 0);

        string? dir = null;
        var tags = new List<string>();
        if (change >= minMove && loc >= 0.7) { dir = "bullish"; tags.Add($"+{change:F1}% today, closed near the high"); }
        else if (change <= -minMove && loc <= 0.3) { dir = "bearish"; tags.Add($"{change:F1}% today, closed near the low"); }
        if (ups >= 3 && fourDay >= 3 && change >= 0 && dir != "bearish") { dir = "bullish"; tags.Add($"theme: up {ups} of the last 4 days ({fourDay:+0.0}%)"); }
        else if (downs >= 3 && fourDay <= -3 && change <= 0 && dir != "bullish") { dir = "bearish"; tags.Add($"theme: down {downs} of the last 4 days ({fourDay:+0.0;-0.0}%)"); }
        if (dir is null) return null;

        var bull = dir == "bullish";
        var trigger = Math.Round(bull ? bar.High + 0.01 : bar.Low - 0.01, 2);
        var stop = Math.Round(bull ? trigger * (1 - stopPct / 100) : trigger * (1 + stopPct / 100), 2);
        var target = Math.Round(bull ? trigger + 2 * (trigger - stop) : trigger - 2 * (stop - trigger), 2);
        if (Math.Abs(trigger - bar.Close) / bar.Close * 100 > maxTriggerDistPct) return null;

        var rank = Math.Abs(fourDay) + Math.Abs(change) * 2;
        return new MoverSetup(ticker, dir, bar.Close, Math.Round(change, 2), 0, string.Join(", ", tags),
            trigger, stop, target, null, Math.Round(rank, 2), IsTheme: true);
    }

    // Key levels: StockJawn's own multi-touch levels, plus Finnhub's support/resistance and chart patterns when the
    // plan allows. The stop moves just under a nearby support (above a resistance for puts) and the target goes
    // to the next level when that still pays at least 1.5x the risk.
    private async Task<List<MoverSetup>> AttachLevelsAsync(List<MoverSetup> setups, Dictionary<string, List<DailyBar>> bars, List<string> notes)
    {
        var useFinnhub = _finnhub.IsConfigured && await NumberAsync("levels_finnhub_enabled", 1) >= 1;
        var result = new List<MoverSetup>();
        foreach (var s in setups)
        {
            var b = bars[s.Ticker];
            var (levels, json, text) = await BuildLevelsAsync(s.Ticker, b, s.Close, useFinnhub);
            result.Add(AdjustToLevels(s, levels) with { Levels = text, KeyLevelsJson = json });
        }
        if (useFinnhub && FinnhubProvider.TechnicalAccessNote is { } note) notes.Add(note);
        return result;
    }

    public async Task<(List<KeyLevel> Levels, JsonObject Json, string Text)> BuildLevelsAsync(string ticker, List<DailyBar> bars, double close, bool useFinnhub)
    {
        var levels = KeyLevels.Compute(bars, close);
        var json = new JsonObject
        {
            ["computed"] = new JsonArray(levels.Take(8).Select(l => (JsonNode)new JsonObject { ["price"] = l.Price, ["touches"] = l.Touches, ["kind"] = l.Kind, ["last"] = l.LastTouch.ToString("yyyy-MM-dd") }).ToArray()),
        };
        var patternText = "";
        if (useFinnhub)
        {
            if (await _finnhub.GetSupportResistanceAsync(ticker) is { Count: > 0 } fh)
            {
                levels.AddRange(fh.Select(p => new KeyLevel(Math.Round(p, 2), 0, p < close ? "support" : "resistance", DateTime.MinValue, "finnhub")));
                json["finnhub_levels"] = new JsonArray(fh.Select(p => (JsonNode)Math.Round(p, 2)).ToArray());
            }
            if (await _finnhub.GetPatternsAsync(ticker) is { Count: > 0 } pats)
            {
                json["patterns"] = new JsonArray(pats.Take(3).Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["type"] = p.Type, ["status"] = p.Status, ["entry"] = p.Entry, ["stop"] = p.StopLoss, ["target"] = p.Target }).ToArray());
                patternText = " · pattern: " + string.Join(", ", pats.Take(2).Select(p => $"{p.Name.ToLowerInvariant()} ({p.Type}{(string.IsNullOrEmpty(p.Status) ? "" : ", " + p.Status)})"));
            }
        }
        var sup = KeyLevels.NearestBelow(levels, close);
        var res = KeyLevels.NearestAbove(levels, close);
        var text = sup is null && res is null ? $"no clear levels in 6 months{patternText}"
            : $"support {(sup is null ? "none" : KeyLevels.Describe(sup))}, resistance {(res is null ? "none" : KeyLevels.Describe(res))}{patternText}";
        return (levels, json, text);
    }

    public static MoverSetup AdjustToLevels(MoverSetup s, List<KeyLevel> levels)
    {
        var bull = s.Direction == "bullish";
        var trigger = s.Trigger;
        var stop = s.Stop;
        var target = s.Target;
        var rank = s.Rank;
        var tags = new List<string>();

        // Tuck the stop just past a level within 3% (traders' "out if it loses support").
        var guard = bull ? KeyLevels.NearestBelow(levels, trigger) : KeyLevels.NearestAbove(levels, trigger);
        if (guard is not null && Math.Abs(guard.Price - trigger) / trigger <= 0.03)
        {
            stop = Math.Round(bull ? guard.Price * 0.995 : guard.Price * 1.005, 2);
            tags.Add($"stop past {(bull ? "support" : "resistance")} {KeyLevels.Describe(guard)}");
        }
        var risk = Math.Abs(trigger - stop);

        var next = bull ? KeyLevels.NearestAbove(levels, trigger) : KeyLevels.NearestBelow(levels, trigger);
        if (next is not null && Math.Abs(next.Price - trigger) >= 1.5 * risk)
        {
            target = Math.Round(bull ? next.Price - 0.01 : next.Price + 0.01, 2);
            tags.Add($"target at {(bull ? "resistance" : "support")} {KeyLevels.Describe(next)}");
        }
        else
        {
            target = Math.Round(bull ? trigger + 2 * risk : trigger - 2 * risk, 2);
            if (next is not null)
            {
                // A wall right above the trigger caps the move.
                tags.Add($"{(bull ? "resistance" : "support")} close by at {KeyLevels.Describe(next)}");
                rank *= 0.7;
            }
        }

        return s with
        {
            Stop = stop,
            Target = target,
            Rank = Math.Round(rank, 2),
            Pattern = tags.Count == 0 ? s.Pattern : $"{s.Pattern}; {string.Join("; ", tags)}",
        };
    }

    // Morning pass: key levels for every research/pending pick the nightly job staged that doesn't have them yet.
    public async Task<List<string>> FillLevelsAsync(DateTime pickDate, CancellationToken ct = default)
    {
        var notes = new List<string>();
        if (!_alpaca.IsConfigured) return ["Alpaca not configured — no bars for levels"];
        var rows = await _db.SelectAsync(Table,
            filter: $"pick_date=eq.{pickDate:yyyy-MM-dd}&approval_status=in.(research,pending)&key_levels=is.null&ticker=not.in.(CASH,EXEC_LOG)",
            select: "id,ticker");
        if (rows.Count == 0) return ["no picks need levels"];

        var tickers = rows.Select(r => r["ticker"]!.ToString().ToUpperInvariant()).Distinct().ToList();
        var bars = await _alpaca.GetDailyBarsAsync(tickers, 200);
        var useFinnhub = _finnhub.IsConfigured && await NumberAsync("levels_finnhub_enabled", 1) >= 1;
        var filled = 0;
        foreach (var r in rows)
        {
            var t = r["ticker"]!.ToString().ToUpperInvariant();
            if (!bars.TryGetValue(t, out var b) || b.Count < 22) continue;
            var (_, json, text) = await BuildLevelsAsync(t, b, b[^1].Close, useFinnhub);
            json["summary"] = text;
            await _db.UpdateAsync(Table, $"id=eq.{r["id"]}", new Dictionary<string, object?> { ["key_levels"] = json });
            filled++;
        }
        notes.Add($"key levels filled for {filled} of {rows.Count} picks");
        if (useFinnhub && FinnhubProvider.TechnicalAccessNote is { } note) notes.Add(note);
        return notes;
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
            var head = s.IsTheme ? $"{NotePrefix} THEME {today:M/d}: {s.Ticker} {s.Pattern}"
                : $"{NotePrefix} {today:M/d}: {s.Ticker} {move} on {s.RelVolume:F1}x volume, {s.Pattern}";
            var line = $"{head} → {(s.Direction == "bullish" ? "calls" : "puts")} {side} ${s.Trigger:F2}, target ${s.Target:F2}, out ${s.Stop:F2}" +
                       (s.Levels is null ? "" : $" · levels: {s.Levels}") +
                       (s.News is null ? "" : $" · news: {s.News}");
            var levelsJson = s.KeyLevelsJson?.DeepClone() as JsonObject;
            if (levelsJson is not null && s.Levels is not null) levelsJson["summary"] = s.Levels;
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
                ["catalyst"] = s.News ?? (s.IsTheme ? $"Sector theme: {s.Pattern} ({today:M/d})" : $"Momentum: {s.Pattern} on {s.RelVolume:F1}x volume ({today:M/d})"),
                ["notes"] = line,
                ["key_levels"] = levelsJson,
            };
        }).ToList();

        await _db.InsertAsync(Table, rows, returnRows: false);
        notes.Add($"wrote {rows.Count} research rows for {pd}");
        _logger.LogInformation("[movers-scan] Wrote {Count} setups for {PickDate}", rows.Count, pd);
    }

    private static bool IsPlainTicker(string t)
        => t.Length is >= 1 and <= 5 && t.All(char.IsLetter);

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";

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
            return double.TryParse(row?["effective_weight"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
