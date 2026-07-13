using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

namespace StockResearchAgent.Api.Services.Knowledge;

public interface IKnowledgeRepository
{
    Task StorePatternAsync(MarketPattern pattern);
    Task StoreCaseAsync(HistoricalCase @case);
    Task StoreRuleAsync(KnowledgeRule rule);
    Task<List<SimilarCaseMatch>> FindSimilarCasesAsync(KnowledgeQuery query, int limit = 5);
    Task<List<PatternMatch>> FindMatchingPatternsAsync(KnowledgeQuery query, int limit = 5);
    Task<List<string>> RetrieveLessonsAsync(KnowledgeQuery query, int limit = 10);
    Task<PredictionStatsAggregate?> RetrieveHistoricalStatisticsAsync(KnowledgeQuery query);
    Task<List<KnowledgeRule>> RetrieveRulesAsync(KnowledgeQuery query, int limit = 10);
    Task<List<HistoricalCase>> GetCasesAsync();
    Task<List<MarketPattern>> GetPatternsAsync();
}

public interface ICaseLibraryBuilder
{
    Task<List<HistoricalCase>> BuildCasesAsync(List<PredictionWithOutcome> predictionsWithOutcomes);
}

public interface IKnowledgePatternDetectionService
{
    List<MarketPattern> DetectPatterns(List<HistoricalCase> cases);
}

public interface IConceptLearningService
{
    List<string> InferConcepts(List<MarketFeature> features, List<MarketEvidence> evidence, MarketThesis thesis);
}

public interface IKnowledgeRuleGenerator
{
    List<KnowledgeRule> GenerateRules(List<HistoricalCase> cases, List<MarketPattern> patterns);
}

public interface IKnowledgeRetrievalService
{
    Task<KnowledgeRetrievalResult> RetrieveAsync(EvaluationContext context);
}

public interface IKnowledgeEngine
{
    Task<KnowledgeBuildResult> RunKnowledgeCycleAsync(int limit = 500);
}
