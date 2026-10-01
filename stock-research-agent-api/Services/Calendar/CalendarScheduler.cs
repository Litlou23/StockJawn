using System.Globalization;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Calendar;

// Refreshes market_events every day at calendar_refresh_time_et (default 18:00 ET, weekends too so Monday is ready),
// and once at startup when the table looks stale, so a fresh deploy doesn't wait until evening.
public class CalendarScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CalendarScheduler> _logger;
    private DateTime _lastRunDate = DateTime.MinValue;
    private bool _startupChecked;

    public CalendarScheduler(IServiceScopeFactory scopeFactory, ILogger<CalendarScheduler> logger)
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
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SupabaseClient>();
                if (db.IsConfigured && await ReadAsync(db, "calendar_enabled", "effective_weight", "1") != "0")
                {
                    var now = TradingCalendar.NowEt();
                    var at = TimeSpan.TryParseExact(await ReadAsync(db, "calendar_refresh_time_et", "reason", "18:00"), @"hh\:mm",
                        CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(18, 0, 0);
                    var due = _lastRunDate != now.Date && now.TimeOfDay >= at;
                    if (!_startupChecked)
                    {
                        _startupChecked = true;
                        var fresh = await db.CountAsync("market_events", $"updated_at=gte.{DateTime.UtcNow.AddHours(-30):yyyy-MM-ddTHH:mm:ssZ}");
                        if (fresh == 0) due = true;
                    }
                    if (due)
                    {
                        _lastRunDate = now.Date;
                        var svc = scope.ServiceProvider.GetRequiredService<EventsCalendarService>();
                        await svc.RefreshAsync(sendAlert: now.TimeOfDay >= at, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[calendar] Scheduled refresh failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

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
