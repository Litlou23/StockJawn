using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const double MaxAdjustmentPercent = 0.50;   // ±50% max — let the learning engine express conviction
    private const double MaxDailyMovement = 0.05;       // 5% per day — converge in days not months
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

    /// <summary>
    /// Neutral correctness threshold: abs move &lt; 2% means the neutral call was correct.
    /// </summary>
    private const double NeutralCorrectThreshold = 2.0;

    /// <summary>
    /// Determines whether a prediction was "correct" — works for both directional and neutral types.
    /// Directional: uses DirectionCorrect from the outcome evaluator.
    /// Neutral: abs percent move &lt; 2% means the "no edge" / "range bound" call was right.
    /// Returns null if the outcome doesn't have enough data to determine correctness.
    /// </summary>
    private static bool? ResolveCorrectness(PredictionCandidate pred, PredictionOutcome outcome)
    {
        if (PredictionCategoryHelper.IsDirectional(pred.PredictionType))
            return outcome.DirectionCorrect;
        // Neutral: need PercentMove to evaluate
        if (outcome.PercentMove is null) return null;
        return Math.Abs(outcome.PercentMove.Value) < NeutralCorrectThreshold;
    }

    private readonly ResearchRepository _repo;
    private readonly PredictionProfileRepository _profileRepo;
    private readonly NeutralOutcomeRepository _neutralRepo;
    private readonly PortfolioChallengeRepository _portfolioRepo;
    private readonly PaperStockCandidateRepository _candidateRepo;
    private readonly PatternDetectionService _patternDetection;
    private readonly TradeSetupEngine _setupEngine;
    private readonly IOpenAiCompletionService _ai;
    private readonly WeightUpdateValidator _guardrail;
    private readonly ILogger<LearningEngine> _logger;

    public LearningEngine(
        ResearchRepository repo, PredictionProfileRepository profileRepo,
        NeutralOutcomeRepository neutralRepo,
        PortfolioChallengeRepository portfolioRepo,
        PaperStockCandidateRepository candidateRepo,
        PatternDetectionService patternDetection,
        TradeSetupEngine setupEngine,
        IOpenAiCompletionService ai, WeightUpdateValidator guardrail,
        ILogger<LearningEngine> logger)
    {
        _repo = repo;
        _profileRepo = profileRepo;
        _neutralRepo = neutralRepo;
        _portfolioRepo = portfolioRepo;
        _candidateRepo = candidateRepo;
        _patternDetection = patternDetection;
        _setupEngine = setupEngine;
        _ai = ai;
        _guardrail = guardrail;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Unified outcome map — merges prediction_outcomes + neutral_prediction_outcomes
    // so neutral evaluations feed into all learning stages.
    // -----------------------------------------------------------------------

    private async Task<Dictionary<string, PredictionOutcome>> BuildUnifiedOutcomeMapAsync(
        List<PredictionCandidate> predictions, string? profileId)
    {
        var outcomes = profileId is not null
            ? await _repo.GetOutcomesForProfileAsync(profileId, 500)
            : await _repo.GetRecentOutcomesAsync(500);
        var map = outcomes.ToDictionary(o => o.PredictionId);

        // Find neutral predictions that aren't in directional outcomes
        var neutralPredIds = predictions
            .Where(p => PredictionCategoryHelper.IsNeutralEvaluable(p.PredictionType)
                        && !map.ContainsKey(p.Id))
            .Select(p => p.Id).ToList();

        if (neutralPredIds.Count > 0)
        {
            var neutralOutcomes = await _neutralRepo.GetForPredictionsAsync(neutralPredIds);
            foreach (var no in neutralOutcomes)
            {
                // Convert NeutralPredictionOutcome → PredictionOutcome so existing
                // ResolveCorrectness and downstream code works unchanged.
                map[no.PredictionId] = new PredictionOutcome
                {
                    Id = no.Id,
                    PredictionId = no.PredictionId,
                    EvaluationTime = no.EvaluationTime,
                    StartPrice = no.EntryPrice,
                    ClosePrice = no.ExitPrice,
                    HighAfterPrediction = no.HighAfter,
                    LowAfterPrediction = no.LowAfter,
                    PercentMove = no.RealizedMovePercent,
                    OutcomeScore = no.NeutralAccuracyScore,
                    MaxFavorablePercent = no.MaxRunUp,
                    MaxAdversePercent = no.MaxDrawdown,
                    OutcomeSummary = no.OutcomeSummary,
                    Lesson = no.Lesson,
                    // DirectionCorrect is null for neutrals — ResolveCorrectness uses PercentMove instead
                };
            }
            if (neutralOutcomes.Count > 0)
                _logger.LogInformation("[learning-engine] Merged {Count} neutral outcomes into unified map", neutralOutcomes.Count);
        }

        return map;
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
    /// Runs learning for all enabled profiles that have learning turned on.
    /// Iterates champion first, then challengers. Returns the champion result
    /// for backward compatibility with callers that expect a single result.
    /// </summary>
    public async Task<LearningUpdateResult> RunLearningForAllProfilesAsync()
    {
        var profiles = await _profileRepo.GetEnabledProfilesAsync();
        var learningProfiles = profiles.Where(p => p.LearningEnabled).ToList();

        _logger.LogInformation("[learning-engine] Running learning for {Count} profiles", learningProfiles.Count);

        LearningUpdateResult? championResult = null;

        foreach (var profile in learningProfiles)
        {
            var isChampion = profile.Role == ProfileRole.champion;
            _logger.LogInformation("[learning-engine] Running learning for profile {Name} ({Role})",
                profile.ProfileName, profile.Role);

            var result = await RunFullLearningCycleAsync(profile.Id, isChampion);

            if (isChampion)
                championResult = result;
        }

        // Return champion result for backward compatibility
        return championResult ?? new LearningUpdateResult { Report = "No champion profile found." };
    }

    /// <summary>
    /// Full learning cycle: observations → stats → calibration → weights → AI report.
    /// When profileId is provided, all stages filter data to that profile.
    /// When isChampion is false (challenger), weight updates route to profile configs
    /// instead of shared scoring_weight_overrides, and expensive AI stages are skipped.
    /// </summary>
    public async Task<LearningUpdateResult> RunFullLearningCycleAsync(string? profileId = null, bool isChampion = true)
    {
        var errors = new List<string>();

        // Stage 1: Extract signal observations from evaluated predictions
        var obsCreated = await ExtractSignalObservationsAsync(errors, profileId);

        // Stage 2: Compute signal performance stats (legacy binary tracking)
        var (perfUpdated, signalStats) = await ComputeSignalPerformanceAsync(profileId, isChampion);

        // Stage 2b-2e: Layered signal analytics (ChatGPT-recommended approach)
        var calibrationCount = await ComputeSignalCalibrationAsync(profileId, isChampion);
        var correlationCount = await ComputeSignalCorrelationsAsync(profileId, isChampion);
        var influenceCount = await ComputeSignalInfluenceAsync(profileId, isChampion);
        var interactionCount = await ComputeSignalInteractionsAsync(profileId, isChampion);

        // Stage 3: Confidence calibration — detect AND correct overconfidence
        var calibration = await ComputeConfidenceCalibrationAsync(profileId);
        await ApplyCalibrationFactorAsync(calibration, profileId, isChampion);

        // Stage 3c: Self-tuning confidence caps — detect caps that crush correct calls
        var capTuningCount = await ComputeCapEffectivenessAsync(profileId, isChampion);

        // Stage 4: Optimize weights (signal-level first-order adjustments)
        var (weightsAdjusted, weightChanges) = await OptimizeWeightsAsync(signalStats, profileId, isChampion);

        // Stage 4b: Pattern detection evidence (second-order adjustments)
        var patternRecommendations = await ProducePatternRecommendationsAsync(profileId);
        var (patternAdjusted, patternChanges) = await ApplyPatternRecommendationsAsync(patternRecommendations, profileId, isChampion);
        weightsAdjusted += patternAdjusted;
        weightChanges.AddRange(patternChanges);

        // Stage 4c: Decision threshold optimization — learn optimal edge/score thresholds
        var (thresholdAdjusted, thresholdChanges) = await OptimizeDecisionThresholdsAsync(profileId, isChampion);
        weightsAdjusted += thresholdAdjusted;
        weightChanges.AddRange(thresholdChanges);

        // Stage 5: Setup Analytics — learn complete trade setups
        var setupStatsUpdated = await ComputeSetupPerformanceAsync(profileId, isChampion);

        // Stage 5b: Supersession Learning — learn from prediction revisions
        var supersessionCount = await ComputeSupersessionAnalyticsAsync(profileId, isChampion);
        var revisionAnalytics = await GetSupersessionAnalyticsAsync();

        // Stage 5c: Volatility Opportunity Learning — learn which VOE opportunities are profitable
        var voeSummary = await ComputeVolatilityOpportunityLearningAsync(profileId, isChampion);
        weightChanges.AddRange(voeSummary.WeightChanges);

        // Stage 5d: Risk Management Learning — learn from stop-loss/take-profit/trailing-stop exits
        var riskLearningSummary = await ComputeRiskManagementLearningAsync(profileId, isChampion);

        // Stage 6: Generate AI-summarized learning report (champion only — expensive)
        string? aiSummary = null;
        if (isChampion)
            aiSummary = await GenerateAiLearningReportAsync(signalStats, calibration, weightChanges, voeSummary, riskLearningSummary, profileId);

        // Stage 7: Generate structured insights (champion only — writes to shared tables)
        var insights = new List<object>();
        if (isChampion)
            insights = await GenerateLearningInsightsAsync(profileId);

        var report = $"Learning cycle complete: {obsCreated} observations, {perfUpdated} signal stats, " +
                     $"{calibrationCount} calibration buckets, {correlationCount} correlations, " +
                     $"{influenceCount} influence stats, {interactionCount} interaction pairs, " +
                     $"{weightsAdjusted} weight adjustments, {setupStatsUpdated} setup stats, " +
                     $"{supersessionCount} supersession records, {voeSummary.TotalRecords} VOE records, " +
                     $"{riskLearningSummary.TotalEvents} risk events learned, " +
                     $"{insights.Count} insights.";

        _logger.LogInformation("[learning-engine] {Report}", report);

        return new LearningUpdateResult
        {
            InsightsGenerated = insights.Count,
            WeightsAdjusted = weightsAdjusted,
            ObservationsCreated = obsCreated,
            SupersessionRecordsCreated = supersessionCount,
            RevisionAnalytics = revisionAnalytics.TotalSupersessions > 0 ? revisionAnalytics : null,
            Report = report,
            AiSummary = aiSummary,
            Errors = errors,
        };
    }

    // -----------------------------------------------------------------------
    // Stage 1: Extract Signal Observations from score_debug_json
    // -----------------------------------------------------------------------

    public async Task<int> ExtractSignalObservationsAsync(List<string>? errors = null, string? profileId = null)
    {
        // Fetch evaluated predictions — these are the ones with outcomes to learn from.
        // Previously fetched by created_at.desc without status filter, which returned mostly
        // open predictions and missed the bulk of evaluated data.
        var predictions = await _repo.GetRecentPredictionsAsync(500, status: "evaluated", profileId: profileId);
        var outcomeMap = await BuildUnifiedOutcomeMapAsync(predictions, profileId);

        var observations = new List<object>();
        var processedCount = 0;

        foreach (var pred in predictions)
        {
            if (!outcomeMap.TryGetValue(pred.Id, out var outcome))
                continue;

            // Determine correctness for both directional and neutral predictions
            var resolvedCorrect = ResolveCorrectness(pred, outcome);
            if (resolvedCorrect is null) continue; // not enough data to evaluate

            var isNeutral = !PredictionCategoryHelper.IsDirectional(pred.PredictionType);

            // Skip if we already have observations for this prediction
            if (await _repo.HasObservationsForPredictionAsync(pred.Id))
                continue;

            // Parse score_debug_json (handles both {"Breakdown":{...}} envelope and direct format)
            var breakdown = ScoringBreakdownEnvelope.Parse(pred.ScoreDebugJson);

            if (breakdown is null)
            {
                _logger.LogWarning("[learning-engine] Failed to parse score_debug_json for prediction {Id}, json starts with: {Start}",
                    pred.Id, pred.ScoreDebugJson?[..Math.Min(pred.ScoreDebugJson?.Length ?? 0, 100)]);
                continue;
            }

            // Debug: log first parsed breakdown to verify envelope unwrapping
            if (processedCount == 0)
            {
                _logger.LogInformation("[learning-engine] First breakdown parsed — TrendBull={TB}, TrendBear={TBr}, MomBull={MB}, VolBull={VB}",
                    breakdown.TrendBullish, breakdown.TrendBearish, breakdown.MomentumBullish, breakdown.VolumeBullish);
            }

            var direction = isNeutral ? "neutral"
                : pred.PredictionType == PredictionType.bearish ? "bearish" : "bullish";
            var correct = resolvedCorrect == true;

            // Pre-compute total weighted contribution for contribution_percent
            double totalContribution = 0;
            var bucketContributions = new Dictionary<string, double>();
            foreach (var bucket in BucketNames)
            {
                var (bull, bear) = GetBucketScores(breakdown, bucket);
                var dominantScore = isNeutral ? (bull + bear) / 2.0
                    : direction == "bullish" ? bull : bear;
                var weight = DefaultBaseWeights.GetValueOrDefault(bucket, 1.0);
                var contribution = dominantScore * weight;
                bucketContributions[bucket] = contribution;
                totalContribution += contribution;
            }

            // Extract per-bucket observations
            foreach (var bucket in BucketNames)
            {
                var (bull, bear) = GetBucketScores(breakdown, bucket);
                var dominantScore = isNeutral ? (bull + bear) / 2.0
                    : direction == "bullish" ? bull : bear;
                var weight = DefaultBaseWeights.GetValueOrDefault(bucket, 1.0);
                var contribution = bucketContributions[bucket];
                var contributionPct = totalContribution > 0
                    ? Math.Round(contribution / totalContribution * 100, 2) : 0;

                observations.Add(new Dictionary<string, object?>
                {
                    ["prediction_id"] = pred.Id,
                    ["outcome_id"] = outcome.Id,
                    ["signal_name"] = bucket,
                    ["bull_score"] = bull,
                    ["bear_score"] = bear,
                    ["predicted_direction"] = direction,
                    ["correct"] = correct,
                    ["raw_weight"] = weight,
                    ["effective_weight"] = weight,
                    ["weighted_contribution"] = contribution,
                    ["contribution_percent"] = contributionPct,
                    ["actual_return_percent"] = outcome.PercentMove ?? (object?)null,
                    ["confidence"] = (double?)pred.ConfidenceScore,
                    ["outcome_score"] = outcome.OutcomeScore ?? (object?)null,
                    ["market_regime"] = DetectMarketRegime(breakdown),
                    ["profile_id"] = pred.ProfileId ?? (object?)null,
                    ["created_at"] = DateTimeOffset.UtcNow.ToString("o"),
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

    public async Task<(int Updated, List<ResearchSignalPerformance> Stats)> ComputeSignalPerformanceAsync(
        string? profileId = null, bool isChampion = true)
    {
        // Use 180-day window for medium-term stats
        var observations = await _repo.GetSignalObservationsAsync(limit: 5000, windowDays: 180, profileId: profileId);
        if (observations.Count == 0)
            return (0, []);

        // Signal-specific accuracy: weight each observation by how strongly
        // the signal contributed. A signal scoring 0 on a correct prediction
        // gets no credit; a signal scoring 20 on a correct prediction gets full credit.
        // This produces differentiated accuracy per signal instead of the old
        // binary correct/incorrect which was identical across all signals.
        var stats = new Dictionary<(string Signal, string Direction),
            (int Total, double WeightedCorrect, double TotalWeight, double TotalScore, double DecayWeightedSum)>();

        foreach (var obs in observations)
        {
            if (obs.Correct is null) continue;

            // Time-decay: recent observations count more
            var ageInDays = (DateTimeOffset.UtcNow - obs.CreatedAt).TotalDays;
            var decayWeight = Math.Exp(-ageInDays * Math.Log(2) / TimeDecayHalfLifeDays);

            // Signal strength = dominant score (how strongly this signal fired)
            var dominantScore = Math.Max(obs.BullScore, obs.BearScore);
            // Was the signal aligned with the predicted direction?
            var alignedScore = obs.PredictedDirection == "bullish" ? obs.BullScore : obs.BearScore;
            var opposedScore = obs.PredictedDirection == "bullish" ? obs.BearScore : obs.BullScore;
            // Contribution weight: how much this signal pushed toward the predicted direction
            // Positive = aligned, negative = opposed. Clamp to [0, 1] range for weighting.
            var signalWeight = Math.Max(0.1, dominantScore); // min 0.1 so silent signals still count a bit

            void Tally(string direction)
            {
                var key = (obs.SignalName, direction);
                var (total, weightedCorrect, totalWt, totalScore, decaySum) = stats.GetValueOrDefault(key);
                total++;
                totalWt += signalWeight;
                decaySum += decayWeight;
                totalScore += obs.OutcomeScore ?? 50;

                if (obs.Correct == true)
                {
                    // Credit proportional to alignment: signal that pushed right direction gets full credit
                    var alignmentCredit = alignedScore > opposedScore ? signalWeight
                        : alignedScore == opposedScore ? signalWeight * 0.5 // neutral signal
                        : signalWeight * 0.2; // signal pushed wrong way but prediction was still correct
                    weightedCorrect += alignmentCredit;
                }
                else
                {
                    // Wrong prediction: signal that pushed toward the wrong direction gets penalized
                    // Signal that opposed the (wrong) prediction actually showed good judgment
                    if (opposedScore > alignedScore)
                        weightedCorrect += signalWeight * 0.3; // partial credit for opposing a bad call
                }

                stats[key] = (total, weightedCorrect, totalWt, totalScore, decaySum);
            }

            Tally(obs.PredictedDirection);
            Tally("all");
        }

        var results = new List<ResearchSignalPerformance>();
        foreach (var ((signal, direction), (total, weightedCorrect, totalWeight, totalScore, _)) in stats)
        {
            var accuracy = totalWeight > 0 ? weightedCorrect / totalWeight : 0;
            // Clamp to [0, 1]
            accuracy = Math.Clamp(accuracy, 0, 1);

            var perf = new ResearchSignalPerformance
            {
                SignalName = signal,
                SignalType = signal.StartsWith("research") ? "research" : "scoring_bucket",
                Direction = direction,
                TotalPredictions = total,
                CorrectPredictions = (int)Math.Round(accuracy * total), // approximate for display
                Accuracy = accuracy,
                AverageOutcomeScore = total > 0 ? totalScore / total : 0,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            };

            if (isChampion)
            {
                await _repo.UpsertSignalPerformanceAsync(new
                {
                    signal_name = signal,
                    signal_type = perf.SignalType,
                    direction,
                    total_predictions = total,
                    correct_predictions = perf.CorrectPredictions,
                    accuracy = Math.Round(perf.Accuracy, 4),
                    average_outcome_score = Math.Round(perf.AverageOutcomeScore, 2),
                    last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
                });
            }

            results.Add(perf);
        }

        return (results.Count, results);
    }

    // -----------------------------------------------------------------------
    // Stage 2b: Score-Bucket Calibration
    // Instead of binary correct/incorrect per signal, analyze performance
    // by signal strength ranges to learn which scores are predictive.
    // -----------------------------------------------------------------------

    public async Task<int> ComputeSignalCalibrationAsync(string? profileId = null, bool isChampion = true)
    {
        var observations = await _repo.GetSignalObservationsAsync(limit: 5000, windowDays: 180, profileId: profileId);
        if (observations.Count == 0) return 0;

        // Group by signal → score bucket → tally
        var bucketRanges = new[] { (Label: "0-5", Min: 0.0, Max: 5.0), (Label: "6-10", Min: 6.0, Max: 10.0),
            (Label: "11-15", Min: 11.0, Max: 15.0), (Label: "16-20", Min: 16.0, Max: 20.0),
            (Label: "21-25", Min: 21.0, Max: 25.0) };

        var stats = new Dictionary<(string Signal, string Direction, string Bucket),
            (int Total, int Correct, double ReturnSum, double ScoreSum)>();

        foreach (var obs in observations)
        {
            if (obs.Correct is null) continue;
            var netScore = Math.Abs(obs.BullScore - obs.BearScore);
            var dominantScore = Math.Max(obs.BullScore, obs.BearScore);
            var bucketLabel = bucketRanges.FirstOrDefault(b => dominantScore >= b.Min && dominantScore <= b.Max).Label
                ?? "0-5";
            var returnPct = obs.ActualReturnPercent ?? 0;

            void Tally(string direction)
            {
                var key = (obs.SignalName, direction, bucketLabel);
                var (total, correct, retSum, sSum) = stats.GetValueOrDefault(key);
                total++;
                if (obs.Correct == true) correct++;
                retSum += returnPct;
                sSum += obs.OutcomeScore ?? 50;
                stats[key] = (total, correct, retSum, sSum);
            }
            Tally(obs.PredictedDirection);
            Tally("all");
        }

        var upserted = 0;
        foreach (var ((signal, direction, bucket), (total, correct, retSum, scoreSum)) in stats)
        {
            if (isChampion)
            {
                await _repo.UpsertCalibrationBucketAsync(new
                {
                    signal_name = signal,
                    direction,
                    score_bucket = bucket,
                    sample_count = total,
                    correct_count = correct,
                    accuracy = total > 0 ? Math.Round((double)correct / total, 4) : 0,
                    avg_return_percent = total > 0 ? Math.Round(retSum / total, 4) : 0,
                    avg_outcome_score = total > 0 ? Math.Round(scoreSum / total, 2) : 0,
                    last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
                });
            }
            upserted++;
        }

        _logger.LogInformation("[learning-engine] Calibration: upserted {Count} signal-bucket stats", upserted);
        return upserted;
    }

    // -----------------------------------------------------------------------
    // Stage 2c: Signal Correlation Analysis
    // Compute Pearson r between each signal's net score and actual return %.
    // -----------------------------------------------------------------------

    public async Task<int> ComputeSignalCorrelationsAsync(string? profileId = null, bool isChampion = true)
    {
        var observations = await _repo.GetSignalObservationsAsync(limit: 5000, windowDays: 180, profileId: profileId);
        if (observations.Count == 0) return 0;

        // Group by signal+direction, collect (netScore, return) pairs
        var pairs = new Dictionary<(string Signal, string Direction), List<(double NetScore, double Return)>>();

        foreach (var obs in observations)
        {
            if (obs.Correct is null || obs.ActualReturnPercent is null) continue;
            var netScore = obs.BullScore - obs.BearScore;
            if (obs.PredictedDirection == "bearish") netScore = -netScore; // flip so positive = predicted direction

            void Add(string direction)
            {
                var key = (obs.SignalName, direction);
                if (!pairs.ContainsKey(key)) pairs[key] = [];
                pairs[key].Add((netScore, obs.ActualReturnPercent.Value));
            }
            Add(obs.PredictedDirection);
            Add("all");
        }

        var upserted = 0;
        foreach (var ((signal, direction), dataPoints) in pairs)
        {
            if (dataPoints.Count < 5) continue; // need minimum sample
            var r = ComputePearsonR(dataPoints);
            var avgNet = dataPoints.Average(d => d.NetScore);
            var avgRet = dataPoints.Average(d => d.Return);

            if (isChampion)
            {
                await _repo.UpsertSignalCorrelationAsync(new
                {
                    signal_name = signal,
                    direction,
                    correlation_r = Math.Round(r, 4),
                    sample_count = dataPoints.Count,
                    avg_net_score = Math.Round(avgNet, 2),
                    avg_return_percent = Math.Round(avgRet, 4),
                    last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
                });
            }
            upserted++;
        }

        _logger.LogInformation("[learning-engine] Correlations: upserted {Count} signal correlations", upserted);
        return upserted;
    }

    private static double ComputePearsonR(List<(double X, double Y)> data)
    {
        if (data.Count < 3) return 0;
        var n = data.Count;
        var sumX = data.Sum(d => d.X);
        var sumY = data.Sum(d => d.Y);
        var sumXY = data.Sum(d => d.X * d.Y);
        var sumX2 = data.Sum(d => d.X * d.X);
        var sumY2 = data.Sum(d => d.Y * d.Y);

        var numerator = n * sumXY - sumX * sumY;
        var denominator = Math.Sqrt((n * sumX2 - sumX * sumX) * (n * sumY2 - sumY * sumY));
        return denominator == 0 ? 0 : numerator / denominator;
    }

    // -----------------------------------------------------------------------
    // Stage 2d: Counterfactual Influence Analysis
    // For each prediction, replay scoring without each signal to measure
    // how much each signal influenced the decision.
    // -----------------------------------------------------------------------

    public async Task<int> ComputeSignalInfluenceAsync(string? profileId = null, bool isChampion = true)
    {
        var observations = await _repo.GetSignalObservationsAsync(limit: 5000, windowDays: 180, profileId: profileId);
        if (observations.Count == 0) return 0;

        // Group observations by prediction
        var byPrediction = observations
            .Where(o => o.Correct is not null)
            .GroupBy(o => o.PredictionId)
            .Where(g => g.Count() == BucketNames.Length) // only complete sets
            .ToList();

        // Per-signal influence tallies: decisive / reinforcing / redundant
        var influence = new Dictionary<(string Signal, string Direction),
            (int Total, int Decisive, int Reinforcing, int Redundant, double MarginImpactSum,
             int DecisiveCorrect, int DecisiveTotal)>();

        foreach (var predGroup in byPrediction)
        {
            var signalObs = predGroup.ToDictionary(o => o.SignalName);
            var first = predGroup.First();
            var direction = first.PredictedDirection;
            var correct = first.Correct == true;

            // Compute full bull/bear totals
            double fullBull = 0, fullBear = 0;
            foreach (var obs in predGroup)
            {
                fullBull += obs.BullScore;
                fullBear += obs.BearScore;
            }
            var fullMargin = fullBull - fullBear; // positive = bullish wins

            foreach (var bucket in BucketNames)
            {
                if (!signalObs.TryGetValue(bucket, out var obs)) continue;

                // Remove this signal
                var withoutBull = fullBull - obs.BullScore;
                var withoutBear = fullBear - obs.BearScore;
                var withoutMargin = withoutBull - withoutBear;

                // Did removing it flip the prediction?
                var originalSide = fullMargin >= 0 ? "bullish" : "bearish";
                var withoutSide = withoutMargin >= 0 ? "bullish" : "bearish";
                var flipped = originalSide != withoutSide;
                var marginImpact = Math.Abs(fullMargin - withoutMargin);

                // Classify: decisive (flips), reinforcing (>20% margin change), redundant
                string category;
                if (flipped) category = "decisive";
                else if (marginImpact > Math.Abs(fullMargin) * 0.2) category = "reinforcing";
                else category = "redundant";

                void Tally(string dir)
                {
                    var key = (bucket, dir);
                    var (t, d, r, rd, mis, dc, dt) = influence.GetValueOrDefault(key);
                    t++;
                    mis += marginImpact;
                    if (category == "decisive") { d++; dt++; if (correct) dc++; }
                    else if (category == "reinforcing") r++;
                    else rd++;
                    influence[key] = (t, d, r, rd, mis, dc, dt);
                }
                Tally(direction);
                Tally("all");
            }
        }

        var upserted = 0;
        foreach (var ((signal, dir), (total, decisive, reinforcing, redundant, marginSum, decCorrect, decTotal)) in influence)
        {
            if (isChampion)
            {
                await _repo.UpsertSignalInfluenceAsync(new
                {
                    signal_name = signal,
                    direction = dir,
                    total_predictions = total,
                    decisive_count = decisive,
                    reinforcing_count = reinforcing,
                    redundant_count = redundant,
                    avg_margin_impact = total > 0 ? Math.Round(marginSum / total, 2) : 0,
                    decisive_accuracy = decTotal > 0 ? Math.Round((double)decCorrect / decTotal, 4) : (double?)null,
                    last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
                });
            }
            upserted++;
        }

        _logger.LogInformation("[learning-engine] Influence: upserted {Count} signal influence stats", upserted);
        return upserted;
    }

    // -----------------------------------------------------------------------
    // Stage 2e: Signal Interaction Discovery
    // Track pairwise signal combinations and their joint performance.
    // -----------------------------------------------------------------------

    private const double StrongThreshold = 10.0; // dominant score > 10 = "strong"

    public async Task<int> ComputeSignalInteractionsAsync(string? profileId = null, bool isChampion = true)
    {
        var observations = await _repo.GetSignalObservationsAsync(limit: 5000, windowDays: 180, profileId: profileId);
        if (observations.Count == 0) return 0;

        var byPrediction = observations
            .Where(o => o.Correct is not null)
            .GroupBy(o => o.PredictionId)
            .Where(g => g.Count() == BucketNames.Length)
            .ToList();

        // For each pair of signals, track: both-strong, a-strong-b-weak, a-weak-b-strong
        var interactions = new Dictionary<(string A, string B, string Direction),
            (int BothStrong, int BothStrongCorrect, double BothStrongReturnSum,
             int AStrongBWeak, int AStrongBWeakCorrect,
             int AWeakBStrong, int AWeakBStrongCorrect)>();

        foreach (var predGroup in byPrediction)
        {
            var signalObs = predGroup.ToDictionary(o => o.SignalName);
            var first = predGroup.First();
            var direction = first.PredictedDirection;
            var correct = first.Correct == true;
            var returnPct = first.ActualReturnPercent ?? 0;

            for (int i = 0; i < BucketNames.Length; i++)
            {
                for (int j = i + 1; j < BucketNames.Length; j++)
                {
                    var a = BucketNames[i];
                    var b = BucketNames[j];
                    if (!signalObs.TryGetValue(a, out var obsA) || !signalObs.TryGetValue(b, out var obsB))
                        continue;

                    var aStrong = Math.Max(obsA.BullScore, obsA.BearScore) >= StrongThreshold;
                    var bStrong = Math.Max(obsB.BullScore, obsB.BearScore) >= StrongThreshold;

                    void Tally(string dir)
                    {
                        var key = (a, b, dir);
                        var (bs, bsc, bsr, asb, asbc, abs2, absc) = interactions.GetValueOrDefault(key);
                        if (aStrong && bStrong)
                        {
                            bs++; if (correct) bsc++; bsr += returnPct;
                        }
                        else if (aStrong && !bStrong)
                        {
                            asb++; if (correct) asbc++;
                        }
                        else if (!aStrong && bStrong)
                        {
                            abs2++; if (correct) absc++;
                        }
                        interactions[key] = (bs, bsc, bsr, asb, asbc, abs2, absc);
                    }
                    Tally(direction);
                    Tally("all");
                }
            }
        }

        var upserted = 0;
        foreach (var ((a, b, dir), (bs, bsc, bsr, asb, asbc, abs2, absc)) in interactions)
        {
            var bsAcc = bs > 0 ? (double)bsc / bs : 0;
            var asbAcc = asb > 0 ? (double)asbc / asb : 0;
            var absAcc = abs2 > 0 ? (double)absc / abs2 : 0;
            var avgIndividual = (asbAcc + absAcc) / 2.0;
            var synergy = avgIndividual > 0 ? (bsAcc - avgIndividual) / avgIndividual : 0;

            if (isChampion)
            {
                await _repo.UpsertSignalInteractionAsync(new
                {
                    signal_a = a,
                    signal_b = b,
                    direction = dir,
                    both_strong_count = bs,
                    both_strong_accuracy = Math.Round(bsAcc, 4),
                    both_strong_avg_return = bs > 0 ? Math.Round(bsr / bs, 4) : 0,
                    a_strong_b_weak_count = asb,
                    a_strong_b_weak_accuracy = Math.Round(asbAcc, 4),
                    a_weak_b_strong_count = abs2,
                    a_weak_b_strong_accuracy = Math.Round(absAcc, 4),
                    synergy_score = Math.Round(synergy, 4),
                    last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
                });
            }
            upserted++;
        }

        _logger.LogInformation("[learning-engine] Interactions: upserted {Count} signal pair stats", upserted);
        return upserted;
    }

    // -----------------------------------------------------------------------
    // Stage 3: Confidence Calibration
    // -----------------------------------------------------------------------

    public async Task<ConfidenceAnalysis> ComputeConfidenceCalibrationAsync(string? profileId = null)
    {
        var predictions = await _repo.GetRecentPredictionsAsync(500, status: "evaluated", profileId: profileId);
        var outcomeMap = await BuildUnifiedOutcomeMapAsync(predictions, profileId);

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
                    && outcomeMap.ContainsKey(p.Id) && ResolveCorrectness(p, outcomeMap[p.Id]) is not null)
                .ToList();

            if (inBucket.Count < 3) continue;

            var correct = inBucket.Count(p => ResolveCorrectness(p, outcomeMap[p.Id]) == true);
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
    private async Task ApplyCalibrationFactorAsync(ConfidenceAnalysis calibration,
        string? profileId = null, bool isChampion = true)
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

            if (totalWeight < 20) return;

            var avgError = weightedError / totalWeight;
            // avgError < 0 means overconfident → we need factor < 1.0 to dampen
            // avgError > 0 means underconfident → factor > 1.0 to boost

            // Target calibration factor: 1.0 + (error scaled to factor range)
            // Scale: a -0.20 avg error → target factor of ~0.90
            var targetFactor = Math.Clamp(1.0 + (avgError * 0.5), 0.85, 1.15);

            // Get current calibration_factor from overrides
            var currentOverrides = await GetEffectiveOverridesAsync(profileId, isChampion);
            var currentFactor = currentOverrides
                .Where(o => o.SignalName == "calibration_factor")
                .Select(o => o.EffectiveWeight)
                .FirstOrDefault(1.0);

            // Gradual movement: max 1% per day toward target
            var delta = targetFactor - currentFactor;
            var movement = Math.Clamp(delta, -MaxDailyMovement, MaxDailyMovement);
            var newFactor = Math.Round(currentFactor + movement, 4);
            newFactor = Math.Clamp(newFactor, 0.85, 1.15);

            if (Math.Abs(movement) < 0.0005) return;

            // Guardrail gate
            var validation = _guardrail.ValidateCalibrationUpdate(totalWeight, avgError, movement);
            if (!validation.Approved)
            {
                _logger.LogInformation("[learning-engine] Calibration update blocked: {Reason}", validation.Reason);
                return;
            }

            var reason = $"Calibration error: {avgError * 100:F1}% across {totalWeight} predictions. " +
                         $"Target factor: {targetFactor:F4}. " +
                         (calibration.IsOverconfident
                             ? "System is overconfident — dampening confidence scores."
                             : "Calibration adjustment applied.");

            var fullOverride = new ScoringWeightOverride
            {
                SignalName = "calibration_factor",
                BaseWeight = 1.0,
                AdjustmentPercent = newFactor - 1.0,
                EffectiveWeight = newFactor,
                Confidence = Math.Min((double)totalWeight / 200.0, 1.0),
                SampleSize = totalWeight,
                Status = "active",
                Reason = reason,
            };
            await WriteWeightUpdateAsync("calibration_factor", newFactor, fullOverride, profileId, isChampion);

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
    // Stage 3c: Self-Tuning Confidence Caps
    // -----------------------------------------------------------------------
    // The system has hard caps that limit confidence based on risk, conflict,
    // earnings, etc.  These caps sometimes crush confidence on predictions that
    // are actually correct (e.g., risk ≥ 60 caps to 50, but 57% of those were
    // right — better than the 50-65 band).
    //
    // This stage groups resolved predictions by their cap_reason from
    // score_debug_json, measures accuracy per reason, and persists the analysis
    // to cap_tuning_stats.  ScoringEngine reads these stats to dynamically
    // loosen or tighten caps based on observed effectiveness.
    // -----------------------------------------------------------------------

    private async Task<int> ComputeCapEffectivenessAsync(string? profileId = null, bool isChampion = true)
    {
        try
        {
            var predictionsWithOutcomes = await _repo.GetRecentPredictionsWithOutcomesAsync(500, profileId);
            if (predictionsWithOutcomes.Count < 20) return 0;

            // Group by cap reason extracted from score_debug_json
            var capGroups = new Dictionary<string, List<(PredictionWithOutcome pw, ScoringBreakdown? debug)>>();

            foreach (var pw in predictionsWithOutcomes)
            {
                if (pw.Outcome is null || ResolveCorrectness(pw.Prediction, pw.Outcome) is null) continue;

                ScoringBreakdown? debug = null;
                var capReason = "none";

                if (!string.IsNullOrEmpty(pw.Prediction.ScoreDebugJson))
                {
                    debug = ScoringBreakdownEnvelope.Parse(pw.Prediction.ScoreDebugJson);
                    capReason = debug?.ConfidenceCap ?? "none";
                }

                if (!capGroups.ContainsKey(capReason))
                    capGroups[capReason] = new();
                capGroups[capReason].Add((pw, debug));
            }

            var upsertCount = 0;
            foreach (var (reason, items) in capGroups)
            {
                if (items.Count < 5) continue; // need meaningful sample

                var correct = items.Count(i => ResolveCorrectness(i.pw.Prediction, i.pw.Outcome!) == true);
                var accuracy = (double)correct / items.Count;
                var avgConf = (int)items.Average(i => i.pw.Prediction.ConfidenceScore);
                var avgRisk = (int)items
                    .Select(i => i.pw.Prediction.RiskScore)
                    .DefaultIfEmpty(50)
                    .Average();
                var avgMargin = items.Where(i => i.debug?.DecisionMargin > 0)
                    .Select(i => i.debug!.DecisionMargin)
                    .DefaultIfEmpty(0)
                    .Average();

                // Direct calibration error (ChatGPT recommendation):
                // Compare observed win rate directly against the mean predicted
                // confidence for this group, avoiding the moving-target problem of
                // band-based comparisons.  If confidence averages 42% but accuracy
                // is 57%, calibration error = +15% → cap is too aggressive.
                var predictedProb = avgConf / 100.0;
                var calibrationError = accuracy - predictedProb; // positive = underconfident
                var isEffective = calibrationError <= 0.10; // 10% tolerance

                // Recommend a new cap: if ineffective, suggest raising by up to 15
                int? recommendedCap = null;
                int? currentCap = null;
                int capDelta = 0;

                if (!isEffective && reason != "none")
                {
                    currentCap = avgConf;
                    // Boost proportional to calibration error
                    var boost = (int)Math.Round(calibrationError * 30);
                    boost = Math.Clamp(boost, 5, 15);
                    recommendedCap = Math.Min(avgConf + boost, 85);
                    capDelta = boost;
                }

                var notes = isEffective
                    ? $"Cap is effective: accuracy {accuracy:P0} vs predicted {predictedProb:P0} (error {calibrationError:+0.0%;-0.0%})"
                    : $"Cap is INEFFECTIVE: accuracy {accuracy:P0} vs predicted {predictedProb:P0} (error {calibrationError:+0.0%;-0.0%}). " +
                      $"Recommend raising cap by {capDelta} points.";

                if (isChampion)
                {
                    await _repo.UpsertCapTuningStatAsync(new
                    {
                        cap_reason = reason,
                        sample_size = items.Count,
                        accuracy = Math.Round(accuracy, 4),
                        avg_confidence = avgConf,
                        avg_risk = avgRisk,
                        avg_opposition_ratio = Math.Round(avgMargin, 4),
                        recommended_cap = recommendedCap,
                        current_cap = currentCap,
                        cap_delta = capDelta,
                        is_effective = isEffective,
                        analysis_notes = notes,
                        computed_at = DateTimeOffset.UtcNow,
                    });
                }
                upsertCount++;
            }

            // Compute aggregate risk_cap_boost from risk-related caps
            var riskCaps = capGroups
                .Where(g => g.Key.StartsWith("Risk") && g.Value.Count >= 5)
                .ToList();

            if (riskCaps.Count > 0)
            {
                var totalRiskCapped = riskCaps.Sum(g => g.Value.Count);
                var totalRiskCorrect = riskCaps.Sum(g =>
                    g.Value.Count(i => i.pw.Outcome is not null && ResolveCorrectness(i.pw.Prediction, i.pw.Outcome) == true));
                var riskCapAcc = (double)totalRiskCorrect / totalRiskCapped;
                // Direct calibration error: compare observed accuracy vs mean predicted confidence
                var predictedProb = riskCaps.SelectMany(g => g.Value)
                    .Average(i => i.pw.Prediction.ConfidenceScore) / 100.0;
                var calError = riskCapAcc - predictedProb; // positive = underconfident

                // If calibration error > 5% (predictions are underconfident), boost caps.
                // Also allows TIGHTENING: if error < -5%, reduce boost (ChatGPT recommendation).
                var currentOverrides = await GetEffectiveOverridesAsync(profileId, isChampion);
                var currentBoost = currentOverrides
                    .Where(o => o.SignalName == "risk_cap_boost")
                    .Select(o => o.EffectiveWeight)
                    .FirstOrDefault(0.0);

                if (totalRiskCapped >= 10)
                {
                    // Guardrail gate
                    var capValidation = _guardrail.ValidateCapBoostUpdate(
                        totalRiskCapped, calError, 0); // movement checked after compute
                    if (!capValidation.Approved)
                    {
                        _logger.LogInformation("[learning-engine] Cap boost update blocked: {Reason}", capValidation.Reason);
                        return upsertCount;
                    }

                    double targetBoost;
                    if (calError > 0.05)
                    {
                        // Underconfident: boost caps
                        targetBoost = Math.Min(calError * 30, 15);
                    }
                    else if (calError < -0.05)
                    {
                        // Overconfident: tighten caps (reduce boost toward 0)
                        targetBoost = Math.Max(currentBoost + calError * 20, 0);
                    }
                    else
                    {
                        targetBoost = currentBoost; // within tolerance, hold steady
                    }

                    var delta = targetBoost - currentBoost;
                    var movement = Math.Clamp(delta, -2.0, 2.0); // max 2 pts/day
                    var newBoost = Math.Clamp(currentBoost + movement, 0, 15);

                    var capOverride = new ScoringWeightOverride
                    {
                        SignalName = "risk_cap_boost",
                        BaseWeight = 0.0,
                        AdjustmentPercent = newBoost,
                        EffectiveWeight = newBoost,
                        Confidence = Math.Min((double)totalRiskCapped / 100.0, 1.0),
                        SampleSize = totalRiskCapped,
                        Status = "active",
                        Reason = $"Risk-capped: accuracy {riskCapAcc:P0} vs predicted {predictedProb:P0} " +
                                 $"(cal error {calError:+0.0%;-0.0%}, {totalRiskCapped} samples). Boost: {newBoost:F1} pts.",
                    };
                    await WriteWeightUpdateAsync("risk_cap_boost", newBoost, capOverride, profileId, isChampion);

                    _logger.LogInformation(
                        "[learning-engine] Risk cap boost: {Old:F1} → {New:F1} (acc={Acc:P0}, predicted={Pred:P0}, calError={Err:+0.0%;-0.0%})",
                        currentBoost, newBoost, riskCapAcc, predictedProb, calError);
                }
            }

            // --- Risk-specific calibration (ChatGPT recommendation #3) ---
            // Judge risk quality using MAE (max adverse excursion), not directional
            // accuracy.  High risk should correlate with high MAE, low risk with low MAE.
            // If they diverge, the risk model is miscalibrated.
            var riskBuckets = new (string Label, int Min, int Max)[]
            {
                ("risk_low", 0, 40), ("risk_med", 40, 60),
                ("risk_high", 60, 80), ("risk_extreme", 80, 100),
            };

            foreach (var (label, rMin, rMax) in riskBuckets)
            {
                var inBucket = predictionsWithOutcomes
                    .Where(pw => pw.Prediction.RiskScore >= rMin && pw.Prediction.RiskScore < rMax
                        && pw.Outcome?.MaxAdversePercent is not null)
                    .ToList();

                if (inBucket.Count < 5) continue;

                var avgMAE = inBucket.Average(pw => pw.Outcome!.MaxAdversePercent!.Value);
                var avgMFE = inBucket.Where(pw => pw.Outcome?.MaxFavorablePercent is not null)
                    .Select(pw => pw.Outcome!.MaxFavorablePercent!.Value)
                    .DefaultIfEmpty(0)
                    .Average();
                var avgRisk = inBucket.Average(pw => pw.Prediction.RiskScore);

                // Risk-MAE correlation: low risk should have low MAE
                // If risk says "safe" (low) but MAE is high → risk is underestimating danger
                var riskCalibrated = label switch
                {
                    "risk_low" => avgMAE < 3.0,    // low risk should mean < 3% adverse move
                    "risk_med" => avgMAE < 5.0,     // medium risk < 5%
                    "risk_high" => avgMAE < 8.0,    // high risk < 8%
                    _ => true,                       // extreme risk = anything goes
                };

                if (isChampion)
                {
                    await _repo.UpsertCapTuningStatAsync(new
                    {
                        cap_reason = label,
                        sample_size = inBucket.Count,
                        accuracy = Math.Round(avgMAE, 4), // repurpose accuracy field for avg MAE
                        avg_confidence = (int)inBucket.Average(pw => pw.Prediction.ConfidenceScore),
                        avg_risk = (int)avgRisk,
                        avg_opposition_ratio = Math.Round(avgMFE, 4), // repurpose for MFE
                        recommended_cap = (int?)null,
                        current_cap = (int?)null,
                        cap_delta = 0,
                        is_effective = riskCalibrated,
                        analysis_notes = $"Risk {label}: avg MAE {avgMAE:F2}%, avg MFE {avgMFE:F2}%, " +
                            $"avg risk score {avgRisk:F0}, {inBucket.Count} samples. " +
                            $"Risk model {(riskCalibrated ? "CALIBRATED" : "MISCALIBRATED")}.",
                        computed_at = DateTimeOffset.UtcNow,
                    });
                }
                upsertCount++;
            }

            _logger.LogInformation(
                "[learning-engine] Cap effectiveness: analyzed {Count} cap/risk reasons across {Total} predictions",
                upsertCount, predictionsWithOutcomes.Count);

            return upsertCount;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[learning-engine] Failed to compute cap effectiveness");
            return 0;
        }
    }

    // -----------------------------------------------------------------------
    // Stage 4: Weight Optimization (safe, gradual, Bayesian-smoothed)
    // -----------------------------------------------------------------------

    public async Task<(int Adjusted, List<WeightChangeSummary> Changes)> OptimizeWeightsAsync(
        List<ResearchSignalPerformance> signalStats, string? profileId = null, bool isChampion = true)
    {
        var changes = new List<WeightChangeSummary>();
        var currentOverrides = await GetEffectiveOverridesAsync(profileId, isChampion);
        var overrideMap = currentOverrides.ToDictionary(o => o.SignalName);

        // ── Fetch correlation & influence data for multi-signal optimization ──
        var correlations = await _repo.GetSignalCorrelationsAsync();
        var influenceRows = await _repo.GetSignalInfluenceAsync();

        var corrMap = new Dictionary<string, double>();
        foreach (var row in correlations)
        {
            var name = row["signal_name"]?.GetValue<string>() ?? "";
            corrMap[name] = row["correlation_r"]?.GetValue<double>() ?? 0;
        }

        var redundancyMap = new Dictionary<string, double>(); // 0 = all decisive, 1 = all redundant
        foreach (var row in influenceRows)
        {
            var name = row["signal_name"]?.GetValue<string>() ?? "";
            var decisive = row["decisive_count"]?.GetValue<int>() ?? 0;
            var reinforcing = row["reinforcing_count"]?.GetValue<int>() ?? 0;
            var redundant = row["redundant_count"]?.GetValue<int>() ?? 0;
            var total = decisive + reinforcing + redundant;
            redundancyMap[name] = total > 0 ? (double)redundant / total : 0.5;
        }

        var allDirectionStats = signalStats
            .Where(s => s.Direction == "all" && s.TotalPredictions >= MinObservationsForAdjustment)
            .ToList();

        foreach (var stat in allDirectionStats)
        {
            var baseWeight = DefaultBaseWeights.GetValueOrDefault(stat.SignalName, 1.0);
            var currentAdj = overrideMap.TryGetValue(stat.SignalName, out var existing)
                ? existing.AdjustmentPercent : 0.0;

            // ── Multi-factor target: accuracy + correlation + influence ──
            // Bayesian smoothing: blend with prior (50% accuracy)
            var bayesianAccuracy = (stat.CorrectPredictions + 25.0) / (stat.TotalPredictions + 50.0);
            var accuracySignal = (bayesianAccuracy - 0.5) * 2.0; // [-1, 1]

            // Correlation signal: positive r → upweight, negative r → downweight
            var correlation = corrMap.GetValueOrDefault(stat.SignalName, 0);
            var correlationSignal = Math.Clamp(correlation * 3.0, -1.0, 1.0); // amplify: ±0.33 → ±1.0

            // Redundancy penalty: mostly-redundant signals should be downweighted
            var redundancy = redundancyMap.GetValueOrDefault(stat.SignalName, 0.5);
            var redundancyPenalty = redundancy > 0.7 ? -0.15 : 0.0; // penalize if >70% redundant

            // Composite target: 50% accuracy + 35% correlation + redundancy guard
            // When corr/influence data is missing, falls back to accuracy-only at 50% strength
            var targetAdj = accuracySignal * 0.50 + correlationSignal * 0.35 + redundancyPenalty;
            targetAdj = Math.Clamp(targetAdj, -MaxAdjustmentPercent, MaxAdjustmentPercent);

            // Gradual movement — use guardrail-aware daily limit
            var effectiveDailyLimit = await _guardrail.GetEffectiveDailyMovementAsync(stat.SignalName);
            var delta = targetAdj - currentAdj;
            var movement = Math.Clamp(delta, -effectiveDailyLimit, effectiveDailyLimit);
            var newAdj = Math.Round(currentAdj + movement, 4);

            if (Math.Abs(movement) < 0.001) continue;

            // Guardrail gate — refuse update when evidence is insufficient
            var validation = await _guardrail.ValidateSignalWeightUpdateAsync(
                stat.SignalName, stat.TotalPredictions, stat.Accuracy, movement);
            if (!validation.Approved)
            {
                _logger.LogInformation("[learning-engine] Weight update blocked for {Signal}: {Reason}",
                    stat.SignalName, validation.Reason);
                continue;
            }

            var effectiveWeight = baseWeight * (1.0 + newAdj);
            var confidence = Math.Min((double)stat.TotalPredictions / 200.0, 1.0);

            var reason = $"Accuracy: {stat.Accuracy * 100:F1}% ({bayesianAccuracy * 100:F1}% Bayesian), " +
                         $"Corr: {correlation:+0.000;-0.000}, " +
                         $"Redundancy: {redundancy * 100:F0}%. " +
                         $"Target adj: {targetAdj * 100:F1}%.";

            var weightOverride = new ScoringWeightOverride
            {
                SignalName = stat.SignalName,
                BaseWeight = baseWeight,
                AdjustmentPercent = newAdj,
                EffectiveWeight = effectiveWeight,
                Confidence = confidence,
                SampleSize = stat.TotalPredictions,
                Status = "active",
                Reason = reason,
            };
            await WriteWeightUpdateAsync(stat.SignalName, effectiveWeight, weightOverride, profileId, isChampion);

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
    // Stage 4c: Decision Threshold Optimization
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluates whether the decision thresholds (min_edge_margin, min_score_for_direction,
    /// min_ratio_for_direction) should be adjusted based on prediction outcomes.
    ///
    /// The core question: are we making directional calls without enough edge (thresholds too low),
    /// or leaving money on the table by calling things neutral (thresholds too high)?
    ///
    /// Stored in scoring_weight_overrides with base_weight = current default, effective_weight = the
    /// actual threshold value the ScoringEngine reads.
    /// </summary>
    private async Task<(int Adjusted, List<WeightChangeSummary> Changes)> OptimizeDecisionThresholdsAsync(
        string? profileId = null, bool isChampion = true)
    {
        var changes = new List<WeightChangeSummary>();

        try
        {
            var predictions = await _repo.GetRecentPredictionsAsync(500, status: "evaluated", profileId: profileId);
            var outcomeMap = await BuildUnifiedOutcomeMapAsync(predictions, profileId);

            // Split into directional vs neutral predictions that have outcomes
            var directionalWithOutcomes = predictions
                .Where(p => PredictionCategoryHelper.IsDirectional(p.PredictionType)
                    && outcomeMap.ContainsKey(p.Id))
                .Select(p => (Pred: p, Outcome: outcomeMap[p.Id]))
                .ToList();

            var neutralWithOutcomes = predictions
                .Where(p => !PredictionCategoryHelper.IsDirectional(p.PredictionType)
                    && p.PredictionType != PredictionType.unavailable
                    && outcomeMap.ContainsKey(p.Id)
                    && outcomeMap[p.Id].PercentMove is not null)
                .Select(p => (Pred: p, Outcome: outcomeMap[p.Id]))
                .ToList();

            if (directionalWithOutcomes.Count + neutralWithOutcomes.Count < 50)
            {
                _logger.LogInformation(
                    "[learning-engine] Threshold optimization: insufficient data ({Count} predictions), skipping",
                    directionalWithOutcomes.Count + neutralWithOutcomes.Count);
                return (0, changes);
            }

            // Metric 1: Directional accuracy — if low, thresholds may be too loose
            var directionalCorrect = directionalWithOutcomes.Count(x => x.Outcome.DirectionCorrect == true);
            var directionalAccuracy = directionalWithOutcomes.Count > 0
                ? (double)directionalCorrect / directionalWithOutcomes.Count : 0.5;

            // Metric 2: Neutral miss rate — neutrals that moved >2% in either direction
            var neutralMisses = neutralWithOutcomes
                .Count(x => Math.Abs(x.Outcome.PercentMove!.Value) >= NeutralCorrectThreshold);
            var neutralMissRate = neutralWithOutcomes.Count > 0
                ? (double)neutralMisses / neutralWithOutcomes.Count : 0.0;

            // Metric 3: Weak directional predictions — directional calls near the margin
            // that ended up wrong (these would be filtered out by a higher threshold)
            var weakDirectionalWrong = directionalWithOutcomes
                .Where(x => x.Outcome.DirectionCorrect == false)
                .Count(x =>
                {
                    var bull = x.Pred.BullishScore ?? 0;
                    var bear = x.Pred.BearishScore ?? 0;
                    var margin = Math.Abs(bull - bear);
                    // "Weak" = within 4 points of the current edge margin threshold
                    return margin < 14;
                });

            _logger.LogInformation(
                "[learning-engine] Threshold analysis: directional accuracy={Accuracy:P1}, " +
                "neutral miss rate={MissRate:P1}, weak directional wrong={WeakWrong}",
                directionalAccuracy, neutralMissRate, weakDirectionalWrong);

            // --- Compute target adjustment for min_edge_margin ---
            // If directional accuracy < 45% AND there are weak wrong calls, raise the margin.
            // If neutral miss rate > 40%, lower the margin (we're being too conservative).
            var currentOverrides = await GetEffectiveOverridesAsync(profileId, isChampion);
            var overrideMap = currentOverrides.ToDictionary(o => o.SignalName);

            var currentEdgeMargin = overrideMap.TryGetValue("min_edge_margin", out var emOverride)
                ? emOverride.EffectiveWeight : 10.0;

            double targetEdgeMargin = currentEdgeMargin;

            if (directionalAccuracy < 0.45 && weakDirectionalWrong >= 5)
            {
                // Poor accuracy with weak wrong calls → raise margin to filter them
                targetEdgeMargin = Math.Min(currentEdgeMargin + 1.0, 18.0);
            }
            else if (directionalAccuracy > 0.55 && neutralMissRate > 0.35)
            {
                // Good accuracy but missing moves in neutrals → lower margin to catch more
                targetEdgeMargin = Math.Max(currentEdgeMargin - 1.0, 6.0);
            }

            // Gradual movement: cap at 1.0 per cycle
            var edgeDelta = Math.Clamp(targetEdgeMargin - currentEdgeMargin, -1.0, 1.0);
            var newEdgeMargin = Math.Round(currentEdgeMargin + edgeDelta, 1);

            if (Math.Abs(edgeDelta) >= 0.5)
            {
                var reason = $"Directional accuracy: {directionalAccuracy * 100:F1}% ({directionalWithOutcomes.Count} preds). " +
                             $"Neutral miss rate: {neutralMissRate * 100:F1}% ({neutralWithOutcomes.Count} neutrals). " +
                             $"Weak wrong calls: {weakDirectionalWrong}.";

                var edgeOverride = new ScoringWeightOverride
                {
                    SignalName = "min_edge_margin",
                    BaseWeight = 10.0, // original default
                    AdjustmentPercent = (newEdgeMargin - 10.0) / 10.0,
                    EffectiveWeight = newEdgeMargin,
                    Confidence = Math.Min((double)(directionalWithOutcomes.Count + neutralWithOutcomes.Count) / 200.0, 1.0),
                    SampleSize = directionalWithOutcomes.Count + neutralWithOutcomes.Count,
                    Status = "active",
                    Reason = reason,
                };
                await WriteWeightUpdateAsync("min_edge_margin", newEdgeMargin, edgeOverride, profileId, isChampion);

                changes.Add(new WeightChangeSummary
                {
                    SignalName = "min_edge_margin",
                    PreviousWeight = currentEdgeMargin,
                    NewWeight = newEdgeMargin,
                    ChangePercent = edgeDelta / currentEdgeMargin * 100,
                    Reason = reason,
                });

                _logger.LogInformation(
                    "[learning-engine] Threshold update: min_edge_margin {Old} → {New} ({Reason})",
                    currentEdgeMargin, newEdgeMargin, reason);
            }

            // --- min_score_for_direction: keep proportional to edge margin ---
            // Default ratio is 20/10 = 2.0. Maintain that ratio as edge margin moves.
            var currentScoreThreshold = overrideMap.TryGetValue("min_score_for_direction", out var sdOverride)
                ? sdOverride.EffectiveWeight : 20.0;
            var targetScoreThreshold = Math.Round(newEdgeMargin * 2.0, 1);
            var scoreDelta = Math.Clamp(targetScoreThreshold - currentScoreThreshold, -2.0, 2.0);
            var newScoreThreshold = Math.Round(currentScoreThreshold + scoreDelta, 1);

            if (Math.Abs(scoreDelta) >= 1.0)
            {
                var reason = $"Tracking min_edge_margin at 2:1 ratio. Edge margin now {newEdgeMargin}.";

                var scoreOverride = new ScoringWeightOverride
                {
                    SignalName = "min_score_for_direction",
                    BaseWeight = 20.0,
                    AdjustmentPercent = (newScoreThreshold - 20.0) / 20.0,
                    EffectiveWeight = newScoreThreshold,
                    Confidence = Math.Min((double)(directionalWithOutcomes.Count + neutralWithOutcomes.Count) / 200.0, 1.0),
                    SampleSize = directionalWithOutcomes.Count + neutralWithOutcomes.Count,
                    Status = "active",
                    Reason = reason,
                };
                await WriteWeightUpdateAsync("min_score_for_direction", newScoreThreshold, scoreOverride, profileId, isChampion);

                changes.Add(new WeightChangeSummary
                {
                    SignalName = "min_score_for_direction",
                    PreviousWeight = currentScoreThreshold,
                    NewWeight = newScoreThreshold,
                    ChangePercent = scoreDelta / currentScoreThreshold * 100,
                    Reason = reason,
                });
            }

            return (changes.Count, changes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[learning-engine] Failed to optimize decision thresholds");
            return (0, changes);
        }
    }

    // -----------------------------------------------------------------------
    // Stage 5: Setup Analytics — learn complete trade setups
    // -----------------------------------------------------------------------

    /// <summary>
    /// Computes performance statistics for each unique setup fingerprint.
    /// This is the heart of setup-level learning: which COMBINATIONS of
    /// signals consistently produce positive outcomes?
    /// </summary>
    public async Task<int> ComputeSetupPerformanceAsync(string? profileId = null, bool isChampion = true)
    {
        try
        {
            var predictions = await _repo.GetRecentPredictionsAsync(500, status: "evaluated", profileId: profileId);
            var outcomeMap = await BuildUnifiedOutcomeMapAsync(predictions, profileId);

            // Group evaluated predictions by their setup fingerprint
            var setupGroups = new Dictionary<string, List<(PredictionCandidate Pred, PredictionOutcome Outcome, ScoringBreakdown? Breakdown)>>();

            foreach (var pred in predictions)
            {
                if (!outcomeMap.TryGetValue(pred.Id, out var outcome) || ResolveCorrectness(pred, outcome) is null)
                    continue;

                // Parse scoring breakdown to extract signals
                var breakdown = ScoringBreakdownEnvelope.Parse(pred.ScoreDebugJson);
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

                var wins = group.Count(g => ResolveCorrectness(g.Pred, g.Outcome) == true);
                var losses = group.Count - wins;
                var winRate = (double)wins / group.Count;

                var winReturns = group
                    .Where(g => ResolveCorrectness(g.Pred, g.Outcome) == true && g.Outcome.PercentMove.HasValue)
                    .Select(g => Math.Abs(g.Outcome.PercentMove!.Value))
                    .ToList();
                var lossReturns = group
                    .Where(g => ResolveCorrectness(g.Pred, g.Outcome) == false && g.Outcome.PercentMove.HasValue)
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
                    var rWins = regimeGroup.Count(g => ResolveCorrectness(g.Pred, g.Outcome) == true);
                    var rWinRate = (double)rWins / regimeGroup.Count();
                    var rWinReturns = regimeGroup.Where(g => ResolveCorrectness(g.Pred, g.Outcome) == true && g.Outcome.PercentMove.HasValue)
                        .Select(g => Math.Abs(g.Outcome.PercentMove!.Value)).ToList();
                    var rLossReturns = regimeGroup.Where(g => ResolveCorrectness(g.Pred, g.Outcome) == false && g.Outcome.PercentMove.HasValue)
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
                    var recentWinRate = (double)recentGroup.Count(g => ResolveCorrectness(g.Pred, g.Outcome) == true) / recentGroup.Count;
                    if (winRate - recentWinRate > 0.15) isTrusted = false; // degrading
                }

                var avgConfirmation = group
                    .Select(g => ReconstructEvidence(g.Breakdown!).Count(e => e.Value.IsActive))
                    .Average();

                if (isChampion)
                {
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
                }

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

    // -----------------------------------------------------------------------
    // Stage 5b: Supersession Learning
    // For each superseded prediction, build a learning record capturing the
    // before/after state and (when available) the replacement's outcome.
    // -----------------------------------------------------------------------

    public async Task<int> ComputeSupersessionAnalyticsAsync(string? profileId = null, bool isChampion = true)
    {
        try
        {
            var superseded = await _repo.GetSupersededPredictionsAsync(200, profileId);
            if (superseded.Count == 0) return 0;

            // Get evaluated predictions (superseded + their replacements) for unified outcome lookup
            var allPreds = await _repo.GetRecentPredictionsAsync(500, status: "evaluated", profileId: profileId);
            var outcomeMap = await BuildUnifiedOutcomeMapAsync(allPreds, profileId);

            var records = new List<object>();
            var processed = 0;

            foreach (var original in superseded)
            {
                if (string.IsNullOrEmpty(original.SupersededBy)) continue;

                // Skip if we already have a record for this pair
                if (await _repo.HasSupersessionRecordAsync(original.Id, original.SupersededBy))
                    continue;

                var replacement = await _repo.GetPredictionByIdAsync(original.SupersededBy);
                if (replacement is null) continue;

                // Parse scoring breakdowns for context
                var origBreakdown = ScoringBreakdownEnvelope.Parse(original.ScoreDebugJson);
                var replBreakdown = ScoringBreakdownEnvelope.Parse(replacement.ScoreDebugJson);

                var hoursBetween = (replacement.CreatedAt - original.CreatedAt).TotalHours;
                var origRegime = origBreakdown is not null ? DetectMarketRegime(origBreakdown) : null;
                var replRegime = replBreakdown is not null ? DetectMarketRegime(replBreakdown) : null;

                // Outcome of the replacement (if evaluated)
                outcomeMap.TryGetValue(replacement.Id, out var replOutcome);

                var origType = original.PredictionType.ToString();
                var replType = replacement.PredictionType.ToString();

                records.Add(new
                {
                    original_prediction_id = original.Id,
                    replacement_prediction_id = replacement.Id,
                    ticker = original.Ticker,
                    time_window = original.TimeWindow,
                    original_type = origType,
                    replacement_type = replType,
                    transition_label = $"{origType}→{replType}",
                    hours_between = Math.Round(hoursBetween, 2),
                    original_created_at = original.CreatedAt.ToString("o"),
                    replacement_created_at = replacement.CreatedAt.ToString("o"),
                    confidence_delta = replacement.ConfidenceScore - original.ConfidenceScore,
                    risk_delta = replacement.RiskScore - original.RiskScore,
                    bull_score_delta = (replacement.BullishScore ?? 0) - (original.BullishScore ?? 0),
                    bear_score_delta = (replacement.BearishScore ?? 0) - (original.BearishScore ?? 0),
                    original_market_regime = origRegime,
                    replacement_market_regime = replRegime,
                    regime_changed = origRegime != replRegime,
                    original_catalyst_strength = origBreakdown?.CatalystStrength,
                    replacement_catalyst_strength = replBreakdown?.CatalystStrength,
                    replacement_correct = replOutcome?.DirectionCorrect,
                    replacement_return_percent = replOutcome?.PercentMove,
                    replacement_outcome_score = replOutcome?.OutcomeScore,
                    created_at = DateTimeOffset.UtcNow.ToString("o"),
                });
                processed++;

                // Batch insert every 50 (champion only writes to shared tables)
                if (isChampion && records.Count >= 50)
                {
                    await _repo.SaveSupersessionLearningRecordsAsync(records);
                    records.Clear();
                }
            }

            if (isChampion && records.Count > 0)
                await _repo.SaveSupersessionLearningRecordsAsync(records);

            _logger.LogInformation("[learning-engine] Supersession learning: created {Count} records from {Total} superseded predictions",
                processed, superseded.Count);
            return processed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[learning-engine] Supersession learning computation failed");
            return 0;
        }
    }

    /// <summary>
    /// Build aggregated analytics from persisted supersession records.
    /// Called by the dashboard/insights, not by the learning pipeline.
    /// </summary>
    public async Task<SupersessionAnalytics> GetSupersessionAnalyticsAsync()
    {
        var rows = await _repo.GetSupersessionLearningRecordsAsync(500);
        if (rows.Count == 0)
            return new SupersessionAnalytics { Summary = "No supersession data yet." };

        var byTransition = new Dictionary<string, List<SupersessionRow>>();
        var byRegime = new Dictionary<string, List<SupersessionRow>>();
        var byNeutralType = new Dictionary<string, List<SupersessionRow>>();
        var allHours = new List<double>();

        foreach (var r in rows)
        {
            var row = new SupersessionRow
            {
                Label = r["transition_label"]?.ToString() ?? "unknown",
                Hours = GetDouble(r, "hours_between"),
                ConfDelta = (int)GetDouble(r, "confidence_delta"),
                RiskDelta = (int)GetDouble(r, "risk_delta"),
                BullDelta = GetDouble(r, "bull_score_delta"),
                BearDelta = GetDouble(r, "bear_score_delta"),
                Correct = GetNullableBool(r, "replacement_correct"),
                ReturnPct = GetNullableDouble(r, "replacement_return_percent"),
                OriginalType = r["original_type"]?.ToString() ?? "",
                ReplacementType = r["replacement_type"]?.ToString() ?? "",
                OrigRegime = r["original_market_regime"]?.ToString(),
                ReplRegime = r["replacement_market_regime"]?.ToString(),
                RegimeChanged = GetNullableBool(r, "regime_changed") == true,
            };

            allHours.Add(row.Hours);

            if (!byTransition.ContainsKey(row.Label))
                byTransition[row.Label] = [];
            byTransition[row.Label].Add(row);

            // Regime breakdown (use original regime)
            var regime = row.OrigRegime ?? "unknown";
            if (!byRegime.ContainsKey(regime))
                byRegime[regime] = [];
            byRegime[regime].Add(row);

            // Neutral-type breakdown (only for neutral originals)
            if (!PredictionCategoryHelper.IsDirectional(Enum.TryParse<PredictionType>(row.OriginalType, true, out var pt) ? pt : PredictionType.neutral))
            {
                var neutralType = row.OriginalType;
                if (!byNeutralType.ContainsKey(neutralType))
                    byNeutralType[neutralType] = [];
                byNeutralType[neutralType].Add(row);
            }
        }

        // Transition stats
        var transitionStats = new Dictionary<string, TransitionStats>();
        var totalEvaluated = 0;
        var totalCorrect = 0;

        foreach (var (label, items) in byTransition)
        {
            var evaluated = items.Where(i => i.Correct.HasValue).ToList();
            var correct = evaluated.Count(i => i.Correct == true);
            totalEvaluated += evaluated.Count;
            totalCorrect += correct;

            transitionStats[label] = new TransitionStats
            {
                Count = items.Count,
                AvgHoursBetween = Math.Round(items.Average(i => i.Hours), 1),
                AvgConfidenceDelta = Math.Round(items.Average(i => (double)i.ConfDelta), 1),
                AvgRiskDelta = Math.Round(items.Average(i => (double)i.RiskDelta), 1),
                AvgBullScoreDelta = Math.Round(items.Average(i => i.BullDelta), 2),
                AvgBearScoreDelta = Math.Round(items.Average(i => i.BearDelta), 2),
                EvaluatedCount = evaluated.Count,
                CorrectCount = correct,
                Accuracy = evaluated.Count > 0 ? Math.Round((double)correct / evaluated.Count, 4) : 0,
                AvgReturnPercent = evaluated.Count > 0
                    ? Math.Round(evaluated.Where(i => i.ReturnPct.HasValue).Select(i => i.ReturnPct!.Value).DefaultIfEmpty(0).Average(), 4)
                    : 0,
                IsImprovement = evaluated.Count >= 3 && (double)correct / evaluated.Count > 0.5,
            };
        }

        var overallImprovement = totalEvaluated > 0 ? (double)totalCorrect / totalEvaluated : 0;

        // Ranked transitions
        var ranked = transitionStats.Select(t => new RankedTransition
        {
            TransitionLabel = t.Key,
            Count = t.Value.Count,
            Accuracy = t.Value.Accuracy,
            EvaluatedCount = t.Value.EvaluatedCount,
        }).ToList();

        var mostCommon = ranked.OrderByDescending(r => r.Count).ToList();
        var mostSuccessful = ranked.Where(r => r.EvaluatedCount >= 2)
            .OrderByDescending(r => r.Accuracy).ThenByDescending(r => r.Count).ToList();
        var leastSuccessful = ranked.Where(r => r.EvaluatedCount >= 2)
            .OrderBy(r => r.Accuracy).ThenByDescending(r => r.Count).ToList();

        // Regime breakdown
        var regimeStats = byRegime.ToDictionary(
            kvp => kvp.Key,
            kvp =>
            {
                var items = kvp.Value;
                var evaluated = items.Where(i => i.Correct.HasValue).ToList();
                var correct = evaluated.Count(i => i.Correct == true);
                return new RegimeTransitionStats
                {
                    Count = items.Count,
                    EvaluatedCount = evaluated.Count,
                    CorrectCount = correct,
                    Accuracy = evaluated.Count > 0 ? Math.Round((double)correct / evaluated.Count, 4) : 0,
                    AvgHoursBetween = Math.Round(items.Average(i => i.Hours), 1),
                    RegimeChangedDuringTransition = items.Any(i => i.RegimeChanged),
                };
            });

        var regimeCounts = byRegime.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);

        // Neutral-type breakdown
        var neutralStats = byNeutralType.ToDictionary(
            kvp => kvp.Key,
            kvp =>
            {
                var items = kvp.Value;
                var evaluated = items.Where(i => i.Correct.HasValue).ToList();
                var correct = evaluated.Count(i => i.Correct == true);
                var supersededTo = items.GroupBy(i => i.ReplacementType)
                    .ToDictionary(g => g.Key, g => g.Count());
                return new NeutralTypeStats
                {
                    NeutralType = kvp.Key,
                    TimesSuperseded = items.Count,
                    AvgHoursBeforeSupersession = Math.Round(items.Average(i => i.Hours), 1),
                    SupersededTo = supersededTo,
                    EvaluatedCount = evaluated.Count,
                    CorrectCount = correct,
                    ReplacementAccuracy = evaluated.Count > 0 ? Math.Round((double)correct / evaluated.Count, 4) : 0,
                };
            });

        // Timing
        allHours.Sort();
        var median = allHours.Count > 0
            ? allHours[allHours.Count / 2]
            : 0;

        return new SupersessionAnalytics
        {
            TotalSupersessions = rows.Count,
            ByTransition = transitionStats,
            OverallImprovementRate = Math.Round(overallImprovement, 4),
            MostCommonTransitions = mostCommon,
            MostSuccessfulTransitions = mostSuccessful,
            LeastSuccessfulTransitions = leastSuccessful,
            ByMarketRegime = regimeCounts,
            RegimeBreakdown = regimeStats,
            NeutralTypeBreakdown = neutralStats,
            AvgHoursBeforeSupersession = allHours.Count > 0 ? Math.Round(allHours.Average(), 1) : 0,
            MedianHoursBeforeSupersession = Math.Round(median, 1),
            Summary = $"{rows.Count} supersessions across {transitionStats.Count} transition types. " +
                      $"Replacement accuracy: {overallImprovement * 100:F1}% ({totalCorrect}/{totalEvaluated} evaluated). " +
                      $"Avg {(allHours.Count > 0 ? allHours.Average() : 0):F1}h before revision.",
        };
    }

    // Internal row for analytics aggregation
    private class SupersessionRow
    {
        public string Label { get; init; } = "";
        public double Hours { get; init; }
        public int ConfDelta { get; init; }
        public int RiskDelta { get; init; }
        public double BullDelta { get; init; }
        public double BearDelta { get; init; }
        public bool? Correct { get; init; }
        public double? ReturnPct { get; init; }
        public string OriginalType { get; init; } = "";
        public string ReplacementType { get; init; } = "";
        public string? OrigRegime { get; init; }
        public string? ReplRegime { get; init; }
        public bool RegimeChanged { get; init; }
    }

    private async Task<string> BuildSupersessionReportSectionAsync()
    {
        try
        {
            var analytics = await GetSupersessionAnalyticsAsync();
            if (analytics.TotalSupersessions == 0)
                return "  No supersession data yet.";

            var lines = new List<string>
            {
                $"  Total revisions: {analytics.TotalSupersessions}",
                $"  Avg time before revision: {analytics.AvgHoursBeforeSupersession:F1}h (median {analytics.MedianHoursBeforeSupersession:F1}h)",
            };

            // Transition breakdown
            lines.Add("  Transitions:");
            foreach (var (label, stats) in analytics.ByTransition.OrderByDescending(t => t.Value.Count))
            {
                var accStr = stats.EvaluatedCount > 0
                    ? $", accuracy {stats.Accuracy * 100:F0}% ({stats.CorrectCount}/{stats.EvaluatedCount})"
                    : ", not yet evaluated";
                lines.Add($"    {label}: {stats.Count}x, avg {stats.AvgHoursBetween:F1}h gap, conf delta {stats.AvgConfidenceDelta:+0;-0}{accStr}");
            }

            // Top/bottom transitions
            if (analytics.MostSuccessfulTransitions.Count > 0)
            {
                var top = analytics.MostSuccessfulTransitions.First();
                lines.Add($"  Most successful: {top.TransitionLabel} ({top.Accuracy * 100:F0}% accuracy, {top.EvaluatedCount} evaluated)");
            }
            if (analytics.LeastSuccessfulTransitions.Count > 0)
            {
                var bottom = analytics.LeastSuccessfulTransitions.First();
                lines.Add($"  Least successful: {bottom.TransitionLabel} ({bottom.Accuracy * 100:F0}% accuracy, {bottom.EvaluatedCount} evaluated)");
            }

            // Neutral-type breakdown
            if (analytics.NeutralTypeBreakdown.Count > 0)
            {
                lines.Add("  Neutral types superseded:");
                foreach (var (type, stats) in analytics.NeutralTypeBreakdown.OrderByDescending(n => n.Value.TimesSuperseded))
                {
                    var targets = string.Join(", ", stats.SupersededTo.Select(kvp => $"{kvp.Key}×{kvp.Value}"));
                    lines.Add($"    {type}: {stats.TimesSuperseded}x, avg {stats.AvgHoursBeforeSupersession:F1}h → [{targets}]");
                }
            }

            // Regime context
            if (analytics.RegimeBreakdown.Count > 0)
            {
                lines.Add("  By market regime:");
                foreach (var (regime, stats) in analytics.RegimeBreakdown.OrderByDescending(r => r.Value.Count))
                    lines.Add($"    {regime}: {stats.Count}x, accuracy {stats.Accuracy * 100:F0}%");
            }

            lines.Add($"  Overall replacement accuracy: {analytics.OverallImprovementRate * 100:F1}%");
            return string.Join("\n", lines);
        }
        catch
        {
            return "  Supersession analytics unavailable.";
        }
    }

    // -----------------------------------------------------------------------
    // Stage 5c: Volatility Opportunity Learning
    // -----------------------------------------------------------------------

    public async Task<VolatilityOpportunityLearningSummary> ComputeVolatilityOpportunityLearningAsync(
        string? profileId = null, bool isChampion = true)
    {
        var summary = new VolatilityOpportunityLearningSummary();

        try
        {
            var records = await _repo.GetAllVolatilityLearningStatsAsync(limit: 1000, windowDays: 90, profileId: profileId);
            var resolved = records.Where(r => r.DirectionCorrect is not null).ToList();

            summary.TotalRecords = resolved.Count;
            if (resolved.Count < MinObservationsForAdjustment)
            {
                _logger.LogInformation(
                    "[learning-engine] Stage 5c: {Count} resolved records < {Min} minimum, skipping",
                    resolved.Count, MinObservationsForAdjustment);
                return summary;
            }

            // Q1: Opportunity type performance
            var byType = resolved
                .Where(r => !string.IsNullOrEmpty(r.OpportunityType))
                .GroupBy(r => r.OpportunityType!)
                .Where(g => g.Count() >= 10)
                .ToList();

            foreach (var group in byType)
            {
                var correct = group.Count(r => r.DirectionCorrect == true);
                var total = group.Count();
                var winRate = (double)correct / total;
                var successRate = group.Count(r => r.OpportunitySuccess == true) / (double)total;
                var avgScore = group.Where(r => r.OutcomeScore.HasValue).Select(r => r.OutcomeScore!.Value).DefaultIfEmpty(0).Average();
                var avgMfe = group.Where(r => r.MaxFavorableExcursion.HasValue).Select(r => r.MaxFavorableExcursion!.Value).DefaultIfEmpty(0).Average();
                var avgMae = group.Where(r => r.MaxAdverseExcursion.HasValue).Select(r => r.MaxAdverseExcursion!.Value).DefaultIfEmpty(0).Average();

                summary.OpportunityTypeStats.Add(new OpportunityTypePerformance
                {
                    OpportunityType = group.Key,
                    WinRate = winRate,
                    SuccessRate = successRate,
                    SampleSize = total,
                    AvgOutcomeScore = avgScore,
                    AvgMfe = avgMfe,
                    AvgMae = avgMae,
                });
            }

            // Q2: Regime performance
            var byRegime = resolved
                .Where(r => !string.IsNullOrEmpty(r.StockVolatilityRegime))
                .GroupBy(r => r.StockVolatilityRegime!)
                .Where(g => g.Count() >= 10)
                .ToList();

            foreach (var group in byRegime)
            {
                var correct = group.Count(r => r.DirectionCorrect == true);
                var total = group.Count();
                summary.RegimeStats.Add(new RegimePerformance
                {
                    Regime = group.Key,
                    WinRate = (double)correct / total,
                    SampleSize = total,
                    AvgOutcomeScore = group.Where(r => r.OutcomeScore.HasValue).Select(r => r.OutcomeScore!.Value).DefaultIfEmpty(0).Average(),
                });
            }

            // Q3: Cross-learning — opportunity type × regime
            var crossGroups = resolved
                .Where(r => !string.IsNullOrEmpty(r.OpportunityType) && !string.IsNullOrEmpty(r.StockVolatilityRegime))
                .GroupBy(r => (r.OpportunityType!, r.StockVolatilityRegime!))
                .Where(g => g.Count() >= 5)
                .ToList();

            foreach (var group in crossGroups)
            {
                var correct = group.Count(r => r.DirectionCorrect == true);
                var total = group.Count();
                summary.CrossLearning.Add(new CrossLearningEntry
                {
                    OpportunityType = group.Key.Item1,
                    Regime = group.Key.Item2,
                    WinRate = (double)correct / total,
                    SampleSize = total,
                });
            }

            // Q4: ATR percentile learning — bin into quartiles
            var withAtr = resolved.Where(r => r.AtrPercentile.HasValue).ToList();
            if (withAtr.Count >= 20)
            {
                var atrBuckets = new[] { (0.0, 25.0, "0-25"), (25.0, 50.0, "25-50"), (50.0, 75.0, "50-75"), (75.0, 100.0, "75-100") };
                foreach (var (lo, hi, label) in atrBuckets)
                {
                    var bucket = withAtr.Where(r => r.AtrPercentile!.Value >= lo && r.AtrPercentile!.Value < (hi == 100.0 ? 101.0 : hi)).ToList();
                    if (bucket.Count < 5) continue;
                    var correct = bucket.Count(r => r.DirectionCorrect == true);
                    summary.AtrPercentileBuckets.Add(new AtrBucketPerformance
                    {
                        Bucket = label,
                        WinRate = (double)correct / bucket.Count,
                        SampleSize = bucket.Count,
                    });
                }
            }

            // Q5: Gap learning
            var withGap = resolved.Where(r => !string.IsNullOrEmpty(r.GapType) && r.GapType != "None").ToList();
            if (withGap.Count >= 10)
            {
                var gapGroups = withGap.GroupBy(r => r.GapType!).Where(g => g.Count() >= 5);
                foreach (var g in gapGroups)
                {
                    var correct = g.Count(r => r.DirectionCorrect == true);
                    summary.GapStats.Add(new GapPerformance
                    {
                        GapType = g.Key,
                        WinRate = (double)correct / g.Count(),
                        SampleSize = g.Count(),
                        AvgGapPercent = g.Where(r => r.GapPercent.HasValue).Select(r => r.GapPercent!.Value).DefaultIfEmpty(0).Average(),
                    });
                }
            }

            // Q6: Catalyst interaction — catalyst age vs success
            var withCatalyst = resolved.Where(r => r.CatalystAgeHours.HasValue && r.CatalystAgeHours > 0).ToList();
            if (withCatalyst.Count >= 10)
            {
                var catalystBuckets = new[] { (0.0, 4.0, "0-4h"), (4.0, 24.0, "4-24h"), (24.0, 72.0, "1-3d"), (72.0, double.MaxValue, "3d+") };
                foreach (var (lo, hi, label) in catalystBuckets)
                {
                    var bucket = withCatalyst.Where(r => r.CatalystAgeHours!.Value >= lo && r.CatalystAgeHours!.Value < hi).ToList();
                    if (bucket.Count < 3) continue;
                    var correct = bucket.Count(r => r.DirectionCorrect == true);
                    summary.CatalystAgeBuckets.Add(new CatalystAgePerformance
                    {
                        AgeBucket = label,
                        WinRate = (double)correct / bucket.Count,
                        SampleSize = bucket.Count,
                    });
                }
            }

            // Q7: Recovery learning — bounce quality vs outcome
            var withBounce = resolved.Where(r => !string.IsNullOrEmpty(r.BounceQualityRealized)).ToList();
            if (withBounce.Count >= 10)
            {
                var bounceGroups = withBounce.GroupBy(r => r.BounceQualityRealized!).Where(g => g.Count() >= 3);
                foreach (var g in bounceGroups)
                {
                    var correct = g.Count(r => r.DirectionCorrect == true);
                    summary.RecoveryStats.Add(new RecoveryPerformance
                    {
                        BounceQuality = g.Key,
                        WinRate = (double)correct / g.Count(),
                        SampleSize = g.Count(),
                        AvgRecoverySpeed = g.Where(r => r.RecoverySpeed.HasValue).Select(r => r.RecoverySpeed!.Value).DefaultIfEmpty(0).Average(),
                    });
                }
            }

            // Weight recommendations — adjust volatility bucket based on opportunity success rates
            var weightChanges = await ApplyVolatilityWeightRecommendationsAsync(resolved, summary, profileId, isChampion);
            summary.WeightChanges = weightChanges;

            _logger.LogInformation(
                "[learning-engine] Stage 5c: {Count} records, {Types} opportunity types, {Regimes} regimes, {Changes} weight changes",
                resolved.Count, summary.OpportunityTypeStats.Count, summary.RegimeStats.Count, weightChanges.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[learning-engine] Stage 5c failed");
        }

        return summary;
    }

    private async Task<List<WeightChangeSummary>> ApplyVolatilityWeightRecommendationsAsync(
        List<VolatilityLearningRecord> resolved, VolatilityOpportunityLearningSummary summary,
        string? profileId = null, bool isChampion = true)
    {
        var changes = new List<WeightChangeSummary>();

        // Only adjust if we have enough data across opportunity types
        var typesWithEnoughData = summary.OpportunityTypeStats
            .Where(t => t.SampleSize >= MinObservationsForAdjustment)
            .ToList();
        if (typesWithEnoughData.Count == 0) return changes;

        // Compute overall VOE-tagged success rate vs baseline
        var voeTagged = resolved.Where(r => !string.IsNullOrEmpty(r.OpportunityType) && r.OpportunityType != "None").ToList();
        var noVoe = resolved.Where(r => string.IsNullOrEmpty(r.OpportunityType) || r.OpportunityType == "None").ToList();

        if (voeTagged.Count < MinObservationsForAdjustment) return changes;

        var voeWinRate = (double)voeTagged.Count(r => r.DirectionCorrect == true) / voeTagged.Count;
        var baselineWinRate = noVoe.Count >= 10
            ? (double)noVoe.Count(r => r.DirectionCorrect == true) / noVoe.Count
            : 0.5;

        // Bayesian-smoothed adjustment: if VOE-tagged predictions outperform baseline, boost volatility weight
        var smoothed = (voeTagged.Count(r => r.DirectionCorrect == true) + 25.0) / (voeTagged.Count + 50.0);
        var diff = smoothed - 0.5;
        var proposedMovement = Math.Clamp(diff * 0.1, -MaxDailyMovement, MaxDailyMovement);

        if (Math.Abs(proposedMovement) < 0.001) return changes;

        const string signal = "volatility";
        var validation = await _guardrail.ValidateSignalWeightUpdateAsync(
            signal, voeTagged.Count, voeWinRate, proposedMovement);

        if (!validation.Approved)
        {
            _logger.LogInformation("[learning-engine] Stage 5c weight update blocked: {Reason}", validation.Reason);
            return changes;
        }

        var currentOverrides = await GetEffectiveOverridesAsync(profileId, isChampion);
        var existing = currentOverrides.FirstOrDefault(o => o.SignalName == signal);
        var baseWeight = DefaultBaseWeights.GetValueOrDefault(signal, 0.7);
        var currentAdj = existing?.AdjustmentPercent ?? 0.0;

        var effectiveMovement = await _guardrail.GetEffectiveDailyMovementAsync(signal);
        var clampedMovement = Math.Clamp(proposedMovement, -effectiveMovement, effectiveMovement);
        var newAdj = Math.Clamp(currentAdj + clampedMovement, -MaxAdjustmentPercent, MaxAdjustmentPercent);

        if (Math.Abs(clampedMovement) < 0.001) return changes;

        var effectiveWeight = baseWeight * (1.0 + newAdj);

        var volOverride = new ScoringWeightOverride
        {
            SignalName = signal,
            BaseWeight = baseWeight,
            AdjustmentPercent = newAdj,
            EffectiveWeight = effectiveWeight,
            Confidence = Math.Min((double)voeTagged.Count / 200, 1.0),
            SampleSize = voeTagged.Count,
            Status = "active",
            Reason = $"VOE Stage 5c: {voeTagged.Count} tagged predictions, win rate {voeWinRate:P0} vs baseline {baselineWinRate:P0}",
        };
        await WriteWeightUpdateAsync(signal, effectiveWeight, volOverride, profileId, isChampion);

        changes.Add(new WeightChangeSummary
        {
            SignalName = signal,
            PreviousWeight = baseWeight * (1.0 + currentAdj),
            NewWeight = effectiveWeight,
            ChangePercent = clampedMovement * 100,
            Reason = $"[voe-learning] VOE win rate {voeWinRate:P0} (n={voeTagged.Count})",
        });

        return changes;
    }

    private string BuildVolatilityOpportunitySummarySection(VolatilityOpportunityLearningSummary summary)
    {
        if (summary.TotalRecords < MinObservationsForAdjustment)
            return "  Insufficient data for volatility opportunity analysis.";

        var lines = new List<string>();

        if (summary.OpportunityTypeStats.Count > 0)
        {
            lines.Add("  By opportunity type:");
            foreach (var t in summary.OpportunityTypeStats.OrderByDescending(t => t.WinRate))
                lines.Add($"    {t.OpportunityType}: {t.WinRate * 100:F0}% win, {t.SuccessRate * 100:F0}% success (n={t.SampleSize}, MFE {t.AvgMfe:F1}%, MAE {t.AvgMae:F1}%)");
        }

        if (summary.RegimeStats.Count > 0)
        {
            lines.Add("  By volatility regime:");
            foreach (var r in summary.RegimeStats.OrderByDescending(r => r.WinRate))
                lines.Add($"    {r.Regime}: {r.WinRate * 100:F0}% win (n={r.SampleSize})");
        }

        if (summary.CrossLearning.Count > 0)
        {
            var best = summary.CrossLearning.OrderByDescending(c => c.WinRate).Take(3);
            lines.Add("  Best opportunity×regime combos:");
            foreach (var c in best)
                lines.Add($"    {c.OpportunityType} in {c.Regime}: {c.WinRate * 100:F0}% (n={c.SampleSize})");
        }

        if (summary.WeightChanges.Count > 0)
        {
            lines.Add("  Weight adjustments:");
            foreach (var w in summary.WeightChanges)
                lines.Add($"    {w.SignalName}: {w.ChangePercent:+0.0;-0.0}% → {w.NewWeight:F3}");
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "  No actionable patterns yet.";
    }

    // -----------------------------------------------------------------------
    // Stage 5c Models
    // -----------------------------------------------------------------------

    public record VolatilityOpportunityLearningSummary
    {
        public int TotalRecords { get; set; }
        public List<OpportunityTypePerformance> OpportunityTypeStats { get; init; } = [];
        public List<RegimePerformance> RegimeStats { get; init; } = [];
        public List<CrossLearningEntry> CrossLearning { get; init; } = [];
        public List<AtrBucketPerformance> AtrPercentileBuckets { get; init; } = [];
        public List<GapPerformance> GapStats { get; init; } = [];
        public List<CatalystAgePerformance> CatalystAgeBuckets { get; init; } = [];
        public List<RecoveryPerformance> RecoveryStats { get; init; } = [];
        public List<WeightChangeSummary> WeightChanges { get; set; } = [];
    }

    public record OpportunityTypePerformance
    {
        public string OpportunityType { get; init; } = "";
        public double WinRate { get; init; }
        public double SuccessRate { get; init; }
        public int SampleSize { get; init; }
        public double AvgOutcomeScore { get; init; }
        public double AvgMfe { get; init; }
        public double AvgMae { get; init; }
    }

    public record RegimePerformance
    {
        public string Regime { get; init; } = "";
        public double WinRate { get; init; }
        public int SampleSize { get; init; }
        public double AvgOutcomeScore { get; init; }
        public double ExpectedValuePercent { get; init; }
    }

    public record CrossLearningEntry
    {
        public string OpportunityType { get; init; } = "";
        public string Regime { get; init; } = "";
        public double WinRate { get; init; }
        public int SampleSize { get; init; }
    }

    public record AtrBucketPerformance
    {
        public string Bucket { get; init; } = "";
        public double WinRate { get; init; }
        public int SampleSize { get; init; }
    }

    public record GapPerformance
    {
        public string GapType { get; init; } = "";
        public double WinRate { get; init; }
        public int SampleSize { get; init; }
        public double AvgGapPercent { get; init; }
    }

    public record CatalystAgePerformance
    {
        public string AgeBucket { get; init; } = "";
        public double WinRate { get; init; }
        public int SampleSize { get; init; }
    }

    public record RecoveryPerformance
    {
        public string BounceQuality { get; init; } = "";
        public double WinRate { get; init; }
        public int SampleSize { get; init; }
        public double AvgRecoverySpeed { get; init; }
    }

    private static double GetDouble(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return 0;
        if (node is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), out var parsed) ? parsed : 0;
    }

    private static double? GetNullableDouble(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return null;
        if (node is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), out var parsed) ? parsed : null;
    }

    private static bool? GetNullableBool(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return null;
        if (node is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        return bool.TryParse(node.ToString(), out var parsed) ? parsed : null;
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

    public async Task<List<PatternRecommendation>> ProducePatternRecommendationsAsync(string? profileId = null)
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
                // With 5% daily movement cap, allow meaningful adjustments per cycle
                var multiplier = regime == "overconfidence"
                    ? -0.05 // tighten confidence cap by 5%
                    : -0.05 * Math.Min(failureRate * 2, 1.0); // up to 5% penalty, scaled by failure concentration

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
            // Scale adjustment proportionally to synergy strength and evidence.
            // With ≥20 co-occurrences and strong synergy, allow up to the full daily movement cap.
            foreach (var combo in combos.BestCombinations.Where(c => c.SynergyScore > 3 && c.CoOccurrences >= 8))
            {
                var evidenceScale = Math.Min((double)combo.CoOccurrences / 20, 1.0);
                var boost = combo.SynergyScore / 100.0 * evidenceScale; // proportional to synergy %
                boost = Math.Min(boost, MaxDailyMovement); // respect daily cap
                foreach (var signal in new[] { combo.Signal1, combo.Signal2 })
                {
                    recommendations.Add(new PatternRecommendation
                    {
                        Type = "synergy_weight",
                        SignalName = signal,
                        RecommendedAdjustment = Math.Round(boost, 4),
                        Confidence = Math.Min((double)combo.CoOccurrences / 20, 1.0),
                        Evidence = combo.CoOccurrences,
                        Reason = $"Synergy: {combo.Signal1}+{combo.Signal2} joint accuracy {combo.JointAccuracy:F1}% ({combo.SynergyScore:+0.0}% synergy, n={combo.CoOccurrences})",
                    });
                }
            }

            foreach (var combo in combos.WorstCombinations.Where(c => c.SynergyScore < -3 && c.CoOccurrences >= 8))
            {
                var evidenceScale = Math.Min((double)combo.CoOccurrences / 20, 1.0);
                var penalty = combo.SynergyScore / 100.0 * evidenceScale; // negative = penalty
                penalty = Math.Max(penalty, -MaxDailyMovement); // respect daily cap
                foreach (var signal in new[] { combo.Signal1, combo.Signal2 })
                {
                    recommendations.Add(new PatternRecommendation
                    {
                        Type = "synergy_weight",
                        SignalName = signal,
                        RecommendedAdjustment = Math.Round(penalty, 4),
                        Confidence = Math.Min((double)combo.CoOccurrences / 20, 1.0),
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
        List<PatternRecommendation> recommendations, string? profileId = null, bool isChampion = true)
    {
        var changes = new List<WeightChangeSummary>();
        if (recommendations.Count == 0) return (0, changes);

        var currentOverrides = await GetEffectiveOverridesAsync(profileId, isChampion);
        var overrideMap = currentOverrides.ToDictionary(o => o.SignalName);

        // Group synergy recommendations by signal and average them
        var synergyBySignal = recommendations
            .Where(r => r.Type == "synergy_weight" && BucketNames.Contains(r.SignalName))
            .GroupBy(r => r.SignalName)
            .ToDictionary(g => g.Key, g => g.Average(r => r.RecommendedAdjustment));

        foreach (var (signal, avgAdjustment) in synergyBySignal)
        {
            // Guardrail gate — validate pattern evidence before applying
            var patternEvidence = recommendations
                .Where(r => r.Type == "synergy_weight" && r.SignalName == signal)
                .Sum(r => r.Evidence);
            var patternConfidence = recommendations
                .Where(r => r.Type == "synergy_weight" && r.SignalName == signal)
                .Average(r => r.Confidence);
            var patternValidation = _guardrail.ValidatePatternRecommendation(
                signal, patternEvidence, patternConfidence, avgAdjustment);
            if (!patternValidation.Approved)
            {
                _logger.LogInformation("[learning-engine] Pattern adjustment blocked for {Signal}: {Reason}",
                    signal, patternValidation.Reason);
                continue;
            }

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

            var synergyOverride = new ScoringWeightOverride
            {
                SignalName = signal,
                BaseWeight = baseWeight,
                AdjustmentPercent = newAdj,
                EffectiveWeight = effectiveWeight,
                Confidence = Math.Min(recommendations.Where(r => r.SignalName == signal).Average(r => r.Confidence), 1.0),
                SampleSize = recommendations.Where(r => r.SignalName == signal).Sum(r => r.Evidence),
                Status = "active",
                Reason = $"Synergy adjustment: {string.Join("; ", reasons.Take(2))}",
            };
            await WriteWeightUpdateAsync(signal, effectiveWeight, synergyOverride, profileId, isChampion);

            changes.Add(new WeightChangeSummary
            {
                SignalName = signal,
                PreviousWeight = baseWeight * (1.0 + currentAdj),
                NewWeight = effectiveWeight,
                ChangePercent = movement * 100,
                Reason = $"[pattern-detection] {string.Join("; ", reasons.Take(2))}",
            });
        }

        // Apply regime recommendations as actual weight overrides that ConfidenceEngine reads
        var regimeRecs = recommendations.Where(r => r.Type == "regime_confidence_cap").ToList();
        foreach (var rec in regimeRecs)
        {
            // Map regime names to weight keys that ConfidenceEngine reads
            var weightKey = rec.SignalName switch
            {
                "bull_market" => "regime_bull_penalty",
                "bear_market" => "regime_bear_penalty",
                "overconfidence" => "regime_overconfidence_penalty",
                _ => "regime_sideways_penalty", // sideways or unknown
            };

            var currentPenalty = overrideMap.TryGetValue(weightKey, out var existing)
                ? existing.EffectiveWeight : 1.0;

            // Regime penalty moves toward 1.0 + adjustment (which is negative, so < 1.0)
            var targetPenalty = Math.Clamp(1.0 + rec.RecommendedAdjustment, 0.70, 1.0);
            var movement = Math.Clamp(targetPenalty - currentPenalty, -MaxDailyMovement, MaxDailyMovement);
            var newPenalty = Math.Clamp(currentPenalty + movement, 0.70, 1.0);

            if (Math.Abs(movement) < 0.001) continue;

            var regimeOverride = new ScoringWeightOverride
            {
                SignalName = weightKey,
                BaseWeight = 1.0,
                AdjustmentPercent = newPenalty - 1.0,
                EffectiveWeight = newPenalty,
                Confidence = rec.Confidence,
                SampleSize = rec.Evidence,
                Status = "active",
                Reason = $"[pattern-detection] {rec.Reason}",
            };
            await WriteWeightUpdateAsync(weightKey, newPenalty, regimeOverride, profileId, isChampion);

            changes.Add(new WeightChangeSummary
            {
                SignalName = weightKey,
                PreviousWeight = currentPenalty,
                NewWeight = newPenalty,
                ChangePercent = movement * 100,
                Reason = $"[pattern-detection] Regime penalty: {rec.Reason}",
            });

            _logger.LogInformation(
                "[learning-engine] Applied regime penalty {Key}: {Prev:F3} → {New:F3} ({Movement:+0.0;-0.0}%)",
                weightKey, currentPenalty, newPenalty, movement * 100);
        }

        // Also save as insights for visibility in the learning report
        if (regimeRecs.Count > 0 && isChampion)
        {
            var insights = regimeRecs.Select(r => new
            {
                insight_type = "pattern_detection",
                summary = r.Reason,
                evidence = $"{r.Evidence} failures in cluster. Applied confidence penalty: {r.RecommendedAdjustment * 100:F1}%.",
                action_recommendation = "Regime-aware confidence penalty written to scoring weights.",
                confidence = r.Confidence,
            }).Cast<object>().ToList();

            await _repo.SaveLearningInsightsAsync(insights);
        }

        return (changes.Count, changes);
    }

    // -----------------------------------------------------------------------
    // Stage 5d: Risk Management Learning
    // Learn from stop-loss, take-profit, and trailing-stop exits to improve
    // prediction quality and risk threshold calibration.
    // -----------------------------------------------------------------------

    public async Task<RiskLearningSummary> ComputeRiskManagementLearningAsync(string? profileId = null, bool isChampion = true)
    {
        var summary = new RiskLearningSummary();

        try
        {
            var riskCloses = await _portfolioRepo.GetRecentRiskManagedClosesAsync(200);
            if (riskCloses.Count == 0) return summary;

            summary.TotalEvents = riskCloses.Count;

            // Classify each close by risk event type
            foreach (var pos in riskCloses)
            {
                var reason = pos.ReasonExited ?? "";
                var eventType = reason.StartsWith("STOP-LOSS") ? "stop_loss"
                    : reason.StartsWith("TAKE-PROFIT") ? "take_profit"
                    : reason.StartsWith("TRAILING-STOP") ? "trailing_stop"
                    : "unknown";

                var pnlPct = pos.EntryPrice > 0
                    ? ((pos.ExitPrice ?? pos.EntryPrice) - pos.EntryPrice) / pos.EntryPrice * 100
                    : 0;
                var isProfitable = pnlPct > 0;

                summary.EventsByType[eventType] = summary.EventsByType.GetValueOrDefault(eventType) + 1;
                summary.PnlByType[eventType] = summary.PnlByType.GetValueOrDefault(eventType) + pnlPct;
                summary.TickerCounts[pos.Ticker] = summary.TickerCounts.GetValueOrDefault(pos.Ticker) + 1;
            }

            // Look up timeframes via prediction_id → paper_stock_candidate
            var predictionIds = riskCloses
                .Where(p => p.PredictionId is not null)
                .Select(p => p.PredictionId!)
                .Distinct().ToList();
            var candidateMap = predictionIds.Count > 0
                ? await _candidateRepo.GetCandidateMapByPredictionIdsAsync(predictionIds)
                : new Dictionary<string, PaperStockCandidate>();

            // Group by timeframe tier
            foreach (var pos in riskCloses)
            {
                var timeframe = StockTimeframe.one_day;
                if (pos.PredictionId is not null && candidateMap.TryGetValue(pos.PredictionId, out var cand))
                    timeframe = cand.Timeframe;

                var tier = timeframe switch
                {
                    StockTimeframe.one_day => "day",
                    StockTimeframe.two_day or StockTimeframe.one_week => "swing",
                    _ => "longterm",
                };

                var reason = pos.ReasonExited ?? "";
                var eventType = reason.StartsWith("STOP-LOSS") ? "stop_loss"
                    : reason.StartsWith("TAKE-PROFIT") ? "take_profit"
                    : reason.StartsWith("TRAILING-STOP") ? "trailing_stop"
                    : "unknown";

                var tierKey = $"{tier}_{eventType}";
                summary.EventsByTierAndType[tierKey] = summary.EventsByTierAndType.GetValueOrDefault(tierKey) + 1;
            }

            // Upsert into stock_learning_stats (champion only)
            if (isChampion)
            {
                // By event type
                foreach (var (eventType, count) in summary.EventsByType)
                {
                    var avgPnl = count > 0 ? summary.PnlByType.GetValueOrDefault(eventType) / count : 0;
                    var profitable = eventType != "stop_loss"; // take_profit and trailing_stop are "correct"
                    var correctCount = profitable ? count : 0;

                    await _candidateRepo.UpsertLearningStatAsync(
                        "risk_event_type", eventType,
                        profitable, avgPnl, count > 0 ? 50.0 : 0);
                }

                // By ticker (top stopped-out tickers)
                foreach (var (ticker, count) in summary.TickerCounts.Where(t => t.Value >= 2))
                {
                    var tickerCloses = riskCloses.Where(p => p.Ticker == ticker).ToList();
                    var stopLosses = tickerCloses.Count(p => (p.ReasonExited ?? "").StartsWith("STOP-LOSS"));
                    var profitable = tickerCloses.Count(p =>
                        p.ExitPrice.HasValue && p.ExitPrice.Value > p.EntryPrice);
                    var avgPnl = tickerCloses
                        .Where(p => p.ExitPrice.HasValue && p.EntryPrice > 0)
                        .Select(p => (p.ExitPrice!.Value - p.EntryPrice) / p.EntryPrice * 100)
                        .DefaultIfEmpty(0).Average();

                    await _candidateRepo.UpsertLearningStatAsync(
                        "risk_event_ticker", ticker,
                        profitable > stopLosses, avgPnl, count);
                }

                // By timeframe tier
                foreach (var (tierKey, count) in summary.EventsByTierAndType)
                {
                    var isProfitable = !tierKey.Contains("stop_loss");
                    await _candidateRepo.UpsertLearningStatAsync(
                        "risk_event_timeframe", tierKey,
                        isProfitable, 0, count);
                }
            }

            // Write learned risk thresholds to scoring_weight_overrides so
            // PortfolioLifecycleService picks them up via GetActiveWeightOverridesAsync
            if (summary.TotalEvents >= 10)
            {
                await LearnRiskThresholdsAsync(riskCloses, candidateMap, profileId, isChampion);
            }

            _logger.LogInformation(
                "[learning-engine] Risk management learning: {Total} events — " +
                "{SL} stop-losses, {TP} take-profits, {TS} trailing-stops",
                summary.TotalEvents,
                summary.EventsByType.GetValueOrDefault("stop_loss"),
                summary.EventsByType.GetValueOrDefault("take_profit"),
                summary.EventsByType.GetValueOrDefault("trailing_stop"));

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[learning-engine] Risk management learning failed");
            return summary;
        }
    }

    /// <summary>
    /// Computes optimal stop-loss/take-profit thresholds from closed position data
    /// and writes them to scoring_weight_overrides for PortfolioLifecycleService to consume.
    /// </summary>
    private async Task LearnRiskThresholdsAsync(
        List<PortfolioPosition> riskCloses,
        Dictionary<string, PaperStockCandidate> candidateMap,
        string? profileId, bool isChampion)
    {
        // Group positions by tier (day/swing/longterm)
        var tiers = new Dictionary<string, List<(double PnlPct, string Reason)>>();
        foreach (var pos in riskCloses)
        {
            if (pos.EntryPrice <= 0) continue;
            var timeframe = StockTimeframe.one_day;
            if (pos.PredictionId is not null && candidateMap.TryGetValue(pos.PredictionId, out var cand))
                timeframe = cand.Timeframe;
            var tier = timeframe switch
            {
                StockTimeframe.one_day => "day",
                StockTimeframe.two_day or StockTimeframe.one_week => "swing",
                _ => "longterm",
            };
            var pnlPct = ((pos.ExitPrice ?? pos.EntryPrice) - pos.EntryPrice) / pos.EntryPrice;
            var reason = pos.ReasonExited ?? "";
            if (!tiers.ContainsKey(tier)) tiers[tier] = [];
            tiers[tier].Add((pnlPct, reason));
        }

        // For each tier with enough data, compute and write thresholds
        foreach (var (tier, positions) in tiers)
        {
            if (positions.Count < 5) continue;

            var stopLossExits = positions.Where(p => p.Reason.StartsWith("STOP-LOSS")).ToList();
            var takeProfitExits = positions.Where(p => p.Reason.StartsWith("TAKE-PROFIT")).ToList();

            // Stop-loss: use median loss magnitude, clamped to [0.02, 0.15]
            if (stopLossExits.Count >= 3)
            {
                var losses = stopLossExits.Select(p => Math.Abs(p.PnlPct)).OrderBy(x => x).ToList();
                var medianLoss = losses[losses.Count / 2];
                var newSl = Math.Clamp(medianLoss * 1.1, 0.02, 0.15); // 10% wider than median
                var slKey = $"risk_sl_{tier}";

                var slOverride = new ScoringWeightOverride
                {
                    SignalName = slKey,
                    BaseWeight = tier == "day" ? 0.05 : tier == "swing" ? 0.08 : 0.10,
                    AdjustmentPercent = 0,
                    EffectiveWeight = newSl,
                    Confidence = Math.Min(stopLossExits.Count / 30.0, 1.0),
                    SampleSize = stopLossExits.Count,
                    Status = "active",
                    Reason = $"Learned from {stopLossExits.Count} stop-loss exits (median loss {medianLoss:P1})",
                };
                await WriteWeightUpdateAsync(slKey, newSl, slOverride, profileId, isChampion);
            }

            // Take-profit: use median gain magnitude, clamped to [0.03, 0.25]
            if (takeProfitExits.Count >= 3)
            {
                var gains = takeProfitExits.Select(p => Math.Abs(p.PnlPct)).OrderBy(x => x).ToList();
                var medianGain = gains[gains.Count / 2];
                var newTp = Math.Clamp(medianGain * 0.9, 0.03, 0.25); // 10% tighter than median
                var tpKey = $"risk_tp_{tier}";

                var tpOverride = new ScoringWeightOverride
                {
                    SignalName = tpKey,
                    BaseWeight = tier == "day" ? 0.08 : tier == "swing" ? 0.15 : 0.20,
                    AdjustmentPercent = 0,
                    EffectiveWeight = newTp,
                    Confidence = Math.Min(takeProfitExits.Count / 30.0, 1.0),
                    SampleSize = takeProfitExits.Count,
                    Status = "active",
                    Reason = $"Learned from {takeProfitExits.Count} take-profit exits (median gain {medianGain:P1})",
                };
                await WriteWeightUpdateAsync(tpKey, newTp, tpOverride, profileId, isChampion);
            }

            _logger.LogInformation(
                "[learning-engine] Risk thresholds updated for tier={Tier}: {SL} SL samples, {TP} TP samples",
                tier, stopLossExits.Count, takeProfitExits.Count);
        }
    }

    /// <summary>Builds a text section for the AI learning report from risk management data.</summary>
    private string BuildRiskManagementReportSection(RiskLearningSummary summary)
    {
        if (summary.TotalEvents == 0)
            return "  No risk management events recorded yet.";

        var lines = new List<string>();

        foreach (var (eventType, count) in summary.EventsByType.OrderByDescending(x => x.Value))
        {
            var avgPnl = count > 0 ? summary.PnlByType.GetValueOrDefault(eventType) / count : 0;
            var label = eventType.Replace("_", "-").ToUpperInvariant();
            lines.Add($"  {label}: {count} events, avg P&L: {avgPnl:+0.00;-0.00}%");
        }

        // Timeframe tier breakdown
        if (summary.EventsByTierAndType.Count > 0)
        {
            lines.Add("  By timeframe tier:");
            foreach (var (tierKey, count) in summary.EventsByTierAndType.OrderByDescending(x => x.Value))
                lines.Add($"    {tierKey}: {count}");
        }

        // Most stopped-out tickers
        var topStopped = summary.TickerCounts
            .OrderByDescending(t => t.Value).Take(5)
            .Select(t => $"{t.Key} ({t.Value})");
        if (topStopped.Any())
            lines.Add($"  Most risk-closed tickers: {string.Join(", ", topStopped)}");

        return string.Join("\n", lines);
    }

    // -----------------------------------------------------------------------
    // Stage 6: AI-Summarized Learning Report
    // -----------------------------------------------------------------------

    public async Task<string?> GenerateAiLearningReportAsync(
        List<ResearchSignalPerformance> signalStats,
        ConfidenceAnalysis calibration,
        List<WeightChangeSummary> weightChanges,
        VolatilityOpportunityLearningSummary? voeSummary = null,
        RiskLearningSummary? riskLearningSummary = null,
        string? profileId = null)
    {
        if (!_ai.IsConfigured)
        {
            _logger.LogWarning("[learning-engine] OpenAI not configured, skipping AI summary");
            return null;
        }

        // Fetch evaluated predictions (the bulk of learning data) and open predictions (for pending count)
        var evaluatedPredictions = await _repo.GetRecentPredictionsAsync(500, status: "evaluated", profileId: profileId);
        var openPredictions = await _repo.GetRecentPredictionsAsync(200, status: "open", profileId: profileId);
        var predictions = evaluatedPredictions.Concat(openPredictions).ToList();
        var outcomeMap = await BuildUnifiedOutcomeMapAsync(evaluatedPredictions, profileId);

        // Include all evaluated predictions: directional and neutral
        var evaluated = evaluatedPredictions
            .Where(p => outcomeMap.ContainsKey(p.Id) && ResolveCorrectness(p, outcomeMap[p.Id]) is not null)
            .ToList();

        var correct = evaluated.Count(p => ResolveCorrectness(p, outcomeMap[p.Id]) == true);

        // New predictions since last report (for freshness — even without outcomes)
        var lastReportDate = DateTimeOffset.UtcNow.AddHours(-25);
        var newPredictions = predictions.Where(p => p.CreatedAt >= lastReportDate).ToList();
        var newByType = newPredictions.GroupBy(p => p.PredictionType)
            .Select(g => $"{g.Key}: {g.Count()}").ToList();
        var pendingEval = openPredictions.Count;
        var bullPreds = evaluated.Where(p => p.PredictionType == PredictionType.bullish).ToList();
        var bearPreds = evaluated.Where(p => p.PredictionType == PredictionType.bearish).ToList();
        var neutralPreds = evaluated.Where(p => !PredictionCategoryHelper.IsDirectional(p.PredictionType)).ToList();
        var bullCorrect = bullPreds.Count(p => outcomeMap[p.Id].DirectionCorrect == true);
        var bearCorrect = bearPreds.Count(p => outcomeMap[p.Id].DirectionCorrect == true);
        var neutralCorrect = neutralPreds.Count(p => ResolveCorrectness(p, outcomeMap[p.Id]) == true);

        // --- Per-timeframe accuracy breakdown ---
        var timeframeLines = evaluated
            .GroupBy(p => p.TimeWindow)
            .Where(g => g.Count() >= 3)
            .Select(g =>
            {
                var twCorrect = g.Count(p => ResolveCorrectness(p, outcomeMap[p.Id]) == true);
                return $"  {g.Key}: {(double)twCorrect / g.Count() * 100:F0}% accuracy ({g.Count()} predictions)";
            }).ToList();

        // --- Per-ticker performance (best and worst) ---
        var tickerGroups = evaluated
            .GroupBy(p => p.Ticker)
            .Where(g => g.Count() >= 2)
            .Select(g =>
            {
                var tCorrect = g.Count(p => ResolveCorrectness(p, outcomeMap[p.Id]) == true);
                var avgReturn = g.Where(p => outcomeMap[p.Id].PercentMove.HasValue)
                    .Select(p => outcomeMap[p.Id].PercentMove!.Value).DefaultIfEmpty(0).Average();
                return new { Ticker = g.Key, Count = g.Count(), Accuracy = (double)tCorrect / g.Count(), AvgReturn = avgReturn };
            })
            .OrderByDescending(t => t.Accuracy).ToList();
        var bestTickers = tickerGroups.Take(5)
            .Select(t => $"  {t.Ticker}: {t.Accuracy * 100:F0}% acc, {t.AvgReturn:+0.00;-0.00}% avg return ({t.Count} preds)").ToList();
        var worstTickers = tickerGroups.TakeLast(Math.Min(3, tickerGroups.Count))
            .Select(t => $"  {t.Ticker}: {t.Accuracy * 100:F0}% acc, {t.AvgReturn:+0.00;-0.00}% avg return ({t.Count} preds)").ToList();

        // --- Price prediction accuracy (target/stop hit rates, MFE/MAE) ---
        var withOutcomeDetails = evaluated.Where(p => outcomeMap[p.Id].MaxFavorablePercent.HasValue).ToList();
        var targetHits = evaluated.Count(p => outcomeMap[p.Id].TargetHit == true);
        var stopHits = evaluated.Count(p => outcomeMap[p.Id].StopHit == true);
        var avgMfe = withOutcomeDetails.Count > 0
            ? withOutcomeDetails.Average(p => outcomeMap[p.Id].MaxFavorablePercent!.Value) : 0;
        var avgMae = withOutcomeDetails.Count > 0
            ? withOutcomeDetails.Average(p => outcomeMap[p.Id].MaxAdversePercent ?? 0) : 0;
        var avgReturn = evaluated.Where(p => outcomeMap[p.Id].PercentMove.HasValue).Select(p => outcomeMap[p.Id].PercentMove!.Value)
            .DefaultIfEmpty(0).Average();

        // --- Recent specific examples (last 5 evaluated, most recent first) ---
        var recentExamples = evaluated
            .OrderByDescending(p => outcomeMap[p.Id].EvaluationTime)
            .Take(5)
            .Select(p =>
            {
                var o = outcomeMap[p.Id];
                var result = ResolveCorrectness(p, o) == true ? "CORRECT" : "WRONG";
                var move = o.PercentMove.HasValue ? $"{o.PercentMove.Value:+0.00;-0.00}%" : "n/a";
                return $"  {p.Ticker} {p.PredictionType} ({p.TimeWindow}, conf {p.ConfidenceScore}): {result}, moved {move}";
            }).ToList();

        // --- Trend: last 7 days vs prior 7 days ---
        var now = DateTimeOffset.UtcNow;
        var last7 = evaluated.Where(p => outcomeMap[p.Id].EvaluationTime >= now.AddDays(-7)).ToList();
        var prior7 = evaluated.Where(p => outcomeMap[p.Id].EvaluationTime >= now.AddDays(-14) && outcomeMap[p.Id].EvaluationTime < now.AddDays(-7)).ToList();
        var last7Acc = last7.Count > 0 ? (double)last7.Count(p => ResolveCorrectness(p, outcomeMap[p.Id]) == true) / last7.Count * 100 : 0;
        var prior7Acc = prior7.Count > 0 ? (double)prior7.Count(p => ResolveCorrectness(p, outcomeMap[p.Id]) == true) / prior7.Count * 100 : 0;

        // --- Confidence vs return (do high-confidence picks actually return more?) ---
        var highConfPreds = evaluated.Where(p => p.ConfidenceScore >= 60).ToList();
        var lowConfPreds = evaluated.Where(p => p.ConfidenceScore < 40).ToList();
        var highConfReturn = highConfPreds.Where(p => outcomeMap[p.Id].PercentMove.HasValue)
            .Select(p => outcomeMap[p.Id].PercentMove!.Value).DefaultIfEmpty(0).Average();
        var lowConfReturn = lowConfPreds.Where(p => outcomeMap[p.Id].PercentMove.HasValue)
            .Select(p => outcomeMap[p.Id].PercentMove!.Value).DefaultIfEmpty(0).Average();
        var highConfAcc = highConfPreds.Count > 0
            ? (double)highConfPreds.Count(p => ResolveCorrectness(p, outcomeMap[p.Id]) == true) / highConfPreds.Count * 100 : 0;
        var lowConfAcc = lowConfPreds.Count > 0
            ? (double)lowConfPreds.Count(p => ResolveCorrectness(p, outcomeMap[p.Id]) == true) / lowConfPreds.Count * 100 : 0;

        // --- Current weight snapshot ---
        var currentOverrides = await GetEffectiveOverridesAsync(profileId, profileId is null);
        var weightSnapshot = currentOverrides
            .Where(o => o.EffectiveWeight != 1.0)
            .Select(o => $"  {o.SignalName}: {o.EffectiveWeight:F3}")
            .ToList();

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

        // Fetch the new layered analytics for the AI
        var calibrationRows = await _repo.GetCalibrationBucketsAsync();
        var correlationRows = await _repo.GetSignalCorrelationsAsync();
        var influenceRows = await _repo.GetSignalInfluenceAsync();
        var interactionRows = await _repo.GetSignalInteractionsAsync();

        var calibrationLines = calibrationRows
            .Select(r => $"  {r["signal_name"]} [{r["score_bucket"]}]: {GetDouble(r, "accuracy") * 100:F0}% acc, " +
                          $"{GetDouble(r, "avg_return_percent"):+0.00;-0.00}% avg return ({r["sample_count"]} samples)")
            .Take(20).ToList();

        var correlationLines = correlationRows
            .Select(r => $"  {r["signal_name"]}: r={GetDouble(r, "correlation_r"):F3} ({r["sample_count"]} samples)")
            .Take(8).ToList();

        var influenceLines = influenceRows
            .Select(r => $"  {r["signal_name"]}: {r["decisive_count"]} decisive, {r["reinforcing_count"]} reinforcing, " +
                          $"{r["redundant_count"]} redundant" +
                          (r["decisive_accuracy"] is not null ? $", decisive acc={GetDouble(r, "decisive_accuracy") * 100:F0}%" : ""))
            .Take(8).ToList();

        var interactionLines = interactionRows
            .Where(r => GetDouble(r, "synergy_score") != 0)
            .Select(r => $"  {r["signal_a"]}+{r["signal_b"]}: synergy={GetDouble(r, "synergy_score"):+0.00;-0.00}, " +
                          $"both-strong acc={GetDouble(r, "both_strong_accuracy") * 100:F0}% ({r["both_strong_count"]} samples)")
            .Take(10).ToList();

        static double GetDouble(System.Text.Json.Nodes.JsonObject r, string key)
        {
            var node = r[key];
            if (node is null) return 0;
            if (node is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
            return double.TryParse(node.ToString(), out var parsed) ? parsed : 0;
        }

        var prompt = $@"You are the learning analyst for STOCKJAWN, an AI stock prediction system.
Write a daily learning report for the system owner. This must be DIFFERENT from yesterday's report — focus on new data, changing trends, and specific actionable recommendations.

OVERALL PERFORMANCE:
- Total evaluated predictions: {evaluated.Count}
- Overall accuracy: {(evaluated.Count > 0 ? (double)correct / evaluated.Count * 100 : 0):F1}%
- Average return per prediction: {avgReturn:+0.00;-0.00}%
- Bullish: {(bullPreds.Count > 0 ? (double)bullCorrect / bullPreds.Count * 100 : 0):F1}% accuracy ({bullPreds.Count} predictions)
- Bearish: {(bearPreds.Count > 0 ? (double)bearCorrect / bearPreds.Count * 100 : 0):F1}% accuracy ({bearPreds.Count} predictions)
- Neutral: {(neutralPreds.Count > 0 ? (double)neutralCorrect / neutralPreds.Count * 100 : 0):F1}% accuracy ({neutralPreds.Count} predictions, correct = abs move < 2%)

TREND (is accuracy improving or declining?):
  Last 7 days: {last7Acc:F1}% accuracy ({last7.Count} predictions)
  Prior 7 days: {prior7Acc:F1}% accuracy ({prior7.Count} predictions)
  Direction: {(last7.Count >= 3 && prior7.Count >= 3 ? (last7Acc > prior7Acc + 5 ? "IMPROVING" : last7Acc < prior7Acc - 5 ? "DECLINING" : "STABLE") : "insufficient data to compare")}

ACCURACY BY TIMEFRAME:
{(timeframeLines.Count > 0 ? string.Join("\n", timeframeLines) : "  Not enough data per timeframe yet")}

CONFIDENCE VS ACTUAL RETURNS:
  High confidence (≥60): {highConfAcc:F0}% accuracy, {highConfReturn:+0.00;-0.00}% avg return ({highConfPreds.Count} predictions)
  Low confidence (<40): {lowConfAcc:F0}% accuracy, {lowConfReturn:+0.00;-0.00}% avg return ({lowConfPreds.Count} predictions)
  {(highConfPreds.Count >= 5 && lowConfPreds.Count >= 5 ? (highConfAcc > lowConfAcc ? "Confidence IS predictive of accuracy" : "WARNING: Confidence is NOT predictive — high-conf picks aren't better") : "Not enough samples to compare")}

PRICE PREDICTION QUALITY:
  Target hit rate: {(evaluated.Count > 0 ? (double)targetHits / evaluated.Count * 100 : 0):F0}% ({targetHits}/{evaluated.Count})
  Stop hit rate: {(evaluated.Count > 0 ? (double)stopHits / evaluated.Count * 100 : 0):F0}% ({stopHits}/{evaluated.Count})
  Avg max favorable excursion (MFE): {avgMfe:F2}%
  Avg max adverse excursion (MAE): {avgMae:F2}%
  MFE/MAE ratio: {(avgMae > 0 ? avgMfe / avgMae : 0):F2} {(avgMae > 0 && avgMfe / avgMae > 1.5 ? "(good — winners run further than losers)" : avgMae > 0 && avgMfe / avgMae < 1.0 ? "(BAD — losers run further than winners)" : "")}

BEST PERFORMING TICKERS:
{(bestTickers.Count > 0 ? string.Join("\n", bestTickers) : "  Not enough per-ticker data yet")}

WORST PERFORMING TICKERS:
{(worstTickers.Count > 0 ? string.Join("\n", worstTickers) : "  Not enough per-ticker data yet")}

RECENT PREDICTIONS AND OUTCOMES (most recent 5):
{(recentExamples.Count > 0 ? string.Join("\n", recentExamples) : "  No recent evaluated predictions")}

TOP SIGNALS (legacy binary tracking — treat with skepticism if all signals show same accuracy):
{string.Join("\n", topSignals)}

WEAK SIGNALS:
{string.Join("\n", weakSignals)}

SIGNAL CALIBRATION BY STRENGTH (does higher signal score predict better outcomes?):
{(calibrationLines.Count > 0 ? string.Join("\n", calibrationLines) : "  No data yet — first run pending")}

SIGNAL CORRELATIONS (Pearson r between signal strength and actual return %):
{(correlationLines.Count > 0 ? string.Join("\n", correlationLines) : "  No data yet — first run pending")}

SIGNAL INFLUENCE (counterfactual: how often was each signal decisive vs redundant?):
{(influenceLines.Count > 0 ? string.Join("\n", influenceLines) : "  No data yet — first run pending")}

SIGNAL INTERACTIONS (do certain signal pairs work better together?):
{(interactionLines.Count > 0 ? string.Join("\n", interactionLines) : "  No data yet — first run pending")}

CONFIDENCE CALIBRATION:
{string.Join("\n", calibBuckets)}
Calibration status: {calibration.Summary}

CURRENT SIGNAL WEIGHTS (non-default only):
{(weightSnapshot.Count > 0 ? string.Join("\n", weightSnapshot) : "  All weights at default (1.0)")}

WEIGHT CHANGES APPLIED THIS CYCLE:
{(weightChangeLines.Count > 0 ? string.Join("\n", weightChangeLines) : "  None today")}

NEW PREDICTIONS SINCE LAST REPORT:
  New predictions: {newPredictions.Count} ({string.Join(", ", newByType)})
  Pending evaluation: {pendingEval} open predictions awaiting outcome data

PREDICTION REVISIONS (supersession learning):
{await BuildSupersessionReportSectionAsync()}

VOLATILITY OPPORTUNITY LEARNING (VOE Stage 5c):
{BuildVolatilityOpportunitySummarySection(voeSummary ?? new VolatilityOpportunityLearningSummary())}

RISK MANAGEMENT OUTCOMES (portfolio stop-loss/take-profit/trailing-stop closures):
{BuildRiskManagementReportSection(riskLearningSummary ?? new RiskLearningSummary())}

INSTRUCTIONS:
- Write 4-6 paragraphs, conversational but data-driven. No bullet points, no headers — flowing prose only.
- PARAGRAPH 1: Open with the single most important change or finding. Name specific numbers. Compare to prior period if trend data exists. Example: ""Accuracy dropped from 52% to 41% this week, driven entirely by bearish calls going 1-for-7.""
- PARAGRAPH 2: Analyze which predictions actually worked and which didn't. Reference specific tickers and recent examples. Don't just say ""bullish outperformed bearish"" — say ""AAPL and MSFT bullish calls both hit targets while TSLA bearish was stopped out twice.""
- PARAGRAPH 3: Evaluate price prediction quality. Are targets being set too aggressively (low hit rate, high stop rate)? Is the MFE/MAE ratio healthy? Are we leaving money on the table (high MFE but low target hits)?
- PARAGRAPH 4: Assess signal effectiveness. Which signals actually drive correct predictions vs. which are along for the ride? If influence data shows redundant signals, name them and recommend downweighting. If correlations show a signal with strong predictive power, highlight it.
- PARAGRAPH 5: Analyze risk management performance. Are stop-losses firing too often on a particular timeframe or ticker? Are take-profits and trailing stops locking in gains effectively? If certain tickers keep hitting stop-losses, recommend avoiding them or adjusting thresholds. If a timeframe tier has excessive stop-loss events, recommend widening stops for that tier.
- PARAGRAPH 6: Provide 2-3 specific, actionable recommendations. Not generic advice — concrete changes. Examples: ""Consider dropping the sentiment signal weight below 0.5 — it's been redundant in 80% of predictions."" Or ""Day-trade stop-losses at 5% are too tight — 3 of 5 stop-outs recovered within the day."" Or ""TSLA has triggered 4 stop-losses in 2 weeks — consider excluding it from day trades.""
- PARAGRAPH 7 (optional): Note what data is still missing or pending, and what you'd want to see before making stronger recommendations.
- CRITICAL: Every report must contain specific numbers, ticker names, and concrete recommendations. A report that could apply to any day is a BAD report.
- Keep under 700 words";

        try
        {
            var result = await _ai.CompleteAsync(new AiCompletionRequest
            {
                Messages =
                [
                    new() { Role = "system", Content = "You are a quantitative finance analyst writing a daily learning report for STOCKJAWN, an AI stock prediction system. Your job is to identify what changed, what's working, what's failing, and recommend specific weight or threshold adjustments. Every report must cite concrete numbers and ticker names. Never write a generic report." },
                    new() { Role = "user", Content = prompt },
                ],
                MaxOutputTokens = 1000,
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

    public async Task<List<object>> GenerateLearningInsightsAsync(string? profileId = null)
    {
        var perfStats = await _repo.GetAllSignalPerformanceAsync();
        var predictions = await _repo.GetRecentPredictionsAsync(300, status: "evaluated", profileId: profileId);
        // Unified outcome map not needed here — outcomeMap built after perfStats section
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

        // 4. Per-ticker patterns (includes directional and neutral predictions)
        var outcomeMap = await BuildUnifiedOutcomeMapAsync(predictions, profileId);
        var tickerStats = new Dictionary<string, (int Correct, int Wrong, int Total)>();
        foreach (var pred in predictions)
        {
            if (!outcomeMap.TryGetValue(pred.Id, out var outcome)) continue;
            var isNeutral = !PredictionCategoryHelper.IsDirectional(pred.PredictionType);
            bool? isCorrect;
            if (isNeutral)
            {
                if (outcome.PercentMove is null) continue;
                isCorrect = Math.Abs(outcome.PercentMove.Value) < 2.0;
            }
            else
            {
                if (outcome.DirectionCorrect is null) continue;
                isCorrect = outcome.DirectionCorrect;
            }
            var (correct, wrong, total) = tickerStats.GetValueOrDefault(pred.Ticker);
            total++;
            if (isCorrect == true) correct++; else wrong++;
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

        // 6. Supersession insights — which prediction revisions improve accuracy?
        try
        {
            var supAnalytics = await GetSupersessionAnalyticsAsync();
            if (supAnalytics.TotalSupersessions >= 3)
            {
                foreach (var (label, stats) in supAnalytics.ByTransition)
                {
                    if (stats.Count < 3) continue;

                    var assessmentVerb = stats.IsImprovement ? "improves" : "does not clearly improve";
                    insights.Add(new
                    {
                        insight_type = "supersession",
                        summary = $"Transition {label}: {stats.Count} occurrences, replacement accuracy {stats.Accuracy * 100:F0}% ({stats.CorrectCount}/{stats.EvaluatedCount}). Revision {assessmentVerb} predictions.",
                        evidence = $"Avg {stats.AvgHoursBetween:F1}h between predictions. Confidence delta: {stats.AvgConfidenceDelta:+0;-0}, risk delta: {stats.AvgRiskDelta:+0;-0}.",
                        action_recommendation = stats.IsImprovement
                            ? $"The {label} transition is effective. Consider being more aggressive about superseding."
                            : $"The {label} transition shows marginal improvement. More data needed.",
                        confidence = Math.Min((double)stats.EvaluatedCount / 20, 1.0),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[learning-engine] Failed to generate supersession insights");
        }

        // 7. Volatility opportunity insights
        try
        {
            var voeRecords = await _repo.GetAllVolatilityLearningStatsAsync(limit: 500, windowDays: 90);
            var voeResolved = voeRecords.Where(r => r.DirectionCorrect is not null).ToList();

            if (voeResolved.Count >= 20)
            {
                var byType = voeResolved
                    .Where(r => !string.IsNullOrEmpty(r.OpportunityType) && r.OpportunityType != "None")
                    .GroupBy(r => r.OpportunityType!)
                    .Where(g => g.Count() >= 5)
                    .OrderByDescending(g => (double)g.Count(r => r.DirectionCorrect == true) / g.Count())
                    .ToList();

                if (byType.Count > 0)
                {
                    var best = byType.First();
                    var bestWin = (double)best.Count(r => r.DirectionCorrect == true) / best.Count();
                    var worst = byType.Last();
                    var worstWin = (double)worst.Count(r => r.DirectionCorrect == true) / worst.Count();

                    insights.Add(new
                    {
                        insight_type = "volatility_opportunity",
                        summary = $"Best VOE type: {best.Key} ({bestWin * 100:F0}% win, n={best.Count()}). " +
                                  $"Worst: {worst.Key} ({worstWin * 100:F0}% win, n={worst.Count()}).",
                        evidence = $"Based on {voeResolved.Count} resolved VOE-tagged predictions over 90 days.",
                        action_recommendation = worstWin < 0.4
                            ? $"Consider reducing confidence for {worst.Key} opportunities."
                            : "All opportunity types are performing adequately.",
                        confidence = Math.Min((double)voeResolved.Count / 100, 1.0),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[learning-engine] Failed to generate VOE insights");
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
    // Profile-Aware Weight Routing Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Routes a weight update to the correct storage:
    /// - Champion profiles write to scoring_weight_overrides (shared table).
    /// - Challenger profiles write to prediction_profile_configs (per-profile table).
    /// </summary>
    private async Task WriteWeightUpdateAsync(string signalName, double effectiveWeight,
        ScoringWeightOverride fullOverride, string? profileId, bool isChampion)
    {
        if (isChampion)
        {
            await _repo.UpsertWeightOverrideAsync(fullOverride);
        }
        else if (profileId is not null)
        {
            await _profileRepo.SetProfileConfigAsync(profileId, signalName, effectiveWeight);
        }
    }

    /// <summary>
    /// Reads current weight overrides appropriate for the profile:
    /// - Champion: reads from scoring_weight_overrides directly.
    /// - Challenger: starts with champion base overrides, then layers profile-specific weights.
    /// </summary>
    private async Task<List<ScoringWeightOverride>> GetEffectiveOverridesAsync(string? profileId, bool isChampion)
    {
        if (isChampion)
            return await _repo.GetActiveWeightOverridesAsync();

        if (profileId is null)
            return await _repo.GetActiveWeightOverridesAsync();

        // For challengers, start with champion base overrides then layer profile-specific
        var baseOverrides = await _repo.GetActiveWeightOverridesAsync();
        var profileWeights = await _profileRepo.GetProfileWeightsAsync(profileId);

        // Convert profile weights to override format for compatibility
        var overrideMap = baseOverrides.ToDictionary(o => o.SignalName);
        foreach (var (key, value) in profileWeights)
        {
            if (overrideMap.TryGetValue(key, out var existing))
            {
                overrideMap[key] = existing with
                {
                    EffectiveWeight = value,
                    AdjustmentPercent = existing.BaseWeight > 0 ? (value / existing.BaseWeight) - 1.0 : 0,
                };
            }
            else
            {
                overrideMap[key] = new ScoringWeightOverride
                {
                    SignalName = key,
                    BaseWeight = 1.0,
                    AdjustmentPercent = value - 1.0,
                    EffectiveWeight = value,
                    Status = "active",
                };
            }
        }

        return overrideMap.Values.ToList();
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

// -----------------------------------------------------------------------
// Risk Management Learning Models
// -----------------------------------------------------------------------

public class RiskLearningSummary
{
    public int TotalEvents { get; set; }
    public Dictionary<string, int> EventsByType { get; set; } = new();
    public Dictionary<string, double> PnlByType { get; set; } = new();
    public Dictionary<string, int> TickerCounts { get; set; } = new();
    public Dictionary<string, int> EventsByTierAndType { get; set; } = new();
}
