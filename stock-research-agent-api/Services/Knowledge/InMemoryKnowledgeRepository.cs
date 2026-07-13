using System.Collections.Concurrent;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Knowledge;

public class InMemoryKnowledgeRepository : IKnowledgeRepository
{
    private readonly ConcurrentDictionary<string, HistoricalCase> _cases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MarketPattern> _patterns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, KnowledgeRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    public Task StorePatternAsync(MarketPattern pattern)
    {
        _patterns[pattern.PatternId] = pattern;
        return Task.CompletedTask;
    }

    public Task StoreCaseAsync(HistoricalCase @case)
    {
        _cases[@case.CaseId] = @case;
        return Task.CompletedTask;
    }

    public Task StoreRuleAsync(KnowledgeRule rule)
    {
        _rules[rule.RuleId] = rule;
        return Task.CompletedTask;
    }

    public Task<List<SimilarCaseMatch>> FindSimilarCasesAsync(KnowledgeQuery query, int limit = 5)
    {
        var matches = _cases.Values
            .Select(c => new SimilarCaseMatch
            {
                Case = c,
                SimilarityScore = ScoreSimilarity(c, query),
                MatchingSignals = c.Tags.Intersect([.. query.FeatureIds, .. query.EvidenceIds, .. query.Concepts], StringComparer.OrdinalIgnoreCase).ToList(),
            })
            .Where(m => m.SimilarityScore > 0)
            .OrderByDescending(m => m.SimilarityScore)
            .Take(limit)
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<List<PatternMatch>> FindMatchingPatternsAsync(KnowledgeQuery query, int limit = 5)
    {
        var signals = new HashSet<string>([.. query.FeatureIds, .. query.EvidenceIds, .. query.Concepts], StringComparer.OrdinalIgnoreCase);
        var matches = _patterns.Values
            .Select(p =>
            {
                var candidates = p.SupportingFeatures.Concat(p.SupportingEvidence).Concat(p.Concepts).ToList();
                var intersect = candidates.Intersect(signals, StringComparer.OrdinalIgnoreCase).ToList();
                var score = candidates.Count > 0 ? (double)intersect.Count / candidates.Count : 0.0;
                return new PatternMatch { Pattern = p, MatchScore = score, MatchingSignals = intersect };
            })
            .Where(m => m.MatchScore > 0)
            .OrderByDescending(m => m.MatchScore)
            .ThenByDescending(m => m.Pattern.Confidence)
            .Take(limit)
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<List<string>> RetrieveLessonsAsync(KnowledgeQuery query, int limit = 10)
    {
        var lessons = _cases.Values
            .Where(c => ScoreSimilarity(c, query) > 0)
            .SelectMany(c => c.LessonsLearned)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
        return Task.FromResult(lessons);
    }

    public Task<PredictionStatsAggregate?> RetrieveHistoricalStatisticsAsync(KnowledgeQuery query)
    {
        var matches = _cases.Values.Where(c => ScoreSimilarity(c, query) > 0).ToList();
        if (matches.Count == 0) return Task.FromResult<PredictionStatsAggregate?>(null);

        var correct = matches.Count(c => c.Outcome.DirectionCorrect == true);
        var incorrect = matches.Count(c => c.Outcome.DirectionCorrect == false);
        var total = matches.Count;
        return Task.FromResult<PredictionStatsAggregate?>(new PredictionStatsAggregate
        {
            TotalPredictions = total,
            EvaluatedPredictions = total,
            CorrectPredictions = correct,
            IncorrectPredictions = incorrect,
            PendingPredictions = 0,
            InconclusivePredictions = 0,
            AccuracyPercent = total > 0 ? Math.Round(100.0 * correct / total, 1) : null,
        });
    }

    public Task<List<KnowledgeRule>> RetrieveRulesAsync(KnowledgeQuery query, int limit = 10)
    {
        var signals = new HashSet<string>([.. query.FeatureIds, .. query.EvidenceIds, .. query.Concepts], StringComparer.OrdinalIgnoreCase);
        var rules = _rules.Values
            .Where(r => r.Conditions.Any(c => signals.Contains(c)) || r.Concepts.Any(c => signals.Contains(c)))
            .OrderByDescending(r => r.Confidence)
            .Take(limit)
            .ToList();
        return Task.FromResult(rules);
    }

    public Task<List<HistoricalCase>> GetCasesAsync() => Task.FromResult(_cases.Values.OrderByDescending(c => c.Date).ToList());
    public Task<List<MarketPattern>> GetPatternsAsync() => Task.FromResult(_patterns.Values.OrderByDescending(p => p.Confidence).ToList());

    private static double ScoreSimilarity(HistoricalCase @case, KnowledgeQuery query)
    {
        double score = 0;
        if (@case.Ticker.Equals(query.Ticker, StringComparison.OrdinalIgnoreCase)) score += 0.2;
        if (@case.MarketRegime.Equals(query.MarketRegime, StringComparison.OrdinalIgnoreCase)) score += 0.15;
        if (query.PredictionType is not null && @case.Prediction.PredictionType.ToString() == query.PredictionType) score += 0.15;

        score += OverlapScore(@case.Features.Select(f => f.FeatureId), query.FeatureIds, 0.25);
        score += OverlapScore(@case.Evidence.Select(e => e.EvidenceId), query.EvidenceIds, 0.15);
        score += OverlapScore(@case.Concepts, query.Concepts, 0.10);
        return Math.Round(score, 4);
    }

    private static double OverlapScore(IEnumerable<string> left, IEnumerable<string> right, double weight)
    {
        var l = left.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var r = right.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (l.Count == 0 || r.Count == 0) return 0;
        var overlap = l.Intersect(r, StringComparer.OrdinalIgnoreCase).Count();
        return weight * overlap / Math.Max(l.Count, r.Count);
    }
}
