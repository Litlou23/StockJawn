using System.Globalization;
using StockResearchAgent.Api.Services.Calendar;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Scanner;

// News gap scan at each news_gap_times_et (default 17:30,19:30 night; 8:45,9:15 morning). news_gap_enabled = 0 turns it off.
public class NewsGapScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NewsGapScheduler> _logger;
    private readonly HashSet<string> _done = [];

    public NewsGapScheduler(IServiceScopeFactory scopeFactory, ILogger<NewsGapScheduler> logger)
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
                var cal = sp.GetRequiredService<TradingCalendar>();
                if (await ReadAsync(db, "news_gap_enabled", "effective_weight", "1") != "0")
                {
                    foreach (var at in Times(await ReadAsync(db, "news_gap_times_et", "reason", "17:30,19:30,08:45,09:15")))
                    {
                        var key = $"news {now:yyyy-MM-dd} {at}";
                        // Only within 20 minutes of the slot, so a restart later doesn't fire an old slot.
                        if (_done.Contains(key) || now.TimeOfDay < at || now.TimeOfDay > at.Add(TimeSpan.FromMinutes(20))) continue;
                        var morning = at.Hours < 12;
                        // Night runs on trading days and the evening before one (Sunday catches weekend news).
                        var due = morning
                            ? await cal.IsTradingDayAsync(now.Date)
                            : await cal.IsTradingDayAsync(now.Date) || await cal.IsTradingDayAsync(now.Date.AddDays(1));
                        _done.Add(key);
                        if (!due) continue;
                        var r = await sp.GetRequiredService<NewsGapScanner>().ScanAsync(morning ? "morning" : "night", write: true, stoppingToken);
                        _logger.LogInformation("[news-gap] {Notes}", string.Join("; ", r.Notes));
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[news-gap] Scheduled run failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private static List<TimeSpan> Times(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => TimeSpan.TryParse(x, CultureInfo.InvariantCulture, out var t) ? t : (TimeSpan?)null)
        .Where(x => x is not null).Select(x => x!.Value).ToList();

    private static async Task<string> ReadAsync(SupabaseClient db, string signal, string column, string fallback)
    {
        try
        {
            var row = await db.SelectSingleAsync("scoring_weight_overrides", $"signal_name=eq.{signal}&status=eq.active");
            var v = row?[column]?.ToString();
            return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        }
        catch
        {
            return fallback;
        }
    }
}
