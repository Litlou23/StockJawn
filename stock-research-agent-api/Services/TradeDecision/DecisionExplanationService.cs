using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Deterministic explanation generator.
///
/// Every string is derived from structured model fields — no string
/// parsing, no debug JSON, no AI. Future AI features should consume
/// the <see cref="DecisionExplanation"/> output rather than rebuilding
/// explanations from raw data.
///
/// Stateless, no I/O, safe to register as singleton.
/// </summary>
public class DecisionExplanationService : IDecisionExplanationService
{
    private const double FavorableRrThreshold = 2.0;

    public DecisionExplanation Explain(DecisionExplanationRequest request)
    {
        var decision = request.Decision;
        var reasons = new List<string>();
        var warnings = new List<string>();
        var failedChecks = new List<string>();
        var evidence = new List<string>();
        var strengths = new List<string>();
        var weaknesses = new List<string>();

        // ── Expected value ───────────────────────────────────────────
        if (request.EvResult is not null)
        {
            if (request.EvResult.PositiveExpectancy)
            {
                reasons.Add("Positive expected value.");
                evidence.Add($"EV = {request.EvResult.ExpectedValue:F2}% per trade (win rate {request.EvResult.WinRate:P0}, avg win {request.EvResult.AverageWinPercent:F1}%, avg loss {request.EvResult.AverageLossPercent:F1}%).");
                strengths.Add("Strong expectancy — historical edge supports this setup.");
            }
            else
            {
                warnings.Add("Negative expected value — historical stats do not support this setup.");
                evidence.Add($"EV = {request.EvResult.ExpectedValue:F2}% per trade.");
                weaknesses.Add("Weak expectancy — losing money on average.");
            }
        }

        // ── Risk/reward ──────────────────────────────────────────────
        if (request.RrResult is not null)
        {
            if (request.RrResult.ValidationError is not null)
            {
                warnings.Add($"Risk/reward could not be computed: {request.RrResult.ValidationError}");
                weaknesses.Add("Risk/reward analysis unavailable.");
            }
            else if (request.RrResult.RiskRewardRatio >= FavorableRrThreshold)
            {
                reasons.Add($"Risk/reward exceeds {FavorableRrThreshold:F0}:1.");
                evidence.Add($"R/R = {request.RrResult.RiskRewardRatio:F2} (risk ${request.RrResult.RiskAmount:F2}, reward ${request.RrResult.RewardAmount:F2}).");
                strengths.Add("Favorable risk/reward profile.");
            }
            else
            {
                warnings.Add($"Risk/reward ratio ({request.RrResult.RiskRewardRatio:F2}) is below the {FavorableRrThreshold:F0}:1 threshold.");
                evidence.Add($"R/R = {request.RrResult.RiskRewardRatio:F2} (risk ${request.RrResult.RiskAmount:F2}, reward ${request.RrResult.RewardAmount:F2}).");
                weaknesses.Add("Moderate risk/reward — potential gain may not justify the risk.");
            }
        }

        // ── Filters ──────────────────────────────────────────────────
        var passed = 0;
        foreach (var filter in request.FilterResults)
        {
            switch (filter.Status)
            {
                case TradeFilterStatus.Pass:
                    passed++;
                    break;
                case TradeFilterStatus.Warning:
                    warnings.Add($"[{filter.FilterName}] {filter.Reason}");
                    weaknesses.Add($"{filter.FilterName} raised a concern.");
                    break;
                case TradeFilterStatus.Fail:
                    failedChecks.Add($"[{filter.FilterName}] {filter.Reason}");
                    weaknesses.Add($"{filter.FilterName} check failed.");
                    break;
            }
        }

        if (passed > 0 && failedChecks.Count == 0)
        {
            reasons.Add("No critical filters failed.");
            strengths.Add($"All {passed} trade filters passed.");
        }
        else if (passed > 0)
        {
            evidence.Add($"{passed}/{request.FilterResults.Count} filters passed.");
        }

        // ── Grade-sourced strengths/weaknesses ───────────────────────
        if (request.GradeResult is not null)
        {
            foreach (var s in request.GradeResult.Strengths)
                if (!strengths.Contains(s)) strengths.Add(s);
            foreach (var w in request.GradeResult.Weaknesses)
                if (!weaknesses.Contains(w)) weaknesses.Add(w);

            evidence.Add($"Trade grade: {request.GradeResult.Grade} (score {request.GradeResult.Score}/100).");
        }

        // ── Confidence / risk context ────────────────────────────────
        if (decision.ConfidenceScore is not null)
        {
            if (decision.ConfidenceScore >= 60)
                strengths.Add($"High confidence score ({decision.ConfidenceScore}).");
            else if (decision.ConfidenceScore < 20)
                weaknesses.Add($"Very low confidence score ({decision.ConfidenceScore}).");
        }

        if (decision.RiskScore is not null && decision.RiskScore >= 75)
            weaknesses.Add($"Elevated risk score ({decision.RiskScore}).");

        // ── Headline ─────────────────────────────────────────────────
        var headline = BuildHeadline(decision, request.GradeResult);

        // ── Summary ──────────────────────────────────────────────────
        var summary = BuildSummary(request.EvResult, request.RrResult, failedChecks.Count);

        // ── Recommendation ───────────────────────────────────────────
        var recommendation = BuildRecommendation(decision.Decision, request.GradeResult);

        return new DecisionExplanation
        {
            Headline = headline,
            Summary = summary,
            Reasons = reasons,
            Warnings = warnings,
            FailedChecks = failedChecks,
            SupportingEvidence = evidence,
            TradeStrengths = strengths,
            TradeWeaknesses = weaknesses,
            Recommendation = recommendation,
        };
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static string BuildHeadline(
        Models.TradeDecision decision,
        TradeGradeResult? grade)
    {
        var quality = grade?.Grade switch
        {
            TradeGrade.APlus => "Exceptional",
            TradeGrade.A     => "High Quality",
            TradeGrade.B     => "Decent",
            TradeGrade.C     => "Marginal",
            TradeGrade.D     => "Weak",
            TradeGrade.Reject => "Poor Quality",
            _                => "Ungraded",
        };

        var direction = (decision.Direction?.ToLowerInvariant()) switch
        {
            "bullish" => "Bullish",
            "bearish" => "Bearish",
            _         => "Neutral",
        };

        return $"{quality} {direction} Opportunity";
    }

    private static string BuildSummary(
        ExpectedValueResult? ev,
        RiskRewardResult? rr,
        int failedCount)
    {
        var parts = new List<string>();

        if (ev is not null)
        {
            parts.Add(ev.PositiveExpectancy
                ? "positive expectancy"
                : "negative expectancy");
        }

        if (rr is not null && rr.ValidationError is null)
        {
            parts.Add(rr.IsFavorable
                ? "favorable risk/reward"
                : "unfavorable risk/reward");
        }

        var qualifier = failedCount == 0
            ? "No critical filters failed."
            : $"{failedCount} filter(s) failed.";

        if (parts.Count == 0)
            return qualifier;

        return $"The trade demonstrates {string.Join(" with ", parts)}. {qualifier}";
    }

    private static string BuildRecommendation(
        TradeDecisionType decisionType,
        TradeGradeResult? grade)
    {
        if (grade?.Grade is TradeGrade.Reject)
            return "Does not meet minimum quality standards — avoid.";

        return decisionType switch
        {
            TradeDecisionType.LiveEligible => "Eligible for live execution.",
            TradeDecisionType.PaperTrade   => "Suitable for paper trading.",
            TradeDecisionType.Consider     => "Suitable for consideration.",
            TradeDecisionType.Watch        => "Monitor only — not yet actionable.",
            TradeDecisionType.Reject       => "Rejected — do not trade.",
            _                              => "No recommendation available.",
        };
    }
}
