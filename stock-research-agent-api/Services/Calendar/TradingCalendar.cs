using System.Globalization;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Calendar;

// Trading days for sell-by dates, earnings hold windows and the scanner. Holidays come from market_events
// (refreshed from Alpaca's official calendar); the built-in NYSE list covers a fresh install or an Alpaca outage.
public class TradingCalendar
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    public static readonly HashSet<DateTime> BuiltInHolidays = new[]
    {
        "2026-01-01", "2026-01-19", "2026-02-16", "2026-04-03", "2026-05-25", "2026-06-19", "2026-07-03",
        "2026-09-07", "2026-11-26", "2026-12-25",
        "2027-01-01", "2027-01-18", "2027-02-15", "2027-03-26", "2027-05-31", "2027-06-18", "2027-07-05",
        "2027-09-06", "2027-11-25", "2027-12-24",
    }.Select(d => DateTime.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToHashSet();

    private readonly SupabaseClient _db;
    private readonly ILogger<TradingCalendar> _logger;
    private HashSet<DateTime> _holidays = new(BuiltInHolidays);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TradingCalendar(SupabaseClient db, ILogger<TradingCalendar> logger)
    {
        _db = db;
        _logger = logger;
    }

    public static DateTime TodayEt() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern).Date;
    public static DateTime NowEt() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern);

    public async Task<bool> IsTradingDayAsync(DateTime d)
    {
        await EnsureLoadedAsync();
        return IsTradingDay(d.Date, _holidays);
    }

    public async Task<DateTime> NextTradingDayAsync(DateTime d) => await AddTradingDaysAsync(d, 1);

    public async Task<DateTime> AddTradingDaysAsync(DateTime d, int n)
    {
        await EnsureLoadedAsync();
        return AddTradingDays(d.Date, n, _holidays);
    }

    public static bool IsTradingDay(DateTime d, HashSet<DateTime> holidays)
        => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d.Date);

    public static DateTime AddTradingDays(DateTime d, int n, HashSet<DateTime> holidays)
    {
        var step = n >= 0 ? 1 : -1;
        var left = Math.Abs(n);
        var cur = d.Date;
        while (left > 0)
        {
            cur = cur.AddDays(step);
            if (IsTradingDay(cur, holidays)) left--;
        }
        return cur;
    }

    public void Invalidate() => _loadedAt = DateTimeOffset.MinValue;

    private async Task EnsureLoadedAsync()
    {
        if (DateTimeOffset.UtcNow - _loadedAt < CacheFor || !_db.IsConfigured) return;
        await _lock.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow - _loadedAt < CacheFor) return;
            var rows = await _db.SelectAsync("market_events", filter: "kind=eq.holiday", select: "event_date");
            var set = new HashSet<DateTime>(BuiltInHolidays);
            foreach (var r in rows)
                if (DateTime.TryParse(r["event_date"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    set.Add(d.Date);
            _holidays = set;
            _loadedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[trading-calendar] Could not load holidays — using the built-in list");
            _loadedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _lock.Release();
        }
    }
}
