using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PatternType
{
    feature_combination,
    evidence_combination,
    thesis_catalyst,
    regime_behavior,
    risk_pattern,
    concept_pattern,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeRuleType
{
    favorable_condition,
    adverse_condition,
    risk_guardrail,
    concept_observation,
}

public record MarketPattern
{
    public string PatternId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public PatternType PatternType { get; init; }
    public List<string> SupportingFeatures { get; init; } = [];
    public List<string> SupportingEvidence { get; init; } = [];
    public int HistoricalSampleSize { get; init; }
    public double WinRate { get; init; }
    public double AverageReturn { get; init; }
    public double AverageDrawdown { get; init; }
    public List<string> MarketRegimes { get; init; } = [];
    public double Confidence { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public List<string> Concepts { get; init; } = [];
}

public record KnowledgeRule
{
    public string RuleId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public KnowledgeRuleType RuleType { get; init; }
    public List<string> Conditions { get; init; } = [];
    public double WinRate { get; init; }
    public double AverageReturn { get; init; }
    public int SampleSize { get; init; }
    public double Confidence { get; init; }
    public List<string> Concepts { get; init; } = [];
}

public record HistoricalCase
{
    public string CaseId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public string MarketRegime { get; init; } = "unknown";
    public List<MarketFact> Facts { get; init; } = [];
    public List<MarketFeature> Features { get; init; } = [];
    public List<MarketEvidence> Evidence { get; init; } = [];
    public MarketThesis MarketThesis { get; init; } = new();
    public PredictionCandidate Prediction { get; init; } = null!;
    public PredictionOutcome Outcome { get; init; } = null!;
    public double? MaximumFavorableExcursion { get; init; }
    public double? MaximumAdverseExcursion { get; init; }
    public List<string> LessonsLearned { get; init; } = [];
    public List<string> Concepts { get; init; } = [];
    public List<string> Tags { get; init; } = [];
}

public record SimilarCaseMatch
{
    public HistoricalCase Case { get; init; } = null!;
    public double SimilarityScore { get; init; }
    public List<string> MatchingSignals { get; init; } = [];
}

public record PatternMatch
{
    public MarketPattern Pattern { get; init; } = null!;
    public double MatchScore { get; init; }
    public List<string> MatchingSignals { get; init; } = [];
}

public record KnowledgeQuery
{
    public string Ticker { get; init; } = "";
    public string MarketRegime { get; init; } = "unknown";
    public List<string> FeatureIds { get; init; } = [];
    public List<string> EvidenceIds { get; init; } = [];
    public List<string> Concepts { get; init; } = [];
    public string? PredictionType { get; init; }
}

public record KnowledgeRetrievalResult
{
    public List<SimilarCaseMatch> SimilarCases { get; init; } = [];
    public List<PatternMatch> MatchingPatterns { get; init; } = [];
    public List<string> KnownRisks { get; init; } = [];
    public double? HistoricalWinRate { get; init; }
    public double? AverageHoldingTimeDays { get; init; }
    public List<string> RelevantLessons { get; init; } = [];
    public List<KnowledgeRule> MatchingRules { get; init; } = [];
}

public record KnowledgeBuildResult
{
    public int CasesIndexed { get; init; }
    public int PatternsDetected { get; init; }
    public int RulesGenerated { get; init; }
    public int ConceptsInferred { get; init; }
    public string Summary { get; init; } = "";
}
