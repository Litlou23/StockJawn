using System.Globalization;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Execution;

/// <summary>
/// Polls Supabase every 30 seconds during configurable market hours for approved picks
/// and hands them to ClaudePickExecutor. This replaces the Netlify → Azure
/// trigger chain: the approval page just writes to Supabase and this service
/// picks it up automatically.
///
/// Poll window is DB-configurable via scoring_weight_overrides:
///   signal_name='broker_poll_window', reason='09:25,16:05' (comma-delimited start,end ET times)
/// Change the DB row to widen/narrow the window without redeploying.
/// </summary>
public class PickExecutorPollingService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    // Defaults if DB config is missing or unparseable.
    private static readonly TimeSpan DefaultOpen = new(9, 25, 0);
    private static readonly TimeSpan DefaultClose = new(16, 5, 0);

    // Re-read DB config every 5 minutes so changes take effect without restart.
    private static readonly TimeSpan ConfigRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PickExecutorPollingService> _logger;

    private TimeSpan _pollOpen = DefaultOpen;
    private TimeSpan _pollClose = DefaultClose;
    private DateTime _lastConfigRead = DateTime.MinValue;

    public PickExecutorPollingService(
        IServiceScopeFactory scopeFactory,
        ILogger<PickExecutorPollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for app startup to finish before polling.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        _logger.LogInformation("[pick-poller] Started — polling every {Interval}s during market hours",
            PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshConfigIfNeededAsync();

                if (IsMarketHours())
                {
                    await RunOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[pick-poller] Unhandled error in poll loop");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("[pick-poller] Stopped");
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ClaudePickExecutor>();
        var result = await executor.ExecuteApprovedPicksAsync(ct);

        if (result.Errors.Count > 0)
        {
            _logger.LogWarning("[pick-poller] Execution had errors: {Errors}",
                string.Join("; ", result.Errors.Take(3)));
        }
        else if (!string.IsNullOrWhiteSpace(result.Summary) && !result.Summary.Contains("no approved picks"))
        {
            _logger.LogInformation("[pick-poller] {Summary}", result.Summary);
        }
    }

    /// <summary>
    /// Reads broker_poll_window from DB every 5 minutes.
    /// Format: "HH:mm,HH:mm" (start,end in ET), e.g. "09:25,20:00" for extended hours.
    /// </summary>
    private async Task RefreshConfigIfNeededAsync()
    {
        if (DateTime.UtcNow - _lastConfigRead < ConfigRefreshInterval)
            return;

        _lastConfigRead = DateTime.UtcNow;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SupabaseClient>();
            if (!db.IsConfigured) return;

            var row = await db.SelectSingleAsync(
                "scoring_weight_overrides",
                "signal_name=eq.broker_poll_window&status=eq.active");

            var value = row?["reason"]?.ToString();
            if (string.IsNullOrWhiteSpace(value)) return;

            var parts = value.Split(',');
            if (parts.Length == 2
                && TimeSpan.TryParseExact(parts[0].Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var open)
                && TimeSpan.TryParseExact(parts[1].Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var close))
            {
                if (_pollOpen != open || _pollClose != close)
                {
                    _logger.LogInformation("[pick-poller] Poll window updated: {Open} – {Close} ET", open, close);
                }
                _pollOpen = open;
                _pollClose = close;
            }
            else
            {
                _logger.LogWarning("[pick-poller] Invalid broker_poll_window format '{Value}', expected 'HH:mm,HH:mm'", value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[pick-poller] Failed to refresh poll window config, keeping {Open} – {Close}", _pollOpen, _pollClose);
        }
    }

    private bool IsMarketHours()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern);

        // Weekdays only
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var time = now.TimeOfDay;
        return time >= _pollOpen && time <= _pollClose;
    }
}
