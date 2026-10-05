using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Calendar;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Scanner;

public record PickCheck(string Id, string Ticker, string Status, string Flags, string Summary, bool Changed);

public record PickCheckResult(DateTime RunEt, int Checked, int Flagged, List<PickCheck> Picks, List<string> Notes);

// Re-does the math on every open pick so Lenny's slips (wrong risk/reward, stale analyst news, a share the budget
// can't buy) are marked before Lou approves. Writes check_flags (problems) and check_summary (R:R, distance to trigger).
public class PickChecker
{
    private const string Table = "claude_daily_picks";
    private static readonly Regex ClaimedRr = new(@"R\s*[:/]\s*R\s*(?:of|is|=|:)?\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnalystAction = new(@"\b(upgrade[ds]?|downgrade[ds]?|initiat\w*)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AlpacaBrokerAdapter _alpaca;
    private readonly SupabaseClient _db;
    private readonly ILogger<PickChecker> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _fmpKey;
    private readonly string _fmpBase;

    public PickChecker(AlpacaBrokerAdapter alpaca, SupabaseClient db, IConfiguration config, ILogger<PickChecker> logger)
    {
        _alpaca = alpaca;
        _db = db;
        _logger = logger;
        _fmpKey = config["FMP_API_KEY"] ?? "";
        _fmpBase = (config["FMP_BASE_URL"] ?? "https://financialmodelingprep.com").TrimEnd('/');
    }

    public async Task<PickCheckResult> RunAsync(bool write, CancellationToken ct = default)
    {
        var now = TradingCalendar.NowEt();
        var notes = new List<string>();
        var rows = await _db.SelectAsync(Table,
            filter: $"pick_date=gte.{now:yyyy-MM-dd}&approval_status=in.(research,pending,approved)&ticker=not.in.(CASH,EXEC_LOG)",
            select: "id,pick_date,ticker,direction,approval_status,order_type,option_contract_symbol,entry_price,trigger_price,target_price,stop_price,level_target,level_stop,catalyst,reason,notes,check_flags");
        if (rows.Count == 0) return new(now, 0, 0, [], ["no open picks"]);

        var minRr = await NumberAsync("pick_check_min_rr", 1.5);
        var maxDist = await NumberAsync("pick_check_max_trigger_dist_pct", 4);
        var maxStop = await NumberAsync("pick_check_max_stop_pct", 10);
        var staleDays = await NumberAsync("pick_check_stale_days", 7);
        var budget = await ScanBudget.LoadAsync(_db);
        var tickers = rows.Select(r => Str(r, "ticker").ToUpperInvariant()).Where(t => t.Length > 0).Distinct().ToList();
        var snaps = _alpaca.IsConfigured ? await _alpaca.GetSnapshotsAsync(tickers) : new();
        var grades = new Dictionary<string, List<Grade>?>(StringComparer.OrdinalIgnoreCase);

        var results = new List<PickCheck>();
        foreach (var r in rows)
        {
            var t = Str(r, "ticker").ToUpperInvariant();
            double? price = snaps.TryGetValue(t, out var s) && s.Last > 0 ? s.Last : null;
            var (issues, summary) = Evaluate(r, price, budget, minRr, maxDist, maxStop);

            var text = $"{Str(r, "catalyst")} {Str(r, "reason")}";
            if (AnalystAction.IsMatch(text) && DateTime.TryParse(Str(r, "pick_date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var pickDate))
            {
                if (!grades.TryGetValue(t, out var g)) grades[t] = g = await GradesAsync(t, notes);
                if (StaleAnalyst(t, text, g, pickDate, staleDays) is { } stale) issues.Add(stale);
            }

            var flags = string.Join("; ", issues);
            var changed = flags != Str(r, "check_flags");
            results.Add(new PickCheck(Str(r, "id"), t, Str(r, "approval_status"), flags, summary, changed));
        }

        if (write)
        {
            foreach (var p in results)
                await _db.UpdateAsync(Table, $"id=eq.{p.Id}", new Dictionary<string, object?>
                {
                    ["check_flags"] = p.Flags,
                    ["check_summary"] = p.Summary,
                    ["checked_at"] = DateTimeOffset.UtcNow,
                });
            var alert = results.Where(p => p.Changed && p.Flags.Length > 0 && p.Status == "pending").ToList();
            if (alert.Count > 0) await AlertAsync(alert, notes);
        }

        var flagged = results.Count(p => p.Flags.Length > 0);
        notes.Insert(0, $"checked {results.Count} open picks, {flagged} with problems (shares budget ${budget.MaxSharePrice:F2}, {budget.Why})");
        return new(now, results.Count, flagged, results, notes);
    }

    public static (List<string> Issues, string Summary) Evaluate(JsonObject r, double? price, ScanBudget.Budget budget, double minRr, double maxDist, double maxStop)
    {
        var issues = new List<string>();
        var summary = new List<string>();
        var bull = Str(r, "direction") != "bearish";
        var entry = Num(r, "trigger_price") ?? Num(r, "entry_price");
        var stop = Num(r, "level_stop") ?? Num(r, "stop_price");
        var target = Num(r, "level_target") ?? Num(r, "target_price");

        if (entry is not > 0 || stop is not > 0 || target is not > 0)
        {
            issues.Add("missing trigger, stop or target");
        }
        else
        {
            var risk = bull ? entry.Value - stop.Value : stop.Value - entry.Value;
            var reward = bull ? target.Value - entry.Value : entry.Value - target.Value;
            var valid = risk > 0 && reward > 0;
            if (risk <= 0) issues.Add($"stop ${stop:F2} is on the wrong side of the ${entry:F2} trigger");
            else if (reward <= 0) issues.Add($"target ${target:F2} is on the wrong side of the ${entry:F2} trigger");
            else
            {
                var rr = reward / risk;
                summary.Add($"R:R {rr:F2} (risk ${risk:F2} to make ${reward:F2})");
                if (rr < minRr) issues.Add($"risk/reward only {rr:F2} (risk ${risk:F2} to make ${reward:F2})");
                var m = ClaimedRr.Match($"{Str(r, "notes")} {Str(r, "reason")}");
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var claimed)
                    && claimed > 0 && Math.Abs(claimed - rr) / rr > 0.25)
                    issues.Add($"notes say R:R {claimed:0.##} but the levels give {rr:F2}");
                if (risk / entry.Value * 100 > maxStop) issues.Add($"stop is {risk / entry.Value * 100:F0}% away from the trigger");
            }

            var shares = Str(r, "order_type") is "" or "stock" && Str(r, "option_contract_symbol").Length == 0;
            if (shares && entry.Value > budget.MaxSharePrice)
                issues.Add($"one share (${entry:F2}) is over the ${budget.MaxSharePrice:F2} budget ({budget.Why}), so StockJawn will refuse it");

            if (valid && price is > 0)
            {
                var ahead = bull ? (entry.Value / price.Value - 1) * 100 : (price.Value / entry.Value - 1) * 100;
                summary.Add($"now ${price:F2}, trigger {(ahead >= 0 ? $"{ahead:F1}% away" : $"passed by {-ahead:F1}%")}");
                if (bull ? price <= stop : price >= stop) summary.Add("ALREADY THROUGH THE STOP");
                else if (ahead < -3) summary.Add("past the trigger by more than the 3% chase limit");
                else if (ahead > maxDist) summary.Add($"trigger more than {maxDist:0}% away");
            }
        }
        return (issues, string.Join(" · ", summary));
    }

    private record Grade(DateTime Date, string Firm, string Action);

    // A rating change older than staleDays (or one by the named firm) isn't a reason to trade this week.
    private static string? StaleAnalyst(string ticker, string text, List<Grade>? grades, DateTime pickDate, double staleDays)
    {
        if (grades is null || grades.Count == 0) return null;
        var firm = grades.Select(g => g.Firm).Distinct()
            .FirstOrDefault(f => f.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } w
                                 && text.Contains(w.Length > 4 ? w[..4] : w, StringComparison.OrdinalIgnoreCase));
        var latest = grades.Where(g => firm is null || g.Firm == firm).MaxBy(g => g.Date);
        if (latest is null) return null;
        var age = (pickDate.Date - latest.Date.Date).TotalDays;
        return age > staleDays
            ? $"catalyst: the latest {(firm ?? "analyst")} rating change on {ticker} is {latest.Action} on {latest.Date:M/d/yy}, {age:F0} days old"
            : null;
    }

    private readonly Dictionary<string, (DateTime At, List<Grade>? Grades)> _gradeCache = new(StringComparer.OrdinalIgnoreCase);

    // Ratings change rarely; one lookup per ticker every 6 hours keeps FMP calls low.
    private async Task<List<Grade>?> GradesAsync(string ticker, List<string> notes)
    {
        if (_fmpKey.Length == 0) return null;
        lock (_gradeCache)
            if (_gradeCache.TryGetValue(ticker, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromHours(6)) return hit.Grades;
        var fresh = await FetchGradesAsync(ticker, notes);
        lock (_gradeCache) _gradeCache[ticker] = (DateTime.UtcNow, fresh);
        return fresh;
    }

    private async Task<List<Grade>?> FetchGradesAsync(string ticker, List<string> notes)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_fmpBase}/stable/grades?symbol={Uri.EscapeDataString(ticker)}&limit=30");
            req.Headers.Add("apikey", _fmpKey);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                if (!notes.Any(n => n.StartsWith("FMP grades"))) notes.Add($"FMP grades returned {(int)resp.StatusCode}; analyst dates not checked");
                return null;
            }
            var list = new List<Grade>();
            foreach (var g in JsonNode.Parse(await resp.Content.ReadAsStringAsync()) as JsonArray ?? new JsonArray())
                if (DateTime.TryParse(g?["date"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    list.Add(new Grade(d, g?["gradingCompany"]?.ToString() ?? "", g?["action"]?.ToString() ?? ""));
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[pick-check] FMP grades failed for {Ticker}", ticker);
            return null;
        }
    }

    private async Task AlertAsync(List<PickCheck> picks, List<string> notes)
    {
        try
        {
            var topic = await StringAsync("ntfy_topic", "stockjawn-picks-7428");
            var server = (await StringAsync("ntfy_base_url", "https://ntfy.sh")).TrimEnd('/');
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{server}/{Uri.EscapeDataString(topic)}")
            {
                Content = new StringContent(string.Join("\n", picks.Select(p => $"{p.Ticker}: {p.Flags}")), Encoding.UTF8, "text/plain"),
            };
            // HTTP headers must be ASCII.
            req.Headers.Add("Title", "StockJawn - check before approving");
            req.Headers.Add("Tags", "warning");
            req.Headers.Add("Click", await StringAsync("approval_page_url", "https://yvyofficial.com/approve"));
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) notes.Add($"alert: ntfy returned {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            notes.Add($"alert failed: {ex.Message}");
        }
    }

    private static string Str(JsonObject r, string k) => r[k]?.ToString() ?? "";

    private static double? Num(JsonObject r, string k)
        => double.TryParse(r[k]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

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
