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
                        .Select(w => w?.ToString() ?? "").Where(w => w != "").OfType<string>().ToList()
                        ?? new List<string>()
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

    /// <summary>
    /// GET /api/dashboard/accuracy-history — daily prediction accuracy for charting.
    /// Returns per-day evaluated/correct/accuracy for the last 60 days,
    /// plus rolling 7-day and 30-day accuracy.
    /// </summary>
    [HttpGet("accuracy-history")]
    public async Task<IActionResult> GetAccuracyHistory()
    {
        try
        {
            var since = DateTimeOffset.UtcNow.AddDays(-90);
            var outcomes = await _researchRepo.GetOutcomesSinceAsync(since, limit: 2000);

            if (outcomes.Count == 0)
                return Ok(new { days = Array.Empty<object>() });

            // Group by evaluation date
            var byDay = outcomes
                .Where(o => o.DirectionCorrect is not null)
                .GroupBy(o => o.EvaluationTime.Date)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    date = g.Key.ToString("yyyy-MM-dd"),
                    evaluated = g.Count(),
                    correct = g.Count(o => o.DirectionCorrect == true),
                    incorrect = g.Count(o => o.DirectionCorrect == false),
                    accuracy = g.Count() > 0
                        ? Math.Round(100.0 * g.Count(o => o.DirectionCorrect == true) / g.Count(), 1)
                        : 0.0,
                })
                .ToList();

            // Compute rolling averages
            var result = new List<object>();
            for (int i = 0; i < byDay.Count; i++)
            {
                var day = byDay[i];

                // 7-day rolling
                var window7 = byDay.Skip(Math.Max(0, i - 6)).Take(Math.Min(7, i + 1)).ToList();
                var total7 = window7.Sum(d => d.evaluated);
                var correct7 = window7.Sum(d => d.correct);
                var accuracy7 = total7 > 0 ? (double?)Math.Round(100.0 * correct7 / total7, 1) : null;

                // 30-day rolling
                var window30 = byDay.Skip(Math.Max(0, i - 29)).Take(Math.Min(30, i + 1)).ToList();
                var total30 = window30.Sum(d => d.evaluated);
                var correct30 = window30.Sum(d => d.correct);
                var accuracy30 = total30 > 0 ? (double?)Math.Round(100.0 * correct30 / total30, 1) : null;

                result.Add(new
                {
                    day.date,
                    day.evaluated,
                    day.correct,
                    day.incorrect,
                    day.accuracy,
                    rolling7 = accuracy7,
                    rolling30 = accuracy30,
                    cumTotal = byDay.Take(i + 1).Sum(d => d.evaluated),
                    cumCorrect = byDay.Take(i + 1).Sum(d => d.correct),
                });
            }

            // Streak data
            var orderedOutcomes = outcomes
                .Where(o => o.DirectionCorrect is not null)
                .OrderByDescending(o => o.EvaluationTime)
                .ToList();

            int currentStreak = 0;
            bool? streakType = null;
            int longestWin = 0, longestLoss = 0, tempStreak = 0;
            bool? lastDirection = null;

            foreach (var o in orderedOutcomes)
            {
                if (streakType is null)
                {
                    streakType = o.DirectionCorrect;
                    currentStreak = 1;
                }
                else if (currentStreak > 0 && o.DirectionCorrect == streakType)
                {
                    currentStreak++;
                }

                // longest streaks
                if (o.DirectionCorrect == lastDirection)
                {
                    tempStreak++;
                }
                else
                {
                    if (lastDirection == true) longestWin = Math.Max(longestWin, tempStreak);
                    if (lastDirection == false) longestLoss = Math.Max(longestLoss, tempStreak);
                    tempStreak = 1;
                    lastDirection = o.DirectionCorrect;
                }
            }
            if (lastDirection == true) longestWin = Math.Max(longestWin, tempStreak);
            if (lastDirection == false) longestLoss = Math.Max(longestLoss, tempStreak);

            return Ok(new
            {
                days = result,
                streak = new
                {
                    current = currentStreak,
                    type = streakType == true ? "win" : streakType == false ? "loss" : "none",
                    longestWin,
                    longestLoss,
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[dashboard] Failed to compute accuracy history");
            return StatusCode(500, new { error = "Failed to compute accuracy history" });
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
