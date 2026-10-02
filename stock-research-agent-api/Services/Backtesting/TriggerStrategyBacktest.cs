using System.Globalization;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Calendar;
using StockResearchAgent.Api.Services.Scanner;
using StockResearchAgent.Api.Services.Supabase;
using static StockResearchAgent.Api.Services.Broker.AlpacaBrokerAdapter;

namespace StockResearchAgent.Api.Services.Backtesting;

public record TriggerTrade(
    DateTime SignalDate, string Ticker, string Direction, string Tags, bool IsTheme, double SpyChangePct,
    double Trigger, double Stop, double Target, DateTime EntryDate, double Entry, DateTime ExitDate, double Exit,
    string ExitReason, double ReturnPct, double RMultiple);

public record TriggerBacktestResult(Guid RunId, DateTime From, DateTime To, int Tickers, int Setups, int Triggered,
    JsonObject Summary, List<TriggerTrade> Trades, List<string> Notes);

// Replays the live trigger strategy on daily bars: each day, the setups MoversScanner would stage (same Evaluate,
// relative strength, themes, key levels, ranking), then the next session the executor's rules: buy only if the
// trigger trades (within the chase limit), no same-day sell unless down same_day_stop_stock_pct (PDT guard), then
// stop / target, sold at the open on the sell-by day. Stock prices only (no option history).
// Daily bars can't order intraday moves: a day touching both stop and target counts as the stop.
public class TriggerStrategyBacktest
{
    private const string DefaultThemes = "XLE,USO,XBI,SMH,XLF,KRE,GLD,SLV,XLU,XHB,ITB,JETS,TAN,URA,XME,ARKK";
    private readonly AlpacaBrokerAdapter _alpaca;
    private readonly SupabaseClient _db;
    private readonly HistoricalDataLoader _history;
    private readonly ILogger<TriggerStrategyBacktest> _logger;

    public TriggerStrategyBacktest(AlpacaBrokerAdapter alpaca, SupabaseClient db, HistoricalDataLoader history, ILogger<TriggerStrategyBacktest> logger)
    {
        _alpaca = alpaca;
        _db = db;
        _history = history;
        _logger = logger;
    }

    public sealed record Options(int Days = 365, int HoldDays = 2, string? Tickers = null, bool Save = true);

    public async Task<TriggerBacktestResult> RunAsync(Options o, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var runId = Guid.NewGuid();
        var minMove = await NumberAsync("scan_min_move_pct", 3);
        var maxMove = await NumberAsync("scan_max_move_pct", 25);
        var minRelVol = await NumberAsync("scan_min_rel_volume", 1.5);
        var minPrice = await NumberAsync("risk_min_stock_price", 4);
        var maxPrice = await NumberAsync("scan_max_price", 100);
        var maxSetups = (int)await NumberAsync("scan_max_candidates", 10);
        var stopPct = await NumberAsync("scan_stop_pct", 2);
        var maxTriggerDist = await NumberAsync("scan_max_trigger_distance_pct", 4);
        var etfMinMove = await NumberAsync("scan_etf_min_move_pct", 1.5);
        var maxThemes = (int)await NumberAsync("scan_max_themes", 4);
        var chasePct = await NumberAsync("trigger_max_chase_pct", 3);
        var sameDayStop = await NumberAsync("same_day_stop_stock_pct", 5);
        var themes = (await StringAsync("scan_theme_etfs", DefaultThemes))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToUpperInvariant()).ToList();

        var universe = !string.IsNullOrWhiteSpace(o.Tickers)
            ? o.Tickers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToUpperInvariant()).ToList()
            : (await _history.GetStoredTickerCountsAsync()).Keys.Select(t => t.ToUpperInvariant()).ToList();
        universe = universe.Where(t => t.Length is >= 1 and <= 5 && t.All(char.IsLetter) && t != "SPY" && !themes.Contains(t)).Distinct().ToList();
        if (universe.Count == 0) return Empty("no tickers: pass ?tickers= or load historical_candles first");

        // Extra ~200 calendar days in front so the first signal day already has key-level history.
        var calendarDays = o.Days + 210;
        var bars = new Dictionary<string, List<DailyBar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in universe.Concat(themes).Append("SPY").Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (k, v) in await _alpaca.GetDailyBarsAsync(chunk, calendarDays)) bars[k] = v;
        }
        if (!bars.TryGetValue("SPY", out var spyBars) || spyBars.Count < 60) return Empty("no SPY history from Alpaca");
        notes.Add($"bars loaded for {bars.Count} of {universe.Count + themes.Count + 1} symbols");

        var spyIndex = spyBars.Select((b, i) => (b.Date, i)).ToDictionary(x => x.Date, x => x.i);
        var index = bars.ToDictionary(kv => kv.Key, kv => kv.Value.Select((b, i) => (b.Date, i)).ToDictionary(x => x.Date, x => x.i), StringComparer.OrdinalIgnoreCase);
        var from = DateTime.UtcNow.Date.AddDays(-o.Days);
        var days = spyBars.Select(b => b.Date).Where(d => d >= from).ToList();
        // Need the next session plus the sell-by day after each signal day.
        days = days.Take(Math.Max(0, days.Count - (o.HoldDays + 1))).ToList();

        var trades = new List<TriggerTrade>();
        var setupCount = 0;
        foreach (var day in days)
        {
            ct.ThrowIfCancellationRequested();
            var si = spyIndex[day];
            var spyWindow = spyBars.GetRange(Math.Max(0, si - 30), si - Math.Max(0, si - 30) + 1);
            var spyChg = si > 0 ? (spyBars[si].Close / spyBars[si - 1].Close - 1) * 100 : 0;

            var setups = new List<MoverSetup>();
            var themeSetups = new List<MoverSetup>();
            foreach (var (t, list) in bars)
            {
                if (t == "SPY" || !index[t].TryGetValue(day, out var i) || i < 22) continue;
                var window = list.GetRange(Math.Max(0, i - 140), i - Math.Max(0, i - 140) + 1);
                if (themes.Contains(t))
                {
                    if (MoversScanner.EvaluateTheme(t, window, day, etfMinMove, stopPct, maxTriggerDist) is { } th) themeSetups.Add(th);
                    continue;
                }
                if (MoversScanner.Evaluate(t, window, day, minMove, minRelVol, minPrice, maxPrice, stopPct, maxTriggerDist, maxMove) is { } s)
                    setups.Add(MoversScanner.ApplyRelativeStrength(s, window[^1], spyWindow));
            }
            var staged = setups.OrderByDescending(s => s.Rank).Take(maxSetups)
                .Concat(themeSetups.OrderByDescending(s => s.Rank).Take(maxThemes))
                .Select(s =>
                {
                    var i = index[s.Ticker][day];
                    var window = bars[s.Ticker].GetRange(Math.Max(0, i - 140), i - Math.Max(0, i - 140) + 1);
                    return MoversScanner.AdjustToLevels(s, KeyLevels.Compute(window, s.Close));
                }).ToList();
            setupCount += staged.Count;

            foreach (var s in staged)
            {
                var list = bars[s.Ticker];
                var i = index[s.Ticker][day];
                if (i + 1 >= list.Count) continue;
                if (Simulate(s, list, i, o.HoldDays, chasePct, sameDayStop) is { } tr)
                    trades.Add(tr with { SignalDate = day, SpyChangePct = Math.Round(spyChg, 2) });
            }
        }

        var summary = Summarize(trades, setupCount);
        summary["config"] = new JsonObject
        {
            ["days"] = o.Days, ["hold_days"] = o.HoldDays, ["min_move"] = minMove, ["min_rel_vol"] = minRelVol, ["max_price"] = maxPrice,
            ["stop_pct"] = stopPct, ["chase_pct"] = chasePct, ["same_day_stop_pct"] = sameDayStop, ["max_setups"] = maxSetups, ["max_themes"] = maxThemes,
        };
        notes.Add($"{days.Count} signal days, {setupCount} setups, {trades.Count} triggered");

        if (o.Save) await SaveAsync(runId, days.FirstOrDefault(), days.LastOrDefault(), universe.Count, summary, trades, notes);
        return new(runId, days.FirstOrDefault(), days.LastOrDefault(), universe.Count, setupCount, trades.Count, summary, trades, notes);

        TriggerBacktestResult Empty(string why) => new(runId, default, default, 0, 0, 0, new JsonObject(), [], [why]);
    }

    public static TriggerTrade? Simulate(MoverSetup s, List<DailyBar> list, int signalIdx, int holdDays, double chasePct, double sameDayStopPct)
    {
        var bull = s.Direction == "bullish";
        var d1 = list[signalIdx + 1];
        var chaseLimit = bull ? s.Trigger * (1 + chasePct / 100) : s.Trigger * (1 - chasePct / 100);

        double entry;
        if (bull)
        {
            if (d1.High < s.Trigger) return null;
            if (d1.Open >= s.Trigger) entry = d1.Open <= chaseLimit ? d1.Open : d1.Low <= chaseLimit ? chaseLimit : double.NaN;
            else entry = s.Trigger;
        }
        else
        {
            if (d1.Low > s.Trigger) return null;
            if (d1.Open <= s.Trigger) entry = d1.Open >= chaseLimit ? d1.Open : d1.High >= chaseLimit ? chaseLimit : double.NaN;
            else entry = s.Trigger;
        }
        if (double.IsNaN(entry)) return null;

        var riskPct = Math.Abs(entry - s.Stop) / entry * 100;
        TriggerTrade Close(DailyBar bar, double px, string why)
        {
            var ret = bull ? (px / entry - 1) * 100 : (entry / px - 1) * 100;
            return new TriggerTrade(default, s.Ticker, s.Direction, s.Pattern, s.IsTheme, 0, s.Trigger, s.Stop, s.Target,
                d1.Date, Math.Round(entry, 2), bar.Date, Math.Round(px, 2), why, Math.Round(ret, 2), riskPct > 0 ? Math.Round(ret / riskPct, 2) : 0);
        }

        // Entry day: the PDT guard only sells if it's down same_day_stop_pct. Counted only when the move clearly came
        // after the buy (it closed there, or the entry was a gap at the open).
        var sameDayPx = bull ? entry * (1 - sameDayStopPct / 100) : entry * (1 + sameDayStopPct / 100);
        var gapEntry = bull ? d1.Open >= s.Trigger : d1.Open <= s.Trigger;
        var hitSameDay = bull ? d1.Low <= sameDayPx : d1.High >= sameDayPx;
        var closedThere = bull ? d1.Close <= sameDayPx : d1.Close >= sameDayPx;
        if (hitSameDay && (gapEntry || closedThere)) return Close(d1, sameDayPx, "same-day stop");

        for (var k = 2; k <= holdDays + 1 && signalIdx + k < list.Count; k++)
        {
            var bar = list[signalIdx + k];
            var stopHitAtOpen = bull ? bar.Open <= s.Stop : bar.Open >= s.Stop;
            var targetHitAtOpen = bull ? bar.Open >= s.Target : bar.Open <= s.Target;
            if (stopHitAtOpen) return Close(bar, bar.Open, "stop (gap)");
            if (targetHitAtOpen) return Close(bar, bar.Open, "target (gap)");
            // Sell-by day: sold on the morning the date arrives.
            if (k == holdDays + 1) return Close(bar, bar.Open, "sell-by");
            var stopHit = bull ? bar.Low <= s.Stop : bar.High >= s.Stop;
            var targetHit = bull ? bar.High >= s.Target : bar.Low <= s.Target;
            if (stopHit) return Close(bar, s.Stop, targetHit ? "stop (both touched)" : "stop");
            if (targetHit) return Close(bar, s.Target, "target");
        }
        var last = list[Math.Min(list.Count - 1, signalIdx + holdDays + 1)];
        return Close(last, last.Close, "end of data");
    }

    public static JsonObject Summarize(List<TriggerTrade> trades, int setups)
    {
        JsonObject Stats(IEnumerable<TriggerTrade> src)
        {
            var t = src.ToList();
            var wins = t.Where(x => x.ReturnPct > 0).ToList();
            var losses = t.Where(x => x.ReturnPct <= 0).ToList();
            var grossWin = wins.Sum(x => x.ReturnPct);
            var grossLoss = -losses.Sum(x => x.ReturnPct);
            return new JsonObject
            {
                ["trades"] = t.Count,
                ["win_rate"] = t.Count > 0 ? Math.Round(100.0 * wins.Count / t.Count, 1) : 0,
                ["avg_win_pct"] = wins.Count > 0 ? Math.Round(wins.Average(x => x.ReturnPct), 2) : 0,
                ["avg_loss_pct"] = losses.Count > 0 ? Math.Round(losses.Average(x => x.ReturnPct), 2) : 0,
                ["avg_return_pct"] = t.Count > 0 ? Math.Round(t.Average(x => x.ReturnPct), 2) : 0,
                ["avg_r"] = t.Count > 0 ? Math.Round(t.Average(x => x.RMultiple), 2) : 0,
                ["profit_factor"] = grossLoss > 0 ? Math.Round(grossWin / grossLoss, 2) : 0,
            };
        }

        var s = Stats(trades);
        s["setups"] = setups;
        s["trigger_rate"] = setups > 0 ? Math.Round(100.0 * trades.Count / setups, 1) : 0;
        s["bullish"] = Stats(trades.Where(x => x.Direction == "bullish"));
        s["bearish"] = Stats(trades.Where(x => x.Direction == "bearish"));
        s["themes"] = Stats(trades.Where(x => x.IsTheme));
        s["stocks"] = Stats(trades.Where(x => !x.IsTheme));
        s["spy_up_day"] = Stats(trades.Where(x => x.SpyChangePct > 0));
        s["spy_down_day"] = Stats(trades.Where(x => x.SpyChangePct <= 0));
        var tags = new JsonObject();
        foreach (var tag in new[] { "held up while SPY faded", "weak while SPY held up", "new 20-day high", "new 20-day low", "double top", "double bottom",
                     "closed near the high", "closed near the low", "target at resistance", "target at support", "resistance close by", "support close by" })
            tags[tag] = Stats(trades.Where(x => x.Tags.Contains(tag, StringComparison.OrdinalIgnoreCase)));
        s["by_tag"] = tags;
        var exits = new JsonObject();
        foreach (var g in trades.GroupBy(x => x.ExitReason)) exits[g.Key] = g.Count();
        s["exits"] = exits;
        // What $10 of risk per trade would have made (sizing to the stop, the way to keep losses small).
        s["pnl_risking_10_per_trade"] = Math.Round(trades.Sum(x => x.RMultiple * 10), 2);
        return s;
    }

    private async Task SaveAsync(Guid runId, DateTime from, DateTime to, int tickers, JsonObject summary, List<TriggerTrade> trades, List<string> notes)
    {
        try
        {
            await _db.InsertAsync("trigger_backtest_runs", new Dictionary<string, object?>
            {
                ["id"] = runId.ToString(),
                ["from_date"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["to_date"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["tickers"] = tickers,
                ["summary"] = summary,
                ["notes"] = string.Join("; ", notes),
            }, returnRows: false);
            foreach (var chunk in trades.Chunk(500))
            {
                await _db.InsertAsync("trigger_backtest_trades", chunk.Select(t => (object)new Dictionary<string, object?>
                {
                    ["run_id"] = runId.ToString(),
                    ["signal_date"] = t.SignalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["ticker"] = t.Ticker,
                    ["direction"] = t.Direction,
                    ["is_theme"] = t.IsTheme,
                    ["tags"] = t.Tags,
                    ["spy_change_pct"] = t.SpyChangePct,
                    ["trigger_price"] = t.Trigger,
                    ["stop_price"] = t.Stop,
                    ["target_price"] = t.Target,
                    ["entry_date"] = t.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["entry_price"] = t.Entry,
                    ["exit_date"] = t.ExitDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["exit_price"] = t.Exit,
                    ["exit_reason"] = t.ExitReason,
                    ["return_pct"] = t.ReturnPct,
                    ["r_multiple"] = t.RMultiple,
                }).ToList(), returnRows: false);
            }
        }
        catch (Exception ex)
        {
            notes.Add($"save failed: {ex.Message}");
            _logger.LogWarning(ex, "[trigger-backtest] save failed");
        }
    }

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
