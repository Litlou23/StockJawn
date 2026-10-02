using System.Globalization;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Calendar;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Scanner;

public record MissedMover(string Ticker, string Kind, double ChangePct, double Price, double DollarVolumeM, double GapPct,
    string? OurStatus, List<string> Reasons);

public record MissedMoversResult(DateTime TradeDate, double SpyChangePct, List<MissedMover> Movers, List<string> Notes);

// "What did we miss": after the close, the day's biggest liquid movers and sector moves next to what we picked,
// with the reason each one got past us. Lenny's EOD job reads missed_movers so repeat misses turn into fixes.
public class MissedMoversReport
{
    private const string Table = "missed_movers";
    private const string DefaultThemes = "XLE,USO,XBI,SMH,XLF,KRE,GLD,SLV,XLU,XHB,ITB,JETS,TAN,URA,XME,ARKK";

    private readonly AlpacaBrokerAdapter _alpaca;
    private readonly SupabaseClient _db;
    private readonly TradingCalendar _calendar;
    private readonly ILogger<MissedMoversReport> _logger;

    public MissedMoversReport(AlpacaBrokerAdapter alpaca, SupabaseClient db, TradingCalendar calendar, ILogger<MissedMoversReport> logger)
    {
        _alpaca = alpaca;
        _db = db;
        _calendar = calendar;
        _logger = logger;
    }

    public async Task<MissedMoversResult> RunAsync(bool write, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var today = TradingCalendar.TodayEt();
        if (!_alpaca.IsConfigured) return new(today, 0, [], ["Alpaca not configured"]);
        if (!await _calendar.IsTradingDayAsync(today)) return new(today, 0, [], ["market closed today"]);

        var minMove = await NumberAsync("missed_movers_min_move_pct", 4);
        var minDollarVolM = await NumberAsync("missed_movers_min_dollar_volume_m", 50);
        var maxRows = (int)await NumberAsync("missed_movers_max_rows", 15);
        var minPrice = await NumberAsync("risk_min_stock_price", 4);
        var maxPrice = await NumberAsync("scan_max_price", 100);
        var themes = (await StringAsync("scan_theme_etfs", DefaultThemes))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToUpperInvariant()).ToList();

        var movers = await _alpaca.GetTopMoversAsync(50);
        var actives = await _alpaca.GetMostActivesAsync(50);
        var universe = movers.Select(m => m.Ticker).Concat(actives.Select(a => a.Ticker))
            .Where(t => t.Length is >= 1 and <= 5 && t.All(char.IsLetter))
            .Select(t => t.ToUpperInvariant()).Where(t => t != "SPY" && !themes.Contains(t)).Distinct().ToList();

        var snaps = await _alpaca.GetSnapshotsAsync(universe.Concat(themes).Append("SPY").ToList());
        var spyChg = snaps.TryGetValue("SPY", out var spy) && spy.PrevClose > 0 ? (spy.Last / spy.PrevClose - 1) * 100 : 0;

        // What we had: anything staged for today or the last few days, with its status.
        var since = await _calendar.AddTradingDaysAsync(today, -3);
        var ours = (await _db.SelectAsync("claude_daily_picks",
                filter: $"pick_date=gte.{since:yyyy-MM-dd}&ticker=not.in.(CASH,EXEC_LOG)", select: "ticker,pick_date,approval_status,notes"))
            .GroupBy(r => r["ticker"]?.ToString()?.ToUpperInvariant() ?? "")
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r["pick_date"]?.ToString()).First());
        var events = (await _db.SelectAsync("market_events",
                filter: $"event_date=gte.{since:yyyy-MM-dd}&event_date=lte.{today:yyyy-MM-dd}&ticker=not.is.null", select: "ticker,title,event_date"))
            .GroupBy(r => r["ticker"]?.ToString()?.ToUpperInvariant() ?? "")
            .ToDictionary(g => g.Key, g => g.First()["title"]?.ToString() ?? "event");

        var rows = new List<MissedMover>();
        foreach (var t in universe)
        {
            if (!snaps.TryGetValue(t, out var s) || s.PrevClose <= 0 || s.Last < minPrice) continue;
            var chg = (s.Last / s.PrevClose - 1) * 100;
            var dollarVolM = s.Volume * (s.Vwap > 0 ? s.Vwap : s.Last) / 1_000_000;
            if (Math.Abs(chg) < minMove || dollarVolM < minDollarVolM) continue;

            var gap = s.Open > 0 ? (s.Open / s.PrevClose - 1) * 100 : 0;
            var status = ours.TryGetValue(t, out var row) ? row["approval_status"]?.ToString() : null;
            var reasons = new List<string>();
            if (status is null) reasons.Add("not on our radar");
            else reasons.Add($"we had it: {status}");
            if (s.Last > maxPrice) reasons.Add($"over our ${maxPrice:F0} price cap");
            reasons.Add(Math.Abs(chg) > 0 && gap / chg >= 0.6
                ? $"moved before the open (gapped {gap:+0.0;-0.0}%)"
                : "moved after the open (market-hours scan territory)");
            reasons.Add(events.TryGetValue(t, out var ev) ? $"calendar had: {ev}" : "no event in our calendar");
            rows.Add(new MissedMover(t, "stock", Math.Round(chg, 2), s.Last, Math.Round(dollarVolM, 1), Math.Round(gap, 2), status, reasons));
        }
        rows = rows.OrderByDescending(r => Math.Abs(r.ChangePct) * Math.Log10(Math.Max(r.DollarVolumeM, 1) + 1)).Take(maxRows).ToList();

        foreach (var t in themes)
        {
            if (!snaps.TryGetValue(t, out var s) || s.PrevClose <= 0) continue;
            var chg = (s.Last / s.PrevClose - 1) * 100;
            if (Math.Abs(chg - spyChg) < 1.5) continue;
            var status = ours.TryGetValue(t, out var row) ? row["approval_status"]?.ToString() : null;
            rows.Add(new MissedMover(t, "theme", Math.Round(chg, 2), s.Last, 0, 0, status,
                [$"sector move {chg:+0.0;-0.0}% vs SPY {spyChg:+0.0;-0.0}%", status is null ? "no pick in this theme" : $"we had it: {status}"]));
        }

        var missed = rows.Count(r => r.Kind == "stock" && r.OurStatus is null);
        notes.Add($"SPY {spyChg:+0.00;-0.00}%: {rows.Count(r => r.Kind == "stock")} big movers ({missed} we never had), {rows.Count(r => r.Kind == "theme")} sector moves");

        if (write)
        {
            var d = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await _db.DeleteAsync(Table, $"trade_date=eq.{d}");
            if (rows.Count > 0)
            {
                await _db.InsertAsync(Table, rows.Select(r => (object)new Dictionary<string, object?>
                {
                    ["trade_date"] = d,
                    ["ticker"] = r.Ticker,
                    ["kind"] = r.Kind,
                    ["change_pct"] = r.ChangePct,
                    ["price"] = r.Price,
                    ["dollar_volume_m"] = r.DollarVolumeM,
                    ["gap_pct"] = r.GapPct,
                    ["spy_change_pct"] = Math.Round(spyChg, 2),
                    ["our_status"] = r.OurStatus,
                    ["reasons"] = string.Join("; ", r.Reasons),
                }).ToList(), returnRows: false);
            }
            notes.Add($"wrote {rows.Count} rows to {Table}");
        }
        return new(today, Math.Round(spyChg, 2), rows, notes);
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
