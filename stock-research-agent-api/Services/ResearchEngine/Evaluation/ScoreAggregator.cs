using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class ScoreAggregator : IScoreAggregator
{
    // Evaluator kinds whose contributions can be scaled by profile weight configs.
    // Keys must match the config_key values stored in prediction_profile_configs.
    internal static readonly Dictionary<EvaluatorKind, string> WeightableKinds = new()
    {
        { EvaluatorKind.trend, "trend" },
        { EvaluatorKind.momentum, "momentum" },
        { EvaluatorKind.volume, "volume" },
        { EvaluatorKind.volatility, "volatility" },
        { EvaluatorKind.market_context, "market_context" },
        { EvaluatorKind.catalyst, "catalyst" },
        { EvaluatorKind.research_signal, "research_signal" },
    };

    public AggregateScoreResult Aggregate(IReadOnlyList<EvaluatorOutput> outputs, string winningDirection, EvaluationContext context)
    {
        // Apply profile weight scaling: if a weight key (e.g. "trend") exists in the
        // weights dictionary, scale that evaluator's contribution accordingly.
        // Missing keys default to 1.0 (no change) — champion uses base weights.
        var weights = context.LearningData.Weights;
        double bullishScore = 0, bearishScore = 0;
        foreach (var o in outputs)
        {
            double scale = 1.0;
            if (WeightableKinds.TryGetValue(o.Kind, out var weightKey))
                scale = weights.TryGetValue(weightKey, out var w) ? w : 1.0;
            bullishScore += o.BullishContribution * scale;
            bearishScore += o.BearishContribution * scale;
        }
        bullishScore = Math.Clamp(bullishScore, 0, 100);
        bearishScore = Math.Clamp(bearishScore, 0, 100);
        var directionalScore = bullishScore - bearishScore;

        int aligned = 0, conflicting = 0;
        bool winIsBullish = winningDirection == "bullish";
        foreach (var output in outputs.Where(o => o.ParticipatesInConfirmation))
        {
            var net = output.BullishContribution - output.BearishContribution;
            if (Math.Abs(net) < 1) continue;
            bool bucketVotesBullish = net > 0;
            if (bucketVotesBullish == winIsBullish) aligned++;
            else conflicting++;
        }

        var evidence = context.Intelligence.Evidence;
        var supportsBull = evidence.Count(e => e.SupportsBullish);
        var supportsBear = evidence.Count(e => e.SupportsBearish);
        var evidenceAgreement = (supportsBull + supportsBear) > 0
            ? Math.Abs(supportsBull - supportsBear) / (double)(supportsBull + supportsBear)
            : 0.0;

        var directionalFeatures = context.Intelligence.Features
            .Where(f => f.Polarity is MarketFeaturePolarity.bullish or MarketFeaturePolarity.bearish)
            .ToList();
        var bullFeatures = directionalFeatures.Count(f => f.Polarity == MarketFeaturePolarity.bullish);
        var bearFeatures = directionalFeatures.Count(f => f.Polarity == MarketFeaturePolarity.bearish);
        var featureAgreement = directionalFeatures.Count > 0
            ? Math.Abs(bullFeatures - bearFeatures) / (double)directionalFeatures.Count
            : 0.0;

        return new AggregateScoreResult
        {
            BullishScore = bullishScore,
            BearishScore = bearishScore,
            DirectionalScore = directionalScore,
            Outputs = outputs.ToDictionary(o => o.Kind, o => o),
            AlignedBuckets = aligned,
            ConflictingBuckets = conflicting,
            EvidenceAgreement = evidenceAgreement,
            FeatureAgreement = featureAgreement,
        };
    }
}
