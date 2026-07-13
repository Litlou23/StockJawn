using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Knowledge;

public class KnowledgeRuleGenerator : IKnowledgeRuleGenerator
{
    public List<KnowledgeRule> GenerateRules(List<HistoricalCase> cases, List<MarketPattern> patterns)
    {
        var rules = new List<KnowledgeRule>();

        foreach (var pattern in patterns.Where(p => p.HistoricalSampleSize >= 5))
        {
            var ruleType = pattern.WinRate >= 0.6 ? KnowledgeRuleType.favorable_condition : KnowledgeRuleType.adverse_condition;
            rules.Add(new KnowledgeRule
            {
                RuleId = $"rule_{pattern.PatternId}",
                Name = pattern.Name,
                Description = pattern.WinRate >= 0.6
                    ? $"{pattern.Name} tends to perform well when repeated historically."
                    : $"{pattern.Name} tends to underperform historically.",
                RuleType = ruleType,
                Conditions = [.. pattern.SupportingFeatures, .. pattern.SupportingEvidence],
                WinRate = pattern.WinRate,
                AverageReturn = pattern.AverageReturn,
                SampleSize = pattern.HistoricalSampleSize,
                Confidence = pattern.Confidence,
                Concepts = pattern.Concepts,
            });
        }

        var groupedByConcept = cases.SelectMany(c => c.Concepts.Select(concept => (concept, c)))
            .GroupBy(x => x.concept)
            .Where(g => g.Count() >= 5);
        foreach (var group in groupedByConcept)
        {
            var sample = group.Count();
            var winRate = group.Count(x => x.c.Outcome.DirectionCorrect == true) / (double)sample;
            rules.Add(new KnowledgeRule
            {
                RuleId = $"rule_concept_{group.Key}",
                Name = group.Key.Replace("_", " "),
                Description = $"Observed concept behavior for {group.Key.Replace("_", " ")}.",
                RuleType = KnowledgeRuleType.concept_observation,
                Conditions = [group.Key],
                WinRate = Math.Round(winRate, 4),
                AverageReturn = Math.Round(group.Average(x => x.c.Outcome.PercentMove ?? 0), 4),
                SampleSize = sample,
                Confidence = Math.Round(Math.Min(0.95, 0.35 + sample / 30.0), 4),
                Concepts = [group.Key],
            });
        }

        return rules;
    }
}
