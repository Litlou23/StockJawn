using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchEngine;
using ResearchSignal = StockResearchAgent.Api.Models.ResearchSignal;

namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public interface IEvaluator
{
    EvaluatorKind Kind { get; }
    EvaluatorOutput Evaluate(EvaluationContext context);
}

public interface ITrendEvaluator : IEvaluator { }
public interface IMomentumEvaluator : IEvaluator { }
public interface IVolumeEvaluator : IEvaluator { }
public interface IVolatilityEvaluator : IEvaluator { }
public interface IMarketContextEvaluator : IEvaluator { }
public interface ICatalystEvaluator : IEvaluator
{
    double ScoreCatalystStrength(EvaluationContext context);
}
public interface ILearningAdjustmentEvaluator : IEvaluator { }
public interface IResearchSignalEvaluator : IEvaluator { }

public interface IScoreAggregator
{
    AggregateScoreResult Aggregate(IReadOnlyList<EvaluatorOutput> outputs, string winningDirection, EvaluationContext context);
}

public interface IConfidenceEngine
{
    ConfidenceResult Evaluate(
        EvaluationContext context,
        AggregateScoreResult aggregate,
        RiskAssessment riskAssessment,
        string winningDirection);
}

public interface IRiskEngine
{
    RiskAssessment Evaluate(EvaluationContext context, string predictionType);
}

public interface IScoringEngine
{
    ScoringEngine.ScoringResult Evaluate(
        MarketSnapshot snapshot,
        TechnicalIndicators indicators,
        BenchmarkContext benchmark,
        Dictionary<string, double> weights,
        List<string> lessons,
        List<ResearchSignal>? researchSignals = null,
        MarketIntelligenceContext? intelligence = null,
        ResearchUniverseContext? researchUniverse = null);
}
