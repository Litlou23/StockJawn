using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.MarketIntelligence;

public class MarketThesisService : IMarketThesisService
{
    public MarketThesis BuildThesis(string ticker, List<MarketEvidence> evidence, List<MarketFeature> features)
    {
        var now = DateTimeOffset.UtcNow;
        if (evidence.Count == 0)
        {
            return new MarketThesis
            {
                ThesisId = $"thesis_{ticker.ToLowerInvariant()}",
                Ticker = ticker,
                Direction = MarketThesisDirection.neutral,
                Narrative = "Evidence is too sparse to form a directional market thesis.",
                Confidence = 0.35,
                SourceComponents = ["MarketThesisService"],
                GeneratedAt = now,
            };
        }

        var bullishEvidence = evidence.Where(e => e.SupportsBullish).OrderByDescending(WeightEvidence).ToList();
        var bearishEvidence = evidence.Where(e => e.SupportsBearish).OrderByDescending(WeightEvidence).ToList();
        var riskFeatures = features.Where(f => f.Polarity == MarketFeaturePolarity.risk).ToList();

        var bullScore = bullishEvidence.Sum(WeightEvidence);
        var bearScore = bearishEvidence.Sum(WeightEvidence);
        var net = bullScore - bearScore;

        var direction = net switch
        {
            > 0.35 => MarketThesisDirection.bullish,
            < -0.35 => MarketThesisDirection.bearish,
            _ => MarketThesisDirection.neutral,
        };

        var supportList = direction == MarketThesisDirection.bearish ? bearishEvidence : bullishEvidence;
        var topSupport = supportList.Take(3).Select(e => e.Title).ToList();
        var topRisks = riskFeatures.Take(3).ToList();

        var narrative = direction switch
        {
            MarketThesisDirection.bullish => BuildNarrative("bullish", topSupport, topRisks),
            MarketThesisDirection.bearish => BuildNarrative("bearish", topSupport, topRisks),
            _ => BuildNeutralNarrative(bullishEvidence, bearishEvidence, topRisks),
        };

        return new MarketThesis
        {
            ThesisId = $"thesis_{ticker.ToLowerInvariant()}",
            Ticker = ticker,
            Direction = direction,
            Narrative = narrative,
            SupportingEvidence = topSupport,
            Risks = topRisks.Select(f => new ThesisRisk
            {
                Title = f.Name,
                Description = f.Description,
                FeatureIds = [f.FeatureId],
            }).ToList(),
            Confidence = Math.Round(Math.Clamp(0.45 + Math.Abs(net) * 0.25, 0.35, 0.9), 4),
            SourceComponents = ["MarketThesisService", .. topSupport],
            GeneratedAt = now,
        };
    }

    private static string BuildNarrative(string direction, List<string> topSupport, List<MarketFeature> risks)
    {
        var supportText = topSupport.Count > 0 ? string.Join(", ", topSupport) : "limited confirming evidence";
        var riskText = risks.Count > 0
            ? $" Key risks: {string.Join(", ", risks.Select(r => r.Name.ToLowerInvariant()))}."
            : string.Empty;

        return $"The market thesis leans {direction} because {supportText.ToLowerInvariant()} are aligned in the current snapshot.{riskText}";
    }

    private static string BuildNeutralNarrative(
        List<MarketEvidence> bullishEvidence,
        List<MarketEvidence> bearishEvidence,
        List<MarketFeature> risks)
    {
        var bullText = bullishEvidence.Count > 0 ? bullishEvidence[0].Title.ToLowerInvariant() : "some bullish evidence";
        var bearText = bearishEvidence.Count > 0 ? bearishEvidence[0].Title.ToLowerInvariant() : "some bearish evidence";
        var riskText = risks.Count > 0
            ? $" Risk is elevated because {string.Join(", ", risks.Select(r => r.Name.ToLowerInvariant()))}."
            : string.Empty;

        return $"The market thesis is neutral because {bullText} and {bearText} are competing without a clear winner.{riskText}";
    }

    private static double WeightEvidence(MarketEvidence evidence) =>
        StrengthWeight(evidence.Strength) * evidence.Confidence;

    private static double StrengthWeight(EvidenceStrength strength) =>
        strength switch
        {
            EvidenceStrength.decisive => 1.0,
            EvidenceStrength.strong => 0.8,
            EvidenceStrength.moderate => 0.6,
            _ => 0.4,
        };
}
