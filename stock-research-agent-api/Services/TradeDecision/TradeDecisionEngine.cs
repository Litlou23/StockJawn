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
/// EV uses real historical stats from TradeStatsProvider (cached 1h).
/// </summary>
public class TradeDecisionEngine : ITradeDecisionEngine
{
    private readonly IExpectedValueCalculator _evCalculator;
    private readonly IRiskRewardAnalyzer _rrAnalyzer;
    private readonly IEnumerable<ITradeFilter> _filters;
    private readonly ITradeGradeService _gradeService;
    private readonly IDecisionExplanationService _explanationService;
    private readonly TradeStatsProvider _statsProvider;

    public TradeDecisionEngine(
        IExpectedValueCalculator evCalculator,
        IRiskRewardAnalyzer rrAnalyzer,
        IEnumerable<ITradeFilter> filters,
        ITradeGradeService gradeService,
        IDecisionExplanationService explanationService,
        TradeStatsProvider statsProvider)
    {
        _evCalculator = evCalculator;
        _rrAnalyzer = rrAnalyzer;
        _filters = filters;
        _gradeService = gradeService;
        _explanationService = explanationService;
        _statsProvider = statsProvider;
    }

    public async Task<Models.TradeDecision> DecideAsync(PredictionCandidate prediction)
    {
        // ── EV calculation from real historical performance ───────────
        var stats = await _statsProvider.GetStatsAsync();
        var evResult = _evCalculator.Calculate(new ExpectedValueRequest
        {
            WinRate = stats.WinRate,
            AverageWinPercent = stats.AverageWinPercent,
            AverageLossPercent = stats.AverageLossPercent,
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
        // Filter failures → Reject (trade is fundamentally flawed).
        // Otherwise → Watch (monitoring for portfolio consideration).
        var failedFilters = filterResults.Where(f => f.Status == TradeFilterStatus.Fail).ToList();
        var decision = TradeDecisionType.Watch;
        var reasons = new List<string>();

        if (failedFilters.Count > 0)
        {
            decision = TradeDecisionType.Reject;
            reasons.Add($"Rejected by {failedFilters.Count} filter(s): {string.Join(", ", failedFilters.Select(f => f.FilterName))}.");
        }
        else
        {
            reasons.Add($"Passed {filterResults.Count} trade filters.");
        }

        var tradeDecision = new Models.TradeDecision
        {
            PredictionId = prediction.Id,
            Ticker = prediction.Ticker,
            Decision = decision,
            TradeGrade = failedFilters.Count > 0 ? TradeGrade.Reject : gradeResult.Grade,
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
            Reasons = reasons,
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
