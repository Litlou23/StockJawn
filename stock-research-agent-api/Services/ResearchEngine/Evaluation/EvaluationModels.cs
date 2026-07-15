using StockResearchAgent.Api.Models;
using ResearchSignal = StockResearchAgent.Api.Models.ResearchSignal;

namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public enum EvaluatorKind
{
    trend,
    momentum,
    volume,
    volatility,
    market_context,
    catalyst,
    learning,
    research_signal,
}

public record EvaluationLearningData
{
    public IReadOnlyDictionary<string, double> Weights { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<string> Lessons { get; init; } = [];
    public double CalibrationFactor { get; init; } = 1.0;
    public int RiskCapBoost { get; init; }
}

public record EvaluationHistoricalStatistics
{
    public IReadOnlyDictionary<string, double> Statistics { get; init; } = new Dictionary<string, double>();
}

public record MarketRegimeSnapshot
{
    public string RegimeId { get; init; } = "unclassified";
    public string Description { get; init; } = "placeholder";
}

public record PredictionSettings
{
    public double MinEdgeMargin { get; init; } = 10;
    public double MinScoreForDirection { get; init; } = 20;
}

/// <summary>
/// Research Universe context injected into the evaluation pipeline.
/// Carries InterestScore, EvidenceCount, and ResearchState from the
/// Research Universe so evaluators and confidence calibration can
/// use discovery-phase data alongside live market signals.
///
/// When null/default (e.g., watchlist fallback tickers with no
/// ResearchAsset), all fields are zero/Discovered — scoring
/// treats the asset as having no research universe signal.
/// </summary>
public record ResearchUniverseContext
{
    /// <summary>Discovery-accumulated interest score (0-100).</summary>
    public int InterestScore { get; init; }

    /// <summary>Number of evidence items accumulated for this asset.</summary>
    public int EvidenceCount { get; init; }

    /// <summary>Current lifecycle state in the Research Universe.</summary>
    public ResearchState ResearchState { get; init; } = ResearchState.Discovered;

    /// <summary>Days the asset has been actively researched.</summary>
    public int DaysActive { get; init; }

    /// <summary>Whether this context came from a real ResearchAsset (true)
    /// or is a default placeholder for watchlist-fallback tickers (false).</summary>
    public bool HasResearchAsset { get; init; }

    /// <summary>Historical volatility from the profile (annualized %). Null if no profile.</summary>
    public double? HistoricalVolatility { get; init; }

    /// <summary>Historical ATR% from the profile. Null if no profile.</summary>
    public double? HistoricalAtrPercent { get; init; }

    /// <summary>Previous prediction accuracy for this ticker from profile. Null if no profile.</summary>
    public double? PreviousPredictionAccuracy { get; init; }

    /// <summary>Previous prediction count from profile. 0 if no profile.</summary>
    public int PreviousPredictionCount { get; init; }
}

public record EvaluationContext
{
    public string Ticker { get; init; } = "";
    public MarketSnapshot Snapshot { get; init; } = new();
    public TechnicalIndicators Indicators { get; init; } = new();
    public BenchmarkContext Benchmark { get; init; } = new();
    public MarketIntelligenceContext Intelligence { get; init; } = new();
    public IReadOnlyList<ResearchSignal> ResearchSignals { get; init; } = [];
    public EvaluationLearningData LearningData { get; init; } = new();
    public EvaluationHistoricalStatistics HistoricalStatistics { get; init; } = new();
    public MarketRegimeSnapshot MarketRegime { get; init; } = new();
    /// <summary>
    /// Rich multi-regime classification from <see cref="Services.MarketRegime.IMarketRegimeEngine"/>.
    /// Null until the regime engine is wired into the evaluation pipeline.
    /// </summary>
    public MarketRegimeResult? MarketRegimeResult { get; init; }
    public PredictionSettings PredictionSettings { get; init; } = new();

    /// <summary>
    /// Research Universe context — InterestScore, EvidenceCount, ResearchState.
    /// Default (empty) when no ResearchAsset is available for this ticker.
    /// </summary>
    public ResearchUniverseContext ResearchUniverse { get; init; } = new();

    /// <summary>
    /// Volatility Opportunity Engine assessment. Null when VOE has not run
    /// (e.g. static Score() compat path). The VolatilityEvaluator falls back
    /// to legacy Bollinger-only scoring when this is null.
    /// </summary>
    public VolatilityOpportunityAssessment? VolatilityAssessment { get; init; }

    public static EvaluationContext Create(
        MarketSnapshot snapshot,
        TechnicalIndicators indicators,
        BenchmarkContext benchmark,
        MarketIntelligenceContext intelligence,
        Dictionary<string, double> weights,
        List<string> lessons,
        List<ResearchSignal> researchSignals,
        ResearchUniverseContext? researchUniverse = null,
        VolatilityOpportunityAssessment? volatilityAssessment = null)
    {
        var riskCapBoost = (int)Math.Clamp(weights.GetValueOrDefault("risk_cap_boost", 0.0), 0, 15);
        var calibrationFactor = Math.Clamp(weights.GetValueOrDefault("calibration_factor", 1.0), 0.85, 1.15);

        return new EvaluationContext
        {
            Ticker = snapshot.Ticker,
            Snapshot = snapshot,
            Indicators = indicators,
            Benchmark = benchmark,
            Intelligence = intelligence,
            ResearchSignals = researchSignals,
            ResearchUniverse = researchUniverse ?? new ResearchUniverseContext(),
            VolatilityAssessment = volatilityAssessment,
            LearningData = new EvaluationLearningData
            {
                Weights = new Dictionary<string, double>(weights),
                Lessons = lessons,
                CalibrationFactor = calibrationFactor,
                RiskCapBoost = riskCapBoost,
            },
        };
    }
}

public record EvaluatorReasoning
{
    public string EvaluatorName { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<string> Reasons { get; init; } = [];
    public List<string> SupportingFeatureIds { get; init; } = [];
    public List<string> SupportingEvidenceIds { get; init; } = [];
    public List<string> SourceComponents { get; init; } = [];
}

public record EvaluatorOutput
{
    public EvaluatorKind Kind { get; init; }
    public double BullishContribution { get; init; }
    public double BearishContribution { get; init; }
    public IReadOnlyList<string> SupportingEvidence { get; init; } = [];
    public double ConfidenceModifier { get; init; }
    public double RiskModifier { get; init; }
    public IReadOnlyList<string> DebugSignals { get; init; } = [];
    public EvaluatorReasoning DebugInformation { get; init; } = new();
    public bool ParticipatesInConfirmation { get; init; } = true;
}

public record AggregateScoreResult
{
    public double BullishScore { get; init; }
    public double BearishScore { get; init; }
    public double DirectionalScore { get; init; }
    public IReadOnlyDictionary<EvaluatorKind, EvaluatorOutput> Outputs { get; init; } =
        new Dictionary<EvaluatorKind, EvaluatorOutput>();
    public int AlignedBuckets { get; init; }
    public int ConflictingBuckets { get; init; }
    public double EvidenceAgreement { get; init; }
    public double FeatureAgreement { get; init; }
}

public record RiskAssessment
{
    public int RiskScore { get; init; }
    public double RiskPenalty { get; init; }
    public bool EarningsNear { get; init; }
    public IReadOnlyList<string> DebugSignals { get; init; } = [];
}

public record ConfidenceResult
{
    public int Confidence { get; init; }
    public double DataQualityFactor { get; init; }
    public double ConfirmationMultiplier { get; init; }
    public double RiskAdjustment { get; init; }
    public double CalibrationFactor { get; init; }
    public double OppositionPenalty { get; init; }
    public double DecisionMargin { get; init; }
    public bool ClearDirection { get; init; }
    public string? ConfidenceCap { get; init; }
}
