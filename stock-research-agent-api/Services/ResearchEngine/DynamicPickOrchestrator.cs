using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.OptionsData;
using StockResearchAgent.Api.Services.Portfolio;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Dynamic pick orchestrator — the daily loop entry point for the
/// /stock-lab and /paper-options pages.
///
///   Stock signal engine -> paper stock candidate -> option contract scanner
///   -> paper option candidate -> stock outcome evaluator -> option outcome
///   evaluator -> learning engine.
///
/// Wraps the existing PredictionGenerator / OutcomeEvaluator / LearningEngine
/// (which keep working unchanged) with a new paper_stock_candidates layer
/// and automatic linked option-candidate generation. No invented data —
/// stock prices come from Twelve Data, option prices from MarketData.app,
/// and if either is unavailable the candidate is saved with
/// status='unavailable' / data_availability='unavailable'.
/// </summary>
public class DynamicPickOrchestrator
{
    private const int LearningMinConfidenceForOptions = 15;
    private const int LearningMaxRiskForOptions = 90;
    private const int ActionableShadowMinConfidence = 40;
    private const int ActionableShadowMaxRisk = 75;
    private const int LiveEligibleMinConfidence = 60;
    private const int LiveEligibleMaxRisk = 65;
    private const int MaxOptionCandidatesPerRun = 25;
    private const int MaxOptionCandidatesPerTickerPerRun = 1;
    private const string ThresholdPolicyVersion = "learning_options_v1";

    private readonly DailyResearchRunService _dailyService;
    private readonly ResearchRepository _researchRepo;
    private readonly PaperStockCandidateRepository _stockRepo;
    private readonly OptionsDataRepository _optionsRepo;
    private readonly CandidateGenerationAuditRepository _auditRepo;
    private readonly PaperOptionsService _paperOptions;
    private readonly MarketDataOptionsProvider _optionsProvider;
    private readonly MarketDataService _marketData;
    private readonly LearningEngine _learning;
    private readonly PortfolioBalanceEngine _portfolio;
    private readonly PortfolioChallengeRepository _portfolioRepo;
    private readonly ILogger<DynamicPickOrchestrator> _logger;

    public DynamicPickOrchestrator(
        DailyResearchRunService dailyService,
        ResearchRepository researchRepo,
        PaperStockCandidateRepository stockRepo,
        OptionsDataRepository optionsRepo,
        CandidateGenerationAuditRepository auditRepo,
        PaperOptionsService paperOptions,
        MarketDataOptionsProvider optionsProvider,
        MarketDataService marketData,
        LearningEngine learning,
        PortfolioBalanceEngine portfolio,
        PortfolioChallengeRepository portfolioRepo,
        ILogger<DynamicPickOrchestrator> logger)
    {
        _dailyService = dailyService;
        _researchRepo = researchRepo;
        _stockRepo = stockRepo;
        _optionsRepo = optionsRepo;
        _auditRepo = auditRepo;
        _paperOptions = paperOptions;
        _optionsProvider = optionsProvider;
        _marketData = marketData;
        _learning = learning;
        _portfolio = portfolio;
        _portfolioRepo = portfolioRepo;
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
        var runPredictions = await _researchRepo.GetPredictionsByRunAsync(scan.RunId);

        _logger.LogInformation("[dynamic] Wrapping {Count} predictions as paper stock candidates", runPredictions.Count);

        var directionalRankings = BuildDirectionalRankings(runPredictions);
        var stockBuilds = new List<StockCandidateBuild>();
        var stockSaveFailures = 0;
        foreach (var pred in runPredictions)
        {
            directionalRankings.TryGetValue(pred.Id, out var ranking);
            var candidate = await BuildStockCandidateFromPredictionAsync(
                pred,
                scan.RunId,
                ranking?.Percentile ?? 0,
                ranking?.IsTopQuartile ?? false);
            var saved = await _stockRepo.SaveCandidateAsync(candidate);
            if (saved is null)
            {
                stockSaveFailures++;
                _logger.LogError("[dynamic] PIPELINE BREAK: Failed to save paper stock candidate for {Ticker} (prediction {PredId}). " +
                    "This likely means the database schema is out of sync with the code. Check for missing columns.",
                    pred.Ticker, pred.Id);
            }
            stockBuilds.Add(new StockCandidateBuild(pred, candidate, saved, ranking));
        }

        if (stockSaveFailures > 0)
        {
            var msg = $"PIPELINE BREAK: {stockSaveFailures}/{runPredictions.Count} paper stock candidates failed to save. " +
                      "Database schema may be missing columns. No portfolio positions or options can be generated from failed saves.";
            _logger.LogError("[dynamic] {Message}", msg);
            errors.Add(msg);
        }

        // 4. Option generation in learning mode with per-run and per-ticker caps.
        var optionAttempts = stockBuilds
            .Where(b => b.SavedCandidate is not null && b.SavedCandidate.QualifiesForOptions)
            .OrderByDescending(b => b.SavedCandidate!.ScorePercentileInRun)
            .ThenByDescending(b => b.Prediction.ConfidenceScore)
            .ThenBy(b => b.Prediction.RiskScore)
            .ThenByDescending(b => b.SavedCandidate!.DataAvailability == "real")
            .ToList();

        var selectedForOptions = new List<StockCandidateBuild>();
        var tickerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var build in optionAttempts)
        {
            if (selectedForOptions.Count >= MaxOptionCandidatesPerRun) break;
            var ticker = build.SavedCandidate!.Ticker;
            tickerCounts.TryGetValue(ticker, out var currentPerTicker);
            if (currentPerTicker >= MaxOptionCandidatesPerTickerPerRun) continue;
            selectedForOptions.Add(build);
            tickerCounts[ticker] = currentPerTicker + 1;
        }

        var selectedIds = selectedForOptions
            .Select(b => b.Prediction.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var optionsGenerated = 0;
        var blockedOptionCandidates = 0;
        var auditRows = new List<CandidateGenerationAuditEntry>();

        foreach (var build in stockBuilds)
        {
            var savedStock = build.SavedCandidate;
            var optionCreated = false;
            var paperOptionCandidateId = (string?)null;
            var optionBlockReason = savedStock?.ExclusionReason;
            var optionChainAvailable = false;
            var marketDataAvailable = savedStock is not null && savedStock.EntryPrice is > 0;

            if (savedStock is not null && savedStock.QualifiesForOptions)
            {
                if (!selectedIds.Contains(build.Prediction.Id))
                {
                    optionBlockReason = "max_candidates_reached";
                    blockedOptionCandidates++;
                }
                else
                {
                    try
                    {
                        var resp = await _paperOptions.GenerateCandidatesAsync(new GenerateCandidatesRequest
                        {
                            PredictionId = savedStock.PredictionId ?? "",
                            DurationPreference = ChooseDuration(savedStock),
                            AutoSave = true,
                            PaperStockCandidateId = savedStock.Id,
                            CandidateMode = savedStock.CandidateMode,
                            QualityTier = savedStock.QualityTier,
                            IsActionable = savedStock.IsActionable,
                            ThresholdPolicyVersion = savedStock.ThresholdPolicyVersion,
                            InclusionReason = savedStock.InclusionReason,
                            ExclusionReason = savedStock.ExclusionReason,
                            ScorePercentileInRun = savedStock.ScorePercentileInRun,
                        });

                        optionChainAvailable = resp?.OptionChainAvailable == true;
                        marketDataAvailable = resp?.MarketDataAvailable == true || marketDataAvailable;

                        if (resp?.SavedCandidate is not null)
                        {
                            optionCreated = true;
                            paperOptionCandidateId = resp.SavedCandidate.Id;
                            optionsGenerated++;
                            optionBlockReason = null;
                        }
                        else
                        {
                            optionBlockReason = resp?.BlockReason ?? "unknown_error";
                            blockedOptionCandidates++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[dynamic] Option generation failed for {Ticker}", savedStock.Ticker);
                        errors.Add($"option-gen {savedStock.Ticker}: {ex.Message}");
                        optionBlockReason = "unknown_error";
                        blockedOptionCandidates++;
                    }
                }
            }
            else if (savedStock is not null)
            {
                optionBlockReason = savedStock.ExclusionReason ?? "confidence_below_learning_threshold";
            }

            auditRows.Add(new CandidateGenerationAuditEntry
            {
                RunId = build.Prediction.RunId,
                Ticker = build.Prediction.Ticker,
                PredictionCandidateId = build.Prediction.Id,
                PaperStockCandidateId = savedStock?.Id,
                PaperOptionCandidateId = paperOptionCandidateId,
                PredictionType = build.Prediction.PredictionType.ToString(),
                ConfidenceScore = build.Prediction.ConfidenceScore,
                RiskScore = build.Prediction.RiskScore,
                ScorePercentileInRun = build.Ranking?.Percentile ?? 0,
                StockCandidateCreated = savedStock is not null,
                OptionCandidateCreated = optionCreated,
                CandidateMode = savedStock?.CandidateMode ?? DetermineCandidateMode(build.Prediction),
                QualityTier = savedStock?.QualityTier ?? DetermineQualityTier(build.Prediction.ConfidenceScore),
                OptionBlockReason = optionBlockReason,
                MarketDataAvailable = marketDataAvailable,
                OptionChainAvailable = optionChainAvailable,
                ThresholdPolicyVersion = ThresholdPolicyVersion,
            });
        }

        foreach (var audit in auditRows)
            await _auditRepo.SaveAsync(audit);

        var savedStockCandidates = stockBuilds
            .Where(b => b.SavedCandidate is not null)
            .Select(b => b.SavedCandidate!)
            .ToList();

        // 5. Auto-open portfolio positions for actionable candidates.
        var portfolioPositionsOpened = 0;
        var activeChallenge = await _portfolioRepo.GetActiveChallengeAsync();
        if (activeChallenge is not null)
        {
            var actionableCandidates = savedStockCandidates
                .Where(c => c.IsActionable
                    && c.Status == PaperStockStatus.open
                    && c.EntryPrice is > 0
                    && PredictionCategoryHelper.IsDirectional(c.PredictionType))
                .ToList();

            foreach (var c in actionableCandidates)
            {
                try
                {
                    var pos = await _portfolio.AutoOpenPositionAsync(
                        activeChallenge.Id,
                        c.PredictionId,
                        c.Ticker,
                        c.EntryPrice!.Value,
                        PositionAssetType.stock,
                        $"Auto from paper stock candidate. Mode={c.CandidateMode}, tier={c.QualityTier}, conf={c.ConfidenceScore}");

                    if (pos is not null) portfolioPositionsOpened++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[dynamic] Portfolio position open failed for {Ticker}", c.Ticker);
                    errors.Add($"portfolio-open {c.Ticker}: {ex.Message}");
                }
            }

            if (portfolioPositionsOpened > 0)
                _logger.LogInformation("[dynamic] Opened {Count} portfolio positions from actionable candidates",
                    portfolioPositionsOpened);
        }

        var optionEligible = savedStockCandidates.Count(c => c.QualifiesForOptions);
        var report = $"Generated {savedStockCandidates.Count} paper stock candidates from {runPredictions.Count} predictions" +
                     (stockSaveFailures > 0 ? $" (WARNING: {stockSaveFailures} FAILED TO SAVE)" : "") +
                     $". {optionEligible} were learning-eligible for options. " +
                     $"Saved {optionsGenerated} paper option candidates and blocked {blockedOptionCandidates}." +
                     (portfolioPositionsOpened > 0 ? $" Opened {portfolioPositionsOpened} portfolio positions." : "");

        return new DynamicMorningResult
        {
            RunId = scan.RunId,
            PredictionsGenerated = scan.PredictionsGenerated,
            StockCandidatesGenerated = savedStockCandidates.Count,
            StockCandidatesQualifiedForOptions = optionEligible,
            OptionCandidatesGenerated = optionsGenerated,
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

        // 1. Evaluate open paper stock candidates
        var openStock = await _stockRepo.GetOpenCandidatesAsync();
        foreach (var c in openStock)
        {
            try
            {
                var ok = await EvaluateStockCandidateAsync(c);
                if (ok) stockEvaluated++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[dynamic] Stock eval failed {Ticker}", c.Ticker);
                errors.Add($"stock-eval {c.Ticker}: {ex.Message}");
            }
        }

        // 2. Evaluate open paper option candidates (existing service)
        var optionOutcomes = await _paperOptions.EvaluateAllOpenAsync();

        // 3. Also run the original prediction outcome evaluator so the
        // existing learning loop keeps producing prediction_outcomes rows.
        var eod = await _dailyService.RunEndOfDayReviewAsync();
        errors.AddRange(eod.Errors);

        // 4. Auto-close portfolio positions whose paper stock candidates were just evaluated.
        //    We look up the exit price by fetching a current quote for each ticker
        //    that had open portfolio positions. This is more reliable than searching
        //    outcomes (which may not have been saved yet at query time).
        var portfolioPositionsClosed = 0;
        foreach (var c in openStock)
        {
            if (c.PredictionId is null) continue;
            try
            {
                var portfolioPositions = await _portfolioRepo.GetOpenPositionsByPredictionIdAsync(c.PredictionId);
                if (portfolioPositions.Count == 0) continue;

                var quote = await _marketData.GetQuoteAsync(c.Ticker);
                if (quote is null || quote.Price <= 0) continue;

                foreach (var pos in portfolioPositions)
                {
                    var closed = await _portfolio.ClosePositionAsync(new ClosePositionRequest
                    {
                        PositionId = pos.Id,
                        ExitPrice = quote.Price,
                        ReasonExited = $"EOD auto-close. {c.Ticker} current price ${quote.Price:F2}.",
                    });

                    if (closed is not null) portfolioPositionsClosed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[dynamic] Portfolio position close failed for prediction {PredId}", c.PredictionId);
                errors.Add($"portfolio-close {c.Ticker}: {ex.Message}");
            }
        }

        var report = $"Evaluated {stockEvaluated} paper stock candidates, " +
                     $"{optionOutcomes.Count} paper option candidates. " +
                     $"Existing predictions: {eod.PredictionsEvaluated}." +
                     (portfolioPositionsClosed > 0 ? $" Closed {portfolioPositionsClosed} portfolio positions." : "");

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
    // 3. Learning update — wraps the existing engine, plus exposes counts
    // for stock_learning_stats / option_learning_stats which already
    // populate during EOD evaluation.
    // -----------------------------------------------------------------------

    public async Task<DynamicLearningResult> RunDynamicLearningUpdateAsync()
    {
        _logger.LogInformation("[dynamic] Starting dynamic learning update...");
        var errors = new List<string>();

        // 1. Existing signal performance + weight adjustment + insights
        var existing = await _dailyService.RunLearningUpdateAsync();
        errors.AddRange(existing.Errors);

        // 2. Count what's been written to the new stat tables
        var stockStats = await _stockRepo.GetAllLearningStatsAsync();
        var optionStats = await _optionsRepo.GetAllOptionLearningStatsAsync();

        var report = $"{existing.Report} " +
                     $"Stock learning rows: {stockStats.Count}. Option learning rows: {optionStats.Count}.";

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
            stockTodayTask,
            optionTodayTask,
            openStockTask,
            openOptionTask,
            stockEvaluatedTodayTask,
            optionEvaluatedTodayTask,
            stockOutcomesTotalTask,
            optionOutcomesTotalTask,
            stockEvaluatedLast7DaysTask,
            optionEvaluatedLast7DaysTask,
            totalStockCandidatesTask,
            totalOptionCandidatesTask,
            optionStatsTask,
            stockStatsTask,
            latestMorningRunTask);

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
            ? await _auditRepo.GetByRunAsync(latestMorningRun.Id)
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

        // Best/worst signals from option_learning_stats (need >= 3 samples)
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

        // Insight of the day — pick the highest-confidence-impact phrase.
        string? insight = null;
        if (best is not null)
            insight = $"{best.StatType}:{best.StatKey} winning {best.WinRate * 100:F0}% over {best.TotalCandidates}";

        var recentStockCandidates = await _stockRepo.GetRecentCandidatesAsync(500);
        var recentStockOutcomes = await _stockRepo.GetRecentOutcomesAsync(500);
        var qualityTierPerformance = BuildQualityTierPerformance(recentStockCandidates, recentStockOutcomes);
        var confidenceCalibration = BuildConfidenceCalibration(recentStockCandidates, recentStockOutcomes);

        // Portfolio challenge summary (if one exists)
        var portfolioSummary = await _portfolio.GetSummaryAsync();

        var totalCandidates = totalStockCandidatesTask.Result + totalOptionCandidatesTask.Result;
        var totalOutcomes = stockOutcomesTotalTask.Result + optionOutcomesTotalTask.Result;
        var outcomeCoverageRate = totalCandidates > 0
            ? Math.Round(100.0 * totalOutcomes / totalCandidates, 1)
            : 0;

        _logger.LogInformation(
            "[dynamic-summary] stockToday={StockToday} optionToday={OptionToday} openStock={OpenStock} openOption={OpenOption} evaluatedToday={EvaluatedToday}",
            stockToday,
            optionToday,
            openStockTask.Result,
            openOptionTask.Result,
            evaluatedToday);

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

    // -----------------------------------------------------------------------
    // Helpers: build a paper stock candidate from a prediction
    // -----------------------------------------------------------------------

    private async Task<PaperStockCandidate> BuildStockCandidateFromPredictionAsync(
        PredictionCandidate pred, string runId, double percentileInRun, bool isTopQuartileDirectional)
    {
        var warnings = new List<string>(pred.MissingDataWarnings);

        var dataAvailability = pred.MissingDataWarnings.Count == 0
            ? "real"
            : (pred.EntryReferencePrice is null or 0 ? "unavailable" : "partial");

        // Try to enrich entry/target/stop with current quote.
        double? entry = pred.EntryReferencePrice;
        double? target = null, stop = null;

        if (entry is null or 0)
        {
            var quote = await _marketData.GetQuoteAsync(pred.Ticker);
            entry = quote?.Price;
            if (quote is null)
                warnings.Add("Twelve Data quote unavailable at candidate creation time.");
        }

        if (entry is double e and > 0)
        {
            // Simple deterministic target/stop bands based on prediction direction.
            // Bullish: +2%/+5% targets, -2% stop. Bearish: mirror.
            switch (pred.PredictionType)
            {
                case PredictionType.bullish:
                    target = Math.Round(e * 1.03, 2);
                    stop = Math.Round(e * 0.98, 2);
                    break;
                case PredictionType.bearish:
                    target = Math.Round(e * 0.97, 2);
                    stop = Math.Round(e * 1.02, 2);
                    break;
            }
        }

        // Deterministic component scores. We derive them from the prediction's
        // own context (we don't call OpenAI for the score itself).
        var catalystScore = ScoreCatalyst(pred);
        var trendScore = ScoreTrend(pred);
        var volumeScore = ScoreVolume(pred);
        var marketContextScore = 50; // placeholder until we wire a market regime signal
        var histAcc = await ScoreHistoricalAccuracyAsync(pred);
        var riskPenalty = pred.RiskScore;            // 0..100
        var missingPenalty = pred.MissingDataWarnings.Count * 10;

        var total = Math.Round(
            (catalystScore * 0.25)
            + (trendScore * 0.20)
            + (volumeScore * 0.15)
            + (marketContextScore * 0.10)
            + (histAcc * 0.15)
            + (pred.ConfidenceScore * 0.15)
            - (riskPenalty * 0.10)
            - missingPenalty,
            1);

        var timeframe = pred.TimeWindow switch
        {
            "1_day" => StockTimeframe.one_day,
            "2_day" => StockTimeframe.two_day,
            "1_week" => StockTimeframe.one_week,
            "1_month" => StockTimeframe.one_month,
            "3_month" => StockTimeframe.three_month,
            "6_month" => StockTimeframe.six_month,
            "1_year" => StockTimeframe.one_year,
            _ => StockTimeframe.one_day,
        };

        var candidateMode = DetermineCandidateMode(pred);
        var qualityTier = DetermineQualityTier(pred.ConfidenceScore);
        var isActionable = candidateMode != CandidateMode.learning;
        var qualifies = PredictionCategoryHelper.IsDirectional(pred.PredictionType)
                     && _optionsProvider.IsConfigured
                     && entry is double and > 0
                     && pred.RiskScore <= LearningMaxRiskForOptions
                     && (pred.ConfidenceScore >= LearningMinConfidenceForOptions || isTopQuartileDirectional);

        var status = (entry is null or 0)
            ? PaperStockStatus.unavailable
            : !PredictionCategoryHelper.IsDirectional(pred.PredictionType)
                ? PaperStockStatus.watch_only
                : PaperStockStatus.open;

        var exclusionReason = DetermineOptionBlockReason(
            pred,
            hasMarketData: entry is > 0,
            isTopQuartileDirectional: isTopQuartileDirectional,
            optionsProviderConfigured: _optionsProvider.IsConfigured);

        var reason = $"Prediction conf={pred.ConfidenceScore}, risk={pred.RiskScore}. " +
                     $"Bull={pred.BullishScore:F1}, Bear={pred.BearishScore:F1}, dir={pred.WinningDirection ?? "n/a"}. " +
                     $"Deterministic total {total} (catalyst={catalystScore}, trend={trendScore}, " +
                     $"volume={volumeScore}, market={marketContextScore}, histAcc={histAcc}, " +
                     $"missingPenalty={missingPenalty}). Mode={candidateMode}, tier={qualityTier}, " +
                     $"run percentile={percentileInRun:F1}. " +
                     $"{(qualifies ? "Qualifies" : "Does not qualify")} for learning-mode options.";

        return new PaperStockCandidate
        {
            PredictionId = pred.Id,
            RunId = runId,
            Ticker = pred.Ticker,
            PredictionType = pred.PredictionType,
            Timeframe = timeframe,
            EntryPrice = entry,
            ReferencePrice = pred.EntryReferencePrice,
            TargetPrice = target,
            StopPrice = stop,
            CatalystScore = catalystScore,
            TrendScore = trendScore,
            VolumeScore = volumeScore,
            MarketContextScore = marketContextScore,
            HistoricalAccuracyScore = histAcc,
            RiskPenalty = riskPenalty,
            MissingDataPenalty = missingPenalty,
            TotalScore = total,
            ConfidenceScore = pred.ConfidenceScore,
            RiskScore = pred.RiskScore,
            CatalystType = InferCatalystType(pred),
            SelectionReason = reason,
            Warnings = warnings,
            DataAvailability = dataAvailability,
            CandidateMode = candidateMode,
            QualityTier = qualityTier,
            IsActionable = isActionable,
            ThresholdPolicyVersion = ThresholdPolicyVersion,
            InclusionReason = qualifies
                ? $"learning-mode eligible: conf={pred.ConfidenceScore}, risk={pred.RiskScore}, percentile={percentileInRun:F1}"
                : $"paper stock candidate retained for evaluation; option path blocked by {exclusionReason ?? "policy"}",
            ExclusionReason = qualifies ? null : exclusionReason,
            ScorePercentileInRun = percentileInRun,
            BullishScore = pred.BullishScore,
            BearishScore = pred.BearishScore,
            WinningDirection = pred.WinningDirection,
            Status = status,
            QualifiesForOptions = qualifies,
        };
    }

    private static double ScoreCatalyst(PredictionCandidate pred)
    {
        // Higher importance + news source mentions = stronger catalyst.
        var hasNews = pred.DataSourcesUsed.Any(s => s == "rss-news");
        var score = pred.ImportanceScore * (hasNews ? 1.0 : 0.7);
        return Math.Round(Math.Clamp(score, 0, 100), 1);
    }

    private static double ScoreTrend(PredictionCandidate pred)
    {
        // Predictions sourced from twelve-data carry trend info via the
        // prediction reason; we proxy with confidence × bullish/bearish.
        var hasTechnical = pred.DataSourcesUsed.Any(s => s == "twelve-data");
        var base_ = hasTechnical ? 60 : 40;
        return Math.Round(Math.Clamp(base_ + (pred.ConfidenceScore - 50) * 0.6, 0, 100), 1);
    }

    private static double ScoreVolume(PredictionCandidate pred)
    {
        // Without a direct volume signal here, we infer from missing-data flags.
        var penalty = pred.MissingDataWarnings.Any(w => w.ToLower().Contains("volume")) ? 30 : 0;
        return Math.Round(Math.Clamp(60.0 - penalty, 0.0, 100.0), 1);
    }

    private async Task<double> ScoreHistoricalAccuracyAsync(PredictionCandidate pred)
    {
        // Pull this ticker's historical accuracy from stock_learning_stats.
        var stats = await _stockRepo.GetAllLearningStatsAsync();
        var byTicker = stats.FirstOrDefault(s => s.StatType == "ticker" && s.StatKey == pred.Ticker);
        if (byTicker is null || byTicker.TotalCandidates < 3) return 50; // neutral until we have data
        return Math.Round(byTicker.Accuracy * 100, 1);
    }

    private static string? InferCatalystType(PredictionCandidate pred)
    {
        var text = (pred.PredictionReason + " " + pred.BullishCase + " " + pred.BearishCase).ToLower();
        if (text.Contains("earnings")) return "earnings";
        if (text.Contains("guidance")) return "guidance";
        if (text.Contains("upgrade") || text.Contains("downgrade")) return "rating_change";
        if (text.Contains("merger") || text.Contains("acquisition")) return "ma";
        if (text.Contains("fda") || text.Contains("approval")) return "regulatory";
        if (pred.DataSourcesUsed.Any(s => s == "rss-news")) return "news";
        return null;
    }

    private static Dictionary<string, DirectionalRanking> BuildDirectionalRankings(List<PredictionCandidate> runPredictions)
    {
        var directional = runPredictions
            .Where(p => PredictionCategoryHelper.IsDirectional(p.PredictionType))
            .OrderByDescending(p => p.ConfidenceScore)
            .ThenBy(p => p.RiskScore)
            .ThenBy(p => p.Ticker)
            .ToList();

        var topQuartileCount = directional.Count == 0
            ? 0
            : Math.Max(1, (int)Math.Ceiling(directional.Count * 0.25));

        var map = new Dictionary<string, DirectionalRanking>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < directional.Count; i++)
        {
            var percentile = directional.Count == 1
                ? 100
                : Math.Round(100.0 * (directional.Count - 1 - i) / (directional.Count - 1), 1);
            map[directional[i].Id] = new DirectionalRanking(percentile, i < topQuartileCount);
        }

        return map;
    }

    private static CandidateMode DetermineCandidateMode(PredictionCandidate pred)
    {
        if (PredictionCategoryHelper.IsDirectional(pred.PredictionType)
            && pred.ConfidenceScore >= LiveEligibleMinConfidence
            && pred.RiskScore <= LiveEligibleMaxRisk)
            return CandidateMode.live_eligible;

        if (PredictionCategoryHelper.IsDirectional(pred.PredictionType)
            && pred.ConfidenceScore >= ActionableShadowMinConfidence
            && pred.RiskScore <= ActionableShadowMaxRisk)
            return CandidateMode.actionable_shadow;

        return CandidateMode.learning;
    }

    private static QualityTier DetermineQualityTier(int confidenceScore) => confidenceScore switch
    {
        <= 14 => QualityTier.very_weak,
        <= 24 => QualityTier.weak,
        <= 39 => QualityTier.medium,
        <= 59 => QualityTier.strong_paper,
        _ => QualityTier.production_candidate,
    };

    private static string? DetermineOptionBlockReason(
        PredictionCandidate pred,
        bool hasMarketData,
        bool isTopQuartileDirectional,
        bool optionsProviderConfigured)
    {
        if (!PredictionCategoryHelper.IsDirectional(pred.PredictionType))
            return "non_directional_prediction";
        if (!hasMarketData)
            return "missing_market_data";
        if (!optionsProviderConfigured)
            return "missing_option_chain";
        if (pred.RiskScore > LearningMaxRiskForOptions)
            return "risk_too_high";
        if (pred.ConfidenceScore < LearningMinConfidenceForOptions && !isTopQuartileDirectional)
            return "confidence_below_learning_threshold";
        return null;
    }

    private static List<QualityTierPerformance> BuildQualityTierPerformance(
        List<PaperStockCandidate> candidates,
        List<PaperStockOutcome> outcomes)
    {
        var outcomeMap = outcomes
            .GroupBy(o => o.PaperStockCandidateId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.EvaluationTime).First());

        return candidates
            .GroupBy(c => c.QualityTier.ToString())
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var withOutcomes = g
                    .Select(c => outcomeMap.TryGetValue(c.Id, out var o) ? o : null)
                    .Where(o => o is not null)
                    .ToList();
                var returns = withOutcomes
                    .Select(o => o!.PercentMove)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .OrderBy(v => v)
                    .ToList();
                var wins = withOutcomes.Count(o => o!.DirectionCorrect == true);

                return new QualityTierPerformance
                {
                    QualityTier = g.Key,
                    CandidateCount = g.Count(),
                    WinRate = withOutcomes.Count > 0 ? Math.Round(100.0 * wins / withOutcomes.Count, 1) : null,
                    AverageReturn = returns.Count > 0 ? Math.Round(returns.Average(), 2) : null,
                    MedianReturn = returns.Count > 0 ? Math.Round(returns[returns.Count / 2], 2) : null,
                };
            })
            .ToList();
    }

    private static List<ConfidenceCalibrationBucket> BuildConfidenceCalibration(
        List<PaperStockCandidate> candidates,
        List<PaperStockOutcome> outcomes)
    {
        var outcomeMap = outcomes
            .GroupBy(o => o.PaperStockCandidateId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.EvaluationTime).First());

        var buckets = new (string Label, Func<int, bool> Match)[]
        {
            ("0-14", c => c <= 14),
            ("15-24", c => c >= 15 && c <= 24),
            ("25-39", c => c >= 25 && c <= 39),
            ("40-59", c => c >= 40 && c <= 59),
            ("60+", c => c >= 60),
        };

        return buckets.Select(bucket =>
        {
            var inBucket = candidates.Where(c => bucket.Match(c.ConfidenceScore)).ToList();
            var evaluated = inBucket
                .Select(c => outcomeMap.TryGetValue(c.Id, out var o) ? o : null)
                .Where(o => o is not null)
                .ToList();
            var wins = evaluated.Count(o => o!.DirectionCorrect == true);

            return new ConfidenceCalibrationBucket
            {
                BucketLabel = bucket.Label,
                CandidateCount = inBucket.Count,
                SuccessRate = evaluated.Count > 0 ? Math.Round(100.0 * wins / evaluated.Count, 1) : null,
            };
        }).ToList();
    }

    private static DurationPreference ChooseDuration(PaperStockCandidate stock)
    {
        // High-confidence + low-risk + short timeframe -> one week.
        // Otherwise lean two_week.
        if (stock.ConfidenceScore >= 75 && stock.RiskScore <= 40 && stock.Timeframe != StockTimeframe.one_week)
            return DurationPreference.one_week;
        if (stock.RiskScore >= 60)
            return DurationPreference.two_week;
        return DurationPreference.system_recommended;
    }

    private sealed record DirectionalRanking(double Percentile, bool IsTopQuartile);

    private sealed record StockCandidateBuild(
        PredictionCandidate Prediction,
        PaperStockCandidate BuiltCandidate,
        PaperStockCandidate? SavedCandidate,
        DirectionalRanking? Ranking);

    // -----------------------------------------------------------------------
    // Helpers: evaluate one paper stock candidate
    // -----------------------------------------------------------------------

    private async Task<bool> EvaluateStockCandidateAsync(PaperStockCandidate c)
    {
        if (c.Status == PaperStockStatus.watch_only || c.Status == PaperStockStatus.unavailable)
            return false;

        if (!PredictionCategoryHelper.IsDirectional(c.PredictionType))
            return false;

        if (c.EntryPrice is null or 0)
        {
            await _stockRepo.SaveOutcomeAsync(new PaperStockOutcome
            {
                PaperStockCandidateId = c.Id,
                PredictionId = c.PredictionId,
                Ticker = c.Ticker,
                EvaluationTime = DateTimeOffset.UtcNow,
                OutcomeSummary = "No entry price recorded — cannot evaluate.",
                Lesson = "Entry price was missing at candidate creation time.",
                Warnings = ["entry_price_missing"],
            });
            await _stockRepo.UpdateCandidateStatusAsync(c.Id, PaperStockStatus.unavailable);
            return true;
        }

        var quote = await _marketData.GetQuoteAsync(c.Ticker);
        if (quote is null)
        {
            await _stockRepo.SaveOutcomeAsync(new PaperStockOutcome
            {
                PaperStockCandidateId = c.Id,
                PredictionId = c.PredictionId,
                Ticker = c.Ticker,
                EvaluationTime = DateTimeOffset.UtcNow,
                OutcomeSummary = "Twelve Data quote unavailable — outcome not computed.",
                Warnings = ["market_data_unavailable"],
            });
            return false; // do not mark evaluated — try again next run
        }

        var entry = c.EntryPrice!.Value;
        var exit = quote.Price;
        var move = (exit - entry) / entry * 100;

        bool? directionCorrect = c.PredictionType switch
        {
            PredictionType.bullish => move > 0,
            PredictionType.bearish => move < 0,
            _ => null,
        };

        bool targetHit = c.TargetPrice is not null && (
            (c.PredictionType == PredictionType.bullish && quote.High >= c.TargetPrice) ||
            (c.PredictionType == PredictionType.bearish && quote.Low <= c.TargetPrice));

        bool stopHit = c.StopPrice is not null && (
            (c.PredictionType == PredictionType.bullish && quote.Low <= c.StopPrice) ||
            (c.PredictionType == PredictionType.bearish && quote.High >= c.StopPrice));

        var invalidation = (c.PredictionType == PredictionType.bullish && move < -3)
                        || (c.PredictionType == PredictionType.bearish && move > 3);

        double outcomeScore = 50;
        if (directionCorrect == true) outcomeScore += Math.Min(Math.Abs(move) * 8, 40);
        else if (directionCorrect == false) outcomeScore -= Math.Min(Math.Abs(move) * 8, 40);
        if (targetHit) outcomeScore += 5;
        if (stopHit) outcomeScore -= 10;
        outcomeScore = Math.Clamp(outcomeScore, 0, 100);

        var maxFavorable = c.PredictionType == PredictionType.bullish
            ? ((quote.High - entry) / entry) * 100
            : ((entry - quote.Low) / entry) * 100;
        var maxAdverse = c.PredictionType == PredictionType.bullish
            ? ((entry - quote.Low) / entry) * 100
            : ((quote.High - entry) / entry) * 100;

        var lesson = BuildStockLesson(c, move, directionCorrect, targetHit, stopHit);

        var outcome = new PaperStockOutcome
        {
            PaperStockCandidateId = c.Id,
            PredictionId = c.PredictionId,
            Ticker = c.Ticker,
            EvaluationTime = DateTimeOffset.UtcNow,
            ExitPrice = exit,
            HighAfter = quote.High,
            LowAfter = quote.Low,
            PercentMove = Math.Round(move, 2),
            DirectionCorrect = directionCorrect,
            TargetHit = targetHit,
            StopHit = stopHit,
            InvalidationHit = invalidation,
            OutcomeScore = outcomeScore,
            OutcomeSummary = $"{c.Ticker} moved {move:F2}%. Direction {(directionCorrect == true ? "correct" : directionCorrect == false ? "wrong" : "n/a")}. " +
                             $"Target hit: {targetHit}. Stop hit: {stopHit}. " +
                             $"Max favorable: {maxFavorable:F2}%, max adverse: {maxAdverse:F2}%.",
            Lesson = lesson,
        };

        await _stockRepo.SaveOutcomeAsync(outcome);
        await _stockRepo.UpdateCandidateStatusAsync(c.Id, PaperStockStatus.evaluated);
        await UpdateStockLearningStatsAsync(c, outcome);
        return true;
    }

    private async Task UpdateStockLearningStatsAsync(PaperStockCandidate c, PaperStockOutcome o)
    {
        var direction = o.DirectionCorrect == true;
        var move = o.PercentMove ?? 0;
        var keys = new (string Type, string Key)[]
        {
            ("ticker", c.Ticker),
            ("timeframe", c.Timeframe.ToString()),
            ("prediction_type", c.PredictionType.ToString()),
            ("confidence_bucket", ConfBucket(c.ConfidenceScore)),
            ("catalyst_type", c.CatalystType ?? "none"),
            ("trend_signal", TrendBucket(c.TrendScore)),
            ("volume_signal", VolumeBucket(c.VolumeScore)),
        };

        foreach (var (t, k) in keys)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            await _stockRepo.UpsertLearningStatAsync(t, k, direction, move, o.OutcomeScore);
        }
    }

    private static string ConfBucket(int conf) => conf switch
    {
        < 50 => "low",
        < 65 => "mid",
        < 80 => "high",
        _ => "very_high",
    };

    private static string TrendBucket(double s) => s switch
    {
        < 40 => "weak",
        < 70 => "ok",
        _ => "strong",
    };

    private static string VolumeBucket(double s) => s switch
    {
        < 40 => "low",
        < 70 => "ok",
        _ => "high",
    };

    private static string BuildStockLesson(PaperStockCandidate c, double move, bool? direction, bool target, bool stop)
    {
        if (direction == true && target)
            return $"{c.Ticker} {c.PredictionType} target hit ({move:F1}%). Score this setup type higher.";
        if (direction == true)
            return $"{c.Ticker} {c.PredictionType} directionally right ({move:F1}%) but target unmet. Setup remains valid.";
        if (direction == false && stop)
            return $"{c.Ticker} {c.PredictionType} stop hit ({move:F1}%). Penalize this setup type.";
        if (direction == false)
            return $"{c.Ticker} {c.PredictionType} wrong direction ({move:F1}%). Reconsider this catalyst type.";
        return $"{c.Ticker} moved {move:F1}% — no direction verdict.";
    }
}
