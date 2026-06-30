using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Orchestrates the daily research loop:
///   1. Morning scan: gather data -> generate predictions -> save -> report
///   2. EOD review: evaluate open predictions -> score outcomes -> report
///   3. Learning update: update signal stats -> adjust weights -> insights
/// </summary>
public class DailyResearchRunService
{
    private readonly PredictionGenerator _predGen;
    private readonly OutcomeEvaluator _outcomeEval;
    private readonly LearningEngine _learning;
    private readonly DailyReportService _reports;
    private readonly ResearchRepository _repo;
    private readonly WatchlistRepository _watchlistRepo;
    private readonly ILogger<DailyResearchRunService> _logger;

    public DailyResearchRunService(
        PredictionGenerator predGen,
        OutcomeEvaluator outcomeEval,
        LearningEngine learning,
        DailyReportService reports,
        ResearchRepository repo,
        WatchlistRepository watchlistRepo,
        ILogger<DailyResearchRunService> logger)
    {
        _predGen = predGen;
        _outcomeEval = outcomeEval;
        _learning = learning;
        _reports = reports;
        _repo = repo;
        _watchlistRepo = watchlistRepo;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Morning Scan
    // -----------------------------------------------------------------------

    /// <summary>
    /// Run the morning scan. If <paramref name="existingRunId"/> is provided, uses that
    /// already-created research_runs row instead of creating a new one (background-job pattern).
    /// </summary>
    public async Task<MorningScanResult> RunMorningScanAsync(string? existingRunId = null)
    {
        _logger.LogInformation("[research-engine] Starting morning scan...");
        var errors = new List<string>();

        ResearchRun? run;
        if (existingRunId is not null)
        {
            run = await _repo.GetResearchRunByIdAsync(existingRunId);
            if (run is null)
                return new MorningScanResult { Report = $"Research run {existingRunId} not found", Errors = [$"Research run {existingRunId} not found"] };
        }
        else
        {
            run = await _repo.CreateResearchRunAsync("morning_scan");
        }

        if (run is null)
            return new MorningScanResult { Report = "Failed to create research run (Supabase not configured?)", Errors = ["Failed to create research run"] };

        try
        {
            // 1. Build market snapshots from active watchlist (not hardcoded)
            var activeWatchlist = await _watchlistRepo.GetActiveWatchlistAsync();
            var tickers = activeWatchlist.Select(w => w.Ticker).ToArray();

            if (tickers.Length == 0)
            {
                _logger.LogWarning("[research-engine] No active watchlist items — run weekly research first to populate");
                await _repo.CompleteResearchRunAsync(run.Id, "No active watchlist items. Run weekly research first.", 0, 0,
                    ["No active watchlist items"]);
                return new MorningScanResult { RunId = run.Id, Report = "No active watchlist items. Run weekly research first to discover tickers.", Errors = ["No active watchlist items"] };
            }

            _logger.LogInformation("[research-engine] Building snapshots for {Count} active watchlist tickers: [{Tickers}]",
                tickers.Length, string.Join(", ", tickers));
            var snapshotTasks = tickers
                .Select(t => _predGen.BuildMarketSnapshotAsync(t, run.Id));
            var snapshots = (await Task.WhenAll(snapshotTasks)).ToList();

            // Save snapshots
            var snapshotRows = snapshots.Select(s => (object)new
            {
                run_id = s.RunId,
                ticker = s.Ticker,
                quote = s.Quote,
                recent_bars = s.RecentBars,
                technical_context = s.TechnicalContext,
                news_context = s.NewsContext,
                data_availability = s.DataAvailability,
            }).ToList();
            await _repo.SaveMarketSnapshotsAsync(snapshotRows);

            // 2. Generate predictions
            _logger.LogInformation("[research-engine] Generating predictions...");
            var (predictions, allInputs) = await _predGen.GeneratePredictionsForWatchlistAsync(
                tickers, run.Id, snapshots);

            // Save predictions
            var predRows = predictions.Select(p => (object)new
            {
                run_id = p.RunId,
                ticker = p.Ticker,
                prediction_type = p.PredictionType.ToString(),
                asset_type = p.AssetType.ToString(),
                time_window = p.TimeWindow,
                confidence_score = p.ConfidenceScore,
                importance_score = p.ImportanceScore,
                risk_score = p.RiskScore,
                entry_reference_price = p.EntryReferencePrice,
                bullish_case = p.BullishCase,
                bearish_case = p.BearishCase,
                prediction_reason = p.PredictionReason,
                invalidation_rule = p.InvalidationRule,
                data_sources_used = p.DataSourcesUsed.ToArray(),
                missing_data_warnings = p.MissingDataWarnings.ToArray(),
                status = p.Status,
            }).ToList();
            var (persisted, ids) = await _repo.SavePredictionsAsync(predRows);

            // Link inputs to saved prediction IDs
            if (ids.Count > 0 && allInputs.Count > 0)
            {
                var inputIdx = 0;
                var linkedInputs = new List<object>();
                for (int i = 0; i < predictions.Count && i < ids.Count; i++)
                {
                    while (inputIdx < allInputs.Count)
                    {
                        var input = allInputs[inputIdx];
                        if (string.IsNullOrEmpty(input.PredictionId) || input.PredictionId == predictions[i].RunId)
                        {
                            linkedInputs.Add(new
                            {
                                prediction_id = ids[i],
                                input_type = input.InputType,
                                source_name = input.SourceName,
                                source_url = input.SourceUrl,
                                source_record_id = input.SourceRecordId,
                                summary = input.Summary,
                            });
                            inputIdx++;
                        }
                        else break;
                    }
                }
                while (inputIdx < allInputs.Count)
                {
                    linkedInputs.Add(new
                    {
                        prediction_id = ids[^1],
                        input_type = allInputs[inputIdx].InputType,
                        source_name = allInputs[inputIdx].SourceName,
                        source_url = allInputs[inputIdx].SourceUrl,
                        source_record_id = allInputs[inputIdx].SourceRecordId,
                        summary = allInputs[inputIdx].Summary,
                    });
                    inputIdx++;
                }
                await _repo.SavePredictionInputsAsync(linkedInputs);
            }

            // 3. Report
            var report = _reports.GenerateMorningReport(predictions, snapshots);

            // 4. Complete run
            await _repo.CompleteResearchRunAsync(run.Id, report, predictions.Count, 0, errors);

            _logger.LogInformation("[research-engine] Morning scan complete: {Count} predictions", predictions.Count);
            return new MorningScanResult { RunId = run.Id, PredictionsGenerated = predictions.Count, Report = report, Errors = errors };
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            await _repo.CompleteResearchRunAsync(run.Id, $"Morning scan failed: {ex.Message}", 0, 0, errors);
            _logger.LogError(ex, "[research-engine] Morning scan failed");
            return new MorningScanResult { RunId = run.Id, Report = $"Morning scan failed: {ex.Message}", Errors = errors };
        }
    }

    // -----------------------------------------------------------------------
    // End-of-Day Review
    // -----------------------------------------------------------------------

    public async Task<EndOfDayReviewResult> RunEndOfDayReviewAsync(string? existingRunId = null)
    {
        _logger.LogInformation("[research-engine] Starting end-of-day review...");
        var errors = new List<string>();

        ResearchRun? run;
        if (existingRunId is not null)
        {
            run = await _repo.GetResearchRunByIdAsync(existingRunId);
            if (run is null)
                return new EndOfDayReviewResult { Report = $"Research run {existingRunId} not found", Errors = [$"Research run {existingRunId} not found"] };
        }
        else
        {
            run = await _repo.CreateResearchRunAsync("end_of_day_review");
        }

        if (run is null)
            return new EndOfDayReviewResult { Report = "Failed to create research run", Errors = ["Failed to create research run"] };

        try
        {
            var (evaluated, skipped, evalErrors) = await _outcomeEval.EvaluateOpenPredictionsAsync();
            errors.AddRange(evalErrors);

            var report = _reports.GenerateEndOfDayReport(evaluated, skipped);
            await _repo.CompleteResearchRunAsync(run.Id, report, 0, evaluated.Count, errors);

            _logger.LogInformation("[research-engine] EOD review complete: {Count} evaluated", evaluated.Count);
            return new EndOfDayReviewResult { RunId = run.Id, PredictionsEvaluated = evaluated.Count, Report = report, Errors = errors };
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            await _repo.CompleteResearchRunAsync(run.Id, $"EOD review failed: {ex.Message}", 0, 0, errors);
            return new EndOfDayReviewResult { RunId = run.Id, Report = $"EOD review failed: {ex.Message}", Errors = errors };
        }
    }

    // -----------------------------------------------------------------------
    // Learning Update
    // -----------------------------------------------------------------------

    public async Task<LearningUpdateResult> RunLearningUpdateAsync(string? existingRunId = null)
    {
        _logger.LogInformation("[research-engine] Starting learning update...");
        var errors = new List<string>();

        ResearchRun? run;
        if (existingRunId is not null)
        {
            run = await _repo.GetResearchRunByIdAsync(existingRunId);
            if (run is null)
                return new LearningUpdateResult { Report = $"Research run {existingRunId} not found", Errors = [$"Research run {existingRunId} not found"] };
        }
        else
        {
            run = await _repo.CreateResearchRunAsync("learning_update");
        }

        if (run is null)
            return new LearningUpdateResult { Report = "Failed to create research run", Errors = ["Failed to create research run"] };

        try
        {
            var (perfUpdated, _) = await _learning.UpdateSignalPerformanceAsync();
            var (weightsAdjusted, weightChanges) = await _learning.UpdateScoringWeightsFromOutcomesAsync();
            var insights = await _learning.GenerateLearningInsightsAsync();

            var parts = new List<string>
            {
                $"Updated {perfUpdated} signal performance records.",
                $"Adjusted {weightsAdjusted} scoring weights.",
                $"Generated {insights.Count} learning insights.",
            };
            if (weightChanges.Count > 0)
                parts.Add("Weight changes: " + string.Join(", ", weightChanges.Select(c => $"{c.Signal}: {c.OldWeight} -> {c.NewWeight}")));
            var report = string.Join(" ", parts);

            await _repo.CompleteResearchRunAsync(run.Id, report, 0, 0, errors);

            _logger.LogInformation("[research-engine] Learning update complete: {Insights} insights, {Weights} weight changes",
                insights.Count, weightsAdjusted);
            return new LearningUpdateResult
            {
                RunId = run.Id, InsightsGenerated = insights.Count,
                WeightsAdjusted = weightsAdjusted, Report = report, Errors = errors,
            };
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            await _repo.CompleteResearchRunAsync(run.Id, $"Learning update failed: {ex.Message}", 0, 0, errors);
            return new LearningUpdateResult { RunId = run.Id, Report = $"Learning update failed: {ex.Message}", Errors = errors };
        }
    }
}
