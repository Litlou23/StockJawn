namespace StockResearchAgent.Api.Services.Execution;

/// <summary>
/// Polls Supabase every 30 seconds during US market hours for approved picks
/// and hands them to ClaudePickExecutor. This replaces the Netlify → Azure
/// trigger chain: the approval page just writes to Supabase and this service
/// picks it up automatically.
/// </summary>
public class PickExecutorPollingService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    // US Eastern market hours: 9:30 AM – 4:00 PM ET.
    // Poll from 9:25 AM (catch approvals right before open) to 4:05 PM (catch last-minute fills).
    private static readonly TimeSpan MarketOpenPoll = new(9, 25, 0);
    private static readonly TimeSpan MarketClosePoll = new(16, 5, 0);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PickExecutorPollingService> _logger;

    public PickExecutorPollingService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PickExecutorPollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
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

    private static bool IsMarketHours()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern);

        // Weekdays only
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var time = now.TimeOfDay;
        return time >= MarketOpenPoll && time <= MarketClosePoll;
    }
}
