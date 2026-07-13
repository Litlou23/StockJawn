using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Evaluates a prediction and produces a capital-allocation decision.
///
/// Pipeline: EV → Risk/Reward → Filters → Grade → Explanation → Decision.
/// Every registered <see cref="ITradeFilter"/> runs on every call —
/// the engine never short-circuits on the first failure.
///
/// Current behaviour: every prediction maps to Watch.
/// Filters are placeholders. EV uses placeholder statistics.
/// </summary>
public class TradeDecisionEngine : ITradeDecisionEngine
{
    private readonly IExpectedValueCalculator _evCalculator;
    private readonly IRiskRewardAnalyzer _rrAnalyzer;
    private readonly IEnumerable<ITradeFilter> _filters;
    private readonly ITradeGradeService _gradeService;
    private readonly IDecisionExplanationService _explanationService;

    public TradeDecisionEngine(
        IExpectedValueCalculator evCalculator,
        IRiskRewardAnalyzer rrAnalyzer,
        IEnumerable<ITradeFilter> filters,
        ITradeGradeService gradeService,
        IDecisionExplanationService explanationService)
    {
        _evCalculator = evCalculator;
        _rrAnalyzer = rrAnalyzer;
        _filters = filters;
        _gradeService = gradeService;
        _explanationService = explanationService;
    }

    public Models.TradeDecision Decide(PredictionCandidate prediction)
    {
        // ── EV calculation (placeholder inputs) ───────────────────────
        var evResult = _evCalculator.Calculate(new ExpectedValueRequest
        {
            WinRate = 0.55,
            AverageWinPercent = 8.0,
            AverageLossPercent = 5.0,
        });

        // ── Risk/reward analysis ──────────────────────────────────────
        var isBullish = prediction.PredictionType == PredictionType.bullish
                     || prediction.WinningDirection == "bullish";

        var rrResult = _rrAnalyzer.Analyze(new RiskRewardRequest
        {
            EntryPrice = prediction.EntryReferencePrice ?? 100.0,
            TargetPrice = prediction.TargetPrice ?? (isBullish ? 110.0 : 90.0),
            StopLossPrice = prediction.StopPrice ?? (isBullish ? 95.0 : 105.0),
            IsBullish = isBullish,
        });

        // ── Run all trade filters ─────────────────────────────────────
        var context = new TradeDecisionContext
        {
            Prediction = prediction,
            EvResult = evResult,
            RrResult = rrResult,
        };

        var filterResults = _filters.Select(f => f.Evaluate(context)).ToList();

        // ── Trade grade ──────────────────────────────────────────────
        var gradeResult = _gradeService.Grade(new TradeGradeRequest
        {
            EvResult = evResult,
            RrResult = rrResult,
            FilterResults = filterResults,
        });

        // ── Warnings (from analytics + filters) ──────────────────────
        var warnings = new List<string>();

        if (prediction.ConfidenceScore < 20)
            warnings.Add("Very low confidence — prediction is exploratory only.");
        if (prediction.RiskScore >= 75)
            warnings.Add($"High risk score ({prediction.RiskScore}) — elevated adverse-move potential.");
        if (string.IsNullOrEmpty(prediction.WinningDirection) ||
            prediction.WinningDirection == "neutral")
            warnings.Add("No directional edge detected.");
        if (!evResult.PositiveExpectancy)
            warnings.Add("Negative expected value — historical stats do not support this setup.");
        if (!rrResult.IsFavorable && rrResult.ValidationError is null)
            warnings.Add($"Risk/reward ratio ({rrResult.RiskRewardRatio:F2}) is below the 2.0 threshold.");
        if (rrResult.ValidationError is not null)
            warnings.Add($"Risk/reward could not be computed: {rrResult.ValidationError}");

        // Surface filter warnings and failures
        foreach (var fr in filterResults)
        {
            if (fr.Status == TradeFilterStatus.Warning)
                warnings.Add($"[{fr.FilterName}] {fr.Reason}");
            else if (fr.Status == TradeFilterStatus.Fail)
                warnings.Add($"[{fr.FilterName}] BLOCKED: {fr.Reason}");
        }

        // ── Decision ──────────────────────────────────────────────────
        // Future phases will use filter results + EV + RR to promote
        // from Watch → Consider → PaperTrade → LiveEligible, and to
        // compute TradeGrade and position sizing.

        var tradeDecision = new Models.TradeDecision
        {
            PredictionId = prediction.Id,
            Ticker = prediction.Ticker,
            Decision = TradeDecisionType.Watch,
            TradeGrade = gradeResult.Grade,
            ExpectedValue = evResult.ExpectedValue,
            RiskRewardRatio = rrResult.RiskRewardRatio > 0 ? rrResult.RiskRewardRatio : prediction.RiskRewardRatio,
            RecommendedPositionSize = null,
            ExpectedValueResult = evResult,
            RiskRewardResult = rrResult,
            FilterResults = filterResults,
            GradeResult = gradeResult,
            ConfidenceScore = prediction.ConfidenceScore,
            RiskScore = prediction.RiskScore,
            Direction = prediction.WinningDirection ?? prediction.PredictionType.ToString(),
            SetupFingerprint = null,
            Reasons = [$"Ran {filterResults.Count} trade filters. Decision gating arrives in a future phase."],
            Warnings = warnings,
        };

        // ── Explanation ──────────────────────────────────────────────
        var explanation = _explanationService.Explain(new DecisionExplanationRequest
        {
            Decision = tradeDecision,
            GradeResult = gradeResult,
            EvResult = evResult,
            RrResult = rrResult,
            FilterResults = filterResults,
        });

        return tradeDecision with { Explanation = explanation };
    }
}
