using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class ScoreAggregator : IScoreAggregator
{
    public AggregateScoreResult Aggregate(IReadOnlyList<EvaluatorOutput> outputs, string winningDirection, EvaluationContext context)
    {
        var bullishScore = Math.Clamp(outputs.Sum(o => o.BullishContribution), 0, 100);
        var bearishScore = Math.Clamp(outputs.Sum(o => o.BearishContribution), 0, 100);
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
