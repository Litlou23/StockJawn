using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.OptionsData;
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
    // Option-qualification thresholds (from spec).
    private const int MinConfidenceForOptions = 65;
    private const int MaxRiskForOptions = 70;

    private readonly DailyResearchRunService _dailyService;
    private readonly ResearchRepository _researchRepo;
    private readonly PaperStockCandidateRepository _stockRepo;
    private readonly OptionsDataRepository _optionsRepo;
    private readonly PaperOptionsService _paperOptions;
    private readonly MarketDataOptionsProvider _optionsProvider;
    private readonly MarketDataService _marketData;
    private readonly LearningEngine _learning;
    private readonly ILogger<DynamicPickOrchestrator> _logger;

    public DynamicPickOrchestrator(
        DailyResearchRunService dailyService,
        ResearchRepository researchRepo,
        PaperStockCandidateRepository stockRepo,
        OptionsDataRepository optionsRepo,
        PaperOptionsService paperOptions,
        MarketDataOptionsProvider optionsProvider,
        MarketDataService marketData,
        LearningEngine learning,
        ILogger<DynamicPickOrchestrator> logger)
    {
        _dailyService = dailyService;
        _researchRepo = researchRepo;
        _stockRepo = stockRepo;
        _optionsRepo = optionsRepo;
        _paperOptions = paperOptions;
        _optionsProvider = optionsProvider;
        _marketData = marketData;
        _learning = learning;
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

        // 2. Load the just-saved predictions for this run
        // (Filter in memory since GetRecentPredictionsAsync doesn't take run_id.)
        var recent = await _researchRepo.GetRecentPredictionsAsync(limit: 100);
        var runPredictions = recent.Where(p => p.RunId == scan.RunId).ToList();

        _logger.LogInformation("[dynamic] Wrapping {Count} predictions as paper stock candidates", runPredictions.Count);

        // 3. Wrap each prediction as a paper_stock_candidate
        var savedStockCandidates = new List<PaperStockCandidate>();
        foreach (var pred in runPredictions)
        {
            var candidate = await BuildStockCandidateFromPredictionAsync(pred, scan.RunId);
            var saved = await _stockRepo.SaveCandidateAsync(candidate);
            if (saved is not null) savedStockCandidates.Add(saved);
        }

        // 4. For each qualifying candidate, generate linked option candidates
        var qualifying = savedStockCandidates.Where(c => c.QualifiesForOptions).ToList();
        var optionsGenerated = 0;

        foreach (var stock in qualifying)
        {
            try
            {
                var resp = await _paperOptions.GenerateCandidatesAsync(new GenerateCandidatesRequest
                {
                    PredictionId = stock.PredictionId ?? "",
                    DurationPreference = ChooseDuration(stock),
                    AutoSave = true,
                    PaperStockCandidateId = stock.Id,
                });

                if (resp is not null) optionsGenerated += resp.Candidates.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[dynamic] Option generation failed for {Ticker}", stock.Ticker);
                errors.Add($"option-gen {stock.Ticker}: {ex.Message}");
            }
        }

        var report = $"Generated {savedStockCandidates.Count} paper stock candidates from {runPredictions.Count} predictions. " +
                     $"{qualifying.Count} qualified for options (conf>={MinConfidenceForOptions}, risk<={MaxRiskForOptions}). " +
                     $"Saved {optionsGenerated} paper option candidates.";

        return new DynamicMorningResult
        {
            RunId = scan.RunId,
            PredictionsGenerated = scan.PredictionsGenerated,
            StockCandidatesGenerated = savedStockCandidates.Count,
            StockCandidatesQualifiedForOptions = qualifying.Count,
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

        var report = $"Evaluated {stockEvaluated} paper stock candidates, " +
                     $"{optionOutcomes.Count} paper option candidates. " +
                     $"Existing predictions: {eod.PredictionsEvaluated}.";

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
        var stockCandidates = await _stockRepo.GetRecentCandidatesAsync(100);
        var optionCandidates = await _optionsRepo.GetAllPaperCandidatesEnhancedAsync(100);
        var optionStats = await _optionsRepo.GetAllOptionLearningStatsAsync();
        var stockStats = await _stockRepo.GetAllLearningStatsAsync();
        var stockOutcomes = await _stockRepo.GetRecentOutcomesAsync(100);
        var optionOutcomes = await _optionsRepo.GetRecentOutcomesEnhancedAsync(100);

        var today = DateTimeOffset.UtcNow.Date;

        var stockToday = stockCandidates.Count(c => c.CreatedAt.UtcDateTime.Date == today);
        var optionToday = optionCandidates.Count(c => c.CreatedAt.UtcDateTime.Date == today);
        var evaluatedToday = stockOutcomes.Count(o => o.EvaluationTime.UtcDateTime.Date == today)
                           + optionOutcomes.Count(o => o.EvaluationTime.UtcDateTime.Date == today);

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

        return new DynamicDashboardSummary
        {
            StockPicksToday = stockToday,
            OptionPicksToday = optionToday,
            OpenStockCandidates = stockCandidates.Count(c => c.Status == PaperStockStatus.open),
            OpenOptionCandidates = optionCandidates.Count(c => c.Status == PaperCandidateStatus.open),
            EvaluatedToday = evaluatedToday,
            BestSignalKey = best is null ? null : $"{best.StatType}:{best.StatKey}",
            BestSignalAccuracy = best?.WinRate ?? 0,
            WorstSignalKey = worst is null || ReferenceEquals(worst, best) ? null : $"{worst.StatType}:{worst.StatKey}",
            WorstSignalAccuracy = worst?.WinRate ?? 0,
            InsightOfTheDay = insight,
        };
    }

    // -----------------------------------------------------------------------
    // Helpers: build a paper stock candidate from a prediction
    // -----------------------------------------------------------------------

    private async Task<PaperStockCandidate> BuildStockCandidateFromPredictionAsync(
        PredictionCandidate pred, string runId)
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
            _ => StockTimeframe.one_day,
        };

        var qualifies = pred.ConfidenceScore >= MinConfidenceForOptions
                     && pred.RiskScore <= MaxRiskForOptions
                     && (pred.PredictionType == PredictionType.bullish || pred.PredictionType == PredictionType.bearish)
                     && _optionsProvider.IsConfigured
                     && entry is double and > 0;

        var status = (entry is null or 0)
            ? PaperStockStatus.unavailable
            : (pred.PredictionType == PredictionType.neutral
                ? PaperStockStatus.watch_only
                : PaperStockStatus.open);

        var reason = $"Prediction conf={pred.ConfidenceScore}, risk={pred.RiskScore}. " +
                     $"Deterministic total {total} (catalyst={catalystScore}, trend={trendScore}, " +
                     $"volume={volumeScore}, market={marketContextScore}, histAcc={histAcc}, " +
                     $"missingPenalty={missingPenalty}). " +
                     $"{(qualifies ? "Qualifies" : "Does not qualify")} for options.";

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

    // -----------------------------------------------------------------------
    // Helpers: evaluate one paper stock candidate
    // -----------------------------------------------------------------------

    private async Task<bool> EvaluateStockCandidateAsync(PaperStockCandidate c)
    {
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
                             $"Target hit: {targetHit}. Stop hit: {stopHit}.",
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
