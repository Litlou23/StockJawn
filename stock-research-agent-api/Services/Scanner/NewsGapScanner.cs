using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.Calendar;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.UniverseDiscovery;
using static StockResearchAgent.Api.Services.Broker.AlpacaBrokerAdapter;

namespace StockResearchAgent.Api.Services.Scanner;

public record NewsGap(
    string Ticker, string Direction, double RefClose, double Last, double GapPct, double ExtHigh, double ExtLow,
    double ExtDollarVolume, double ExtVolRatio, string Headline, string HeadlineTime,
    double Trigger, double Stop, double Target, string Route, string? Skip, double Rank, string? Trend = null, bool Leader = false);

public record NewsGapResult(string Mode, DateTime ScanTimeEt, string PickDate, int Headlines, int Candidates,
    List<NewsGap> Picks, List<NewsGap> Skipped, List<string> Notes);

// Stocks gapping on news outside market hours. Night: after-hours movers become research rows for the nightly job.
// Morning: premarket movers become pending picks with a trigger just above the premarket high.
public class NewsGapScanner
{
    private const string Table = "claude_daily_picks";
    public const string NightPrefix = "NEWS GAP PM";
    public const string MorningPrefix = "NEWS GAP AM";
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    // Index funds have to buy an S&P add, so the demand is real (FRSH, S&P SmallCap 600, 10/6).
    private static readonly Regex IndexAdd = new(@"\bS&P\s*(500|MidCap\s*400|SmallCap\s*600)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DealWords = new(@"\b(to acquire|acquired|acquisition|buyout|merger|merge|take[- ]private|definitive agreement|tender offer)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AlpacaBrokerAdapter _alpaca;
    private readonly FmpClient _fmp;
    private readonly SupabaseClient _db;
    private readonly TradingCalendar _calendar;
    private readonly ILogger<NewsGapScanner> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _alpacaKey;
    private readonly string _alpacaSecret;
    private readonly string _alpacaDataUrl;

    public NewsGapScanner(AlpacaBrokerAdapter alpaca, FmpClient fmp, SupabaseClient db, TradingCalendar calendar, IConfiguration config, ILogger<NewsGapScanner> logger)
    {
        _alpaca = alpaca;
        _alpacaKey = config["ALPACA_API_KEY"] ?? "";
        _alpacaSecret = config["ALPACA_API_SECRET"] ?? "";
        _alpacaDataUrl = (config["ALPACA_DATA_URL"] ?? "https://data.alpaca.markets").TrimEnd('/');
        _fmp = fmp;
        _db = db;
        _calendar = calendar;
        _logger = logger;
    }

    public async Task<NewsGapResult> ScanAsync(string? mode, bool write, CancellationToken ct = default)
    {
        var now = TradingCalendar.NowEt();
        var today = now.Date;
        mode = mode?.ToLowerInvariant() is "night" or "morning" ? mode.ToLowerInvariant() : now.Hour >= 12 ? "night" : "morning";
        var notes = new List<string>();
        NewsGapResult Empty(string why, string pd = "") => new(mode, now, pd, 0, 0, [], [], [why]);

        if (!_alpaca.IsConfigured) return Empty("Alpaca not configured — no market data");

        DateTime refDay, pickDate;
        if (mode == "morning")
        {
            if (!await _calendar.IsTradingDayAsync(today)) return Empty("market closed today");
            if (now.TimeOfDay >= new TimeSpan(9, 30, 0)) return Empty("after 9:30 — the market-hours scan covers today");
            refDay = await _calendar.AddTradingDaysAsync(today, -1);
            pickDate = today;
        }
        else
        {
            var closedToday = await _calendar.IsTradingDayAsync(today) && now.TimeOfDay >= new TimeSpan(16, 5, 0);
            refDay = closedToday ? today : await _calendar.AddTradingDaysAsync(today, -1);
            pickDate = await _calendar.NextTradingDayAsync(refDay);
        }
        var pd = pickDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var closeUtc = ToUtc(refDay.AddHours(16));
        var openUtc = ToUtc(refDay.AddHours(9.5));

        var minGap = await NumberAsync("news_gap_min_pct", 3);
        var maxGap = await NumberAsync("news_gap_max_pct", 20);
        var minPrice = await NumberAsync("risk_min_stock_price", 4);
        var budget = await ScanBudget.LoadAsync(_db);
        var maxShare = budget.MaxSharePrice;
        var maxOption = Math.Max(await NumberAsync("scan_max_price", 100), maxShare);
        var minDollar = await NumberAsync("news_gap_min_dollar_volume", 1_000_000) * (mode == "night" ? 0.5 : 1);
        var minRatio = await NumberAsync("news_gap_min_ext_vol_ratio", 0.03);
        var maxTriggerDist = await NumberAsync("news_gap_max_trigger_dist_pct", 4);
        var maxPicks = (int)await NumberAsync("news_gap_max_picks", 2);
        var maxRows = (int)await NumberAsync("news_gap_max_rows", 6);
        var maxR = await NumberAsync("max_target_r", 3);

        // Headlines since 30 minutes before the close: late-day news often moves the stock after hours.
        var headlines = await HeadlinesAsync(closeUtc.AddMinutes(-30));
        var candidates = headlines.Keys.Where(EventsCalendarService.IsTradableTicker).Take(400).ToList();
        if (candidates.Count == 0) return Empty($"no headlines since {ToEt(closeUtc.AddMinutes(-30)):M/d h:mm tt}", pd);

        var bars = new Dictionary<string, List<DailyBar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in candidates.Chunk(100))
            foreach (var (k, v) in await _alpaca.GetIntradayBarsAsync(chunk, "5Min", openUtc))
                bars[k] = v;

        var nowUtc = DateTime.UtcNow;
        var moves = new List<(string T, double RefClose, double Last, double Gap, double Hi, double Lo, double Vol, double Dollar)>();
        foreach (var t in candidates)
        {
            if (!bars.TryGetValue(t, out var list)) continue;
            var reg = list.Where(b => b.Date >= openUtc && b.Date < closeUtc).ToList();
            var ext = list.Where(b => b.Date >= closeUtc && b.Date <= nowUtc).ToList();
            // Morning levels come from today's premarket only; last night's spike isn't the line to break.
            var range = mode == "morning" ? ext.Where(b => ToEt(b.Date).Date == today).ToList() : ext;
            if (reg.Count == 0 || range.Count == 0) continue;
            var refClose = reg[^1].Close;
            var last = range[^1].Close;
            if (refClose <= 0 || last <= 0) continue;
            var gap = (last / refClose - 1) * 100;
            if (Math.Abs(gap) < minGap || last < minPrice) continue;
            moves.Add((t, refClose, last, gap, range.Max(b => b.High), range.Min(b => b.Low), ext.Sum(b => b.Volume), ext.Sum(b => b.Volume * b.Close)));
        }

        var daily = moves.Count == 0 ? new Dictionary<string, List<DailyBar>>()
            : await _alpaca.GetDailyBarsAsync(moves.Select(m => m.T).ToList(), 120);

        var all = new List<NewsGap>();
        foreach (var m in moves)
        {
            var hist = daily.TryGetValue(m.T, out var d) ? d.Where(b => b.Date.Date <= refDay).ToList() : [];
            var avgVol = hist.Count >= 10 ? hist.TakeLast(20).Average(b => b.Volume) : 0;
            var ratio = avgVol > 0 ? m.Vol / avgVol : 0;
            var h = headlines[m.T];
            all.Add(Evaluate(m.T, m.RefClose, m.Last, m.Hi, m.Lo, m.Dollar, ratio, h.Text, ToEt(h.Utc).ToString("M/d h:mm tt", CultureInfo.InvariantCulture),
                h.Specific, hist, minDollar, minRatio, maxGap, maxTriggerDist, maxShare, maxOption, maxR));
        }

        var picks = all.Where(x => x.Skip is null).OrderByDescending(x => x.Rank).ToList();
        var skipped = all.Where(x => x.Skip is not null).OrderByDescending(x => Math.Abs(x.GapPct)).ToList();
        var market = "unknown";
        var trendOn = await NumberAsync("trend_filter_enabled", 1) >= 1;
        var ownOn = await NumberAsync("own_trend_filter_enabled", 1) >= 1;
        if (picks.Count > 0 && (trendOn || ownOn))
        {
            var groups = new List<TrendRules.Group>();
            if (trendOn)
            {
                var spyDaily = await _alpaca.GetDailyBarsAsync(["SPY"], 120);
                market = spyDaily.TryGetValue("SPY", out var sb) ? TrendRules.Market(sb.Where(b => b.Date.Date <= refDay).ToList()) : "unknown";
                groups = await TrendRules.LoadGroupsAsync(_db);
            }
            var checkedPicks = new List<NewsGap>();
            foreach (var p in picks)
            {
                string? why = null;
                if (trendOn)
                {
                    TrendRules.Group? g = null;
                    if (p.Direction == "bullish" && groups.Count > 0 && _fmp.IsConfigured && await _fmp.GetProfileAsync(p.Ticker) is { } prof
                        && SectorStrength.EtfFor(prof.Sector, prof.Industry) is { } etf)
                        g = groups.FirstOrDefault(x => x.Etf == etf);
                    // A drop on bad news is itself the weakness puts need.
                    why = TrendRules.Check(p.Direction, market, g?.Grp, g?.AboveSma20 ?? false, relativeWeakness: p.Direction == "bearish");
                }
                if (why is null && ownOn && daily.TryGetValue(p.Ticker, out var pd1))
                    why = TrendRules.OwnTrend(p.Direction, pd1.Where(b => b.Date.Date <= refDay).ToList(), p.Last);
                checkedPicks.Add(p with { Trend = why });
            }
            picks = checkedPicks;
        }
        notes.Add($"{mode} scan vs the {refDay:M/d} close: {headlines.Count} tickers in the news, {moves.Count} moving {minGap:F0}%+, " +
                  $"{picks.Count} setups, {skipped.Count} skipped; shares up to ${maxShare:F2} ({budget.Why}); market {market}");

        if (write)
        {
            var written = mode == "night"
                ? await WriteNightAsync(picks.Where(x => x.Route != "over_budget").Take(maxRows).ToList(), now, pd, pickDate, notes)
                : await WriteMorningAsync(picks, now, pd, pickDate, maxPicks, notes);
            if (written.Count > 0 || skipped.Count > 0) await AlertAsync(mode, now, written, picks, skipped, notes);
            picks = written.Count > 0 ? written : picks;
        }
        return new(mode, now, pd, headlines.Count, moves.Count, picks.Take(15).ToList(), skipped.Take(15).ToList(), notes);
    }

    public static NewsGap Evaluate(string t, double refClose, double last, double extHigh, double extLow, double dollar, double ratio,
        string headline, string headlineTime, bool specific, List<DailyBar> hist,
        double minDollar, double minRatio, double maxGap, double maxTriggerDist, double maxShare, double maxOption, double maxR = 3)
    {
        var gap = (last / refClose - 1) * 100;
        var bull = gap > 0;
        string? skip = null;
        if (Math.Abs(gap) > maxGap) skip = $"{gap:+0;-0}% is too far to chase (buyout or all-or-nothing news)";
        else if (bull && gap >= 8 && DealWords.IsMatch(headline)) skip = "buyout target: the deal price caps it";
        else if (dollar < minDollar) skip = $"thin trading outside hours (${dollar / 1000:F0}k)";
        else if (ratio < minRatio) skip = $"only {ratio * 100:F1}% of a normal day's volume so far";
        else if (bull && extHigh > refClose && (extHigh - last) / (extHigh - refClose) > 0.5) skip = "gave back more than half the gap";
        else if (!bull && extLow < refClose && (last - extLow) / (refClose - extLow) > 0.5) skip = "bounced back more than half the drop";

        var trigger = Math.Round(bull ? extHigh + 0.01 : extLow - 0.01, 2);
        if (skip is null && Math.Abs(trigger / last - 1) * 100 > maxTriggerDist)
            skip = $"the {(bull ? "high" : "low")} outside hours (${trigger:F2}) is {Math.Abs(trigger / last - 1) * 100:F1}% away";

        // Stop beyond the far side of the outside-hours range, kept between 2% and 4% of the trigger.
        var riskPct = Math.Clamp(Math.Abs(trigger - (bull ? extLow : extHigh)) / trigger, 0.02, 0.04);
        var stop = Math.Round(bull ? trigger * (1 - riskPct) : trigger * (1 + riskPct), 2);
        var risk = Math.Abs(trigger - stop);
        var levels = hist.Count >= 20 ? KeyLevels.Compute(hist, last) : [];
        var next = bull ? KeyLevels.NearestAbove(levels, trigger) : KeyLevels.NearestBelow(levels, trigger);
        var target = next is not null && Math.Abs(next.Price - trigger) >= 1.5 * risk
            ? Math.Round(bull ? next.Price - 0.01 : next.Price + 0.01, 2)
            : Math.Round(bull ? trigger + 2 * risk : trigger - 2 * risk, 2);
        target = KeyLevels.CapTarget(bull, trigger, risk, target, maxR);

        var route = bull && last <= maxShare ? "shares" : last <= maxOption ? "option" : "over_budget";
        var leader = bull && TrendRules.Leader(hist, last);
        var rank = Math.Abs(gap) * Math.Min(ratio / Math.Max(minRatio, 0.001), 4) * (specific ? 1.2 : 1)
                   * (leader ? 1.25 : 1) * (bull && IndexAdd.IsMatch(headline) ? 1.3 : 1);
        return new NewsGap(t, bull ? "bullish" : "bearish", Math.Round(refClose, 2), Math.Round(last, 2), Math.Round(gap, 2),
            Math.Round(extHigh, 2), Math.Round(extLow, 2), Math.Round(dollar), Math.Round(ratio, 3), headline, headlineTime,
            trigger, stop, target, route, skip, Math.Round(rank, 2), Leader: leader);
    }

    private async Task<List<NewsGap>> WriteNightAsync(List<NewsGap> picks, DateTime now, string pd, DateTime pickDate, List<string> notes)
    {
        // Re-runs replace this scan's own rows; nothing else is touched.
        await _db.DeleteAsync(Table, $"pick_date=eq.{pd}&approval_status=eq.research&notes=like.{Uri.EscapeDataString(NightPrefix)}*");
        if (picks.Count == 0) return picks;
        await _db.InsertAsync(Table, await RowsAsync(picks, now, pd, pickDate, NightPrefix, morning: false), returnRows: false);
        notes.Add($"wrote {picks.Count} research rows for {pd}");
        return picks;
    }

    private async Task<List<NewsGap>> WriteMorningAsync(List<NewsGap> picks, DateTime now, string pd, DateTime pickDate, int maxPicks, List<string> notes)
    {
        var rows = await _db.SelectAsync(Table, filter: $"pick_date=eq.{pd}", select: "ticker,approval_status,notes");
        var taken = rows.Where(r => r["approval_status"]?.ToString() is not ("research" or "skipped" or "rejected" or "expired"))
            .Select(r => r["ticker"]?.ToString()?.ToUpperInvariant()).ToHashSet();
        var mine = rows.Where(r => r["notes"]?.ToString()?.StartsWith(MorningPrefix) == true).ToList();
        // Shares and option ideas get separate caps so a put idea for Lenny doesn't crowd out a buy Lou can approve.
        var fresh = picks.Where(x => !taken.Contains(x.Ticker) && !mine.Any(r => r["ticker"]?.ToString() == x.Ticker)).ToList();
        var staged = fresh.Where(x => x.Route == "shares" && x.Trend is null).Take(Math.Max(0, maxPicks - mine.Count(r => r["approval_status"]?.ToString() != "research")))
            .Concat(fresh.Where(x => x.Route == "option" || (x.Route == "shares" && x.Trend is not null))
                .Take(Math.Max(0, maxPicks - mine.Count(r => r["approval_status"]?.ToString() == "research"))))
            .ToList();
        if (staged.Count == 0)
        {
            notes.Add(mine.Count > 0 ? $"nothing new to stage ({mine.Count} morning news picks already in)" : "nothing new to stage");
            return staged;
        }

        await _db.InsertAsync(Table, await RowsAsync(staged, now, pd, pickDate, MorningPrefix, morning: true), returnRows: false);
        notes.Add($"wrote {staged.Count} picks ({staged.Count(x => x.Route == "shares" && x.Trend is null)} pending shares, {staged.Count(x => x.Route != "shares" || x.Trend is not null)} research for Lenny)");

        var forLenny = staged.Where(x => x.Route != "shares" || x.Trend is not null).ToList();
        if (forLenny.Count > 0)
        {
            try
            {
                await _db.InsertAsync("claude_messages", new Dictionary<string, object?>
                {
                    ["sender"] = "StockJawn",
                    ["recipient"] = "Lenny",
                    ["topic"] = $"Premarket news scan {now:h:mm}: contract needed?",
                    ["body"] = "Gapping on news before the open; these need your call (puts for the drops, calls where shares are over the cap, or buys that fight the trend): " +
                               string.Join("; ", forLenny.Select(x => $"{x.Ticker} {x.GapPct:+0.0;-0.0}% ({x.Headline}) {(x.Direction == "bullish" ? "above" : "below")} ${x.Trigger:F2}, stop ${x.Stop:F2}, target ${x.Target:F2}{(x.Trend is null ? "" : $" [against the trend: {x.Trend}]")}")) +
                               ". If one is worth it, pick the option contract (ask $0.20 or more) and INSERT a new pending row with it; " +
                               "don't update the research row, because the 2-hour approval window counts from when a row was created.",
                }, returnRows: false);
            }
            catch (Exception ex)
            {
                notes.Add($"message to Lenny failed: {ex.Message}");
            }
        }
        return staged;
    }

    private async Task<List<object>> RowsAsync(List<NewsGap> picks, DateTime now, string pd, DateTime pickDate, string prefix, bool morning)
    {
        var exitBy = (await _calendar.AddTradingDaysAsync(pickDate, 2)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var when = morning ? "premarket" : "after hours";
        return picks.Select(s =>
        {
            var bull = s.Direction == "bullish";
            var side = bull ? "above" : "below";
            var how = s.Route == "shares" ? "shares" : bull ? "calls (Lenny: pick the contract)" : "puts (Lenny: pick the contract)";
            var why = $"{s.GapPct:+0.0;-0.0}% {when} on news ({s.Headline}, {s.HeadlineTime}), ${s.ExtDollarVolume / 1e6:F1}M traded, " +
                      $"{when} range ${s.ExtLow:F2}-${s.ExtHigh:F2}";
            return (object)new Dictionary<string, object?>
            {
                ["pick_date"] = pd,
                ["ticker"] = s.Ticker,
                ["direction"] = s.Direction,
                ["conviction"] = "medium",
                ["approval_status"] = morning && s.Route == "shares" && s.Trend is null ? "pending" : "research",
                ["order_type"] = "stock",
                ["entry_price"] = s.Last,
                ["current_price"] = s.Last,
                ["trigger_price"] = s.Trigger,
                ["trigger_direction"] = side,
                ["level_target"] = s.Target,
                ["level_stop"] = s.Stop,
                ["target_price"] = s.Target,
                ["stop_price"] = s.Stop,
                ["exit_by_date"] = exitBy,
                ["total_score"] = 60,
                ["catalyst"] = Trim(s.Headline, 200),
                ["notes"] = $"{prefix} {now:M/d h:mm}: {s.Ticker} {why} → {how} {side} ${s.Trigger:F2}, target ${s.Target:F2}, out ${s.Stop:F2}" +
                            (s.Leader ? " — leader at a multi-month high" : "") +
                            (s.Trend is null ? "" : $" — against the trend: {s.Trend}"),
            };
        }).ToList();
    }

    private async Task AlertAsync(string mode, DateTime now, List<NewsGap> written, List<NewsGap> picks, List<NewsGap> skipped, List<string> notes)
    {
        var lines = new List<string>();
        foreach (var s in written)
        {
            var side = s.Direction == "bullish" ? "above" : "below";
            lines.Add(mode == "morning" && s.Route == "shares" && s.Trend is null
                ? $"APPROVE: {s.Ticker} shares {side} ${s.Trigger:F2} (stop ${s.Stop:F2}, target ${s.Target:F2}) {s.GapPct:+0.0;-0.0}% - {Trim(s.Headline, 70)}"
                : $"{s.Ticker} {s.GapPct:+0.0;-0.0}% {side} ${s.Trigger:F2} - {Trim(s.Headline, 70)}");
        }
        var over = picks.Where(x => x.Route == "over_budget").Take(3).Select(x => $"{x.Ticker} ${x.Last:F0} {x.GapPct:+0.0;-0.0}%").ToList();
        if (over.Count > 0) lines.Add($"Over the price cap: {string.Join(", ", over)}");
        var big = skipped.Take(3).Select(x => $"{x.Ticker} {x.GapPct:+0.0;-0.0}% ({x.Skip})").ToList();
        if (big.Count > 0) lines.Add($"Skipped: {string.Join("; ", big)}");
        if (lines.Count == 0) return;

        try
        {
            var topic = await StringAsync("ntfy_topic", "stockjawn-picks-7428");
            var server = (await StringAsync("ntfy_base_url", "https://ntfy.sh")).TrimEnd('/');
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{server}/{Uri.EscapeDataString(topic)}")
            {
                Content = new StringContent(string.Join("\n", lines), Encoding.UTF8, "text/plain"),
            };
            // HTTP headers must be ASCII.
            req.Headers.Add("Title", mode == "morning" ? $"StockJawn - {now:h:mm} premarket news" : $"StockJawn - after-hours news {now:h:mm}");
            req.Headers.Add("Tags", "newspaper");
            req.Headers.Add("Click", await StringAsync("approval_page_url", "https://yvyofficial.com/approve"));
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) notes.Add($"alert: ntfy returned {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            notes.Add($"alert failed: {ex.Message}");
        }
    }

    private record Headline(DateTime Utc, string Text, bool Specific);

    // One headline per ticker: newest single-company story first, market roundups only as a fallback.
    private async Task<Dictionary<string, Headline>> HeadlinesAsync(DateTime sinceUtc)
    {
        var all = new List<(string Sym, Headline H)>();
        foreach (var n in await NewsAsync(sinceUtc))
            foreach (var s in n.Symbols)
                all.Add((s, new Headline(n.CreatedUtc, n.Headline, n.Symbols.Count <= 3)));

        if (_fmp.IsConfigured)
        {
            try
            {
                foreach (var g in await _fmp.GetUpgradesDowngradesAsync(200))
                {
                    var t = FmpUtc(g.PublishedDate, g.ParsedDate);
                    if (t >= sinceUtc) all.Add((g.Symbol, new Headline(t, $"{g.GradingCompany} {g.Action} to {g.NewGrade}", true)));
                }
                foreach (var a in await _fmp.GetStockNewsAsync(200))
                {
                    var t = FmpUtc(a.PublishedDate, a.ParsedDate);
                    if (t >= sinceUtc) all.Add((a.Symbol, new Headline(t, a.Title, true)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[news-gap] FMP news failed — using Alpaca headlines only");
            }
        }

        return all.Where(x => x.Sym.Length > 0)
            .GroupBy(x => x.Sym.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.Select(x => x.H).OrderByDescending(h => h.Specific).ThenByDescending(h => h.Utc).First());
    }

    private record NewsItem(DateTime CreatedUtc, string Headline, List<string> Symbols);

    // Benzinga headlines via Alpaca since sinceUtc, newest first.
    private async Task<List<NewsItem>> NewsAsync(DateTime sinceUtc, int maxItems = 500)
    {
        var result = new List<NewsItem>();
        if (_alpacaKey.Length == 0 || _alpacaSecret.Length == 0) return result;
        var start = sinceUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        string? pageToken = null;
        try
        {
            do
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{_alpacaDataUrl}/v1beta1/news?start={start}&limit=50&sort=desc&include_content=false" +
                                                                      (pageToken is null ? "" : $"&page_token={Uri.EscapeDataString(pageToken)}"));
                req.Headers.Add("APCA-API-KEY-ID", _alpacaKey);
                req.Headers.Add("APCA-API-SECRET-KEY", _alpacaSecret);
                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[news-gap] Alpaca news returned {Status}", resp.StatusCode);
                    break;
                }
                var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var n in json?["news"] as JsonArray ?? new JsonArray())
                {
                    if (n is null || !DateTime.TryParse(n["created_at"]?.ToString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) continue;
                    var syms = (n["symbols"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString().ToUpperInvariant() ?? "").Where(x => x.Length > 0).ToList();
                    result.Add(new NewsItem(t, n["headline"]?.ToString() ?? "", syms));
                }
                pageToken = json?["next_page_token"]?.ToString();
            } while (!string.IsNullOrEmpty(pageToken) && result.Count < maxItems);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[news-gap] Alpaca news failed");
        }
        return result;
    }

    // FMP dates come back as New York clock time with no zone.
    private static DateTime FmpUtc(string raw, DateTimeOffset parsed)
    {
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Kind == DateTimeKind.Unspecified)
            return ToUtc(d);
        return parsed.UtcDateTime;
    }

    private static DateTime ToUtc(DateTime et) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(et, DateTimeKind.Unspecified), Eastern);
    private static DateTime ToEt(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Eastern);
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

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
