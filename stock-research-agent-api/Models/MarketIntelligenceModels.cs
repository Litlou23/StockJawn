using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FactCategory
{
    price,
    volume,
    volatility,
    momentum,
    trend,
    benchmark,
    catalyst,
    research_signal,
    ownership,
    event_risk,
    market_structure,
    data_quality,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FactSource
{
    market_snapshot,
    technical_indicator,
    benchmark_context,
    news_context,
    research_signal,
    internal_derivation,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FactValueType
{
    numeric,
    boolean,
    text,
    timestamp,
}

public record FactValue
{
    public FactValueType Type { get; init; }
    public double? NumericValue { get; init; }
    public bool? BooleanValue { get; init; }
    public string? TextValue { get; init; }
    public DateTimeOffset? TimestampValue { get; init; }
    public string? Unit { get; init; }

    public static FactValue Number(double value, string? unit = null) =>
        new() { Type = FactValueType.numeric, NumericValue = value, Unit = unit };

    public static FactValue Flag(bool value) =>
        new() { Type = FactValueType.boolean, BooleanValue = value };

    public static FactValue Text(string value) =>
        new() { Type = FactValueType.text, TextValue = value };

    public static FactValue Timestamp(DateTimeOffset value) =>
        new() { Type = FactValueType.timestamp, TimestampValue = value };
}

public record MarketFact
{
    public string FactId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public FactCategory Category { get; init; }
    public FactSource Source { get; init; }
    public FactValue Value { get; init; } = new();
    public DateTimeOffset ObservedAt { get; init; }
    public List<string> SourceComponents { get; init; } = [];
    public string? Notes { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarketFeaturePolarity
{
    bullish,
    bearish,
    neutral,
    risk,
    informational,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeatureStrength
{
    weak,
    moderate,
    strong,
}

public record MarketFeature
{
    public string FeatureId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public MarketFeaturePolarity Polarity { get; init; }
    public FeatureStrength Strength { get; init; } = FeatureStrength.moderate;
    public double Confidence { get; init; }
    public List<string> FactIds { get; init; } = [];
    public List<string> SourceComponents { get; init; } = [];
    public DateTimeOffset DerivedAt { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceStrength
{
    weak,
    moderate,
    strong,
    decisive,
}

public record MarketEvidence
{
    public string EvidenceId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public bool SupportsBullish { get; init; }
    public bool SupportsBearish { get; init; }
    public EvidenceStrength Strength { get; init; } = EvidenceStrength.moderate;
    public double Confidence { get; init; }
    public List<string> SupportingFeatures { get; init; } = [];
    public List<string> ContradictingFeatures { get; init; } = [];
    public List<string> SourceComponents { get; init; } = [];
    public DateTimeOffset GeneratedAt { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarketThesisDirection
{
    bullish,
    bearish,
    neutral,
}

public record ThesisRisk
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public List<string> FeatureIds { get; init; } = [];
}

public record MarketThesis
{
    public string ThesisId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public MarketThesisDirection Direction { get; init; } = MarketThesisDirection.neutral;
    public string Narrative { get; init; } = "";
    public List<string> SupportingEvidence { get; init; } = [];
    public List<ThesisRisk> Risks { get; init; } = [];
    public double Confidence { get; init; }
    public List<string> SourceComponents { get; init; } = [];
    public DateTimeOffset GeneratedAt { get; init; }
}

public record MarketIntelligenceContext
{
    public string Ticker { get; init; } = "";
    public string PipelineVersion { get; init; } = "phase1_facts_features_evidence_thesis";
    public List<MarketFact> Facts { get; init; } = [];
    public List<MarketFeature> Features { get; init; } = [];
    public List<MarketEvidence> Evidence { get; init; } = [];
    public MarketThesis Thesis { get; init; } = new();
    public DateTimeOffset GeneratedAt { get; init; }
}
