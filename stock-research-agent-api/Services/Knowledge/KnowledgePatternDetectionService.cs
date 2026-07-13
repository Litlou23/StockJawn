using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Knowledge;

public class KnowledgePatternDetectionService : IKnowledgePatternDetectionService
{
    public List<MarketPattern> DetectPatterns(List<HistoricalCase> cases)
    {
        var patterns = new List<MarketPattern>();
        var combos = new Dictionary<string, List<HistoricalCase>>(StringComparer.OrdinalIgnoreCase);

        foreach (var @case in cases)
        {
            var tags = @case.Features.Select(f => f.FeatureId)
                .Concat(@case.Evidence.Select(e => e.EvidenceId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
            for (int i = 0; i < tags.Count; i++)
            {
                for (int j = i + 1; j < tags.Count; j++)
                {
                    var key = $"{tags[i]}+{tags[j]}";
                    if (!combos.ContainsKey(key)) combos[key] = [];
                    combos[key].Add(@case);
                }
            }
        }

        foreach (var (key, matchedCases) in combos.Where(kvp => kvp.Value.Count >= 5))
        {
            var sample = matchedCases.Count;
            var correct = matchedCases.Count(c => c.Outcome.DirectionCorrect == true);
            var winRate = (double)correct / sample;
            var avgReturn = matchedCases.Average(c => c.Outcome.PercentMove ?? 0);
            var avgDrawdown = matchedCases.Average(c => c.MaximumAdverseExcursion ?? 0);
            var parts = key.Split('+');

            patterns.Add(new MarketPattern
            {
                PatternId = $"pattern_{key}",
                Name = string.Join(" + ", parts.Select(p => p.Replace("_", " "))),
                Description = $"Generated pattern from recurring combination {key.Replace("_", " ")}.",
                PatternType = ClassifyPattern(parts),
                SupportingFeatures = matchedCases.SelectMany(c => c.Features.Select(f => f.FeatureId)).Distinct(StringComparer.OrdinalIgnoreCase).Where(parts.Contains).ToList(),
                SupportingEvidence = matchedCases.SelectMany(c => c.Evidence.Select(e => e.EvidenceId)).Distinct(StringComparer.OrdinalIgnoreCase).Where(parts.Contains).ToList(),
                HistoricalSampleSize = sample,
                WinRate = Math.Round(winRate, 4),
                AverageReturn = Math.Round(avgReturn, 4),
                AverageDrawdown = Math.Round(avgDrawdown, 4),
                MarketRegimes = matchedCases.Select(c => c.MarketRegime).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Confidence = Math.Round(Math.Min(0.95, 0.4 + sample / 40.0 + Math.Abs(winRate - 0.5)), 4),
                LastSeen = matchedCases.Max(c => c.Date),
                Concepts = matchedCases.SelectMany(c => c.Concepts).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            });
        }

        return patterns.OrderByDescending(p => p.Confidence).ToList();
    }

    private static PatternType ClassifyPattern(string[] parts)
    {
        if (parts.Any(p => p.Contains("event") || p.Contains("earnings"))) return PatternType.thesis_catalyst;
        if (parts.Any(p => p.Contains("risk") || p.Contains("volatility"))) return PatternType.risk_pattern;
        if (parts.Any(p => p.Contains("institutional") || p.Contains("sector"))) return PatternType.concept_pattern;
        if (parts.Any(p => p.Contains("trend") || p.Contains("momentum"))) return PatternType.feature_combination;
        return PatternType.evidence_combination;
    }
}
