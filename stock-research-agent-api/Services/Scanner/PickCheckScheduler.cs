using System.Globalization;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Scanner;

// Runs the pick checker every pick_check_interval_min (default 10). pick_check_enabled = 0 turns it off.
public class PickCheckScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PickCheckScheduler> _logger;

    public PickCheckScheduler(IServiceScopeFactory scopeFactory, ILogger<PickCheckScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = 10.0;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SupabaseClient>();
                minutes = Math.Max(2, await NumberAsync(db, "pick_check_interval_min", 10));
                if (await NumberAsync(db, "pick_check_enabled", 1) != 0)
                {
                    var r = await scope.ServiceProvider.GetRequiredService<PickChecker>().RunAsync(write: true, stoppingToken);
                    if (r.Flagged > 0) _logger.LogInformation("[pick-check] {Notes}", string.Join("; ", r.Notes));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[pick-check] Scheduled run failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
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
