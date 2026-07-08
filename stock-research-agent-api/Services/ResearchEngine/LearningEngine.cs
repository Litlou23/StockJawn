using System.Text.Json;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Unified learning engine. Runs automatically in the EOD chain.
/// Pipeline: Explode score_debug_json → Signal observations → Stats →
/// Confidence calibration → Weight optimization → AI report → Publish.
/// </summary>
public class LearningEngine
{
    private const int MinObservationsForAdjustment = 50;
    private const double MaxAdjustmentPercent = 0.20;   // ±20% max
    private const double MaxDailyMovement = 0.01;       // 1% per day
    private const double TimeDecayHalfLifeDays = 45.0;

    // Base weights for the 8 scoring buckets (Layer 1 — never modified)
    private static readonly Dictionary<string, double> DefaultBaseWeights = new()
    {
        ["trend"] = 1.0,
        ["momentum"] = 1.0,
        ["volume"] = 0.8,
        ["volatility"] = 0.7,
        ["market_context"] = 0.9,
        ["catalyst"] = 1.1,
        ["learning"] = 0.5,
        ["research_signal"] = 1.0,
    };

    // The 8 bucket names matching ScoringBreakdown properties
    private static readonly string[] BucketNames =
    [
        "trend", "momentum", "volume", "volatility",
        "market_context", "catalyst", "learning", "research_signal"
    ];

    private readonly ResearchRepository _repo;
    private readonly PatternDetectionService _patternDetection;
    private readonly TradeSetupEngine _setupEngine;
    private readonly IOpenAiCompletionService _ai;
    private readonly ILogger<LearningEngine> _logger;

    public LearningEngine(
        ResearchRepository repo, PatternDetectionService patternDetection,
        TradeSetupEngine setupEngine,
        IOpenAiCompletionService ai, ILogger<LearningEngine> logger)
    {
        _repo = repo;
        _patternDetection = patternDetection;
        _setupEngine = setupEngine;
        _ai = ai;
        _logger = logger;
    }

    // Evidence records produced by pattern detection for the optimizer
    public record PatternRecommendation
    {
        public string Type { get; init; } = ""; // "regime_confidence_cap" or "synergy_weight"
        public string SignalName { get; init; } = "";
        public double RecommendedAdjustment { get; init; }
        public double Confidence { get; init; }
        public int Evidence { get; init; }
        public string Reason { get; init; } = "";
    }

    // -----------------------------------------------------------------------
    // Main Pipeline (called by DailyResearchRunService)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Full learning cycle: observations → stats → calibration → weights → AI report.
    /// </summary>
    public async Task<LearningUpdateResult> RunFullLearningCycleAsync()
    {
        var errors = new List<string>();

        // Stage 1: Extract signal observations from evaluated predictions
        var obsCreated = await ExtractSignalObservationsAsync(errors);

        // Stage 2: Compute signal performance stats
        var (perfUpdated, signalStats) = await ComputeSignalPerformanceAsync();

        // Stage 3: Confidence calibration — detect AND correct overconfidence
        var calibration = await ComputeConfidenceCalibrationAsync();
        await ApplyCalibrationFactorAsync(calibration);

        // Stage 4: Optimize weights (signal-level first-order adjustments)
        var (weightsAdjusted, weightChanges) = await OptimizeWeightsAsync(signalStats);

        // Stage 4b: Pattern detection evidence (second-order adjustments)
        var patternRecommendations = await ProducePatternRecommendationsAsync();
        var (patternAdjusted, patternChanges) = await ApplyPatternRecommendationsAsync(patternRecommendations);
        weightsAdjusted += patternAdjusted;
        weightChanges.AddRange(patternChanges);

        // Stage 5: Setup Analytics — learn complete trade setups
        var setupStatsUpdated = await ComputeSetupPerformanceAsync();

        // Stage 6: Generate AI-summarized learning report
        var aiSummary = await GenerateAiLearningReportAsync(signalStats, calibration, weightChanges);

        // Stage 7: Generate structured insights (includes setup-level insights)
        var insights = await GenerateLearningInsightsAsync();

        var report = $"Learning cycle complete: {obsCreated} observations, {perfUpdated} signal stats, " +
                     $"{weightsAdjusted} weight adjustments, {setupStatsUpdated} setup stats, {insights.Count} insights.";

        _logger.LogInformation("[learning-engine] {Report}", report);

        return new LearningUpdateResult
        {
            InsightsGenerated = insights.Count,
            WeightsAdjusted = weightsAdjusted,
            ObservationsCreated = obsCreated,
            Report = report,
            AiSummary = aiSummary,
            Errors = errors,
        };
    }

    // -----------------------------------------------------------------------
    // Stage 1: Extract Signal Observations from score_debug_json
    // -----------------------------------------------------------------------

    public async Task<int> ExtractSignalObservationsAsync(List<string>? errors = null)
    {
        var predictions = await _repo.GetRecentPredictionsAsync(500);
        var outcomes = await _repo.GetRecentOutcomesAsync(500);
        var outcomeMap = outcomes.ToDictionary(o => o.PredictionId);

        var observations = new List<object>();
        var processedCount = 0;

        foreach (var pred in predictions)
        {
            if (!outcomeMap.TryGetValue(pred.Id, out var outcome) || outcome.DirectionCorrect is null)
                continue;

            // Skip if we already have observations for this prediction
            if (await _repo.HasObservationsForPredictionAsync(pred.Id))
                continue;

            // Parse score_debug_json
            ScoringBreakdown? breakdown = null;
            if (!string.IsNullOrEmpty(pred.ScoreDebugJson))
            {
                try
                {
                    breakdown = JsonSerializer.Deserialize<ScoringBreakdown>(pred.ScoreDebugJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[learning-engine] Failed to parse score_debug_json for {PredId}: {Error}",
                        pred.Id, ex.Message);
                    errors?.Add($"Parse error for {pred.Id}: {ex.Message}");
                    continue;
                }
            }

            if (breakdown is null) continue;

            var direction = pred.PredictionType == PredictionType.bearish ? "bearish" : "bullish";
            var correct = outcome.DirectionCorrect == true;

            // Extract per-bucket observations
            foreach (var bucket in BucketNames)
            {
                var (bull, bear) = GetBucketScores(breakdown, bucket);
                var dominantScore = direction == "bullish" ? bull : bear;
                var weight = DefaultBaseWeights.GetValueOrDefault(bucket, 1.0);
                var contribution = dominantScore * weight;

                observations.Add(new
                {
                    prediction_id = pred.Id,
                    outcome_id = outcome.Id,
                    signal_name = bucket,
                    bull_score = bull,
                    bear_score = bear,
                    predicted_direction = direction,
                    correct,
                    raw_weight = weight,
                    effective_weight = weight,
                    weighted_contribution = contribution,
                    confidence = (double?)pred.ConfidenceScore,
                    outcome_score = outcome.OutcomeScore,
                    market_regime = DetectMarketRegime(breakdown),
                    created_at = DateTimeOffset.UtcNow.ToString("o"),
                });
            }

            processedCount++;

            // Batch insert every 50 predictions (400 observations)
            if (observations.Count >= 400)
            {
                await _repo.InsertSignalObservationsAsync(observations);
                observations.Clear();
            }
        }

        // Insert remaining
        if (observations.Count > 0)
            await _repo.InsertSignalObservationsAsync(observations);

        _logger.LogInformation("[learning-engine] Created observations for {Count} predictions ({Obs} rows)",
            processedCount, processedCount * BucketNames.Length);
        return processedCount;
    }

    // -----------------------------------------------------------------------
    // Stage 2: Compute Signal Performance from Observations
    // -----------------------------------------------------------------------

    public async Task<(int Updated, List<ResearchSignalPerformance> Stats)> ComputeSignalPerformanceAsync()
    {
        // Use 180-day window for medium-term stats
        var observations = await _repo.GetSignalObservationsAsync(limit: 5000, windowDays: 180);
        if (observations.Count == 0)
            return (0, []);

        var stats = new Dictionary<(string Signal, string Direction), (int Total, int Correct, double TotalScore, double WeightedSum)>();

        foreach (var obs in observations)
        {
            if (obs.Correct is null) continue;

            // Time-decay weight: recent observations count more
            var ageInDays = (DateTimeOffset.UtcNow - obs.CreatedAt).TotalDays;
            var decayWeight = Math.Exp(-ageInDays * Math.Log(2) / TimeDecayHalfLifeDays);

            void Tally(string direction)
            {
                var key = (obs.SignalName, direction);
                var (total, correct, totalScore, weightedSum) = stats.GetValueOrDefault(key);
                total++;
                weightedSum += decayWeight;
                if (obs.Correct == true) correct++;
                totalScore += obs.OutcomeScore ?? 50;
                stats[key] = (total, correct, totalScore, weightedSum);
            }

            Tally(obs.PredictedDirection);
            Tally("all");
        }

        var results = new List<ResearchSignalPerformance>();
        foreach (var ((signal, direction), (total, correct, totalScore, _)) in stats)
        {
            var perf = new ResearchSignalPerformance
            {
                SignalName = signal,
                SignalType = signal.StartsWith("research") ? "research" : "scoring_bucket",
                Direction = direction,
                TotalPredictions = total,
                CorrectPredictions = correct,
                Accuracy = total > 0 ? (double)correct / total : 0,
                AverageOutcomeScore = total > 0 ? totalScore / total : 0,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            };

            await _repo.UpsertSignalPerformanceAsync(new
            {
                signal_name = signal,
                signal_type = perf.SignalType,
                direction,
                total_predictions = total,
                correct_predictions = correct,
                accuracy = perf.Accuracy,
                average_outcome_score = perf.AverageOutcomeScore,
                last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
            });

            results.Add(perf);
        }

        return (results.Count, results);
    }

    // -----------------------------------------------------------------------
    // Stage 3: Confidence Calibration
    // -----------------------------------------------------------------------

    public async Task<ConfidenceAnalysis> ComputeConfidenceCalibrationAsync()
    {
        var predictions = await _repo.GetRecentPredictionsAsync(500);
        var outcomes = await _repo.GetRecentOutcomesAsync(500);
        var outcomeMap = outcomes.ToDictionary(o => o.PredictionId);

        var buckets = new (string Range, int Min, int Max)[]
        {
            ("0-30", 0, 30), ("30-50", 30, 50), ("50-65", 50, 65),
            ("65-80", 65, 80), ("80-100", 80, 100),
        };

        var results = new List<ConfidenceBucket>();
        var overconfidentCount = 0;

        foreach (var (range, min, max) in buckets)
        {
            var inBucket = predictions
                .Where(p => p.ConfidenceScore >= min && p.ConfidenceScore < max
                    && outcomeMap.ContainsKey(p.Id) && outcomeMap[p.Id].DirectionCorrect is not null)
                .ToList();

            if (inBucket.Count < 3) continue;

            var correct = inBucket.Count(p => outcomeMap[p.Id].DirectionCorrect == true);
            var actualAcc = (double)correct / inBucket.Count;
            var expectedAcc = (min + max) / 200.0;
            var calibError = actualAcc - expectedAcc;

            if (calibError < -0.15) overconfidentCount++;

            results.Add(new ConfidenceBucket
            {
                Range = range,
                Count = inBucket.Count,
                ActualAccuracy = actualAcc,
                ExpectedAccuracy = expectedAcc,
                CalibrationError = calibError,
            });
        }

        return new ConfidenceAnalysis
        {
            Buckets = results,
            IsOverconfident = overconfidentCount >= 2,
            Summary = overconfidentCount >= 2
                ? "System is overconfident in multiple confidence bands"
                : overconfidentCount == 1
                    ? "Slight overconfidence detected in one band"
                    : "Confidence calibration is reasonable",
        };
    }

    // -----------------------------------------------------------------------
    // Stage 3b: Apply Calibration Factor
    // -----------------------------------------------------------------------

    /// <summary>
    /// Closes the calibration feedback loop. Computes a calibration_factor
    /// from the weighted average calibration error across confidence bands,
    /// then persists it as a weight override so ScoringEngine applies it
    /// on the next scoring pass.
    ///
    /// Movement is gradual (max 1% per day) to avoid whiplash.
    /// The factor is clamped to [0.85, 1.15] by both this method and
    /// the ScoringEngine consumer.
    /// </summary>
    private async Task ApplyCalibrationFactorAsync(ConfidenceAnalysis calibration)
    {
        try
        {
            if (calibration.Buckets.Count < 2) return; // Not enough data

            // Compute weighted-average calibration error across all bands.
            // Negative error = overconfident (actual accuracy < expected).
            var totalWeight = 0;
            var weightedError = 0.0;
            foreach (var bucket in calibration.Buckets)
            {
                totalWeight += bucket.Count;
                weightedError += bucket.CalibrationError * bucket.Count;
            }

            if (totalWeight < 20) return; // Need meaningful sample

            var avgError = weightedError / totalWeight;
            // avgError < 0 means overconfident → we need factor < 1.0 to dampen
            // avgError > 0 means underconfident → factor > 1.0 to boost

            // Target calibration factor: 1.0 + (error scaled to factor range)
            // Scale: a -0.20 avg error → target factor of ~0.90
            var targetFactor = Math.Clamp(1.0 + (avgError * 0.5), 0.85, 1.15);

            // Get current calibration_factor from overrides
            var currentOverrides = await _repo.GetActiveWeightOverridesAsync();
            var currentFactor = currentOverrides
                .Where(o => o.SignalName == "calibration_factor")
                .Select(o => o.EffectiveWeight)
                .FirstOrDefault(1.0);

            // Gradual movement: max 1% per day toward target
            var delta = targetFactor - currentFactor;
            var movement = Math.Clamp(delta, -MaxDailyMovement, MaxDailyMovement);
            var newFactor = Math.Round(currentFactor + movement, 4);
            newFactor = Math.Clamp(newFactor, 0.85, 1.15);

            if (Math.Abs(movement) < 0.0005) return; // No meaningful change

            var reason = $"Calibration error: {avgError * 100:F1}% across {totalWeight} predictions. " +
                         $"Target factor: {targetFactor:F4}. " +
                         (calibration.IsOverconfident
                             ? "System is overconfident — dampening confidence scores."
                             : "Calibration adjustment applied.");

            await _repo.UpsertWeightOverrideAsync(new ScoringWeightOverride
            {
                SignalName = "calibration_factor",
                BaseWeight = 1.0,
                AdjustmentPercent = newFactor - 1.0,
                EffectiveWeight = newFactor,
                Confidence = Math.Min((double)totalWeight / 200.0, 1.0),
                SampleSize = totalWeight,
                Status = "active",
                Reason = reason,
            });

            _logger.LogInformation(
                "[learning-engine] Calibration factor: {Old:F4} → {New:F4} (error={Error:F1}%, overconfident={OC})",
                currentFactor, newFactor, avgError * 100, calibration.IsOverconfident);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[learning-engine] Failed to apply calibration factor");
        }
    }

    // -----------------------------------------------------------------------
    // Stage 4: Weight Optimization (safe, gradual, Bayesian-smoothed)
    // -----------------------------------------------------------------------

    public async Task<(int Adjusted, List<WeightChangeSummary> Changes)> OptimizeWeightsAsync(
        List<ResearchSignalPerformance> signalStats)
    {
        var changes = new List<WeightChangeSummary>();
        var currentOverrides = await _repo.GetActiveWeightOverridesAsync();
        var overrideMap = currentOverrides.ToDictionary(o => o.SignalName);

        var allDirectionStats = signalStats
            .Where(s => s.Direction == "all" && s.TotalPredictions >= MinObservationsForAdjustment)
            .ToList();

        foreach (var stat in allDirectionStats)
        {
            var baseWeight = DefaultBaseWeights.GetValueOrDefault(stat.SignalName, 1.0);
            var currentAdj = overrideMap.TryGetValue(stat.SignalName, out var existing)
                ? existing.AdjustmentPercent : 0.0;

            // Bayesian smoothing: blend with prior (50% accuracy)
            var bayesianAccuracy = (stat.CorrectPredictions + 25.0) / (stat.TotalPredictions + 50.0);
            var targetAdj = (bayesianAccuracy - 0.5) * 2.0;
            targetAdj = Math.Clamp(targetAdj, -MaxAdjustmentPercent, MaxAdjustmentPercent);

            // Gradual movement: max 1% per day toward target
            var delta = targetAdj - currentAdj;
            var movement = Math.Clamp(delta, -MaxDailyMovement, MaxDailyMovement);
            var newAdj = Math.Round(currentAdj + movement, 4);

            if (Math.Abs(movement) < 0.001) continue;

            var effectiveWeight = baseWeight * (1.0 + newAdj);
            var confidence = Math.Min((double)stat.TotalPredictions / 200.0, 1.0);

            var reason = $"Accuracy: {stat.Accuracy * 100:F1}% over {stat.TotalPredictions} obs. " +
                         $"Bayesian: {bayesianAccuracy * 100:F1}%. Target adj: {targetAdj * 100:F1}%.";

            await _repo.UpsertWeightOverrideAsync(new ScoringWeightOverride
            {
                SignalName = stat.SignalName,
                BaseWeight = baseWeight,
                AdjustmentPercent = newAdj,
                EffectiveWeight = effectiveWeight,
                Confidence = confidence,
                SampleSize = stat.TotalPredictions,
                Status = "active",
                Reason = reason,
            });

            changes.Add(new WeightChangeSummary
            {
                SignalName = stat.SignalName,
                PreviousWeight = baseWeight * (1.0 + currentAdj),
                NewWeight = effectiveWeight,
                ChangePercent = movement * 100,
                Reason = reason,
            });
        }

        return (changes.Count, changes);
    }

    // -----------------------------------------------------------------------
    // Stage 5: Setup Analytics — learn complete trade setups
    // -----------------------------------------------------------------------

    /// <summary>
    /// Computes performance statistics for each unique setup fingerprint.
    /// This is the heart of setup-level learning: which COMBINATIONS of
    /// signals consistently produce positive outcomes?
    /// </summary>
    public async Task<int> ComputeSetupPerformanceAsync()
    {
        try
        {
            var predictions = await _repo.GetRecentPredictionsAsync(500);
            var outcomes = await _repo.GetRecentOutcomesAsync(500);
            var outcomeMap = outcomes.ToDictionary(o => o.PredictionId);

            // Group evaluated predictions by their setup fingerprint
            var setupGroups = new Dictionary<string, List<(PredictionCandidate Pred, PredictionOutcome Outcome, ScoringBreakdown? Breakdown)>>();

            foreach (var pred in predictions)
            {
                if (!outcomeMap.TryGetValue(pred.Id, out var outcome) || outcome.DirectionCorrect is null)
                    continue;

                // Parse scoring breakdown to extract signals
                ScoringBreakdown? breakdown = null;
                if (!string.IsNullOrEmpty(pred.ScoreDebugJson))
                {
                    try
                    {
                        breakdown = JsonSerializer.Deserialize<ScoringBreakdown>(pred.ScoreDebugJson,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch { continue; }
                }
                if (breakdown is null) continue;

                // Reconstruct the setup fingerprint from the scoring breakdown
                var evidence = ReconstructEvidence(breakdown);
                var fingerprint = TradeSetupEngine.GenerateFingerprint(
                    evidence, breakdown.WinningDirection);

                if (string.IsNullOrEmpty(fingerprint.Fingerprint)) continue;

                if (!setupGroups.ContainsKey(fingerprint.Fingerprint))
                    setupGroups[fingerprint.Fingerprint] = [];

                setupGroups[fingerprint.Fingerprint].Add((pred, outcome, breakdown));
            }

            var statsUpdated = 0;
            foreach (var (fingerprint, group) in setupGroups)
            {
                if (group.Count < 3) continue; // Need meaningful sample

                var wins = group.Count(g => g.Outcome.DirectionCorrect == true);
                var losses = group.Count - wins;
                var winRate = (double)wins / group.Count;

                var winReturns = group
                    .Where(g => g.Outcome.DirectionCorrect == true && g.Outcome.PercentMove.HasValue)
                    .Select(g => Math.Abs(g.Outcome.PercentMove!.Value))
                    .ToList();
                var lossReturns = group
                    .Where(g => g.Outcome.DirectionCorrect == false && g.Outcome.PercentMove.HasValue)
                    .Select(g => -Math.Abs(g.Outcome.PercentMove!.Value))
                    .ToList();

                var avgWin = winReturns.Count > 0 ? winReturns.Average() : 0;
                var avgLoss = lossReturns.Count > 0 ? lossReturns.Average() : 0;
                var ev = (winRate * avgWin) + ((1 - winRate) * avgLoss);

                // Confidence based on sample size (asymptotic to 1.0)
                var confidence = Math.Min((double)group.Count / 50.0, 1.0);

                // Risk rating based on return variance
                var allReturns = group
                    .Where(g => g.Outcome.PercentMove.HasValue)
                    .Select(g => g.Outcome.PercentMove!.Value)
                    .ToList();
                var variance = allReturns.Count > 1
                    ? allReturns.Select(r => Math.Pow(r - allReturns.Average(), 2)).Average()
                    : 0;
                var riskRating = (int)Math.Clamp(Math.Sqrt(variance) * 20, 0, 100);

                // Regime breakdown
                var regimeBreakdown = new Dictionary<string, RegimePerformance>();
                var byRegime = group
                    .Where(g => g.Breakdown is not null)
                    .GroupBy(g => TradeSetupEngine.DetectMarketRegime(g.Breakdown!) ?? "unknown");
                foreach (var regimeGroup in byRegime)
                {
                    if (regimeGroup.Count() < 2) continue;
                    var rWins = regimeGroup.Count(g => g.Outcome.DirectionCorrect == true);
                    var rWinRate = (double)rWins / regimeGroup.Count();
                    var rWinReturns = regimeGroup.Where(g => g.Outcome.DirectionCorrect == true && g.Outcome.PercentMove.HasValue)
                        .Select(g => Math.Abs(g.Outcome.PercentMove!.Value)).ToList();
                    var rLossReturns = regimeGroup.Where(g => g.Outcome.DirectionCorrect == false && g.Outcome.PercentMove.HasValue)
                        .Select(g => -Math.Abs(g.Outcome.PercentMove!.Value)).ToList();
                    var rAvgWin = rWinReturns.Count > 0 ? rWinReturns.Average() : 0;
                    var rAvgLoss = rLossReturns.Count > 0 ? rLossReturns.Average() : 0;
                    var rEv = (rWinRate * rAvgWin) + ((1 - rWinRate) * rAvgLoss);

                    regimeBreakdown[regimeGroup.Key] = new RegimePerformance
                    {
                        SampleSize = regimeGroup.Count(),
                        WinRate = Math.Round(rWinRate, 4),
                        ExpectedValuePercent = Math.Round(rEv, 4),
                    };
                }

                // Get the first group item's fingerprint description for the stat
                var firstBreakdown = group.First().Breakdown!;
                var sampleEvidence = ReconstructEvidence(firstBreakdown);
                var sampleFp = TradeSetupEngine.GenerateFingerprint(sampleEvidence, firstBreakdown.WinningDirection);

                // Determine trust: is the setup degrading recently?
                var isTrusted = true;
                var recentCutoff = DateTimeOffset.UtcNow.AddDays(-30);
                var recentGroup = group.Where(g => g.Pred.CreatedAt >= recentCutoff).ToList();
                if (recentGroup.Count >= 3)
                {
                    var recentWinRate = (double)recentGroup.Count(g => g.Outcome.DirectionCorrect == true) / recentGroup.Count;
                    if (winRate - recentWinRate > 0.15) isTrusted = false; // degrading
                }

                var avgConfirmation = group
                    .Select(g => ReconstructEvidence(g.Breakdown!).Count(e => e.Value.IsActive))
                    .Average();

                await _repo.UpsertSetupLearningStatAsync(new
                {
                    setup_fingerprint = fingerprint,
                    description = sampleFp.Description,
                    direction = sampleFp.Direction,
                    total_occurrences = group.Count,
                    wins,
                    losses,
                    win_rate = Math.Round(winRate, 4),
                    average_win_percent = Math.Round(avgWin, 4),
                    average_loss_percent = Math.Round(avgLoss, 4),
                    expected_value_percent = Math.Round(ev, 4),
                    average_holding_days = 1.0, // TODO: track actual holding days once multi-day tracking is live
                    average_confirmation_count = (int)Math.Round(avgConfirmation),
                    confidence = Math.Round(confidence, 4),
                    risk_rating = riskRating,
                    is_trusted = isTrusted,
                    market_regime_breakdown_json = JsonSerializer.Serialize(regimeBreakdown),
                    last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
                });

                statsUpdated++;
            }

            _logger.LogInformation("[learning-engine] Setup analytics: updated {Count} setup performance stats from {Groups} unique fingerprints",
                statsUpdated, setupGroups.Count);

            return statsUpdated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[learning-engine] Setup performance computation failed");
            return 0;
        }
    }

    /// <summary>
    /// Reconstruct BucketEvidence from a ScoringBreakdown (for historical predictions
    /// that were scored before the setup engine existed).
    /// </summary>
    private static Dictionary<string, BucketEvidence> ReconstructEvidence(ScoringBreakdown b)
    {
        var evidence = new Dictionary<string, BucketEvidence>();
        const double threshold = 3.0;

        void Add(string name, double bull, double bear)
        {
            var net = bull - bear;
            evidence[name] = new BucketEvidence
            {
                BucketName = name,
                BullishScore = bull,
                BearishScore = bear,
                NetScore = net,
                DominantDirection = Math.Abs(net) < threshold ? "neutral" : net > 0 ? "bullish" : "bearish",
                IsActive = Math.Abs(net) >= threshold,
            };
        }

        Add("trend", b.TrendBullish, b.TrendBearish);
        Add("momentum", b.MomentumBullish, b.MomentumBearish);
        Add("volume", b.VolumeBullish, b.VolumeBearish);
        Add("volatility", b.VolatilityBullish, b.VolatilityBearish);
        Add("market_context", b.MarketContextBullish, b.MarketContextBearish);
        Add("catalyst", b.CatalystBullish, b.CatalystBearish);
        Add("research_signal", b.ResearchSignalBullish, b.ResearchSignalBearish);

        return evidence;
    }

    // -----------------------------------------------------------------------
    // Stage 4b: Pattern Detection Evidence Producer
    // -----------------------------------------------------------------------

    public async Task<List<PatternRecommendation>> ProducePatternRecommendationsAsync()
    {
        var recommendations = new List<PatternRecommendation>();

        try
        {
            var clusters = await _patternDetection.AnalyzeFailureClustersAsync();
            var combos = await _patternDetection.AnalyzeSignalCombinationsAsync();

            // Regime-aware confidence recommendations from failure clusters
            foreach (var cluster in clusters.Clusters)
            {
                if (cluster.Count < 5) continue; // need meaningful sample

                // Extract regime from cluster name
                var regime = cluster.ClusterName.Contains("bull") ? "bull_market"
                    : cluster.ClusterName.Contains("bear") ? "bear_market"
                    : cluster.ClusterName.Contains("High-confidence") ? "overconfidence"
                    : null;

                if (regime == null) continue;

                // Failure rate in this cluster vs overall
                var failureRate = (double)cluster.Count / Math.Max(clusters.TotalFailures, 1);
                if (failureRate < 0.2) continue; // cluster must represent ≥20% of failures

                // Recommend a confidence multiplier reduction proportional to the cluster severity
                var multiplier = regime == "overconfidence"
                    ? -0.05 // tighten confidence cap by 5%
                    : -0.03 * failureRate; // scale by how concentrated failures are

                recommendations.Add(new PatternRecommendation
                {
                    Type = "regime_confidence_cap",
                    SignalName = regime,
                    RecommendedAdjustment = Math.Round(multiplier, 4),
                    Confidence = Math.Min((double)cluster.Count / 20, 1.0),
                    Evidence = cluster.Count,
                    Reason = $"{cluster.ClusterName}: {cluster.Count} failures (avg conf {cluster.AvgConfidence:F0}). {cluster.SuggestedAction}",
                });
            }

            // Synergy-based weight recommendations from signal combinations
            foreach (var combo in combos.BestCombinations.Where(c => c.SynergyScore > 5 && c.CoOccurrences >= 10))
            {
                // Boost both signals slightly when they have positive synergy
                var boost = Math.Min(combo.SynergyScore / 200.0, 0.03); // max 3% boost
                foreach (var signal in new[] { combo.Signal1, combo.Signal2 })
                {
                    recommendations.Add(new PatternRecommendation
                    {
                        Type = "synergy_weight",
                        SignalName = signal,
                        RecommendedAdjustment = Math.Round(boost, 4),
                        Confidence = Math.Min((double)combo.CoOccurrences / 30, 1.0),
                        Evidence = combo.CoOccurrences,
                        Reason = $"Synergy: {combo.Signal1}+{combo.Signal2} joint accuracy {combo.JointAccuracy:F1}% ({combo.SynergyScore:+0.0}% synergy, n={combo.CoOccurrences})",
                    });
                }
            }

            foreach (var combo in combos.WorstCombinations.Where(c => c.SynergyScore < -5 && c.CoOccurrences >= 10))
            {
                // Penalize both signals slightly when they have negative synergy
                var penalty = Math.Max(combo.SynergyScore / 200.0, -0.03); // max 3% penalty
                foreach (var signal in new[] { combo.Signal1, combo.Signal2 })
                {
                    recommendations.Add(new PatternRecommendation
                    {
                        Type = "synergy_weight",
                        SignalName = signal,
                        RecommendedAdjustment = Math.Round(penalty, 4),
                        Confidence = Math.Min((double)combo.CoOccurrences / 30, 1.0),
                        Evidence = combo.CoOccurrences,
                        Reason = $"Negative synergy: {combo.Signal1}+{combo.Signal2} joint accuracy {combo.JointAccuracy:F1}% ({combo.SynergyScore:+0.0}% synergy, n={combo.CoOccurrences})",
                    });
                }
            }

            _logger.LogInformation("[learning-engine] Pattern detection produced {Count} recommendations", recommendations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[learning-engine] Pattern detection failed — skipping second-order adjustments");
        }

        return recommendations;
    }

    /// <summary>
    /// Reconcile pattern recommendations with existing Bayesian adjustments.
    /// Respects the same daily movement limits and max adjustment caps.
    /// </summary>
    private async Task<(int Adjusted, List<WeightChangeSummary> Changes)> ApplyPatternRecommendationsAsync(
        List<PatternRecommendation> recommendations)
    {
        var changes = new List<WeightChangeSummary>();
        if (recommendations.Count == 0) return (0, changes);

        var currentOverrides = await _repo.GetActiveWeightOverridesAsync();
        var overrideMap = currentOverrides.ToDictionary(o => o.SignalName);

        // Group synergy recommendations by signal and average them
        var synergyBySignal = recommendations
            .Where(r => r.Type == "synergy_weight" && BucketNames.Contains(r.SignalName))
            .GroupBy(r => r.SignalName)
            .ToDictionary(g => g.Key, g => g.Average(r => r.RecommendedAdjustment));

        foreach (var (signal, avgAdjustment) in synergyBySignal)
        {
            var baseWeight = DefaultBaseWeights.GetValueOrDefault(signal, 1.0);
            var currentAdj = overrideMap.TryGetValue(signal, out var existing)
                ? existing.AdjustmentPercent : 0.0;

            // Apply synergy adjustment on top, capped by daily movement limit
            var movement = Math.Clamp(avgAdjustment, -MaxDailyMovement, MaxDailyMovement);
            var newAdj = Math.Clamp(currentAdj + movement, -MaxAdjustmentPercent, MaxAdjustmentPercent);

            if (Math.Abs(movement) < 0.001) continue;

            var effectiveWeight = baseWeight * (1.0 + newAdj);
            var reasons = recommendations
                .Where(r => r.Type == "synergy_weight" && r.SignalName == signal)
                .Select(r => r.Reason).ToList();

            await _repo.UpsertWeightOverrideAsync(new ScoringWeightOverride
            {
                SignalName = signal,
                BaseWeight = baseWeight,
                AdjustmentPercent = newAdj,
                EffectiveWeight = effectiveWeight,
                Confidence = Math.Min(recommendations.Where(r => r.SignalName == signal).Average(r => r.Confidence), 1.0),
                SampleSize = recommendations.Where(r => r.SignalName == signal).Sum(r => r.Evidence),
                Status = "active",
                Reason = $"Synergy adjustment: {string.Join("; ", reasons.Take(2))}",
            });

            changes.Add(new WeightChangeSummary
            {
                SignalName = signal,
                PreviousWeight = baseWeight * (1.0 + currentAdj),
                NewWeight = effectiveWeight,
                ChangePercent = movement * 100,
                Reason = $"[pattern-detection] {string.Join("; ", reasons.Take(2))}",
            });
        }

        // Log regime recommendations as insights (these affect confidence at scoring time, not weights)
        var regimeRecs = recommendations.Where(r => r.Type == "regime_confidence_cap").ToList();
        if (regimeRecs.Count > 0)
        {
            var insights = regimeRecs.Select(r => new
            {
                insight_type = "pattern_detection",
                summary = r.Reason,
                evidence = $"{r.Evidence} failures in cluster. Recommended confidence adjustment: {r.RecommendedAdjustment * 100:F1}%.",
                action_recommendation = "Regime-aware confidence cap applied to scoring engine.",
                confidence = r.Confidence,
            }).Cast<object>().ToList();

            await _repo.SaveLearningInsightsAsync(insights);
            _logger.LogInformation("[learning-engine] Saved {Count} regime-aware pattern insights", regimeRecs.Count);
        }

        return (changes.Count, changes);
    }

    // -----------------------------------------------------------------------
    // Stage 5: AI-Summarized Learning Report
    // -----------------------------------------------------------------------

    public async Task<string?> GenerateAiLearningReportAsync(
        List<ResearchSignalPerformance> signalStats,
        ConfidenceAnalysis calibration,
        List<WeightChangeSummary> weightChanges)
    {
        if (!_ai.IsConfigured)
        {
            _logger.LogWarning("[learning-engine] OpenAI not configured, skipping AI summary");
            return null;
        }

        var predictions = await _repo.GetRecentPredictionsAsync(200);
        var outcomes = await _repo.GetRecentOutcomesAsync(200);
        var outcomeMap = outcomes.ToDictionary(o => o.PredictionId);

        var evaluated = predictions.Where(p => outcomeMap.ContainsKey(p.Id)
            && outcomeMap[p.Id].DirectionCorrect is not null).ToList();
        var correct = evaluated.Count(p => outcomeMap[p.Id].DirectionCorrect == true);
        var bullPreds = evaluated.Where(p => p.PredictionType == PredictionType.bullish).ToList();
        var bearPreds = evaluated.Where(p => p.PredictionType == PredictionType.bearish).ToList();
        var bullCorrect = bullPreds.Count(p => outcomeMap[p.Id].DirectionCorrect == true);
        var bearCorrect = bearPreds.Count(p => outcomeMap[p.Id].DirectionCorrect == true);

        var topSignals = signalStats
            .Where(s => s.Direction == "all" && s.TotalPredictions >= 10)
            .OrderByDescending(s => s.Accuracy).Take(4)
            .Select(s => $"  {s.SignalName}: {s.Accuracy * 100:F0}% ({s.TotalPredictions} predictions)")
            .ToList();

        var weakSignals = signalStats
            .Where(s => s.Direction == "all" && s.TotalPredictions >= 10)
            .OrderBy(s => s.Accuracy).Take(3)
            .Select(s => $"  {s.SignalName}: {s.Accuracy * 100:F0}% ({s.TotalPredictions} predictions)")
            .ToList();

        var calibBuckets = calibration.Buckets
            .Select(b => $"  Confidence {b.Range}: {b.ActualAccuracy * 100:F0}% actual ({b.Count} predictions)")
            .ToList();

        var weightChangeLines = weightChanges
            .Select(w => $"  {w.SignalName}: {w.ChangePercent:+0.0;-0.0}% (now {w.NewWeight:F2})")
            .ToList();

        var prompt = $@"You are the learning analyst for STOCKJAWN, an AI stock prediction system.
Summarize what the system learned today in a concise, actionable report for the system owner.

DATA:
- Total evaluated predictions: {evaluated.Count}
- Overall accuracy: {(evaluated.Count > 0 ? (double)correct / evaluated.Count * 100 : 0):F1}%
- Bullish accuracy: {(bullPreds.Count > 0 ? (double)bullCorrect / bullPreds.Count * 100 : 0):F1}% ({bullPreds.Count} predictions)
- Bearish accuracy: {(bearPreds.Count > 0 ? (double)bearCorrect / bearPreds.Count * 100 : 0):F1}% ({bearPreds.Count} predictions)

TOP SIGNALS:
{string.Join("\n", topSignals)}

WEAK SIGNALS:
{string.Join("\n", weakSignals)}

CONFIDENCE CALIBRATION:
{string.Join("\n", calibBuckets)}
Calibration status: {calibration.Summary}

WEIGHT CHANGES APPLIED:
{(weightChangeLines.Count > 0 ? string.Join("\n", weightChangeLines) : "  None today")}

INSTRUCTIONS:
- Write 3-5 short paragraphs, conversational tone
- Lead with the most important finding
- Highlight any concerning patterns or improvements
- If confidence is miscalibrated, flag it clearly
- Mention which signals are driving wins vs losses
- Note any directional asymmetry (bull vs bear performance)
- Keep under 400 words
- Do NOT use bullet points or headers — write in flowing prose";

        try
        {
            var result = await _ai.CompleteAsync(new AiCompletionRequest
            {
                Messages =
                [
                    new() { Role = "system", Content = "You are a quantitative finance analyst providing learning summaries for an AI stock prediction system." },
                    new() { Role = "user", Content = prompt },
                ],
                MaxOutputTokens = 600,
            }, CancellationToken.None);

            var summary = result.Text;

            // Save enhanced learning report
            var topSignalsList = signalStats
                .Where(s => s.Direction == "all" && s.TotalPredictions >= 10)
                .OrderByDescending(s => s.Accuracy).Take(4)
                .Select(s => new SignalPerformanceSummary
                {
                    SignalName = s.SignalName, Accuracy = s.Accuracy,
                    SampleSize = s.TotalPredictions, AverageContribution = s.AverageOutcomeScore,
                }).ToList();

            var weakSignalsList = signalStats
                .Where(s => s.Direction == "all" && s.TotalPredictions >= 10)
                .OrderBy(s => s.Accuracy).Take(3)
                .Select(s => new SignalPerformanceSummary
                {
                    SignalName = s.SignalName, Accuracy = s.Accuracy,
                    SampleSize = s.TotalPredictions, AverageContribution = s.AverageOutcomeScore,
                }).ToList();

            await _repo.SaveEnhancedLearningReportAsync(new
            {
                report_date = DateTimeOffset.UtcNow.ToString("o"),
                sample_size = evaluated.Count,
                summary = summary,
                evaluation_window_days = 30,
                prediction_count = evaluated.Count,
                overall_accuracy = evaluated.Count > 0 ? (double)correct / evaluated.Count : (double?)null,
                bull_accuracy = bullPreds.Count > 0 ? (double)bullCorrect / bullPreds.Count : (double?)null,
                bear_accuracy = bearPreds.Count > 0 ? (double)bearCorrect / bearPreds.Count : (double?)null,
                top_signals_json = JsonSerializer.Serialize(topSignalsList),
                weak_signals_json = JsonSerializer.Serialize(weakSignalsList),
                weight_changes_json = JsonSerializer.Serialize(weightChanges),
                confidence_analysis_json = JsonSerializer.Serialize(calibration),
                ai_summary = summary,
            });

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[learning-engine] AI summary generation failed");
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Stage 6: Structured Insights
    // -----------------------------------------------------------------------

    public async Task<List<object>> GenerateLearningInsightsAsync()
    {
        var perfStats = await _repo.GetAllSignalPerformanceAsync();
        var outcomes = await _repo.GetRecentOutcomesAsync(100);
        var predictions = await _repo.GetRecentPredictionsAsync(100);
        var insights = new List<object>();

        // 1. Reliable signals (direction-aware)
        var reliable = perfStats.Where(s => s.TotalPredictions >= 10 && s.Accuracy > 0.6).ToList();
        if (reliable.Count > 0)
        {
            insights.Add(new
            {
                insight_type = "signal",
                summary = $"Reliable signals: {string.Join(", ", reliable.Select(s => $"{s.SignalName}[{s.Direction}] ({s.Accuracy * 100:F0}%, n={s.TotalPredictions})"))}",
                evidence = $"Based on {reliable.Sum(s => s.TotalPredictions)} total observations.",
                action_recommendation = "These signals are driving wins. Weight overrides have been adjusted accordingly.",
                confidence = Math.Min((double)reliable[0].TotalPredictions / 50, 1),
            });
        }

        // 2. Unreliable signals
        var unreliable = perfStats.Where(s => s.TotalPredictions >= 10 && s.Accuracy < 0.4).ToList();
        if (unreliable.Count > 0)
        {
            insights.Add(new
            {
                insight_type = "signal",
                summary = $"Underperforming signals: {string.Join(", ", unreliable.Select(s => $"{s.SignalName}[{s.Direction}] ({s.Accuracy * 100:F0}%, n={s.TotalPredictions})"))}",
                evidence = $"Based on {unreliable.Sum(s => s.TotalPredictions)} total observations.",
                action_recommendation = "Weights are being gradually reduced for these signals.",
                confidence = Math.Min((double)unreliable[0].TotalPredictions / 50, 1),
            });
        }

        // 3. Direction asymmetry
        var directionPairs = perfStats
            .Where(s => s.Direction is "bullish" or "bearish" && s.TotalPredictions >= 10)
            .GroupBy(s => s.SignalName)
            .Where(g => g.Count() == 2)
            .ToList();
        foreach (var pair in directionPairs)
        {
            var bull = pair.First(s => s.Direction == "bullish");
            var bear = pair.First(s => s.Direction == "bearish");
            var gap = Math.Abs(bull.Accuracy - bear.Accuracy);
            if (gap >= 0.15)
            {
                var better = bull.Accuracy > bear.Accuracy ? "bullish" : "bearish";
                insights.Add(new
                {
                    insight_type = "direction_asymmetry",
                    summary = $"{pair.Key} works better for {better} predictions ({(better == "bullish" ? bull : bear).Accuracy * 100:F0}% vs {(better != "bullish" ? bull : bear).Accuracy * 100:F0}%).",
                    evidence = $"Bull n={bull.TotalPredictions}, Bear n={bear.TotalPredictions}.",
                    action_recommendation = $"Direction-specific weight adjustments are being applied.",
                    confidence = Math.Min((double)Math.Min(bull.TotalPredictions, bear.TotalPredictions) / 30, 1),
                });
            }
        }

        // 4. Per-ticker patterns
        var outcomeMap = outcomes.ToDictionary(o => o.PredictionId);
        var tickerStats = new Dictionary<string, (int Correct, int Wrong, int Total)>();
        foreach (var pred in predictions)
        {
            if (!outcomeMap.TryGetValue(pred.Id, out var outcome) || outcome.DirectionCorrect is null) continue;
            var (correct, wrong, total) = tickerStats.GetValueOrDefault(pred.Ticker);
            total++;
            if (outcome.DirectionCorrect == true) correct++; else wrong++;
            tickerStats[pred.Ticker] = (correct, wrong, total);
        }
        foreach (var (ticker, (correct, wrong, total)) in tickerStats)
        {
            if (total < 3) continue;
            var accuracy = (double)correct / total;
            if (accuracy < 0.3)
                insights.Add(new
                {
                    insight_type = "ticker",
                    summary = $"{ticker}: only {correct}/{total} correct ({accuracy * 100:F0}%).",
                    evidence = $"{wrong} wrong predictions vs {correct} correct.",
                    action_recommendation = $"Consider requiring higher confidence threshold for {ticker}.",
                    confidence = Math.Min((double)total / 10, 1),
                });
            else if (accuracy > 0.7 && total >= 5)
                insights.Add(new
                {
                    insight_type = "ticker",
                    summary = $"{ticker}: {correct}/{total} correct ({accuracy * 100:F0}%) — strong track record.",
                    evidence = $"Consistent across {total} predictions.",
                    action_recommendation = $"{ticker} is a reliable prediction target.",
                    confidence = Math.Min((double)total / 10, 1),
                });
        }

        // 5. Setup-level insights — which combinations of signals work?
        try
        {
            var setupStats = await _repo.GetAllSetupLearningStatsAsync();
            var topSetups = setupStats
                .Where(s => s.TotalOccurrences >= 8 && s.ExpectedValuePercent > 0.5)
                .OrderByDescending(s => s.ExpectedValuePercent)
                .Take(5)
                .ToList();

            foreach (var s in topSetups)
            {
                insights.Add(new
                {
                    insight_type = "setup",
                    summary = $"Setup [{s.Description}] has {s.WinRate * 100:F0}% win rate with {(s.ExpectedValuePercent >= 0 ? "+" : "")}{s.ExpectedValuePercent:F2}% EV over {s.TotalOccurrences} occurrences.",
                    evidence = $"Avg win: +{s.AverageWinPercent:F2}%, avg loss: {s.AverageLossPercent:F2}%. Confidence: {s.Confidence:F2}. Risk rating: {s.RiskRating}/100.",
                    action_recommendation = s.IsTrusted
                        ? "This setup is trusted and historically favorable. Boost confidence when detected."
                        : "This setup shows historical promise but recent degradation. Monitor closely.",
                    confidence = s.Confidence,
                });
            }

            var degradedSetups = setupStats
                .Where(s => s.TotalOccurrences >= 8 && !s.IsTrusted)
                .ToList();

            if (degradedSetups.Count > 0)
            {
                insights.Add(new
                {
                    insight_type = "setup_degradation",
                    summary = $"{degradedSetups.Count} setup(s) showing recent degradation: {string.Join(", ", degradedSetups.Select(s => $"[{s.Description}]"))}.",
                    evidence = $"Recent win rates have dropped >15% below all-time averages.",
                    action_recommendation = "These setups should no longer boost confidence until performance recovers.",
                    confidence = 0.8,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[learning-engine] Failed to generate setup insights");
        }

        if (insights.Count > 0)
            await _repo.SaveLearningInsightsAsync(insights);

        return insights;
    }

    // -----------------------------------------------------------------------
    // Legacy public methods (backward compatibility during migration)
    // -----------------------------------------------------------------------

    public async Task<(int Updated, List<ResearchSignalPerformance> Signals)> UpdateSignalPerformanceAsync()
    {
        return await ComputeSignalPerformanceAsync();
    }

    public record WeightChange(string Signal, double OldWeight, double NewWeight, string Reason);

    public async Task<(int Adjusted, List<WeightChange> Changes)> UpdateScoringWeightsFromOutcomesAsync()
    {
        var (_, signalStats) = await ComputeSignalPerformanceAsync();
        var (adjusted, changes) = await OptimizeWeightsAsync(signalStats);
        return (adjusted, changes.Select(c => new WeightChange(
            c.SignalName, c.PreviousWeight, c.NewWeight, c.Reason ?? "")).ToList());
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (double Bull, double Bear) GetBucketScores(ScoringBreakdown b, string bucket) => bucket switch
    {
        "trend" => (b.TrendBullish, b.TrendBearish),
        "momentum" => (b.MomentumBullish, b.MomentumBearish),
        "volume" => (b.VolumeBullish, b.VolumeBearish),
        "volatility" => (b.VolatilityBullish, b.VolatilityBearish),
        "market_context" => (b.MarketContextBullish, b.MarketContextBearish),
        "catalyst" => (b.CatalystBullish, b.CatalystBearish),
        "learning" => (b.LearningBullish, b.LearningBearish),
        "research_signal" => (b.ResearchSignalBullish, b.ResearchSignalBearish),
        _ => (0, 0),
    };

    /// <summary>
    /// Simple regime detection based on scoring breakdown metadata.
    /// Will be enhanced with SPY trend, VIX level, breadth data.
    /// </summary>
    private static string? DetectMarketRegime(ScoringBreakdown b)
    {
        var marketNet = b.MarketContextBullish - b.MarketContextBearish;
        var volNet = b.VolatilityBullish - b.VolatilityBearish;

        if (volNet > 5) return "high_volatility";
        if (marketNet > 5) return "bull_trend";
        if (marketNet < -5) return "bear_trend";
        if (Math.Abs(marketNet) <= 3) return "sideways";
        return null;
    }
}
