using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Evidence;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.OpportunityLearning;
using StockResearchAgent.Api.Services.OptionsData;
using StockResearchAgent.Api.Services.Portfolio;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Dynamic pick orchestrator — the daily loop entry point for the
/// /stock-lab and /paper-options pages.
///
/// Delegates to focused services:
///   StockCandidateService  — build, evaluate, learn from stock candidates
///   OptionCandidateService — generate option candidates + audit trail
///   PortfolioLifecycleService — open/close portfolio positions
/// </summary>
public class DynamicPickOrchestrator
{
    private readonly DailyResearchRunService _dailyService;
    private readonly ResearchRepository _researchRepo;
    private readonly StockCandidateService _stockCandidates;
    private readonly OptionCandidateService _optionCandidates;
    private readonly PortfolioLifecycleService _portfolioLifecycle;
    private readonly OptionsDataRepository _optionsRepo;
    private readonly PaperStockCandidateRepository _stockRepo;
    private readonly LearningEngine _learning;
    private readonly IEvidenceService _evidence;
    private readonly IOpportunityLearningService _opportunityLearning;
    private readonly NeutralOutcomeEvaluator _neutralEvaluator;
    private readonly ILogger<DynamicPickOrchestrator> _logger;

    public DynamicPickOrchestrator(
        DailyResearchRunService dailyService,
        ResearchRepository researchRepo,
        StockCandidateService stockCandidates,
        OptionCandidateService optionCandidates,
        PortfolioLifecycleService portfolioLifecycle,
        PaperStockCandidateRepository stockRepo,
        OptionsDataRepository optionsRepo,
        LearningEngine learning,
        IEvidenceService evidence,
        IOpportunityLearningService opportunityLearning,
        NeutralOutcomeEvaluator neutralEvaluator,
        ILogger<DynamicPickOrchestrator> logger)
    {
        _dailyService = dailyService;
        _researchRepo = researchRepo;
        _stockCandidates = stockCandidates;
        _optionCandidates = optionCandidates;
        _portfolioLifecycle = portfolioLifecycle;
        _stockRepo = stockRepo;
        _optionsRepo = optionsRepo;
        _learning = learning;
        _evidence = evidence;
        _opportunityLearning = opportunityLearning;
        _neutralEvaluator = neutralEvaluator;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // 1. Morning picks: stocks + linked options, fully automatic
    // -----------------------------------------------------------------------

    public async Task<DynamicMorningResult> RunDynamicMorningPicksAsync()
    {
        _logger.LogInformation("[dynamic] Starting dynamic morning picks...");
        var errors = new List<string>();

        // 1. Existing morning scan generates predictions
        var scan = await _dailyService.RunMorningScanAsync();
        errors.AddRange(scan.Errors);

        if (string.IsNullOrEmpty(scan.RunId))
        {
            return new DynamicMorningResult
            {
                Report = scan.Report,
                Errors = scan.Errors,
            };
        }

        // 2. Load the just-saved predictions for this run.
        var allRunPredictions = await _researchRepo.GetPredictionsByRunAsync(scan.RunId);

        // 2a. Resolve champion profile so we can filter predictions.
        //     Paper stock candidates + portfolio positions only come from the champion.
        //     Challenger predictions are stored for analysis but don't generate trades.
        var championProfileId = await _researchRepo.GetChampionProfileIdAsync();
        var runPredictions = championProfileId is not null
            ? allRunPredictions.Where(p => p.ProfileId == championProfileId || p.ProfileId is null).ToList()
            : allRunPredictions;

        _logger.LogInformation("[dynamic] Loaded {Total} predictions for run, {Champion} from champion profile",
            allRunPredictions.Count, runPredictions.Count);

        // 2b. Record evidence from ALL predictions (including challengers) into the Evidence Engine.
        try
        {
            var evidenceRecorded = 0;
            foreach (var pred in allRunPredictions)
            {
                await _evidence.RecordAsync(new EvidenceRecord
                {
                    Ticker = pred.Ticker,
                    EvidenceType = EvidenceType.Research,
                    Source = "morning-scan",
                    Weight = pred.PredictionType == PredictionType.bullish ? 1.0
                           : pred.PredictionType == PredictionType.bearish ? -1.0 : 0.0,
                    Importance = pred.ConfidenceScore,
                    Summary = $"Prediction: {pred.PredictionType} conf={pred.ConfidenceScore} risk={pred.RiskScore}. {pred.PredictionReason[..Math.Min(200, pred.PredictionReason.Length)]}",
                    RelatedEventId = pred.Id,
                });
                evidenceRecorded++;
            }
            if (evidenceRecorded > 0)
                _logger.LogInformation("[dynamic] Recorded {Count} evidence items from predictions", evidenceRecorded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[dynamic] Evidence recording failed (non-blocking)");
            errors.Add($"evidence-recording: {ex.Message}");
        }

        _logger.LogInformation("[dynamic] Wrapping {Count} champion predictions as paper stock candidates", runPredictions.Count);

        // 3. Build stock candidates from champion predictions, then batch-save
        var directionalRankings = StockCandidateService.BuildDirectionalRankings(runPredictions);
        var builtCandidates = new List<(PredictionCandidate Pred, PaperStockCandidate Candidate, StockCandidateService.DirectionalRanking? Ranking)>();
        foreach (var pred in runPredictions)
        {
            try
            {
                directionalRankings.TryGetValue(pred.Id, out var ranking);
                var candidate = await _stockCandidates.BuildStockCandidateFromPredictionAsync(
                    pred,
                    scan.RunId,
                    ranking?.Percentile ?? 0,
                    ranking?.IsTopQuartile ?? false);
                builtCandidates.Add((pred, candidate, ranking));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[dynamic] Failed to build stock candidate for {Ticker}", pred.Ticker);
                errors.Add($"build-candidate {pred.Ticker}: {ex.Message}");
            }
        }

        // Batch save all candidates at once (chunks of 50 internally)
        List<PaperStockCandidate> savedList;
        var stockSaveFailures = 0;
        try
        {
            var allCandidatesToSave = builtCandidates.Select(b => b.Candidate).ToList();
            savedList = await _stockRepo.SaveCandidatesBatchAsync(allCandidatesToSave);
            stockSaveFailures = allCandidatesToSave.Count - savedList.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[dynamic] PIPELINE BREAK: Batch save of paper stock candidates failed entirely");
            errors.Add($"batch-save-candidates: {ex.Message}");
            savedList = [];
            stockSaveFailures = builtCandidates.Count;
        }

        // Match saved rows back by prediction_id — ticker+runId is not unique
        // (same ticker can appear per time window and per profile in one run).
        var savedLookup = new Dictionary<string, PaperStockCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in savedList)
        {
            if (!string.IsNullOrEmpty(s.PredictionId))
                savedLookup.TryAdd(s.PredictionId, s);
        }

        var stockBuilds = new List<StockCandidateService.StockCandidateBuild>();
        foreach (var (pred, candidate, ranking) in builtCandidates)
        {
            savedLookup.TryGetValue(pred.Id, out var saved);

            if (saved is null)
            {
                _logger.LogError("[dynamic] PIPELINE BREAK: Failed to save paper stock candidate for {Ticker} (prediction {PredId}). " +
                    "This likely means the database schema is out of sync with the code. Check for missing columns.",
                    pred.Ticker, pred.Id);
            }

            // Classify as a trade setup (non-blocking)
            try
            {
                await _stockCandidates.ClassifyAndSaveSetupAsync(pred, saved?.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[dynamic] Setup classification failed for {Ticker}", pred.Ticker);
            }

            stockBuilds.Add(new StockCandidateService.StockCandidateBuild(pred, candidate, saved, ranking));
        }

        if (stockSaveFailures > 0)
        {
            var msg = $"PIPELINE BREAK: {stockSaveFailures}/{runPredictions.Count} paper stock candidates failed to save. " +
                      "Database schema may be missing columns. No portfolio positions or options can be generated from failed saves.";
            _logger.LogError("[dynamic] {Message}", msg);
            errors.Add(msg);
        }

        // 4. Option generation via extracted service
        var optionResult = await _optionCandidates.GenerateOptionCandidatesAsync(stockBuilds, scan.RunId, errors);

        var savedStockCandidates = stockBuilds
            .Where(b => b.SavedCandidate is not null)
            .Select(b => b.SavedCandidate!)
            .ToList();

        // 5. Auto-open portfolio positions via extracted service
        var actionableCandidates = savedStockCandidates
            .Where(c => c.IsActionable && c.Status == PaperStockStatus.open)
            .ToList();
        var portfolioPositionsOpened = await _portfolioLifecycle.OpenPositionsForCandidatesAsync(
            actionableCandidates, errors);

        var optionEligible = savedStockCandidates.Count(c => c.QualifiesForOptions);
        var report = $"Generated {savedStockCandidates.Count} paper stock candidates from {runPredictions.Count} predictions" +
                     (stockSaveFailures > 0 ? $" (WARNING: {stockSaveFailures} FAILED TO SAVE)" : "") +
                     $". {optionEligible} were learning-eligible for options. " +
                     $"Saved {optionResult.OptionsGenerated} paper option candidates and blocked {optionResult.BlockedCandidates}." +
                     (portfolioPositionsOpened > 0 ? $" Opened {portfolioPositionsOpened} portfolio positions." : "");

        return new DynamicMorningResult
        {
            RunId = scan.RunId,
            PredictionsGenerated = scan.PredictionsGenerated,
            StockCandidatesGenerated = savedStockCandidates.Count,
            StockCandidatesQualifiedForOptions = optionEligible,
            OptionCandidatesGenerated = optionResult.OptionsGenerated,
            Report = report,
            Errors = errors,
            StockCandidates = savedStockCandidates,
        };
    }

    // -----------------------------------------------------------------------
    // 2. EOD review: stocks + options, dynamic
    // -----------------------------------------------------------------------

    public async Task<DynamicEodResult> RunDynamicEodReviewAsync()
    {
        _logger.LogInformation("[dynamic] Starting dynamic EOD review...");
        var errors = new List<string>();
        var stockEvaluated = 0;

        // 1. Evaluate open paper stock candidates via extracted service
        var openStock = await _stockRepo.GetOpenCandidatesAsync();
        foreach (var c in openStock)
        {
            try
            {
                var ok = await _stockCandidates.EvaluateStockCandidateAsync(c);
                if (ok) stockEvaluated++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[dynamic] Stock eval failed {Ticker}", c.Ticker);
                errors.Add($"stock-eval {c.Ticker}: {ex.Message}");
            }
        }

        // 2. Evaluate active trade setups
        var setupsEvaluated = await _stockCandidates.EvaluateActiveTradeSetupsAsync(errors);

        // 3. Evaluate open paper option candidates (existing service)
        var optionOutcomes = await _optionCandidates.EvaluateAllOpenOptionsAsync();

        // 4. Run the original prediction outcome evaluator
        var eod = await _dailyService.RunEndOfDayReviewAsync();
        errors.AddRange(eod.Errors);

        // 5. Auto-close portfolio positions via extracted service
        var (portfolioPositionsClosed, portfolioPositionsSkipped) =
            await _portfolioLifecycle.ClosePositionsForCandidatesAsync(
                openStock, StockCandidateService.MinEvalHours, errors);

        // 6. Evaluate neutral predictions (parallel pipeline — does not touch directional evaluator)
        var neutralEvaluated = 0;
        try
        {
            neutralEvaluated = await _neutralEvaluator.EvaluateOpenNeutralPredictionsAsync(errors);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[dynamic] Neutral evaluation failed");
            errors.Add($"neutral-eval: {ex.Message}");
        }

        var report = $"Evaluated {stockEvaluated} paper stock candidates, " +
                     $"{optionOutcomes.Count} paper option candidates, " +
                     $"{setupsEvaluated} trade setups. " +
                     $"Existing predictions: {eod.PredictionsEvaluated}." +
                     (neutralEvaluated > 0 ? $" Neutral outcomes: {neutralEvaluated}." : "") +
                     (portfolioPositionsClosed > 0 ? $" Closed {portfolioPositionsClosed} portfolio positions." : "") +
                     (portfolioPositionsSkipped > 0 ? $" Skipped {portfolioPositionsSkipped} positions (time window not elapsed)." : "");

        return new DynamicEodResult
        {
            RunId = eod.RunId,
            StockOutcomesEvaluated = stockEvaluated,
            OptionOutcomesEvaluated = optionOutcomes.Count,
            Report = report,
            Errors = errors,
        };
    }

    // -----------------------------------------------------------------------
    // 3. Learning update
    // -----------------------------------------------------------------------

    public async Task<DynamicLearningResult> RunDynamicLearningUpdateAsync()
    {
        _logger.LogInformation("[dynamic] Starting dynamic learning update...");
        var errors = new List<string>();

        var existing = await _dailyService.RunLearningUpdateAsync();
        errors.AddRange(existing.Errors);

        // Opportunity Learning scan (non-blocking)
        int opportunityRecords = 0;
        try
        {
            var oppResult = await _opportunityLearning.ScanForMissedOpportunitiesAsync();
            opportunityRecords = oppResult.RecordsCreated;
            if (oppResult.Errors.Count > 0)
                errors.AddRange(oppResult.Errors.Select(e => $"opportunity: {e}"));
            _logger.LogInformation(
                "[dynamic] Opportunity scan: {Scanned} tickers, {Created} records, {Missed} missed",
                oppResult.TickersScanned, oppResult.RecordsCreated, oppResult.CompletelyMissed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[dynamic] Opportunity scan failed (non-blocking)");
            errors.Add($"opportunity-scan: {ex.Message}");
        }

        var stockStats = await _stockRepo.GetAllLearningStatsAsync();
        var optionStats = await _optionsRepo.GetAllOptionLearningStatsAsync();

        var report = $"{existing.Report} " +
                     $"Stock learning rows: {stockStats.Count}. Option learning rows: {optionStats.Count}. " +
                     $"Opportunity records: {opportunityRecords}.";

        return new DynamicLearningResult
        {
            RunId = existing.RunId,
            StockStatsUpdated = stockStats.Count,
            OptionStatsUpdated = optionStats.Count,
            WeightsAdjusted = existing.WeightsAdjusted,
            InsightsGenerated = existing.InsightsGenerated,
            Report = report,
            Errors = errors,
        };
    }

    // -----------------------------------------------------------------------
    // 4. Dashboard summary
    // -----------------------------------------------------------------------

    public async Task<DynamicDashboardSummary> GetDashboardSummaryAsync()
    {
        var todayStart = DateTimeOffset.UtcNow;
        todayStart = new DateTimeOffset(todayStart.Year, todayStart.Month, todayStart.Day, 0, 0, 0, TimeSpan.Zero);
        var tomorrowStart = todayStart.AddDays(1);
        var last7DaysStart = todayStart.AddDays(-6);

        var createdTodayFilter = BuildUtcRangeFilter("created_at", todayStart, tomorrowStart);
        var evaluatedTodayFilter = BuildUtcRangeFilter("evaluation_time", todayStart, tomorrowStart);
        var evaluatedLast7DaysFilter = BuildUtcRangeFilter("evaluation_time", last7DaysStart, tomorrowStart);

        var stockTodayTask = _stockRepo.CountCandidatesAsync(createdTodayFilter);
        var optionTodayTask = _optionsRepo.CountPaperCandidatesEnhancedAsync(createdTodayFilter);
        var openStockTask = _stockRepo.CountCandidatesAsync("status=eq.open");
        var openOptionTask = _optionsRepo.CountPaperCandidatesEnhancedAsync("status=eq.open");
        var stockEvaluatedTodayTask = _stockRepo.CountOutcomesAsync(evaluatedTodayFilter);
        var optionEvaluatedTodayTask = _optionsRepo.CountOutcomesEnhancedAsync(evaluatedTodayFilter);
        var stockOutcomesTotalTask = _stockRepo.CountOutcomesAsync();
        var optionOutcomesTotalTask = _optionsRepo.CountOutcomesEnhancedAsync();
        var stockEvaluatedLast7DaysTask = _stockRepo.CountOutcomesAsync(evaluatedLast7DaysFilter);
        var optionEvaluatedLast7DaysTask = _optionsRepo.CountOutcomesEnhancedAsync(evaluatedLast7DaysFilter);
        var totalStockCandidatesTask = _stockRepo.CountCandidatesAsync();
        var totalOptionCandidatesTask = _optionsRepo.CountPaperCandidatesEnhancedAsync();
        var optionStatsTask = _optionsRepo.GetAllOptionLearningStatsAsync();
        var stockStatsTask = _stockRepo.GetAllLearningStatsAsync();
        var latestMorningRunTask = _researchRepo.GetLatestResearchRunAsync(ResearchRunType.morning_scan.ToString());

        await Task.WhenAll(
            stockTodayTask, optionTodayTask, openStockTask, openOptionTask,
            stockEvaluatedTodayTask, optionEvaluatedTodayTask,
            stockOutcomesTotalTask, optionOutcomesTotalTask,
            stockEvaluatedLast7DaysTask, optionEvaluatedLast7DaysTask,
            totalStockCandidatesTask, totalOptionCandidatesTask,
            optionStatsTask, stockStatsTask, latestMorningRunTask);

        var stockToday = stockTodayTask.Result;
        var optionToday = optionTodayTask.Result;
        var evaluatedToday = stockEvaluatedTodayTask.Result + optionEvaluatedTodayTask.Result;
        var optionStats = optionStatsTask.Result;
        var stockStats = stockStatsTask.Result;
        var latestMorningRun = latestMorningRunTask.Result;

        var latestRunPredictions = latestMorningRun is not null
            ? await _researchRepo.GetPredictionsByRunAsync(latestMorningRun.Id)
            : [];
        var latestRunStockCandidates = latestMorningRun is not null
            ? await _stockRepo.GetCandidatesByRunAsync(latestMorningRun.Id)
            : [];
        var latestRunAudits = latestMorningRun is not null
            ? await _optionCandidates.GetAuditsByRunAsync(latestMorningRun.Id)
            : [];
        var latestRunOptionCreated = latestRunAudits.Count(a => a.OptionCandidateCreated);
        var latestRunBlockedOptions = latestRunAudits.Count(a => !a.OptionCandidateCreated && !string.IsNullOrWhiteSpace(a.OptionBlockReason));
        var topBlockReason = latestRunAudits
            .Where(a => !string.IsNullOrWhiteSpace(a.OptionBlockReason))
            .GroupBy(a => a.OptionBlockReason!)
            .OrderByDescending(g => g.Count())
            .Select(g => new BlockReasonCount(g.Key, g.Count()))
            .ToList();
        var blockBreakdown = topBlockReason;
        var latestRunOptionEligible = latestRunStockCandidates.Count(c => c.QualifiesForOptions);

        var ranked = optionStats
            .Concat(stockStats.Select(s => new OptionLearningStat
            {
                StatType = s.StatType + " (stock)",
                StatKey = s.StatKey,
                TotalCandidates = s.TotalCandidates,
                WinRate = s.Accuracy,
                AverageOutcomeScore = s.AverageOutcomeScore,
            }))
            .Where(s => s.TotalCandidates >= 3)
            .OrderByDescending(s => s.WinRate)
            .ToList();

        var best = ranked.FirstOrDefault();
        var worst = ranked.LastOrDefault();

        string? insight = null;
        if (best is not null)
            insight = $"{best.StatType}:{best.StatKey} winning {best.WinRate * 100:F0}% over {best.TotalCandidates}";

        var recentStockCandidates = await _stockRepo.GetRecentCandidatesAsync(500);
        var recentStockOutcomes = await _stockRepo.GetRecentOutcomesAsync(500);
        var qualityTierPerformance = StockCandidateService.BuildQualityTierPerformance(recentStockCandidates, recentStockOutcomes);
        var confidenceCalibration = StockCandidateService.BuildConfidenceCalibration(recentStockCandidates, recentStockOutcomes);

        var portfolioSummary = await _portfolioLifecycle.GetSummaryAsync();

        var totalCandidates = totalStockCandidatesTask.Result + totalOptionCandidatesTask.Result;
        var totalOutcomes = stockOutcomesTotalTask.Result + optionOutcomesTotalTask.Result;
        var outcomeCoverageRate = totalCandidates > 0
            ? Math.Round(100.0 * totalOutcomes / totalCandidates, 1)
            : 0;

        _logger.LogInformation(
            "[dynamic-summary] stockToday={StockToday} optionToday={OptionToday} openStock={OpenStock} openOption={OpenOption} evaluatedToday={EvaluatedToday}",
            stockToday, optionToday, openStockTask.Result, openOptionTask.Result, evaluatedToday);

        return new DynamicDashboardSummary
        {
            StockPicksToday = stockToday,
            OptionPicksToday = optionToday,
            OpenStockCandidates = openStockTask.Result,
            OpenOptionCandidates = openOptionTask.Result,
            EvaluatedToday = evaluatedToday,
            BestSignalKey = best is null ? null : $"{best.StatType}:{best.StatKey}",
            BestSignalAccuracy = best?.WinRate ?? 0,
            WorstSignalKey = worst is null || ReferenceEquals(worst, best) ? null : $"{worst.StatType}:{worst.StatKey}",
            WorstSignalAccuracy = worst?.WinRate ?? 0,
            InsightOfTheDay = insight,
            LatestRunStartedAt = latestMorningRun?.StartedAt,
            LatestRunId = latestMorningRun?.Id,
            LatestRunPredictionCandidatesGenerated = latestRunPredictions.Count,
            LatestRunPaperStockCandidatesCreated = latestRunStockCandidates.Count,
            LatestRunPaperOptionCandidatesCreated = latestRunOptionCreated,
            LatestRunBlockedOptionCandidates = latestRunBlockedOptions,
            LatestRunTopOptionBlockReason = blockBreakdown.FirstOrDefault()?.Reason,
            TotalStockOutcomes = stockOutcomesTotalTask.Result,
            TotalOptionOutcomes = optionOutcomesTotalTask.Result,
            StockOutcomesAddedToday = stockEvaluatedTodayTask.Result,
            OptionOutcomesAddedToday = optionEvaluatedTodayTask.Result,
            StockOutcomesAddedLast7Days = stockEvaluatedLast7DaysTask.Result,
            OptionOutcomesAddedLast7Days = optionEvaluatedLast7DaysTask.Result,
            CandidatesAwaitingEodEvaluation = openStockTask.Result + openOptionTask.Result,
            OutcomeCoverageRate = outcomeCoverageRate,
            Funnel = new FunnelSummary
            {
                PredictionCandidates = latestRunPredictions.Count,
                StockCandidates = latestRunStockCandidates.Count,
                OptionEligible = latestRunOptionEligible,
                OptionCreated = latestRunOptionCreated,
                Evaluated = latestRunStockCandidates.Count(c => c.Status == PaperStockStatus.evaluated),
                LearningStatsUpdated = stockStats.Count + optionStats.Count,
            },
            BlockReasonBreakdown = blockBreakdown,
            QualityTierPerformance = qualityTierPerformance,
            ConfidenceCalibration = confidenceCalibration,
            PortfolioChallenge = portfolioSummary,
        };
    }

    private static string BuildUtcRangeFilter(string column, DateTimeOffset startInclusive, DateTimeOffset endExclusive)
        => $"{column}=gte.{FormatUtc(startInclusive)}&{column}=lt.{FormatUtc(endExclusive)}";

    private static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
}
