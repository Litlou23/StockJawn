using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchEngine.Evaluation;
using StockResearchAgent.Api.Services.Supabase;
using ResearchSignal = StockResearchAgent.Api.Models.ResearchSignal;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Multi-model ensemble scoring. Runs N scoring formulas in parallel,
/// tracks each model's historical accuracy, and blends results using
/// performance-weighted averaging. Models that consistently outperform
/// get more influence; underperformers get dampened automatically.
///
/// Current models:
///   1. Default   — standard ScoringEngine with current weights
///   2. Momentum  — overweights momentum + volume, underweights catalyst
///   3. Contrarian — inverts overbought/oversold signals, overweights volatility
///
/// The ensemble output replaces the single-model score when enabled.
/// </summary>
public class EnsembleScoringService
{
    private readonly ResearchRepository _repo;
    private readonly IScoringEngine _scoringEngine;
    private readonly ILogger<EnsembleScoringService> _logger;

    // Each model is a named weight profile applied over the base scoring engine
    private static readonly Dictionary<string, Dictionary<string, double>> ModelProfiles = new()
    {
        ["default"] = new()
        {
            ["trend"] = 1.0, ["momentum"] = 1.0, ["volume"] = 1.0,
            ["volatility"] = 1.0, ["market_context"] = 1.0,
            ["catalyst"] = 1.0, ["learning"] = 1.0, ["research_signal"] = 1.0,
        },
        ["momentum_heavy"] = new()
        {
            ["trend"] = 0.8, ["momentum"] = 1.5, ["volume"] = 1.3,
            ["volatility"] = 0.7, ["market_context"] = 0.9,
            ["catalyst"] = 0.6, ["learning"] = 1.0, ["research_signal"] = 0.8,
        },
        ["contrarian"] = new()
        {
            ["trend"] = 0.6, ["momentum"] = 0.7, ["volume"] = 1.2,
            ["volatility"] = 1.5, ["market_context"] = 1.3,
            ["catalyst"] = 0.8, ["learning"] = 1.0, ["research_signal"] = 0.9,
        },
    };

    public EnsembleScoringService(
        ResearchRepository repo,
        IScoringEngine scoringEngine,
        ILogger<EnsembleScoringService> logger)
    {
        _repo = repo;
        _scoringEngine = scoringEngine;
        _logger = logger;
    }

    public record ModelScore
    {
        public string ModelName { get; init; } = "";
        public ScoringEngine.ScoringResult Result { get; init; } = null!;
        public double ModelWeight { get; init; } = 1.0;
        public double HistoricalAccuracy { get; init; }
    }

    public record EnsembleResult
    {
        public ScoringEngine.ScoringResult BlendedResult { get; init; } = null!;
        public List<ModelScore> ModelScores { get; init; } = [];
        public double Agreement { get; init; }
        public string DominantModel { get; init; } = "";
    }

    /// <summary>
    /// Score a ticker through all ensemble models and blend results.
    /// </summary>
    public async Task<EnsembleResult> ScoreWithEnsembleAsync(
        MarketSnapshot snapshot,
        TechnicalIndicators indicators,
        BenchmarkContext benchmark,
        Dictionary<string, double> baseWeights,
        List<string> lessons,
        List<ResearchSignal>? researchSignals = null,
        MarketIntelligenceContext? intelligence = null,
        ResearchUniverseContext? researchUniverse = null,
        VolatilityOpportunityAssessment? volatilityAssessment = null)
    {
        var modelAccuracies = await GetModelAccuraciesAsync();
        var modelScores = new List<ModelScore>();

        foreach (var (modelName, profile) in ModelProfiles)
        {
            // Apply model profile as multipliers on top of base weights
            var adjustedWeights = new Dictionary<string, double>(baseWeights);
            foreach (var (key, multiplier) in profile)
            {
                if (adjustedWeights.TryGetValue(key, out var baseVal))
                    adjustedWeights[key] = baseVal * multiplier;
                else
                    adjustedWeights[key] = multiplier;
            }

            var result = _scoringEngine.Evaluate(
                snapshot, indicators, benchmark, adjustedWeights, lessons, researchSignals, intelligence, researchUniverse, volatilityAssessment);

            var accuracy = modelAccuracies.GetValueOrDefault(modelName, 0.5);
            // Weight = accuracy squared to amplify good models
            var modelWeight = accuracy * accuracy;

            modelScores.Add(new ModelScore
            {
                ModelName = modelName,
                Result = result,
                ModelWeight = modelWeight,
                HistoricalAccuracy = accuracy,
            });
        }

        // Blend results using performance-weighted average
        var blended = BlendResults(modelScores, baseWeights);

        // Compute agreement: how many models agree on direction
        var directions = modelScores.Select(m => m.Result.WinningDirection).ToList();
        var mostCommon = directions.GroupBy(d => d).OrderByDescending(g => g.Count()).First();
        var agreement = (double)mostCommon.Count() / directions.Count;

        // If models disagree strongly, cap confidence
        if (agreement < 0.67)
        {
            var cappedConfidence = Math.Min(blended.Confidence, 50);
            blended = blended with { Confidence = cappedConfidence };
        }

        var dominantModel = modelScores.OrderByDescending(m => m.ModelWeight).First().ModelName;

        return new EnsembleResult
        {
            BlendedResult = blended,
            ModelScores = modelScores,
            Agreement = agreement,
            DominantModel = dominantModel,
        };
    }

    /// <summary>
    /// Record which model was most accurate for a given prediction outcome,
    /// so future ensemble blending improves over time.
    /// </summary>
    public async Task RecordModelOutcomeAsync(
        string predictionId, string modelName, bool correct)
    {
        await _repo.UpsertSignalPerformanceAsync(new
        {
            signal_name = $"ensemble_{modelName}",
            signal_type = "ensemble_model",
            direction = "all",
            total_predictions = 1,
            correct_predictions = correct ? 1 : 0,
            accuracy = correct ? 1.0 : 0.0,
            average_outcome_score = correct ? 100.0 : 0.0,
            last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
        });
    }

    private static ScoringEngine.ScoringResult BlendResults(
        List<ModelScore> scores, Dictionary<string, double> weights)
    {
        var totalWeight = scores.Sum(s => s.ModelWeight);
        if (totalWeight == 0) totalWeight = 1;

        var blendedBull = scores.Sum(s => s.Result.BullishScore * s.ModelWeight) / totalWeight;
        var blendedBear = scores.Sum(s => s.Result.BearishScore * s.ModelWeight) / totalWeight;
        var blendedConf = scores.Sum(s => s.Result.Confidence * s.ModelWeight) / totalWeight;
        var blendedRisk = scores.Sum(s => s.Result.Risk * s.ModelWeight) / totalWeight;

        // Use the primary (highest-weight) model's result as base — including its
        // direction and prediction type from ScoringEngine (no independent formula).
        var primary = scores.OrderByDescending(s => s.ModelWeight).First().Result;

        // Override direction only if models unanimously disagree with primary
        var direction = primary.WinningDirection;
        var predType = primary.PredictionType;
        var nonPrimaryVotes = scores
            .Where(s => s.ModelName != scores.OrderByDescending(x => x.ModelWeight).First().ModelName)
            .Select(s => s.Result.WinningDirection)
            .ToList();
        if (nonPrimaryVotes.Count > 0 && nonPrimaryVotes.All(d => d != primary.WinningDirection && d != "neutral"))
        {
            // All non-primary models disagree — use blended scores for direction,
            // reading configurable threshold from weights (same as ScoringEngine)
            var minEdgeMargin = weights.GetValueOrDefault("min_edge_margin", 10.0);
            var margin = blendedBull - blendedBear;
            direction = margin >= minEdgeMargin ? "bullish"
                : -margin >= minEdgeMargin ? "bearish"
                : "neutral";
            if (direction == "neutral") predType = "neutral_no_edge";
        }

        return new ScoringEngine.ScoringResult
        {
            BullishScore = Math.Round(blendedBull, 2),
            BearishScore = Math.Round(blendedBear, 2),
            DirectionalScore = Math.Round(blendedBull - blendedBear, 2),
            WinningDirection = direction,
            DirectionMargin = Math.Round(Math.Abs(blendedBull - blendedBear), 2),
            Confidence = (int)Math.Round(Math.Clamp(blendedConf, 0, 95)),
            Risk = (int)Math.Round(Math.Clamp(blendedRisk, 0, 100)),
            PredictionType = predType,
            Breakdown = primary.Breakdown,
            Signals = primary.Signals,
            Evidence = primary.Evidence,
            Thesis = primary.Thesis,
            Reasoning = primary.Reasoning,
        };
    }

    private async Task<Dictionary<string, double>> GetModelAccuraciesAsync()
    {
        var result = new Dictionary<string, double>();
        var perfStats = await _repo.GetAllSignalPerformanceAsync();

        foreach (var stat in perfStats.Where(s => s.SignalName.StartsWith("ensemble_") && s.TotalPredictions >= 10))
        {
            var modelName = stat.SignalName.Replace("ensemble_", "");
            result[modelName] = stat.Accuracy;
        }

        // Default accuracy for new models
        foreach (var name in ModelProfiles.Keys)
            result.TryAdd(name, 0.5);

        return result;
    }
}
