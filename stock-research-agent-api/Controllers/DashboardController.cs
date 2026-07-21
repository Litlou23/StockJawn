using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// GET /api/dashboard/summary — aggregated dashboard data for the Next.js frontend.
/// Pulls from watchlist, research, and learning repositories in parallel.
/// </summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly WatchlistRepository _watchlistRepo;
    private readonly ResearchRepository _researchRepo;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(
        WatchlistRepository watchlistRepo,
        ResearchRepository researchRepo,
        ILogger<DashboardController> logger)
    {
        _watchlistRepo = watchlistRepo;
        _researchRepo = researchRepo;
        _logger = logger;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        try
        {
            // Resolve champion profile for prediction filtering
            var championId = await _researchRepo.GetChampionProfileIdAsync();

            // Fire all queries in parallel
            var activeTask = _watchlistRepo.GetWatchlistByStatusAsync("active");
            var reviewTask = _watchlistRepo.GetWatchlistByStatusAsync("review_needed");
            var swapTask = _watchlistRepo.GetWatchlistByStatusAsync("swap_candidate");
            var candidatesTask = _watchlistRepo.GetRecentCandidatesAsync(10);
            var changesTask = _watchlistRepo.GetRecentChangeLogsAsync(10);
            var recentRunsTask = _researchRepo.GetRecentResearchRunsAsync(10);
            var predictionStatsTask = _researchRepo.GetPredictionStatsAsync(profileId: championId);
            var directionalStatsTask = _researchRepo.GetDirectionalStockStatsAsync();
            var longTermStatsTask = _researchRepo.GetLongTermStockStatsAsync();
            var scanResultStatsTask = _researchRepo.GetScanResultStatsAsync();
            var paperOptionStatsTask = _researchRepo.GetPaperOptionStatsAsync();
            var recentPredictionsTask = _researchRepo.GetRecentPredictionsWithOutcomesAsync(10, profileId: championId);
            var recentScanResultsTask = _researchRepo.GetRecentScanResultsAsync(10);
            var signalPerfTask = _researchRepo.GetAllSignalPerformanceAsync();
            var insightsTask = _researchRepo.GetRecentLearningInsightsAsync(5);
            var weightsTask = _researchRepo.GetScoringWeightsAsync();

            await Task.WhenAll(
                activeTask, reviewTask, swapTask, candidatesTask, changesTask,
                recentRunsTask, predictionStatsTask, directionalStatsTask,
                longTermStatsTask, scanResultStatsTask, paperOptionStatsTask,
                recentPredictionsTask, recentScanResultsTask, signalPerfTask,
                insightsTask, weightsTask);

            var active = activeTask.Result;
            var review = reviewTask.Result;
            var swap = swapTask.Result;
            var candidates = candidatesTask.Result;
            var changes = changesTask.Result;
            var runs = recentRunsTask.Result;
            var predictionStats = predictionStatsTask.Result;
            var directionalStats = directionalStatsTask.Result;
            var longTermStats = longTermStatsTask.Result;
            var scanResultStats = scanResultStatsTask.Result;
            var paperOptionStats = paperOptionStatsTask.Result;
            var recentPredictions = recentPredictionsTask.Result;
            var recentScanResults = recentScanResultsTask.Result;
            var signalPerf = signalPerfTask.Result;
            var insights = insightsTask.Result;
            var weights = weightsTask.Result;

            // Derive job statuses from research_runs
            var latestMorningScan = runs.FirstOrDefault(r => r.RunType == Models.ResearchRunType.morning_scan);
            var latestEodReview = runs.FirstOrDefault(r => r.RunType == Models.ResearchRunType.end_of_day_review);
            var latestLearningUpdate = runs.FirstOrDefault(r => r.RunType == Models.ResearchRunType.learning_update);

            // Data quality warnings
            var warnings = new List<string>();
            if (active.Count == 0) warnings.Add("No active watchlist items — run weekly research to populate.");
            if (predictionStats.TotalPredictions == 0) warnings.Add("No predictions generated yet — run a morning scan.");
            if (predictionStats.EvaluatedPredictions == 0) warnings.Add("No outcomes recorded yet — run an EOD review after predictions have had time.");
            if (signalPerf.Count == 0) warnings.Add("No signal performance data — the learning engine hasn't run yet.");

            // Check for items with missing data
            var itemsWithMissingData = active
                .Where(i => i.MissingDataWarnings is System.Text.Json.Nodes.JsonArray arr && arr.Count > 0)
                .Select(i => new
                {
                    i.Ticker,
                    Warnings = (i.MissingDataWarnings as System.Text.Json.Nodes.JsonArray)?
                        .Select(w => w?.ToString() ?? "").Where(w => w != "").ToList() ?? new List<string>()
                })
                .ToList();

            if (itemsWithMissingData.Count > 0)
                warnings.Add($"{itemsWithMissingData.Count} watchlist item(s) have missing data warnings.");

            return Ok(new
            {
                overview = new
                {
                    activeCount = active.Count,
                    reviewNeededCount = review.Count,
                    swapCandidateCount = swap.Count,
                    candidatesScored = candidates.Count,
                },
                predictionStats = new
                {
                    predictionStats.TotalPredictions,
                    predictionStats.EvaluatedPredictions,
                    predictionStats.CorrectPredictions,
                    predictionStats.IncorrectPredictions,
                    predictionStats.InconclusivePredictions,
                    predictionStats.PendingPredictions,
                    predictionStats.AccuracyPercent,
                },
                directionalStockStats = new
                {
                    directionalStats.Total,
                    directionalStats.Evaluated,
                    directionalStats.Correct,
                    directionalStats.Incorrect,
                    directionalStats.Pending,
                    directionalStats.AccuracyPercent,
                },
                longTermStockStats = new
                {
                    longTermStats.Total,
                    longTermStats.Evaluated,
                    longTermStats.Correct,
                    longTermStats.Incorrect,
                    longTermStats.Pending,
                    longTermStats.AccuracyPercent,
                },
                paperOptionStats = new
                {
                    paperOptionStats.Total,
                    paperOptionStats.Evaluated,
                    paperOptionStats.Profitable,
                    paperOptionStats.Unprofitable,
                    paperOptionStats.Open,
                    paperOptionStats.WinRatePercent,
                },
                scanResultStats = new
                {
                    scanResultStats.Total,
                    scanResultStats.NeutralNoEdge,
                    scanResultStats.NeutralRangeBound,
                    scanResultStats.NeutralHighVolatility,
                    scanResultStats.WatchOnly,
                    scanResultStats.Rejected,
                    scanResultStats.Unavailable,
                    scanResultStats.Legacy,
                },
                watchlist = new
                {
                    active = active.Select(i => new
                    {
                        i.Ticker, i.CompanyName, i.TotalScore, i.Category,
                        i.WatchReason, i.ThesisSummary, i.DataConfidence,
                        i.CatalystScore, i.RiskScore, i.InvalidationPoint,
                        lastReviewedAt = i.LastReviewedAt?.ToString("o"),
                    }),
                    reviewNeeded = review.Select(i => new
                    {
                        i.Ticker, i.CompanyName, i.TotalScore, i.SwapReason,
                        i.DataConfidence, reviewByDate = i.ReviewByDate,
                    }),
                    swapCandidates = swap.Select(i => new
                    {
                        i.Ticker, i.CompanyName, i.TotalScore, i.SwapReason, i.DataConfidence,
                    }),
                },
                recentChanges = changes.Select(c => new
                {
                    c.Ticker, c.ChangeType, c.PreviousStatus, c.NewStatus,
                    c.PreviousScore, c.NewScore, c.Reason,
                    createdAt = c.CreatedAt.ToString("o"),
                }),
                jobs = new
                {
                    morningScan = FormatJobStatus(latestMorningScan),
                    eodReview = FormatJobStatus(latestEodReview),
                    learningUpdate = FormatJobStatus(latestLearningUpdate),
                },
                recentPredictions = recentPredictions.Select(pw => new
                {
                    pw.Prediction.Id,
                    pw.Prediction.Ticker,
                    pw.Prediction.PredictionType,
                    pw.Prediction.ConfidenceScore,
                    pw.Prediction.ImportanceScore,
                    pw.Prediction.RiskScore,
                    pw.Prediction.Status,
                    pw.Prediction.PredictionReason,
                    pw.Prediction.BullishCase,
                    pw.Prediction.BearishCase,
                    pw.Prediction.EntryReferencePrice,
                    pw.Prediction.Atr14,
                    pw.Prediction.AtrPercent,
                    pw.Prediction.ExpectedMoveDollar,
                    pw.Prediction.ExpectedMovePercent,
                    pw.Prediction.PredictedPrice,
                    pw.Prediction.PredictedMovePercent,
                    pw.Prediction.ProjectedPriceLow,
                    pw.Prediction.ProjectedPriceHigh,
                    pw.Prediction.TargetPrice,
                    pw.Prediction.StopPrice,
                    pw.Prediction.InvalidationPrice,
                    pw.Prediction.SupportLevel,
                    pw.Prediction.ResistanceLevel,
                    pw.Prediction.RiskRewardRatio,
                    pw.Prediction.PricePredictionMethod,
                    pw.Prediction.PricePredictionWarnings,
                    pw.Prediction.InvalidationRule,
                    pw.Prediction.TimeWindow,
                    pw.Prediction.DataSourcesUsed,
                    pw.Prediction.MissingDataWarnings,
                    createdAt = pw.Prediction.CreatedAt.ToString("o"),
                    hasOutcome = pw.Outcome is not null,
                    verdict = pw.Outcome?.DirectionCorrect,
                    targetHit = pw.Outcome?.TargetHit,
                    stopHit = pw.Outcome?.StopHit,
                    wasInProjectedZone = pw.Outcome?.WasInProjectedZone,
                    priceAccuracyPercent = pw.Outcome?.PriceAccuracyPercent,
                    pricePredictionErrorPercent = pw.Outcome?.PricePredictionErrorPercent,
                    finalMovePercent = pw.Outcome?.PercentMove,
                    maxFavorablePercent = pw.Outcome?.MaxFavorablePercent,
                    maxAdversePercent = pw.Outcome?.MaxAdversePercent,
                    evaluatedAt = pw.Outcome?.EvaluationTime.ToString("o"),
                }),
                recentScanResults = recentScanResults.Select(p => new
                {
                    p.Id,
                    p.Ticker,
                    predictionType = p.PredictionType.ToString(),
                    p.ConfidenceScore,
                    p.RiskScore,
                    p.PredictionReason,
                    p.TimeWindow,
                    createdAt = p.CreatedAt.ToString("o"),
                }),
                learning = new
                {
                    signalPerformance = signalPerf.Select(s => new
                    {
                        s.SignalName, s.SignalType, s.TotalPredictions,
                        s.CorrectPredictions, s.Accuracy, s.AverageOutcomeScore,
                        lastUpdatedAt = s.LastUpdatedAt.ToString("o"),
                    }),
                    recentInsights = insights.Select(i => new
                    {
                        i.InsightType, i.Summary, i.ActionRecommendation,
                        i.Confidence, createdAt = i.CreatedAt.ToString("o"),
                    }),
                    scoringWeights = weights.Select(w => new
                    {
                        w.SignalName, w.Weight, w.Reason,
                    }),
                },
                dataQuality = new
                {
                    warnings,
                    missingDataByTicker = itemsWithMissingData,
                    supabaseConfigured = _researchRepo.IsConfigured,
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[dashboard] Failed to build summary");
            return StatusCode(500, new { error = "Failed to build dashboard summary", detail = ex.Message });
        }
    }

    private static object? FormatJobStatus(Models.ResearchRun? run)
    {
        if (run is null) return new { status = "never_run", lastRun = (string?)null };
        return new
        {
            status = run.Status.ToString(),
            lastRun = run.StartedAt.ToString("o"),
            completedAt = run.CompletedAt?.ToString("o"),
            summary = run.Summary,
            predictionsGenerated = run.PredictionsGenerated,
            predictionsEvaluated = run.PredictionsEvaluated,
            errors = run.Errors,
        };
    }
}
