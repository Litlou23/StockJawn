using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Knowledge;

public class ConceptLearningService : IConceptLearningService
{
    public List<string> InferConcepts(List<MarketFeature> features, List<MarketEvidence> evidence, MarketThesis thesis)
    {
        var featureIds = features.Select(f => f.FeatureId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evidenceIds = evidence.Select(e => e.EvidenceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var concepts = new List<string>();

        if (featureIds.Contains("institutional_buying") && featureIds.Contains("high_relative_volume"))
            concepts.Add("institutional_accumulation");
        if (featureIds.Contains("institutional_selling") || evidenceIds.Contains("institutional_distribution"))
            concepts.Add("distribution");
        if (featureIds.Contains("strong_uptrend") && featureIds.Contains("momentum_accelerating_bullish") && featureIds.Contains("high_volatility"))
            concepts.Add("volatility_expansion");
        if (featureIds.Contains("weak_trend") && featureIds.Contains("high_volatility"))
            concepts.Add("trend_exhaustion");
        if (featureIds.Contains("sector_leadership") && thesis.Direction == MarketThesisDirection.bullish)
            concepts.Add("sector_leadership");
        if (featureIds.Contains("event_risk") && thesis.Direction == MarketThesisDirection.bullish)
            concepts.Add("earnings_catalyst");
        if (featureIds.Contains("sector_lagging") && featureIds.Contains("high_volatility"))
            concepts.Add("market_panic");

        return concepts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
