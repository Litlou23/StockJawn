using System.Globalization;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Scanner;

// Runs the movers scan once each weekday after the close (movers_scan_time_et, default 16:30 ET),
// before the 9 PM nightly-research job reads the rows. movers_scan_enabled = 0 turns it off.
public class MoversScanScheduler : BackgroundService
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MoversScanScheduler> _logger;
    private DateTime _lastRunDate = DateTime.MinValue;

    public MoversScanScheduler(IServiceScopeFactory scopeFactory, ILogger<MoversScanScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern);
                if (now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && _lastRunDate != now.Date)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<SupabaseClient>();
                    var enabled = await ReadAsync(db, "movers_scan_enabled", "effective_weight", "1") != "0";
                    var at = TimeSpan.TryParseExact(await ReadAsync(db, "movers_scan_time_et", "reason", "16:30"), @"hh\:mm",
                        CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(16, 30, 0);
                    // Only same evening: a restart at 8 PM still runs, but the next morning doesn't re-scan yesterday.
                    if (enabled && now.TimeOfDay >= at && now.TimeOfDay < new TimeSpan(23, 0, 0))
                    {
                        _lastRunDate = now.Date;
                        var result = await scope.ServiceProvider.GetRequiredService<MoversScanner>().ScanAsync(write: true, stoppingToken);
                        _logger.LogInformation("[movers-scan] {Notes}", string.Join("; ", result.Notes));
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[movers-scan] Scheduled scan failed");
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
