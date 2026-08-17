using StockResearchAgent.Api.Services;

namespace StockResearchAgent.Api.Services.MetaLabeling;

/// <summary>
/// Weekly (by default) meta-labeler retraining loop. Runs labeling + training
/// automatically so the model tracks live prediction outcomes without a
/// human in the loop.
///
/// Interval is configurable via META_LABELER_RETRAIN_DAYS (positive integer,
/// default 7). Set to 0 to disable auto-retrain entirely.
///
/// The scheduler will not fire while a labeling/training job is already
/// running (JobStatusTracker check) so a manual trigger and the scheduler
/// can't collide.
/// </summary>
public class MetaLabelerScheduler : BackgroundService
{
    private const string LabelJob = "meta-labeler-label";
    private const string TrainJob = "meta-labeler-train";
    private const int DefaultIntervalDays = 7;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JobStatusTracker _jobs;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetaLabelerScheduler> _logger;

    public MetaLabelerScheduler(
        IServiceScopeFactory scopeFactory,
        JobStatusTracker jobs,
        IConfiguration configuration,
        ILogger<MetaLabelerScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _jobs = jobs;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalDays = ReadInterval();
        if (intervalDays <= 0)
        {
            _logger.LogInformation("[meta-labeler-scheduler] Auto-retrain disabled (META_LABELER_RETRAIN_DAYS=0)");
            return;
        }

        _logger.LogInformation(
            "[meta-labeler-scheduler] Auto-retrain enabled — every {Days} day(s)", intervalDays);

        // Small startup delay so the app has time to finish other DI wiring
        // and the initial model load before we potentially retrain.
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_jobs.GetStatus(LabelJob)?.State == "running"
                    || _jobs.GetStatus(TrainJob)?.State == "running")
                {
                    _logger.LogInformation(
                        "[meta-labeler-scheduler] Manual job in progress — skipping this cycle");
                }
                else
                {
                    await RunCycleAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[meta-labeler-scheduler] Cycle failed (will retry next tick)");
            }

            var wait = TimeSpan.FromDays(ReadInterval());
            try { await Task.Delay(wait, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        _logger.LogInformation("[meta-labeler-scheduler] Starting scheduled label + train cycle");

        using var scope = _scopeFactory.CreateScope();
        var labeler = scope.ServiceProvider.GetRequiredService<TripleBarrierLabeler>();
        var trainer = scope.ServiceProvider.GetRequiredService<MetaLabelerTrainingService>();
        var inference = scope.ServiceProvider.GetRequiredService<MetaLabelerService>();

        _jobs.MarkStarted(LabelJob);
        var labelResult = await labeler.LabelRecentAsync(limit: 5000);
        _jobs.MarkCompleted(LabelJob,
            $"Auto: labeled {labelResult.Labeled} ({labelResult.Wins}W/{labelResult.Losses}L), " +
            $"skipped {labelResult.Skipped}");

        if (ct.IsCancellationRequested) return;

        _jobs.MarkStarted(TrainJob);
        var trainResult = await trainer.TrainAsync();
        if (trainResult.Success)
        {
            await inference.ReloadAsync();
            _jobs.MarkCompleted(TrainJob,
                $"Auto: v{trainResult.Version} — AUC={trainResult.Auc:F3}, F1={trainResult.F1:F3}, " +
                $"trained on {trainResult.TrainRows} rows");
            _logger.LogInformation(
                "[meta-labeler-scheduler] Cycle complete — model v{V} promoted (AUC={Auc:F3})",
                trainResult.Version, trainResult.Auc);
        }
        else
        {
            _jobs.MarkFailed(TrainJob, trainResult.Error ?? "auto-train failed");
            _logger.LogWarning("[meta-labeler-scheduler] Training failed: {Err}", trainResult.Error);
        }
    }

    private int ReadInterval()
    {
        var v = _configuration["META_LABELER_RETRAIN_DAYS"];
        if (int.TryParse(v, out var n) && n >= 0) return n;
        return DefaultIntervalDays;
    }
}
