using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Services.Calendar;
using StockResearchAgent.Api.Services.Execution;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Alerts;

// Phone alerts for what Lou would otherwise only find by checking: a trigger hit, an order placed or filled, a pick
// blocked or failed, a position sold, Robinhood NOT READY. Reads pick status only (never touches orders); alert_log
// makes each alert send once, even across restarts. trade_alerts_enabled = 0 turns it off.
public class TradeAlertWatcher : BackgroundService
{
    private const string Table = "claude_daily_picks";
    private const string LogTable = "alert_log";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TradeAlertWatcher> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private string? _lastNotReadyKey;

    public TradeAlertWatcher(IServiceScopeFactory scopeFactory, ILogger<TradeAlertWatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = TradingCalendar.NowEt();
                using var scope = _scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<SupabaseClient>();
                if (now.Hour is >= 7 and < 21 && await NumberAsync(db, "trade_alerts_enabled", 1) >= 1
                    && await sp.GetRequiredService<TradingCalendar>().IsTradingDayAsync(now.Date))
                {
                    await CheckPicksAsync(db, now);
                    await CheckReadinessAsync(db, sp.GetRequiredService<ClaudePickExecutor>(), now);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[trade-alerts] check failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    public record AlertEvent(string PickId, string Event, string Title, string Body, string Tags);

    public static List<AlertEvent> EventsFor(JsonObject p, DateTime todayEt)
    {
        var id = p["id"]?.ToString() ?? "";
        var t = p["ticker"]?.ToString() ?? "?";
        var what = Describe(p);
        var events = new List<AlertEvent>();
        bool Today(string col) => Et(p[col]) is { } d && d.Date == todayEt;

        // A failed pick gets the "did not buy" alert instead.
        if (Today("trigger_hit_at") && p["approval_status"]?.ToString() != "failed")
            events.Add(new(id, "triggered", $"{t} hit its trigger", $"{what}: {p["trigger_direction"]} ${Num(p["trigger_price"])} hit at ${Num(p["trigger_hit_price"])}. StockJawn is placing the order.", "dart"));
        if (Today("executed_at") && p["approval_status"]?.ToString() == "executed")
            events.Add(new(id, "ordered", $"{t} order placed", $"{what} order sent to Robinhood.", "outbox_tray"));
        if (Today("filled_at") && p["fill_status"]?.ToString() == "filled")
            events.Add(new(id, "filled", $"{t} filled", $"{what} filled at ${Num(p["filled_avg_price"])} x{Num(p["filled_quantity"])}.", "white_check_mark"));
        if (Today("exited_at") && p["exit_status"]?.ToString()?.StartsWith("closed") == true)
            events.Add(new(id, "closed", $"{t} sold", $"{what}: {p["exit_reason"] ?? p["exit_status"]}.{Pnl(p)}", "moneybag"));
        if (p["approval_status"]?.ToString() == "failed" && p["pick_date"]?.ToString() == todayEt.ToString("yyyy-MM-dd"))
            events.Add(new(id, "failed", $"{t} did not buy", $"{what}: {p["execution_error"] ?? p["execution_notes"] ?? "failed"}", "warning"));
        return events;
    }

    private async Task CheckPicksAsync(SupabaseClient db, DateTime now)
    {
        var since = now.Date.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var picks = await db.SelectAsync(Table,
            filter: $"pick_date=gte.{since}&ticker=not.in.(CASH,EXEC_LOG)&or=(trigger_hit_at.not.is.null,executed_at.not.is.null,exited_at.not.is.null,approval_status.eq.failed)",
            select: "id,ticker,pick_date,order_type,direction,option_strike,option_expiration,approval_status,trigger_price,trigger_direction,trigger_hit_at,trigger_hit_price,executed_at,fill_status,filled_at,filled_avg_price,filled_quantity,order_quantity,exit_status,exit_reason,exit_price,exited_at,execution_error,execution_notes");
        var events = picks.SelectMany(p => EventsFor(p, now.Date)).ToList();
        if (events.Count == 0) return;

        var ids = string.Join(',', events.Select(e => e.PickId).Distinct());
        var sent = (await db.SelectAsync(LogTable, filter: $"pick_id=in.({ids})", select: "pick_id,event"))
            .Select(r => $"{r["pick_id"]}|{r["event"]}").ToHashSet();
        foreach (var e in events.Where(e => !sent.Contains($"{e.PickId}|{e.Event}")))
        {
            // Log first so a failed push can't turn into a repeat every minute.
            await db.InsertAsync(LogTable, new Dictionary<string, object?> { ["pick_id"] = e.PickId, ["event"] = e.Event, ["detail"] = e.Body }, returnRows: false);
            await PushAsync(db, e.Title, e.Body, e.Tags);
        }
    }

    private async Task CheckReadinessAsync(SupabaseClient db, ClaudePickExecutor executor, DateTime now)
    {
        if (executor.LastReadiness is not { Ready: false } r || now.TimeOfDay < new TimeSpan(9, 0, 0) || now.TimeOfDay > new TimeSpan(16, 0, 0)) return;
        var key = $"{now:yyyy-MM-dd-HH}";
        if (_lastNotReadyKey == key) return;
        _lastNotReadyKey = key;
        await PushAsync(db, "Robinhood NOT READY", $"StockJawn can't trade: {string.Join("; ", r.Problems)}. Approved picks are waiting.", "rotating_light");
    }

    private async Task PushAsync(SupabaseClient db, string title, string body, string tags)
    {
        try
        {
            var topic = await StringAsync(db, "ntfy_topic", "stockjawn-picks-7428");
            var server = (await StringAsync(db, "ntfy_base_url", "https://ntfy.sh")).TrimEnd('/');
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{server}/{Uri.EscapeDataString(topic)}")
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            // HTTP headers must be ASCII.
            req.Headers.Add("Title", Ascii($"StockJawn - {title}"));
            req.Headers.Add("Tags", tags);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) _logger.LogWarning("[trade-alerts] ntfy returned {Status}", (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[trade-alerts] push failed");
        }
    }

    private static string Describe(JsonObject p)
    {
        var type = p["order_type"]?.ToString() ?? "stock";
        var t = p["ticker"]?.ToString() ?? "?";
        return type is "call" or "put"
            ? $"{t} ${Num(p["option_strike"])} {type} {p["option_expiration"]}"
            : $"{t} shares";
    }

    private static string Pnl(JsonObject p)
    {
        if (!double.TryParse(p["filled_avg_price"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var inPx) ||
            !double.TryParse(p["exit_price"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var outPx)) return "";
        var qty = double.TryParse(p["filled_quantity"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var q) && q > 0 ? q : 1;
        var mult = p["order_type"]?.ToString() is "call" or "put" ? 100 : 1;
        var pnl = (outPx - inPx) * qty * mult;
        return $" Bought ${inPx:0.00}, sold ${outPx:0.00}: {(pnl >= 0 ? "+" : "-")}${Math.Abs(pnl):0.00}.";
    }

    private static DateTime? Et(JsonNode? n)
        => DateTime.TryParse(n?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"))
            : null;

    private static string Num(JsonNode? n)
        => double.TryParse(n?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v.ToString("0.##", CultureInfo.InvariantCulture) : "?";

    private static string Ascii(string s) => new(s.Select(c => c < 128 ? c : '-').ToArray());

    private static async Task<string> StringAsync(SupabaseClient db, string signal, string fallback)
    {
        try
        {
            var row = await db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signal}&status=eq.active");
            return row?["reason"]?.ToString() is { Length: > 0 } v ? v.Trim() : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static async Task<double> NumberAsync(SupabaseClient db, string signal, double fallback)
    {
        try
        {
            var row = await db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signal}&status=eq.active");
            return double.TryParse(row?["effective_weight"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
