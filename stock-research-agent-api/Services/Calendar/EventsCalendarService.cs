using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.UniverseDiscovery;

namespace StockResearchAgent.Api.Services.Calendar;

public record CalendarRefreshResult(int Earnings, int Economic, int Holidays, int Opex, int Warnings, string? Alert, List<string> Notes);

// Keeps market_events current: earnings (Finnhub, FMP fallback), US economic data (Finnhub), holidays and early
// closes (Alpaca's official calendar, built-in list fallback) and monthly option expirations. Then flags positions
// that have an event before their sell-by date and sends tomorrow's list to the ntfy topic.
public class EventsCalendarService
{
    private const string Table = "market_events";
    private const string Picks = "claude_daily_picks";
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly string[] OpenExitStates = ["protected", "watching", "cancelling_stop", "target_sell_placed", "stop_sell_placed", "option_sell_placed", "adopt"];

    private readonly SupabaseClient _db;
    private readonly FinnhubProvider _finnhub;
    private readonly FmpClient _fmp;
    private readonly AlpacaBrokerAdapter _alpaca;
    private readonly TradingCalendar _calendar;
    private readonly ILogger<EventsCalendarService> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public EventsCalendarService(SupabaseClient db, FinnhubProvider finnhub, FmpClient fmp, AlpacaBrokerAdapter alpaca,
        TradingCalendar calendar, ILogger<EventsCalendarService> logger)
    {
        _db = db;
        _finnhub = finnhub;
        _fmp = fmp;
        _alpaca = alpaca;
        _calendar = calendar;
        _logger = logger;
    }

    public async Task<CalendarRefreshResult> RefreshAsync(bool sendAlert, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var today = TradingCalendar.TodayEt();

        var holidays = await RefreshHolidaysAsync(today, notes);
        _calendar.Invalidate();
        var earnings = await RefreshEarningsAsync(today, notes);
        var economic = await RefreshEconomicAsync(today, notes);
        var opex = await RefreshOpexAsync(today);
        var warnings = await RefreshHoldingWarningsAsync(today, notes);

        string? alert = null;
        if (sendAlert && await NumberAsync("calendar_alert_enabled", 1) >= 1)
            alert = await SendTomorrowAlertAsync(today, notes);

        _logger.LogInformation("[calendar] earnings={E} economic={Ec} holidays={H} opex={O} warnings={W} — {Notes}",
            earnings, economic, holidays, opex, warnings, string.Join("; ", notes));
        return new(earnings, economic, holidays, opex, warnings, alert, notes);
    }

    private async Task<int> RefreshHolidaysAsync(DateTime today, List<string> notes)
    {
        var end = today.AddDays(120);
        var days = await _alpaca.GetMarketCalendarAsync(today, end);
        var rows = new List<object>();
        string source;
        if (days is { Count: > 0 })
        {
            source = "alpaca";
            var open = days.ToDictionary(d => d.Date);
            for (var d = today; d <= days.Max(x => x.Date); d = d.AddDays(1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                if (!open.TryGetValue(d, out var md))
                    rows.Add(Row(d, null, "holiday", null, "Market closed", "high", source));
                else if (string.CompareOrdinal(md.Close, "16:00") < 0)
                    rows.Add(Row(d, $"closes {md.Close}", "early_close", null, $"Market closes early ({md.Close} ET)", "medium", source));
            }
        }
        else
        {
            source = "builtin";
            notes.Add("holidays: Alpaca calendar unavailable, used the built-in NYSE list");
            foreach (var d in TradingCalendar.BuiltInHolidays.Where(h => h >= today && h <= end).OrderBy(h => h))
                rows.Add(Row(d, null, "holiday", null, "Market closed", "high", source));
        }

        await _db.DeleteAsync(Table, $"kind=in.(holiday,early_close)&source=eq.{source}&event_date=gte.{today:yyyy-MM-dd}");
        await InsertChunkedAsync(rows);
        return rows.Count;
    }

    private async Task<int> RefreshEarningsAsync(DateTime today, List<string> notes)
    {
        var rows = new Dictionary<string, object>();
        var source = "finnhub";
        foreach (var e in await _finnhub.GetUpcomingEarningsAsync(21))
        {
            if (!IsTradableTicker(e.Ticker)) continue;
            if (!DateTime.TryParse(e.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d.Date < today) continue;
            var details = new JsonObject { ["eps_estimate"] = e.EstimateEps };
            rows[$"{e.Ticker}|{d:yyyy-MM-dd}"] = Row(d, HourLabel(e.Hour), "earnings", e.Ticker.ToUpperInvariant(), $"{e.Ticker.ToUpperInvariant()} earnings", "medium", source, details);
        }

        if (rows.Count == 0 && _fmp.IsConfigured)
        {
            source = "fmp";
            foreach (var e in await _fmp.GetEarningsCalendarAsync(21))
            {
                if (!IsTradableTicker(e.Symbol)) continue;
                if (!DateTime.TryParse(e.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d.Date < today) continue;
                rows[$"{e.Symbol}|{d:yyyy-MM-dd}"] = Row(d, null, "earnings", e.Symbol.ToUpperInvariant(), $"{e.Symbol.ToUpperInvariant()} earnings", "medium", source,
                    new JsonObject { ["eps_estimate"] = e.EpsEstimated });
            }
        }

        if (rows.Count == 0)
        {
            notes.Add("earnings: no provider returned data (check FINNHUB_API_KEY / FMP_API_KEY) — kept the old rows");
            return 0;
        }
        await _db.DeleteAsync(Table, $"kind=eq.earnings&source=eq.{source}&event_date=gte.{today:yyyy-MM-dd}");
        await InsertChunkedAsync(rows.Values.ToList());
        return rows.Count;
    }

    // Foreign over-the-counter names (5 letters ending in F or Y: CASIF, JDWPY) we never trade.
    public static bool IsTradableTicker(string? t)
        => !string.IsNullOrEmpty(t) && t.Length <= 5 && t.All(char.IsLetter) && !(t.Length == 5 && t[^1] is 'F' or 'Y' or 'f' or 'y');

    private async Task<int> RefreshEconomicAsync(DateTime today, List<string> notes)
    {
        var source = "finnhub";
        var events = (await _finnhub.GetEconomicCalendarAsync(14))
            .Select(e => (e.Event, e.Country, e.Date, e.Impact, e.Actual, e.Estimate, e.Previous)).ToList();
        if (events.Count == 0 && _fmp.IsConfigured)
        {
            source = "fmp";
            events = (await _fmp.GetEconomicCalendarAsync(14))
                .Where(e => e.Country.Equals("US", StringComparison.OrdinalIgnoreCase) && e.Impact is not null
                            && (e.Impact.Equals("High", StringComparison.OrdinalIgnoreCase) || e.Impact.Equals("Medium", StringComparison.OrdinalIgnoreCase)))
                .Select(e => (e.Event, e.Country, e.Date, e.Impact, e.Actual, e.Estimate, e.Previous)).ToList();
        }
        if (events.Count == 0)
        {
            notes.Add("economic: Finnhub and FMP returned none (their plans may not include the economic calendar) — the nightly job adds them");
            return 0;
        }

        var rows = new Dictionary<string, object>();
        foreach (var e in events)
        {
            if (!DateTime.TryParse(e.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)) continue;
            var et = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Eastern);
            if (et.Date < today) continue;
            var importance = e.Impact?.Equals("high", StringComparison.OrdinalIgnoreCase) == true ? "high" : "medium";
            rows[$"{e.Event}|{et:yyyy-MM-dd}"] = Row(et.Date, et.ToString("h:mm tt", CultureInfo.InvariantCulture), "economic", null, e.Event, importance, source,
                new JsonObject { ["estimate"] = e.Estimate, ["previous"] = e.Previous, ["actual"] = e.Actual });
        }
        await _db.DeleteAsync(Table, $"kind=eq.economic&source=eq.{source}&event_date=gte.{today:yyyy-MM-dd}");
        await InsertChunkedAsync(rows.Values.ToList());
        return rows.Count;
    }

    private async Task<int> RefreshOpexAsync(DateTime today)
    {
        var rows = new List<object>();
        for (var m = 0; m < 4; m++)
        {
            var first = new DateTime(today.Year, today.Month, 1).AddMonths(m);
            var friday = first.AddDays(((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7).AddDays(14);
            // A holiday Friday moves monthly expiration to Thursday.
            var day = await _calendar.IsTradingDayAsync(friday) ? friday : await _calendar.AddTradingDaysAsync(friday, -1);
            if (day < today) continue;
            var quarterly = day.Month % 3 == 0;
            rows.Add(Row(day, null, "opex", null, quarterly ? "Quarterly options expiration (quad witching)" : "Monthly options expiration",
                quarterly ? "high" : "medium", "computed"));
        }
        await _db.DeleteAsync(Table, $"kind=eq.opex&source=eq.computed&event_date=gte.{today:yyyy-MM-dd}");
        await InsertChunkedAsync(rows);
        return rows.Count;
    }

    // Positions we hold that report (or have a company event) before their sell-by date.
    private async Task<int> RefreshHoldingWarningsAsync(DateTime today, List<string> notes)
    {
        var since = today.AddDays(-30).ToString("yyyy-MM-dd");
        var open = await _db.SelectAsync(Picks,
            filter: $"approval_status=eq.executed&pick_date=gte.{since}&or=(exit_status.is.null,exit_status.in.({string.Join(",", OpenExitStates)}))",
            select: "id,ticker,exit_by_date,event_warning,earnings_play");
        var count = 0;
        foreach (var p in open)
        {
            var ticker = p["ticker"]?.ToString() ?? "";
            var end = DateTime.TryParse(p["exit_by_date"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var e)
                ? e.Date : await _calendar.AddTradingDaysAsync(today, 2);
            var events = await _db.SelectAsync(Table,
                filter: $"ticker=eq.{ticker}&kind=in.(earnings,company)&event_date=gte.{today:yyyy-MM-dd}&event_date=lte.{end:yyyy-MM-dd}",
                order: "event_date", select: "event_date,event_time,title");
            string? warning = null;
            if (events.Count > 0 && p["earnings_play"]?.ToString()?.ToLowerInvariant() != "true")
            {
                var ev = events[0];
                var d = DateTime.Parse(ev["event_date"]!.ToString(), CultureInfo.InvariantCulture);
                var when = ev["event_time"]?.ToString() is { Length: > 0 } t ? $" {t}" : "";
                warning = $"{ev["title"]} {d:ddd M/d}{when} — before the sell-by date {end:M/d}";
                count++;
            }
            if (warning != p["event_warning"]?.ToString())
                await _db.UpdateAsync(Picks, $"id=eq.{p["id"]}", new Dictionary<string, object?> { ["event_warning"] = warning });
        }
        if (count > 0) notes.Add($"{count} holding(s) have an event before their sell-by date");
        return count;
    }

    private async Task<string?> SendTomorrowAlertAsync(DateTime today, List<string> notes)
    {
        var nowEt = TradingCalendar.NowEt();
        // After the close we're looking at the next session; before it, today's.
        var day = nowEt.TimeOfDay >= new TimeSpan(16, 0, 0) || !await _calendar.IsTradingDayAsync(today)
            ? await _calendar.NextTradingDayAsync(today) : today;
        var d = day.ToString("yyyy-MM-dd");

        var watch = (await _db.SelectAsync(Picks, filter: $"pick_date=gte.{today:yyyy-MM-dd}&approval_status=in.(research,pending,approved)", select: "ticker"))
            .Concat(await _db.SelectAsync(Picks, filter: $"approval_status=eq.executed&or=(exit_status.is.null,exit_status.in.({string.Join(",", OpenExitStates)}))", select: "ticker"))
            .Select(r => r["ticker"]?.ToString()?.ToUpperInvariant()).OfType<string>().Where(t => t is not ("CASH" or "EXEC_LOG")).ToHashSet();

        var lines = new List<string>();
        foreach (var e in await _db.SelectAsync(Table, filter: $"event_date=eq.{d}&kind=in.(economic,holiday,early_close,opex)&importance=in.(high,medium)", order: "importance,event_time"))
            lines.Add($"{e["title"]}{(e["event_time"]?.ToString() is { Length: > 0 } t ? $" {t}" : "")}");

        // Tonight's after-close reports and tomorrow's before-open reports are what move the next session.
        var earningsFilter = $"kind=eq.earnings&or=(and(event_date.eq.{today:yyyy-MM-dd},event_time.eq.after close),and(event_date.eq.{d},or(event_time.is.null,event_time.neq.after close)))";
        var reports = await _db.SelectAsync(Table, filter: earningsFilter, select: "ticker,event_date,event_time");
        var watched = reports.Where(r => watch.Contains(r["ticker"]?.ToString() ?? "")).Select(r => $"{r["ticker"]} ({r["event_time"] ?? "time n/a"})").ToList();
        if (watched.Count > 0) lines.Add("Earnings we're watching: " + string.Join(", ", watched));
        if (reports.Count > 0) lines.Add($"{reports.Count} companies report tonight/tomorrow morning");

        foreach (var w in await _db.SelectAsync(Picks, filter: "event_warning=not.is.null&approval_status=eq.executed", select: "ticker,event_warning"))
            lines.Add($"Holding {w["ticker"]}: {w["event_warning"]}");

        // HTTP headers must be ASCII, so no em dash in the title.
        var title = $"StockJawn - {day:ddd M/d}";
        var body = lines.Count > 0 ? string.Join("\n", lines) : "No major scheduled events.";
        try
        {
            var topic = await StringAsync("ntfy_topic", "stockjawn-picks-7428");
            var server = (await StringAsync("ntfy_base_url", "https://ntfy.sh")).TrimEnd('/');
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{server}/{Uri.EscapeDataString(topic)}")
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            req.Headers.Add("Title", title);
            req.Headers.Add("Tags", "calendar");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) notes.Add($"alert: ntfy returned {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            notes.Add($"alert failed: {ex.Message}");
        }
        return $"{title}\n{body}";
    }

    private static string? HourLabel(string? hour) => hour?.ToLowerInvariant() switch
    {
        "bmo" => "before open",
        "amc" => "after close",
        "dmh" => "during market",
        _ => null,
    };

    private static Dictionary<string, object?> Row(DateTime date, string? time, string kind, string? ticker, string title,
        string importance, string source, JsonObject? details = null) => new()
    {
        ["event_date"] = date.ToString("yyyy-MM-dd"),
        ["event_time"] = time,
        ["kind"] = kind,
        ["ticker"] = ticker,
        ["title"] = title,
        ["importance"] = importance,
        ["source"] = source,
        ["details"] = details,
        ["updated_at"] = DateTimeOffset.UtcNow,
    };

    private async Task InsertChunkedAsync(List<object> rows)
    {
        foreach (var chunk in rows.Chunk(400))
            await _db.InsertAsync(Table, chunk.ToList(), returnRows: false);
    }

    private async Task<double> NumberAsync(string signal, double fallback)
    {
        var row = await _db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signal}&status=eq.active");
        return double.TryParse(row?["effective_weight"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private async Task<string> StringAsync(string signal, string fallback)
    {
        var row = await _db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signal}&status=eq.active");
        return row?["reason"]?.ToString() is { Length: > 0 } v ? v.Trim() : fallback;
    }
}
