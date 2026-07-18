using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.OptionsData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Handles building, evaluating, and scoring paper stock candidates.
/// Extracted from DynamicPickOrchestrator to reduce its dependency count.
/// </summary>
public class StockCandidateService
{
    // -----------------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------------

    public const int LearningMinConfidenceForOptions = 15;
    public const int LearningMaxRiskForOptions = 90;
    public const int ActionableShadowMinConfidence = 40;
    public const int ActionableShadowMaxRisk = 75;
    public const int LiveEligibleMinConfidence = 60;
    public const int LiveEligibleMaxRisk = 65;
    public const string ThresholdPolicyVersion = "learning_options_v1";

    // -----------------------------------------------------------------------
    // Timeframe evaluation windows
    // -----------------------------------------------------------------------

    /// <summary>
    /// Minimum hours before a paper stock candidate should be evaluated,
    /// based on its timeframe. Matches the prediction evaluator's logic.
    /// </summary>
    public static readonly Dictionary<StockTimeframe, int> MinEvalHours = new()
    {
        [StockTimeframe.one_day] = 24,     // full trading day close-to-close
        [StockTimeframe.two_day] = 30,
        [StockTimeframe.one_week] = 120,      // 5 trading days
        [StockTimeframe.one_month] = 504,      // 21 trading days
        [StockTimeframe.three_month] = 1512,   // 63 trading days
        [StockTimeframe.six_month] = 3024,     // 126 trading days
        [StockTimeframe.one_year] = 6048,      // 252 trading days
    };

    /// <summary>
    /// Maximum hours before a candidate expires (not evaluated, just closed).
    /// </summary>
    public static readonly Dictionary<StockTimeframe, int> MaxEvalHours = new()
    {
        [StockTimeframe.one_day] = 48,
        [StockTimeframe.two_day] = 96,
        [StockTimeframe.one_week] = 240,
        [StockTimeframe.one_month] = 1008,
        [StockTimeframe.three_month] = 3024,
        [StockTimeframe.six_month] = 6048,
        [StockTimeframe.one_year] = 12096,
    };

    // -----------------------------------------------------------------------
    // Dependencies
    // -----------------------------------------------------------------------

    private readonly PaperStockCandidateRepository _stockRepo;
    private readonly ResearchRepository _researchRepo;
    private readonly MarketDataService _marketData;
    private readonly MarketDataOptionsProvider _optionsProvider;
    private readonly TradeSetupEngine _setupEngine;
    private readonly ILogger<StockCandidateService> _logger;

    public StockCandidateService(
        PaperStockCandidateRepository stockRepo,
        ResearchRepository researchRepo,
        MarketDataService marketData,
        MarketDataOptionsProvider optionsProvider,
        TradeSetupEngine setupEngine,
        ILogger<StockCandidateService> logger)
    {
        _stockRepo = stockRepo;
        _researchRepo = researchRepo;
        _marketData = marketData;
        _optionsProvider = optionsProvider;
        _setupEngine = setupEngine;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Records
    // -----------------------------------------------------------------------

    public sealed record DirectionalRanking(double Percentile, bool IsTopQuartile);

    public sealed record StockCandidateBuild(
        PredictionCandidate Prediction,
        PaperStockCandidate BuiltCandidate,
        PaperStockCandidate? SavedCandidate,
        DirectionalRanking? Ranking);

    // -----------------------------------------------------------------------
    // Build a paper stock candidate from a prediction
    // -----------------------------------------------------------------------

    public async Task<PaperStockCandidate> BuildStockCandidateFromPredictionAsync(
        PredictionCandidate pred, string runId, double percentileInRun, bool isTopQuartileDirectional)
    {
        var warnings = new List<string>(pred.MissingDataWarnings);

        var dataAvailability = pred.MissingDataWarnings.Count == 0
            ? "real"
            : (pred.EntryReferencePrice is null || pred.EntryReferencePrice == 0 ? "unavailable" : "partial");

        // Try to enrich entry/target/stop with current quote.
        double? entry = pred.EntryReferencePrice;
        double? target = null, stop = null;

        if (entry is null || entry == 0)
        {
            var quote = await _marketData.GetQuoteAsync(pred.Ticker);
            entry = quote?.Price;
            if (quote is null)
                warnings.Add("Twelve Data quote unavailable at candidate creation time.");
        }

        if (entry is double e && e > 0)
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

        // Derive component scores from ScoringEngine's breakdown (single source of truth).
        // The prediction's ScoreDebugJson contains the authoritative ScoringBreakdown.
        var breakdown = ScoringBreakdownEnvelope.Parse(pred.ScoreDebugJson);

        // Map ScoringEngine bucket scores to candidate fields.
        // Each bucket produces bull/bear contributions; we use the net (bull - bear)
        // shifted to 0-100 scale (50 = neutral) for the candidate record.
        var catalystScore = breakdown is not null
            ? Math.Clamp(50 + (breakdown.CatalystBullish - breakdown.CatalystBearish), 0, 100)
            : 50.0;
        var trendScore = breakdown is not null
            ? Math.Clamp(50 + (breakdown.TrendBullish - breakdown.TrendBearish), 0, 100)
            : 50.0;
        var volumeScore = breakdown is not null
            ? Math.Clamp(50 + (breakdown.VolumeBullish - breakdown.VolumeBearish), 0, 100)
            : 50.0;
        var marketContextScore = breakdown is not null
            ? Math.Clamp(50 + (breakdown.MarketContextBullish - breakdown.MarketContextBearish), 0, 100)
            : 50.0;
        var histAcc = await ScoreHistoricalAccuracyAsync(pred);
        var riskPenalty = (double)pred.RiskScore;
        var missingPenalty = (double)(pred.MissingDataWarnings.Count * 10);

        // TotalScore derived from ScoringEngine's directional score + confidence,
        // not independently recalculated.
        var total = breakdown is not null
            ? Math.Round(Math.Clamp(
                (Math.Abs(breakdown.DirectionalScore) * 0.40)
                + (pred.ConfidenceScore * 0.35)
                + (histAcc * 0.15)
                - (riskPenalty * 0.10),
                0, 100), 1)
            : Math.Round(Math.Clamp(pred.ConfidenceScore - riskPenalty * 0.10 - missingPenalty, 0, 100), 1);

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
        var qualityTier = DetermineQualityTier(pred.ConfidenceScore, pred.ActionabilityTier);
        var isActionable = candidateMode != CandidateMode.learning;
        var qualifies = PredictionCategoryHelper.IsDirectional(pred.PredictionType)
                     && _optionsProvider.IsConfigured
                     && entry is double entryVal && entryVal > 0
                     && pred.RiskScore <= LearningMaxRiskForOptions
                     && (pred.ConfidenceScore >= LearningMinConfidenceForOptions || isTopQuartileDirectional);

        var status = (entry is null || entry == 0)
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
                     $"Total {total:F1} (from ScoringEngine breakdown: catalyst={catalystScore:F0}, trend={trendScore:F0}, " +
                     $"volume={volumeScore:F0}, market={marketContextScore:F0}, histAcc={histAcc:F0}). " +
                     $"Mode={candidateMode}, tier={qualityTier}, " +
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

    // -----------------------------------------------------------------------
    // Evaluate one paper stock candidate
    // -----------------------------------------------------------------------

    public async Task<bool> EvaluateStockCandidateAsync(PaperStockCandidate c)
    {
        if (c.Status == PaperStockStatus.watch_only || c.Status == PaperStockStatus.unavailable)
            return false;

        if (!PredictionCategoryHelper.IsDirectional(c.PredictionType))
            return false;

        // ── Timeframe gate: don't evaluate too early ──
        var ageHours = (DateTimeOffset.UtcNow - c.CreatedAt).TotalHours;
        var minHours = MinEvalHours.GetValueOrDefault(c.Timeframe, 6);
        var maxHours = MaxEvalHours.GetValueOrDefault(c.Timeframe, 240);

        if (ageHours < minHours)
        {
            _logger.LogDebug("[dynamic] {Ticker}: too early to evaluate ({Age:F1}h < {Min}h for {Tf})",
                c.Ticker, ageHours, minHours, c.Timeframe);
            return false;
        }

        if (ageHours > maxHours)
        {
            _logger.LogInformation("[dynamic] {Ticker}: expired ({Age:F0}h > {Max}h for {Tf})",
                c.Ticker, ageHours, maxHours, c.Timeframe);
            await _stockRepo.UpdateCandidateStatusAsync(c.Id, PaperStockStatus.expired);
            return false;
        }

        if (c.EntryPrice is null || c.EntryPrice == 0)
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
        var failureReason = directionCorrect == false
            ? BuildFailureReason(c) : null;

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
            FailureReason = failureReason,
        };

        await _stockRepo.SaveOutcomeAsync(outcome);
        await _stockRepo.UpdateCandidateStatusAsync(c.Id, PaperStockStatus.evaluated);
        await UpdateStockLearningStatsAsync(c, outcome);
        return true;
    }

    // -----------------------------------------------------------------------
    // Trade setup classification + evaluation
    // -----------------------------------------------------------------------

    public async Task ClassifyAndSaveSetupAsync(PredictionCandidate pred, string? paperStockCandidateId)
    {
        var setup = await _setupEngine.ClassifySetupAsync(pred, paperStockCandidateId);
        if (setup is not null)
            await _setupEngine.SaveSetupAsync(setup);
    }

    public async Task<int> EvaluateActiveTradeSetupsAsync(List<string> errors)
    {
        var setupsEvaluated = 0;
        try
        {
            var activeSetups = await _researchRepo.GetActiveTradeSetupsAsync();
            foreach (var setupRow in activeSetups)
            {
                try
                {
                    var ticker = setupRow["ticker"]?.ToString();
                    if (string.IsNullOrEmpty(ticker)) continue;

                    var quote = await _marketData.GetQuoteAsync(ticker);
                    if (quote is null || quote.Price <= 0) continue;

                    var setupId = setupRow["id"]?.ToString() ?? "";
                    var createdStr = setupRow["created_at"]?.ToString();
                    var createdAt = DateTimeOffset.TryParse(createdStr, out var ca) ? ca : DateTimeOffset.UtcNow;
                    var daysHeld = (int)(DateTimeOffset.UtcNow - createdAt).TotalDays;

                    var setup = new TradeSetup
                    {
                        Id = setupId,
                        Ticker = ticker,
                        Direction = setupRow["direction"]?.ToString() ?? "neutral",
                        EntryPrice = setupRow["entry_price"] is JsonNode ep ? ep.GetValue<double>() : null,
                        TargetPrice = setupRow["target_price"] is JsonNode tp ? tp.GetValue<double>() : null,
                        StopPrice = setupRow["stop_price"] is JsonNode sp ? sp.GetValue<double>() : null,
                        InvalidationPrice = setupRow["invalidation_price"] is JsonNode ip ? ip.GetValue<double>() : null,
                        MaxHoldingDays = setupRow["max_holding_days"] is JsonNode mh ? mh.GetValue<int>() : 5,
                    };

                    var outcome = TradeSetupEngine.EvaluateSetup(
                        setup, quote.Price, quote.Price, quote.Price, daysHeld);

                    if (outcome is not null)
                    {
                        await _researchRepo.UpdateTradeSetupStatusAsync(setupId, outcome.Resolution.ToString(), outcome);
                        setupsEvaluated++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[stock-candidate] Setup evaluation failed for one setup");
                }
            }
            if (setupsEvaluated > 0)
                _logger.LogInformation("[stock-candidate] Evaluated {Count} trade setups", setupsEvaluated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[stock-candidate] Trade setup evaluation batch failed");
            errors.Add($"setup-eval: {ex.Message}");
        }
        return setupsEvaluated;
    }

    // -----------------------------------------------------------------------
    // Dashboard analytics
    // -----------------------------------------------------------------------

    public static List<QualityTierPerformance> BuildQualityTierPerformance(
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

    public static List<ConfidenceCalibrationBucket> BuildConfidenceCalibration(
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

    // -----------------------------------------------------------------------
    // Historical accuracy scoring
    // -----------------------------------------------------------------------

    private async Task<double> ScoreHistoricalAccuracyAsync(PredictionCandidate pred)
    {
        // Pull this ticker's historical accuracy from stock_learning_stats.
        var stats = await _stockRepo.GetAllLearningStatsAsync();
        var byTicker = stats.FirstOrDefault(s => s.StatType == "ticker" && s.StatKey == pred.Ticker);
        if (byTicker is null || byTicker.TotalCandidates < 3) return 50; // neutral until we have data
        return Math.Round(byTicker.Accuracy * 100, 1);
    }

    // -----------------------------------------------------------------------
    // Learning stats updates
    // -----------------------------------------------------------------------

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

        // ── Per-ticker per-bucket accuracy with responsibility-weighted attribution ──
        await UpdateTickerBucketStatsAsync(c, o);
    }

    private async Task UpdateTickerBucketStatsAsync(PaperStockCandidate c, PaperStockOutcome o)
    {
        var direction = o.DirectionCorrect == true;
        var move = o.PercentMove ?? 0;

        // Net scores per bucket (positive = bullish contribution, negative = bearish)
        var bucketScores = new (string Name, double NetScore)[]
        {
            ("trend", c.TrendScore),
            ("momentum", 0), // not stored on candidate — skip for now
            ("volume", c.VolumeScore),
            ("volatility", 0), // not stored on candidate — skip for now
            ("market_context", c.MarketContextScore),
            ("catalyst", c.CatalystScore),
        };

        // Try to extract full breakdown from prediction's ScoreDebugJson if available
        ScoringBreakdown? breakdown = null;
        if (c.PredictionId is not null)
        {
            try
            {
                var prediction = await _researchRepo.GetPredictionByIdAsync(c.PredictionId);
                if (prediction?.ScoreDebugJson is not null)
                {
                    breakdown = ScoringBreakdownEnvelope.Parse(prediction.ScoreDebugJson);
                }
            }
            catch { /* best effort */ }
        }

        if (breakdown is not null)
        {
            bucketScores =
            [
                ("trend", breakdown.TrendScore),
                ("momentum", breakdown.MomentumScore),
                ("volume", breakdown.VolumeScore),
                ("volatility", breakdown.VolatilitySetupScore),
                ("market_context", breakdown.MarketContextScore),
                ("catalyst", breakdown.CatalystScore),
                ("research_signal", breakdown.ResearchSignalScore),
            ];
        }

        // Determine which buckets supported the prediction direction
        bool predBullish = c.PredictionType == PredictionType.bullish;

        // Calculate total positive evidence (buckets that agreed with the prediction)
        double totalPositiveEvidence = 0;
        foreach (var (_, net) in bucketScores)
        {
            bool bucketAgreed = predBullish ? net > 0 : net < 0;
            if (bucketAgreed) totalPositiveEvidence += Math.Abs(net);
        }

        if (totalPositiveEvidence < 1) return; // no buckets meaningfully contributed

        foreach (var (name, net) in bucketScores)
        {
            if (Math.Abs(net) < 3) continue; // ignore near-neutral buckets

            bool bucketAgreed = predBullish ? net > 0 : net < 0;
            string tickerBucketKey = $"{c.Ticker}|{name}";

            if (bucketAgreed)
            {
                // Bucket supported the prediction — it shares responsibility for the outcome
                // Weight by contribution: responsibility = |net| / totalPositiveEvidence
                await _stockRepo.UpsertLearningStatAsync(
                    "ticker_bucket", tickerBucketKey, direction, move, o.OutcomeScore);
            }
            else
            {
                // Bucket disagreed with prediction direction
                // If prediction was wrong, this bucket was RIGHT — credit it
                // If prediction was right, this bucket was wrong — penalize it
                await _stockRepo.UpsertLearningStatAsync(
                    "ticker_bucket", tickerBucketKey, !direction, move, o.OutcomeScore);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Static helper methods
    // -----------------------------------------------------------------------

    public static string? InferCatalystType(PredictionCandidate pred)
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

    public static Dictionary<string, DirectionalRanking> BuildDirectionalRankings(List<PredictionCandidate> runPredictions)
    {
        // Rank by Expected Value first (best risk/reward bets), then confidence as tiebreaker.
        // Predictions without EV (missing price data) sort after those with EV.
        var directional = runPredictions
            .Where(p => PredictionCategoryHelper.IsDirectional(p.PredictionType))
            .OrderByDescending(p => p.ExpectedValuePercent ?? double.MinValue)
            .ThenByDescending(p => p.ConfidenceScore)
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

    public static CandidateMode DetermineCandidateMode(PredictionCandidate pred)
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

    /// <summary>
    /// Maps ActionabilityTier (from ScoringEngine) to QualityTier (for candidate tracking).
    /// Falls back to confidence-based mapping when ActionabilityTier is not available.
    /// </summary>
    public static QualityTier DetermineQualityTier(int confidenceScore, ActionabilityTier? actionabilityTier = null)
    {
        if (actionabilityTier is not null)
        {
            return actionabilityTier.Value switch
            {
                ActionabilityTier.scan => QualityTier.very_weak,
                ActionabilityTier.watch_only => QualityTier.weak,
                ActionabilityTier.actionable => QualityTier.medium,
                ActionabilityTier.strong => QualityTier.strong_paper,
                ActionabilityTier.strongest => QualityTier.production_candidate,
                _ => QualityTier.very_weak,
            };
        }

        // Fallback for predictions without ActionabilityTier
        return confidenceScore switch
        {
            <= 14 => QualityTier.very_weak,
            <= 24 => QualityTier.weak,
            <= 39 => QualityTier.medium,
            <= 59 => QualityTier.strong_paper,
            _ => QualityTier.production_candidate,
        };
    }

    public static string? DetermineOptionBlockReason(
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

    /// <summary>
    /// Identifies which signal buckets were the likely culprits for a wrong prediction.
    /// Uses responsibility-weighted attribution: buckets that contributed more to the
    /// wrong prediction get more blame.
    /// </summary>
    public static string BuildFailureReason(PaperStockCandidate c)
    {
        bool predBullish = c.PredictionType == PredictionType.bullish;
        var buckets = new (string Name, double NetScore)[]
        {
            ("Trend", c.TrendScore),
            ("Volume", c.VolumeScore),
            ("Market context", c.MarketContextScore),
            ("Catalyst", c.CatalystScore),
        };

        // Buckets that agreed with the wrong prediction (culprits)
        var culprits = buckets
            .Where(b => Math.Abs(b.NetScore) >= 3
                && (predBullish ? b.NetScore > 0 : b.NetScore < 0))
            .OrderByDescending(b => Math.Abs(b.NetScore))
            .ToList();

        // Buckets that correctly disagreed (they were right)
        var correctDissenters = buckets
            .Where(b => Math.Abs(b.NetScore) >= 3
                && (predBullish ? b.NetScore < 0 : b.NetScore > 0))
            .OrderByDescending(b => Math.Abs(b.NetScore))
            .ToList();

        var parts = new List<string>();

        if (culprits.Count > 0)
        {
            var topCulprit = culprits[0];
            parts.Add($"Biggest culprit: {topCulprit.Name} ({topCulprit.NetScore:+0;-0})");
            if (culprits.Count > 1)
                parts.Add($"Also contributed: {string.Join(", ", culprits.Skip(1).Select(b => $"{b.Name} ({b.NetScore:+0;-0})"))}");
        }

        if (correctDissenters.Count > 0)
            parts.Add($"Correctly disagreed: {string.Join(", ", correctDissenters.Select(b => $"{b.Name} ({b.NetScore:+0;-0})"))}");

        return parts.Count > 0 ? string.Join(". ", parts) + "." : "No clear signal culprit identified.";
    }

    public static string BuildStockLesson(PaperStockCandidate c, double move, bool? direction, bool target, bool stop)
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

    public static DurationPreference ChooseDuration(PaperStockCandidate stock)
    {
        // High-confidence + low-risk + short timeframe -> one week.
        // Otherwise lean two_week.
        if (stock.ConfidenceScore >= 75 && stock.RiskScore <= 40 && stock.Timeframe != StockTimeframe.one_week)
            return DurationPreference.one_week;
        if (stock.RiskScore >= 60)
            return DurationPreference.two_week;
        return DurationPreference.system_recommended;
    }

    public static string ConfBucket(int conf) => conf switch
    {
        < 50 => "low",
        < 65 => "mid",
        < 80 => "high",
        _ => "very_high",
    };

    public static string TrendBucket(double s) => s switch
    {
        < 40 => "weak",
        < 70 => "ok",
        _ => "strong",
    };

    public static string VolumeBucket(double s) => s switch
    {
        < 40 => "low",
        < 70 => "ok",
        _ => "high",
    };
}
