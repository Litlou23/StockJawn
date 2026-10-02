using System.Globalization;
using StockResearchAgent.Api.Services.Calendar;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Scanner;

// Market-hours scan at each intraday_scan_times_et (default 10:00,11:30) and the "what did we miss" report at
// missed_movers_time_et (default 16:15), trading days only. intraday_scan_enabled / missed_movers_enabled = 0 turn them off.
public class IntradayScanScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IntradayScanScheduler> _logger;
    private readonly HashSet<string> _done = [];

    public IntradayScanScheduler(IServiceScopeFactory scopeFactory, ILogger<IntradayScanScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = TradingCalendar.NowEt();
                using var scope = _scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                await StartRequestedBacktestAsync(sp.GetRequiredService<SupabaseClient>(), stoppingToken);
                if (await sp.GetRequiredService<TradingCalendar>().IsTradingDayAsync(now.Date))
                {
                    var db = sp.GetRequiredService<SupabaseClient>();
                    if (await ReadAsync(db, "intraday_scan_enabled", "effective_weight", "1") != "0")
                    {
                        foreach (var at in Times(await ReadAsync(db, "intraday_scan_times_et", "reason", "10:00,11:30")))
                        {
                            // Only within 20 minutes of the slot, so a restart at 2 PM doesn't fire the 10:00 scan.
                            var key = $"scan {now:yyyy-MM-dd} {at}";
                            if (_done.Contains(key) || now.TimeOfDay < at || now.TimeOfDay > at.Add(TimeSpan.FromMinutes(20))) continue;
                            _done.Add(key);
                            var r = await sp.GetRequiredService<IntradayScanner>().ScanAsync(write: true, stoppingToken);
                            _logger.LogInformation("[intraday-scan] {Notes}", string.Join("; ", r.Notes));
                        }
                    }
                    if (await ReadAsync(db, "missed_movers_enabled", "effective_weight", "1") != "0")
                    {
                        var at = Times(await ReadAsync(db, "missed_movers_time_et", "reason", "16:15")).FirstOrDefault(new TimeSpan(16, 15, 0));
                        var key = $"missed {now:yyyy-MM-dd}";
                        if (!_done.Contains(key) && now.TimeOfDay >= at && now.TimeOfDay < new TimeSpan(20, 0, 0))
                        {
                            _done.Add(key);
                            var r = await sp.GetRequiredService<MissedMoversReport>().RunAsync(write: true, stoppingToken);
                            _logger.LogInformation("[missed-movers] {Notes}", string.Join("; ", r.Notes));
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[intraday-scan] Scheduled run failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    // Set trigger_backtest_request to 1 (reason = days, e.g. "365") to start a trigger backtest without the job secret.
    private async Task StartRequestedBacktestAsync(SupabaseClient db, CancellationToken ct)
    {
        if (await ReadAsync(db, "trigger_backtest_request", "effective_weight", "0") is "0" or "0.0") return;
        await db.UpdateAsync("scoring_weight_overrides", "signal_name=eq.trigger_backtest_request",
            new Dictionary<string, object?> { ["effective_weight"] = 0, ["last_updated"] = DateTime.UtcNow });
        var days = int.TryParse(await ReadAsync(db, "trigger_backtest_request", "reason", "365"), out var d) ? Math.Clamp(d, 30, 1100) : 365;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var r = await scope.ServiceProvider.GetRequiredService<StockResearchAgent.Api.Services.Backtesting.TriggerStrategyBacktest>()
                    .RunAsync(new(days), ct);
                _logger.LogInformation("[trigger-backtest] run {Run}: {Notes}", r.RunId, string.Join("; ", r.Notes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[trigger-backtest] requested run failed");
            }
        }, ct);
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
