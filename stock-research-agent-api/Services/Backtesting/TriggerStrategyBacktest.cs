using System.Globalization;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Calendar;
using StockResearchAgent.Api.Services.Scanner;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.UniverseDiscovery;
using static StockResearchAgent.Api.Services.Broker.AlpacaBrokerAdapter;

namespace StockResearchAgent.Api.Services.Backtesting;

public record TriggerTrade(
    DateTime SignalDate, string Ticker, string Direction, string Tags, bool IsTheme, double SpyChangePct,
    double Trigger, double Stop, double Target, DateTime EntryDate, double Entry, DateTime ExitDate, double Exit,
    string ExitReason, double ReturnPct, double RMultiple, string Market = "", string? Against = null, double EntryRelVol = 0, string? OwnAgainst = null);

public record TriggerBacktestResult(Guid RunId, DateTime From, DateTime To, int Tickers, int Setups, int Triggered,
    JsonObject Summary, List<TriggerTrade> Trades, List<string> Notes);

// Replays the live trigger strategy on daily bars: each day, the setups MoversScanner would stage (same Evaluate,
// relative strength, themes, key levels, ranking), then the next session the executor's rules: buy only if the
// trigger trades (within the chase limit), no same-day sell unless down same_day_stop_stock_pct (PDT guard), then
// stop / target, sold at the open on the sell-by day. Stock prices only (no option history).
// Daily bars can't order intraday moves: a day touching both stop and target counts as the stop.
// Each day is replayed four ways so each rule's effect shows on its own: old rules; + market/group trend (10/5);
// + targets capped at max_target_r; + the stock's own trend (above its 50-day for buys) (10/6).
public class TriggerStrategyBacktest
{
    private const string DefaultThemes = "XLE,USO,XBI,SMH,XLF,KRE,GLD,SLV,XLU,XHB,ITB,JETS,TAN,URA,XME,ARKK";
    private readonly AlpacaBrokerAdapter _alpaca;
    private readonly SupabaseClient _db;
    private readonly HistoricalDataLoader _history;
    private readonly FmpClient _fmp;
    private readonly ILogger<TriggerStrategyBacktest> _logger;

    public TriggerStrategyBacktest(AlpacaBrokerAdapter alpaca, SupabaseClient db, HistoricalDataLoader history, FmpClient fmp, ILogger<TriggerStrategyBacktest> logger)
    {
        _alpaca = alpaca;
        _fmp = fmp;
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
        var sectorEtfs = (await StringAsync("sector_etfs", SectorStrength.DefaultEtfs))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToUpperInvariant()).ToList();
        var minRelVolAtBuy = await NumberAsync("trigger_min_rel_volume", 1.2);
        var maxR = await NumberAsync("max_target_r", 3);
        var leaderBoost = await NumberAsync("movers_leader_boost", 1);

        var universe = !string.IsNullOrWhiteSpace(o.Tickers)
            ? o.Tickers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToUpperInvariant()).ToList()
            : (await _history.GetStoredTickerCountsAsync()).Keys.Select(t => t.ToUpperInvariant()).ToList();
        universe = universe.Where(t => t.Length is >= 1 and <= 5 && t.All(char.IsLetter) && t != "SPY" && !themes.Contains(t) && !sectorEtfs.Contains(t)).Distinct().ToList();
        if (universe.Count == 0) return Empty("no tickers: pass ?tickers= or load historical_candles first");

        // Extra ~200 calendar days in front so the first signal day already has key-level history.
        var calendarDays = o.Days + 210;
        var bars = new Dictionary<string, List<DailyBar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in universe.Concat(themes).Concat(sectorEtfs).Append("SPY").Distinct().Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (k, v) in await _alpaca.GetDailyBarsAsync(chunk, calendarDays)) bars[k] = v;
        }
        if (!bars.TryGetValue("SPY", out var spyBars) || spyBars.Count < 60) return Empty("no SPY history from Alpaca");
        notes.Add($"bars loaded for {bars.Count} of {universe.Concat(themes).Concat(sectorEtfs).Distinct().Count() + 1} symbols");

        var spyIndex = spyBars.Select((b, i) => (b.Date, i)).ToDictionary(x => x.Date, x => x.i);
        var index = bars.ToDictionary(kv => kv.Key, kv => kv.Value.Select((b, i) => (b.Date, i)).ToDictionary(x => x.Date, x => x.i), StringComparer.OrdinalIgnoreCase);
        var from = DateTime.UtcNow.Date.AddDays(-o.Days);
        var days = spyBars.Select(b => b.Date).Where(d => d >= from).ToList();
        // Need the next session plus the sell-by day after each signal day.
        days = days.Take(Math.Max(0, days.Count - (o.HoldDays + 1))).ToList();

        var trades = new List<TriggerTrade>();
        var newTrades = new List<TriggerTrade>();
        var cappedTrades = new List<TriggerTrade>();
        var ownTrades = new List<TriggerTrade>();
        var setupCount = 0;
        var newSetupCount = 0;
        var cappedSetupCount = 0;
        var ownSetupCount = 0;
        var groupOf = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in days)
        {
            ct.ThrowIfCancellationRequested();
            var si = spyIndex[day];
            var spyWindow = spyBars.GetRange(Math.Max(0, si - 30), si - Math.Max(0, si - 30) + 1);
            var spyChg = si > 0 ? (spyBars[si].Close / spyBars[si - 1].Close - 1) * 100 : 0;
            var market = TrendRules.Market(spyBars.GetRange(Math.Max(0, si - 59), si - Math.Max(0, si - 59) + 1));
            var dayBars = new Dictionary<string, List<DailyBar>>(StringComparer.OrdinalIgnoreCase) { ["SPY"] = spyWindow };
            foreach (var e in sectorEtfs)
                if (bars.TryGetValue(e, out var eb) && index[e].TryGetValue(day, out var ei) && ei >= 22)
                    dayBars[e] = eb.GetRange(Math.Max(0, ei - 30), ei - Math.Max(0, ei - 30) + 1);
            var sectors = SectorStrength.Rank(sectorEtfs, dayBars, day);

            var setups = new List<MoverSetup>();
            var themeSetups = new List<MoverSetup>();
            var ownAgainst = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
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
                {
                    setups.Add(MoversScanner.ApplyLeader(MoversScanner.ApplyRelativeStrength(s, window[^1], spyWindow), window, leaderBoost));
                    ownAgainst[t] = TrendRules.OwnTrend(s.Direction, window, window[^1].Close);
                }
            }
            // New rules: same candidates judged by market + group (live MoversScanner.ApplySectorAsync), then re-ranked.
            var against = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var judged = new List<MoverSetup>();
            foreach (var s in setups.OrderByDescending(s => s.Rank).Take(maxSetups * 3))
            {
                var etf = await GroupEtfAsync(s.Ticker, groupOf);
                var sec = etf is null ? null : sectors.FirstOrDefault(x => x.Etf == etf);
                against[s.Ticker] = TrendRules.Check(s.Direction, market, sec?.Group, sec?.AboveSma20 ?? false, s.Pattern.Contains("weak while SPY held up"));
                if (against[s.Ticker] is not null) continue;
                judged.Add(sec is null || sec.Group == "middle" ? s
                    : s with { Rank = Math.Round(s.Rank * ((s.Direction == "bullish") == (sec.Group == "leading") ? 1.25 : 0.75), 2) });
            }
            var topThemes = themeSetups.OrderByDescending(s => s.Rank).Take(maxThemes).ToList();
            var staged = Stage(setups.OrderByDescending(s => s.Rank).Take(maxSetups).Concat(topThemes), 0);
            var stagedNew = Stage(judged.OrderByDescending(s => s.Rank).Take(maxSetups).Concat(topThemes), 0);
            var stagedCapped = Stage(judged.OrderByDescending(s => s.Rank).Take(maxSetups).Concat(topThemes), maxR);
            var stagedOwn = Stage(judged.Where(s => ownAgainst.GetValueOrDefault(s.Ticker) is null)
                .OrderByDescending(s => s.Rank).Take(maxSetups).Concat(topThemes), maxR);
            setupCount += staged.Count;
            newSetupCount += stagedNew.Count;
            cappedSetupCount += stagedCapped.Count;
            ownSetupCount += stagedOwn.Count;
            foreach (var (s, into) in staged.Select(s => (s, trades)).Concat(stagedNew.Select(s => (s, newTrades)))
                         .Concat(stagedCapped.Select(s => (s, cappedTrades))).Concat(stagedOwn.Select(s => (s, ownTrades))))
            {
                var list = bars[s.Ticker];
                var i = index[s.Ticker][day];
                if (i + 1 >= list.Count) continue;
                if (Simulate(s, list, i, o.HoldDays, chasePct, sameDayStop) is { } tr)
                    into.Add(tr with
                    {
                        SignalDate = day, SpyChangePct = Math.Round(spyChg, 2), Market = market,
                        Against = s.IsTheme ? null : against.GetValueOrDefault(s.Ticker), EntryRelVol = RelVolume(list, i + 1),
                        OwnAgainst = s.IsTheme ? null : ownAgainst.GetValueOrDefault(s.Ticker),
                    });
            }

            List<MoverSetup> Stage(IEnumerable<MoverSetup> picks, double cap) => picks.Select(s =>
            {
                var i = index[s.Ticker][day];
                var window = bars[s.Ticker].GetRange(Math.Max(0, i - 140), i - Math.Max(0, i - 140) + 1);
                return MoversScanner.AdjustToLevels(s, KeyLevels.Compute(window, s.Close), cap);
            }).ToList();
        }

        var summary = Summarize(trades, setupCount);
        summary["new_rules"] = Summarize(newTrades, newSetupCount);
        // Entry-day volume stands in for the buy-time volume check; a whole day's volume is known only after the fact, so it flatters a little.
        summary["new_rules_with_volume"] = Summarize(newTrades.Where(t => t.IsTheme || t.EntryRelVol >= minRelVolAtBuy).ToList(), newSetupCount);
        summary["new_rules_capped"] = Summarize(cappedTrades, cappedSetupCount);
        summary["new_rules_own_trend"] = Summarize(ownTrades, ownSetupCount);
        summary["old_rules_by_own_trend"] = new JsonObject
        {
            ["with_own_trend"] = Stats(trades.Where(t => !t.IsTheme && t.OwnAgainst is null)),
            ["against_own_trend"] = Stats(trades.Where(t => t.OwnAgainst is not null)),
        };
        summary["old_rules_by_trend"] = new JsonObject
        {
            ["with_trend"] = Stats(trades.Where(t => !t.IsTheme && t.Against is null)),
            ["against_trend"] = Stats(trades.Where(t => t.Against is not null)),
        };
        var byMarket = new JsonObject();
        foreach (var m in new[] { "up", "mixed", "down" })
            byMarket[m] = new JsonObject
            {
                ["old_rules"] = Stats(trades.Where(t => t.Market == m)), ["new_rules"] = Stats(newTrades.Where(t => t.Market == m)),
                ["new_rules_own_trend"] = Stats(ownTrades.Where(t => t.Market == m)),
            };
        summary["by_market"] = byMarket;
        summary["config"] = new JsonObject
        {
            ["days"] = o.Days, ["hold_days"] = o.HoldDays, ["min_move"] = minMove, ["min_rel_vol"] = minRelVol, ["max_price"] = maxPrice,
            ["stop_pct"] = stopPct, ["chase_pct"] = chasePct, ["same_day_stop_pct"] = sameDayStop, ["max_setups"] = maxSetups, ["max_themes"] = maxThemes,
            ["min_rel_vol_at_buy"] = minRelVolAtBuy, ["max_target_r"] = maxR,
        };
        notes.Add($"{days.Count} signal days, {setupCount} setups, {trades.Count} triggered");
        notes.Add($"{groupOf.Count} tickers looked up for their group ({groupOf.Values.Count(v => v is null)} unknown)");
        notes.Add($"old rules: {trades.Count} trades, avg {summary["avg_return_pct"]}%/trade, profit factor {summary["profit_factor"]} | " +
                  $"new trend rules: {newTrades.Count} trades, avg {summary["new_rules"]?["avg_return_pct"]}%/trade, profit factor {summary["new_rules"]?["profit_factor"]} | " +
                  $"+ volume: {summary["new_rules_with_volume"]?["trades"]} trades, avg {summary["new_rules_with_volume"]?["avg_return_pct"]}%/trade | " +
                  $"+ {maxR:0.#}R target cap: {cappedTrades.Count} trades, avg {summary["new_rules_capped"]?["avg_return_pct"]}%/trade | " +
                  $"+ own trend: {ownTrades.Count} trades, avg {summary["new_rules_own_trend"]?["avg_return_pct"]}%/trade, profit factor {summary["new_rules_own_trend"]?["profit_factor"]}");

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

    public static JsonObject Stats(IEnumerable<TriggerTrade> src)
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

    public static JsonObject Summarize(List<TriggerTrade> trades, int setups)
    {
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
        foreach (var tag in new[] { "leader at a multi-month high", "held up while SPY faded", "weak while SPY held up", "new 20-day high", "new 20-day low", "double top", "double bottom",
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

    // Entry-day volume vs the 20 days before the signal.
    private static double RelVolume(List<DailyBar> list, int i)
    {
        if (i < 21) return 0;
        var avg = list.Skip(i - 21).Take(20).Average(b => b.Volume);
        return avg > 0 ? Math.Round(list[i].Volume / avg, 2) : 0;
    }

    private async Task<string?> GroupEtfAsync(string ticker, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(ticker, out var etf)) return etf;
        try
        {
            if (_fmp.IsConfigured && await _fmp.GetProfileAsync(ticker) is { } p) etf = SectorStrength.EtfFor(p.Sector, p.Industry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[trigger-backtest] profile lookup failed for {Ticker}", ticker);
        }
        cache[ticker] = etf;
        return etf;
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
