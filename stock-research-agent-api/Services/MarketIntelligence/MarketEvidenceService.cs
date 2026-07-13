using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.MarketIntelligence;

public class MarketEvidenceService : IMarketEvidenceService
{
    public List<MarketEvidence> BuildEvidence(string ticker, List<MarketFeature> features)
    {
        var evidence = new List<MarketEvidence>();
        var featureMap = features.ToDictionary(f => f.FeatureId, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        AddIfAny(evidence, ticker, "trend_confirmation", "Trend Confirmation",
            "Trend structure is aligned enough to support a directional thesis.",
            supporting: ["strong_uptrend"],
            contradicting: ["strong_downtrend", "weak_trend"],
            supportsBullish: true, supportsBearish: false, now, featureMap);

        AddIfAny(evidence, ticker, "trend_deterioration", "Trend Deterioration",
            "Trend structure is aligned enough to support a bearish thesis.",
            supporting: ["strong_downtrend"],
            contradicting: ["strong_uptrend", "weak_trend"],
            supportsBullish: false, supportsBearish: true, now, featureMap);

        AddIfAny(evidence, ticker, "momentum_expansion", "Momentum Expansion",
            "Momentum and participation are moving in the same direction.",
            supporting: ["momentum_accelerating_bullish", "high_relative_volume"],
            contradicting: ["momentum_accelerating_bearish"],
            supportsBullish: true, supportsBearish: false, now, featureMap);

        AddIfAny(evidence, ticker, "momentum_deterioration", "Momentum Deterioration",
            "Momentum is fading with enough confirmation to pressure the setup lower.",
            supporting: ["momentum_accelerating_bearish"],
            contradicting: ["momentum_accelerating_bullish", "high_relative_volume"],
            supportsBullish: false, supportsBearish: true, now, featureMap);

        AddIfAny(evidence, ticker, "relative_strength_leadership", "Relative Strength Leadership",
            "Relative performance versus benchmarks is supporting the long thesis.",
            supporting: ["sector_leadership"],
            contradicting: ["sector_lagging"],
            supportsBullish: true, supportsBearish: false, now, featureMap);

        AddIfAny(evidence, ticker, "relative_strength_drag", "Relative Weakness",
            "Relative performance versus benchmarks is leaning against the long thesis.",
            supporting: ["sector_lagging"],
            contradicting: ["sector_leadership"],
            supportsBullish: false, supportsBearish: true, now, featureMap);

        AddIfAny(evidence, ticker, "breakout_confirmation", "Breakout Confirmation",
            "Market structure shows price forcing through resistance.",
            supporting: ["resistance_break", "support_holding"],
            contradicting: ["support_break"],
            supportsBullish: true, supportsBearish: false, now, featureMap);

        AddIfAny(evidence, ticker, "breakdown_confirmation", "Breakdown Confirmation",
            "Market structure shows price losing support.",
            supporting: ["support_break"],
            contradicting: ["support_holding", "resistance_break"],
            supportsBullish: false, supportsBearish: true, now, featureMap);

        AddIfAny(evidence, ticker, "institutional_accumulation", "Institutional Accumulation",
            "External ownership or informed-flow signals reinforce the long case.",
            supporting: ["institutional_buying"],
            contradicting: ["institutional_selling"],
            supportsBullish: true, supportsBearish: false, now, featureMap);

        AddIfAny(evidence, ticker, "institutional_distribution", "Institutional Distribution",
            "External ownership or informed-flow signals reinforce the short case.",
            supporting: ["institutional_selling"],
            contradicting: ["institutional_buying"],
            supportsBullish: false, supportsBearish: true, now, featureMap);

        AddIfAny(evidence, ticker, "volatility_risk", "Volatility Risk",
            "The setup is exposed to larger-than-normal movement, which weakens clean execution.",
            supporting: ["high_volatility"],
            contradicting: ["adequate_signal_coverage"],
            supportsBullish: false, supportsBearish: false, now, featureMap);

        AddIfAny(evidence, ticker, "event_risk", "Event Risk",
            "A nearby catalyst can dominate technical structure and invalidate a clean directional read.",
            supporting: ["event_risk"],
            contradicting: ["adequate_signal_coverage"],
            supportsBullish: false, supportsBearish: false, now, featureMap);

        return evidence;
    }

    private static void AddIfAny(
        List<MarketEvidence> target,
        string ticker,
        string id,
        string title,
        string description,
        IEnumerable<string> supporting,
        IEnumerable<string> contradicting,
        bool supportsBullish,
        bool supportsBearish,
        DateTimeOffset generatedAt,
        Dictionary<string, MarketFeature> featureMap)
    {
        var supportingFeatures = supporting.Where(featureMap.ContainsKey).ToList();
        if (supportingFeatures.Count == 0) return;

        var contradictingFeatures = contradicting.Where(featureMap.ContainsKey).ToList();
        var confidence = supportingFeatures
            .Select(idValue => featureMap[idValue].Confidence)
            .DefaultIfEmpty(0.5)
            .Average();

        target.Add(new MarketEvidence
        {
            EvidenceId = id,
            Ticker = ticker,
            Title = title,
            Description = description,
            SupportsBullish = supportsBullish,
            SupportsBearish = supportsBearish,
            Strength = StrengthFromConfidence(confidence),
            Confidence = Math.Round(confidence, 4),
            SupportingFeatures = supportingFeatures,
            ContradictingFeatures = contradictingFeatures,
            SourceComponents = ["MarketEvidenceService", .. supportingFeatures],
            GeneratedAt = generatedAt,
        });
    }

    private static EvidenceStrength StrengthFromConfidence(double confidence) =>
        confidence switch
        {
            >= 0.9 => EvidenceStrength.decisive,
            >= 0.78 => EvidenceStrength.strong,
            >= 0.62 => EvidenceStrength.moderate,
            _ => EvidenceStrength.weak,
        };
}
