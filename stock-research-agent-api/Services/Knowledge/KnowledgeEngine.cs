using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Knowledge;

public class KnowledgeEngine : IKnowledgeEngine
{
    private readonly ResearchRepository _repo;
    private readonly ICaseLibraryBuilder _caseBuilder;
    private readonly IKnowledgePatternDetectionService _patternDetection;
    private readonly IKnowledgeRuleGenerator _ruleGenerator;
    private readonly IKnowledgeRepository _knowledgeRepo;
    private readonly ILogger<KnowledgeEngine> _logger;

    public KnowledgeEngine(
        ResearchRepository repo,
        ICaseLibraryBuilder caseBuilder,
        IKnowledgePatternDetectionService patternDetection,
        IKnowledgeRuleGenerator ruleGenerator,
        IKnowledgeRepository knowledgeRepo,
        ILogger<KnowledgeEngine> logger)
    {
        _repo = repo;
        _caseBuilder = caseBuilder;
        _patternDetection = patternDetection;
        _ruleGenerator = ruleGenerator;
        _knowledgeRepo = knowledgeRepo;
        _logger = logger;
    }

    public async Task<KnowledgeBuildResult> RunKnowledgeCycleAsync(int limit = 500)
    {
        var predictionsWithOutcomes = await _repo.GetRecentPredictionsWithOutcomesAsync(limit);
        var cases = await _caseBuilder.BuildCasesAsync(predictionsWithOutcomes);
        foreach (var @case in cases)
            await _knowledgeRepo.StoreCaseAsync(@case);

        var patterns = _patternDetection.DetectPatterns(cases);
        foreach (var pattern in patterns)
            await _knowledgeRepo.StorePatternAsync(pattern);

        var rules = _ruleGenerator.GenerateRules(cases, patterns);
        foreach (var rule in rules)
            await _knowledgeRepo.StoreRuleAsync(rule);

        var concepts = cases.SelectMany(c => c.Concepts).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var summary = $"Knowledge cycle complete: {cases.Count} cases indexed, {patterns.Count} patterns detected, {rules.Count} rules generated, {concepts} concepts inferred.";
        _logger.LogInformation("[knowledge-engine] {Summary}", summary);

        return new KnowledgeBuildResult
        {
            CasesIndexed = cases.Count,
            PatternsDetected = patterns.Count,
            RulesGenerated = rules.Count,
            ConceptsInferred = concepts,
            Summary = summary,
        };
    }
}
