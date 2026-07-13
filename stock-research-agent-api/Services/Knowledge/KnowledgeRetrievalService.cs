using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

namespace StockResearchAgent.Api.Services.Knowledge;

public class KnowledgeRetrievalService : IKnowledgeRetrievalService
{
    private readonly IKnowledgeRepository _repo;
    private readonly IConceptLearningService _concepts;

    public KnowledgeRetrievalService(IKnowledgeRepository repo, IConceptLearningService concepts)
    {
        _repo = repo;
        _concepts = concepts;
    }

    public async Task<KnowledgeRetrievalResult> RetrieveAsync(EvaluationContext context)
    {
        var concepts = _concepts.InferConcepts(
            context.Intelligence.Features,
            context.Intelligence.Evidence,
            context.Intelligence.Thesis);

        var query = new KnowledgeQuery
        {
            Ticker = context.Ticker,
            MarketRegime = context.MarketRegime.RegimeId,
            FeatureIds = context.Intelligence.Features.Select(f => f.FeatureId).ToList(),
            EvidenceIds = context.Intelligence.Evidence.Select(e => e.EvidenceId).ToList(),
            Concepts = concepts,
            PredictionType = null,
        };

        var similarCases = await _repo.FindSimilarCasesAsync(query, 5);
        var patterns = await _repo.FindMatchingPatternsAsync(query, 5);
        var lessons = await _repo.RetrieveLessonsAsync(query, 10);
        var stats = await _repo.RetrieveHistoricalStatisticsAsync(query);
        var rules = await _repo.RetrieveRulesAsync(query, 10);

        var avgHoldingDays = similarCases.Count > 0
            ? similarCases.Average(c => Math.Max(0, (c.Case.Outcome.EvaluationTime - c.Case.Date).TotalDays))
            : (double?)null;

        var knownRisks = patterns
            .Where(p => p.Pattern.PatternType == PatternType.risk_pattern || p.Pattern.AverageDrawdown < -2.0)
            .Select(p => p.Pattern.Description)
            .Concat(rules.Where(r => r.RuleType == KnowledgeRuleType.risk_guardrail || r.RuleType == KnowledgeRuleType.adverse_condition)
                .Select(r => r.Description))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new KnowledgeRetrievalResult
        {
            SimilarCases = similarCases,
            MatchingPatterns = patterns,
            KnownRisks = knownRisks,
            HistoricalWinRate = stats?.AccuracyPercent,
            AverageHoldingTimeDays = avgHoldingDays,
            RelevantLessons = lessons,
            MatchingRules = rules,
        };
    }
}
