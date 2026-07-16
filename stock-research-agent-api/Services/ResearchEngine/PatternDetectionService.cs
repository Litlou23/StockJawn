using System.Text.Json;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// AI-powered pattern detection (#38). Three capabilities:
///   1. Failure cluster analysis — groups wrong predictions by shared traits
///   2. Signal combination analysis — finds which signal pairs predict best
///   3. What-if analysis — simulates weight changes against historical data
/// </summary>
public class PatternDetectionService
{
    private readonly ResearchRepository _repo;
    private readonly IOpenAiCompletionService _ai;
    private readonly ILogger<PatternDetectionService> _logger;

    public PatternDetectionService(
        ResearchRepository repo, IOpenAiCompletionService ai,
        ILogger<PatternDetectionService> logger)
    {
        _repo = repo;
        _ai = ai;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // 1. Failure Cluster Analysis
    // -----------------------------------------------------------------------

    public record FailureCluster
    {
        public string ClusterName { get; init; } = "";
        public int Count { get; init; }
        public List<string> CommonTraits { get; init; } = [];
        public double AvgConfidence { get; init; }
        public string SuggestedAction { get; init; } = "";
    }

    public record FailureClusterResult
    {
        public int TotalFailures { get; init; }
        public List<FailureCluster> Clusters { get; init; } = [];
        public string? AiInsight { get; init; }
    }

    public async Task<FailureClusterResult> AnalyzeFailureClustersAsync()
    {
        var championId = await _repo.GetChampionProfileIdAsync();
        var predictions = await _repo.GetRecentPredictionsAsync(500, profileId: championId);
        var outcomes = await _repo.GetRecentOutcomesAsync(500);
        var outcomeMap = outcomes.ToDictionary(o => o.PredictionId);

        var failures = predictions
            .Where(p => outcomeMap.TryGetValue(p.Id, out var o) && o.DirectionCorrect == false)
            .ToList();

        if (failures.Count == 0)
            return new FailureClusterResult { TotalFailures = 0 };

        // Cluster by common attributes
        var clusters = new List<FailureCluster>();

        // Cluster by market regime
        var regimeGroups = failures
            .Where(p => !string.IsNullOrEmpty(p.ScoreDebugJson))
            .Select(p => (Pred: p, Breakdown: ScoringBreakdownEnvelope.Parse(p.ScoreDebugJson)))
            .Where(x => x.Breakdown is not null)
            .GroupBy(x =>
            {
                var b = x.Breakdown!;
                var mkt = b.MarketContextBullish - b.MarketContextBearish;
                return mkt > 5 ? "bull_market" : mkt < -5 ? "bear_market" : "sideways";
            })
            .Where(g => g.Count() >= 3);

        foreach (var group in regimeGroups)
        {
            var preds = group.Select(g => g.Pred).ToList();
            var traits = new List<string> { $"Market regime: {group.Key}" };

            var avgConf = preds.Average(p => p.ConfidenceScore);
            var directions = preds.GroupBy(p => p.PredictionType)
                .OrderByDescending(g => g.Count()).First();
            traits.Add($"Dominant direction: {directions.Key} ({directions.Count()}/{preds.Count})");

            var avgDataQuality = group
                .Where(g => g.Breakdown is not null)
                .Average(g => g.Breakdown!.DataQualityFactor);
            if (avgDataQuality < 0.85)
                traits.Add($"Low data quality (avg {avgDataQuality:F2})");

            clusters.Add(new FailureCluster
            {
                ClusterName = $"Failures in {group.Key.Replace("_", " ")} conditions",
                Count = preds.Count,
                CommonTraits = traits,
                AvgConfidence = avgConf,
                SuggestedAction = avgConf > 60
                    ? "Reduce confidence cap when market regime conflicts with prediction direction"
                    : "These were already low-confidence — acceptable failure rate",
            });
        }

        // Cluster by high-confidence failures (overconfidence)
        var highConfFailures = failures.Where(p => p.ConfidenceScore >= 65).ToList();
        if (highConfFailures.Count >= 2)
        {
            var tickers = highConfFailures.Select(p => p.Ticker).Distinct().ToList();
            clusters.Add(new FailureCluster
            {
                ClusterName = "High-confidence failures (overconfidence)",
                Count = highConfFailures.Count,
                CommonTraits =
                [
                    $"Confidence range: {highConfFailures.Min(p => p.ConfidenceScore)}-{highConfFailures.Max(p => p.ConfidenceScore)}",
                    $"Tickers: {string.Join(", ", tickers.Take(5))}",
                ],
                AvgConfidence = highConfFailures.Average(p => p.ConfidenceScore),
                SuggestedAction = "Tighten confidence calibration factor; add penalty for single-bucket dominance",
            });
        }

        // Cluster by ticker (repeatedly wrong on same ticker)
        var tickerFailures = failures.GroupBy(p => p.Ticker)
            .Where(g => g.Count() >= 3)
            .OrderByDescending(g => g.Count());
        foreach (var group in tickerFailures.Take(3))
        {
            clusters.Add(new FailureCluster
            {
                ClusterName = $"Repeated failures on {group.Key}",
                Count = group.Count(),
                CommonTraits =
                [
                    $"Directions: {string.Join(", ", group.Select(p => p.PredictionType.ToString()))}",
                    $"Avg confidence: {group.Average(p => p.ConfidenceScore):F0}",
                ],
                AvgConfidence = group.Average(p => p.ConfidenceScore),
                SuggestedAction = $"Consider adding {group.Key} to a 'difficult tickers' list with stricter thresholds",
            });
        }

        // AI insight if available
        string? aiInsight = null;
        if (_ai.IsConfigured && clusters.Count > 0)
        {
            try
            {
                var prompt = $@"Analyze these prediction failure clusters and provide a 2-3 sentence tactical recommendation:
{JsonSerializer.Serialize(clusters, new JsonSerializerOptions { WriteIndented = true })}
Total failures: {failures.Count} out of {predictions.Count} evaluated predictions.
Focus on the most actionable pattern. Be specific about what scoring engine change would help.";

                var result = await _ai.CompleteAsync(new AiCompletionRequest
                {
                    Messages =
                    [
                        new() { Role = "system", Content = "You are a quantitative analyst debugging a stock prediction system." },
                        new() { Role = "user", Content = prompt },
                    ],
                    MaxOutputTokens = 200,
                }, CancellationToken.None);
                aiInsight = result.Text;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[pattern-detection] AI insight generation failed");
            }
        }

        return new FailureClusterResult
        {
            TotalFailures = failures.Count,
            Clusters = clusters,
            AiInsight = aiInsight,
        };
    }

    // -----------------------------------------------------------------------
    // 2. Signal Combination Analysis
    // -----------------------------------------------------------------------

    public record SignalCombination
    {
        public string Signal1 { get; init; } = "";
        public string Signal2 { get; init; } = "";
        public int CoOccurrences { get; init; }
        public double JointAccuracy { get; init; }
        public double Signal1Alone { get; init; }
        public double Signal2Alone { get; init; }
        public double SynergyScore { get; init; }
        public string Interpretation { get; init; } = "";
    }

    public record SignalCombinationResult
    {
        public List<SignalCombination> BestCombinations { get; init; } = [];
        public List<SignalCombination> WorstCombinations { get; init; } = [];
        public string? AiInsight { get; init; }
    }

    public async Task<SignalCombinationResult> AnalyzeSignalCombinationsAsync()
    {
        var observations = await _repo.GetSignalObservationsAsync(limit: 5000, windowDays: 180);
        if (observations.Count < 50) return new SignalCombinationResult();

        // Group observations by prediction to find which signals co-occurred
        var byPrediction = observations.GroupBy(o => o.PredictionId).ToList();

        // For each pair of bucket signals, compute joint accuracy
        var buckets = new[] { "trend", "momentum", "volume", "volatility",
            "market_context", "catalyst", "learning", "research_signal" };

        var combinations = new List<SignalCombination>();

        for (int i = 0; i < buckets.Length; i++)
        {
            for (int j = i + 1; j < buckets.Length; j++)
            {
                var sig1 = buckets[i];
                var sig2 = buckets[j];

                // Find predictions where both signals were strong (dominant score > 5)
                int coOccur = 0, jointCorrect = 0;
                int sig1Strong = 0, sig1Correct = 0;
                int sig2Strong = 0, sig2Correct = 0;

                foreach (var predGroup in byPrediction)
                {
                    var obs1 = predGroup.FirstOrDefault(o => o.SignalName == sig1);
                    var obs2 = predGroup.FirstOrDefault(o => o.SignalName == sig2);
                    if (obs1?.Correct is null || obs2?.Correct is null) continue;

                    var score1 = Math.Max(obs1.BullScore, obs1.BearScore);
                    var score2 = Math.Max(obs2.BullScore, obs2.BearScore);
                    var correct = obs1.Correct == true;

                    if (score1 > 5) { sig1Strong++; if (correct) sig1Correct++; }
                    if (score2 > 5) { sig2Strong++; if (correct) sig2Correct++; }

                    if (score1 > 5 && score2 > 5)
                    {
                        coOccur++;
                        if (correct) jointCorrect++;
                    }
                }

                if (coOccur < 5) continue;

                var jointAcc = (double)jointCorrect / coOccur;
                var s1Acc = sig1Strong > 0 ? (double)sig1Correct / sig1Strong : 0.5;
                var s2Acc = sig2Strong > 0 ? (double)sig2Correct / sig2Strong : 0.5;
                // Synergy = how much better the combination is vs the average of each alone
                var synergy = jointAcc - (s1Acc + s2Acc) / 2.0;

                combinations.Add(new SignalCombination
                {
                    Signal1 = sig1,
                    Signal2 = sig2,
                    CoOccurrences = coOccur,
                    JointAccuracy = Math.Round(jointAcc * 100, 1),
                    Signal1Alone = Math.Round(s1Acc * 100, 1),
                    Signal2Alone = Math.Round(s2Acc * 100, 1),
                    SynergyScore = Math.Round(synergy * 100, 1),
                    Interpretation = synergy > 0.1 ? "Strong positive synergy"
                        : synergy > 0.03 ? "Mild positive synergy"
                        : synergy < -0.1 ? "Negative synergy (signals conflict)"
                        : "Neutral interaction",
                });
            }
        }

        var best = combinations.OrderByDescending(c => c.SynergyScore).Take(5).ToList();
        var worst = combinations.OrderBy(c => c.SynergyScore).Take(3).ToList();

        return new SignalCombinationResult
        {
            BestCombinations = best,
            WorstCombinations = worst,
        };
    }

    // -----------------------------------------------------------------------
    // 3. What-If Analysis
    // -----------------------------------------------------------------------

    public record WhatIfResult
    {
        public string SignalName { get; init; } = "";
        public double CurrentWeight { get; init; }
        public double ProposedWeight { get; init; }
        public double CurrentAccuracy { get; init; }
        public double SimulatedAccuracy { get; init; }
        public double AccuracyDelta { get; init; }
        public int AffectedPredictions { get; init; }
        public int FlippedCorrect { get; init; }
        public int FlippedIncorrect { get; init; }
        public string Recommendation { get; init; } = "";
    }

    public async Task<WhatIfResult> RunWhatIfAnalysisAsync(string signalName, double newWeight)
    {
        var observations = await _repo.GetSignalObservationsAsync(limit: 5000, windowDays: 180);
        var signalObs = observations.Where(o => o.SignalName == signalName).ToList();

        if (signalObs.Count < 10)
            return new WhatIfResult
            {
                SignalName = signalName,
                ProposedWeight = newWeight,
                Recommendation = "Insufficient data for simulation",
            };

        var currentWeight = signalObs.First().EffectiveWeight;
        var currentCorrect = signalObs.Count(o => o.Correct == true);
        var currentAcc = (double)currentCorrect / signalObs.Count;

        // Simulate: for each observation, if increasing/decreasing the weight
        // would have changed the prediction direction, count flips
        var byPrediction = observations.GroupBy(o => o.PredictionId).ToList();
        int flippedCorrect = 0, flippedIncorrect = 0, affected = 0;

        foreach (var predGroup in byPrediction)
        {
            var targetObs = predGroup.FirstOrDefault(o => o.SignalName == signalName);
            if (targetObs?.Correct is null) continue;

            // Compute original total weighted contribution
            var originalTotal = predGroup.Sum(o => o.WeightedContribution);
            // Compute new total with adjusted weight
            var weightDelta = newWeight - currentWeight;
            var contributionDelta = (targetObs.BullScore - targetObs.BearScore) * weightDelta;
            var newTotal = originalTotal + contributionDelta;

            // Did direction flip?
            var originalDir = originalTotal >= 0 ? "bullish" : "bearish";
            var newDir = newTotal >= 0 ? "bullish" : "bearish";

            if (originalDir != newDir)
            {
                affected++;
                // If originally wrong and would have been right, that's a flip correct
                if (targetObs.Correct == false)
                    flippedCorrect++;
                else
                    flippedIncorrect++;
            }
        }

        var simCorrect = currentCorrect + flippedCorrect - flippedIncorrect;
        var simAcc = (double)simCorrect / signalObs.Count;

        var delta = simAcc - currentAcc;
        var recommendation = delta > 0.03
            ? $"Beneficial change: +{delta * 100:F1}% accuracy. Consider applying."
            : delta < -0.03
                ? $"Harmful change: {delta * 100:F1}% accuracy. Do not apply."
                : "Marginal impact. Change would not meaningfully affect outcomes.";

        return new WhatIfResult
        {
            SignalName = signalName,
            CurrentWeight = currentWeight,
            ProposedWeight = newWeight,
            CurrentAccuracy = Math.Round(currentAcc * 100, 1),
            SimulatedAccuracy = Math.Round(simAcc * 100, 1),
            AccuracyDelta = Math.Round(delta * 100, 1),
            AffectedPredictions = affected,
            FlippedCorrect = flippedCorrect,
            FlippedIncorrect = flippedIncorrect,
            Recommendation = recommendation,
        };
    }

    // -----------------------------------------------------------------------
    // Full Analysis (combines all three)
    // -----------------------------------------------------------------------

    public record FullPatternAnalysis
    {
        public FailureClusterResult FailureClusters { get; init; } = new();
        public SignalCombinationResult SignalCombinations { get; init; } = new();
        public string? AiSynthesis { get; init; }
    }

    public async Task<FullPatternAnalysis> RunFullPatternAnalysisAsync()
    {
        var clusters = await AnalyzeFailureClustersAsync();
        var combos = await AnalyzeSignalCombinationsAsync();

        string? synthesis = null;
        if (_ai.IsConfigured)
        {
            try
            {
                var prompt = $@"Synthesize these pattern detection results into 3-4 sentences of actionable advice:

FAILURE CLUSTERS: {clusters.Clusters.Count} patterns found across {clusters.TotalFailures} failures.
Top cluster: {(clusters.Clusters.FirstOrDefault()?.ClusterName ?? "none")}

SIGNAL SYNERGIES:
Best combo: {(combos.BestCombinations.FirstOrDefault() is { } best ? $"{best.Signal1}+{best.Signal2} ({best.SynergyScore:+0.0}% synergy)" : "insufficient data")}
Worst combo: {(combos.WorstCombinations.FirstOrDefault() is { } worst ? $"{worst.Signal1}+{worst.Signal2} ({worst.SynergyScore:+0.0}% synergy)" : "insufficient data")}

Focus on what the system should DO differently — weight changes, new caps, or confidence adjustments.";

                var result = await _ai.CompleteAsync(new AiCompletionRequest
                {
                    Messages =
                    [
                        new() { Role = "system", Content = "You are a quantitative analyst for STOCKJAWN. Be direct, specific, and brief." },
                        new() { Role = "user", Content = prompt },
                    ],
                    MaxOutputTokens = 300,
                }, CancellationToken.None);
                synthesis = result.Text;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[pattern-detection] AI synthesis failed");
            }
        }

        return new FullPatternAnalysis
        {
            FailureClusters = clusters,
            SignalCombinations = combos,
            AiSynthesis = synthesis,
        };
    }
}
