using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Deterministic trade grading.
///
/// Scoring rules (v1):
///   Start at 100.
///   Per Warning filter result:  -10
///   Per Failed  filter result:  -25
///   Negative expected value:    -20
///   Risk/reward ratio &lt; 2.0:    -15
///   Clamp to [0, 100].
///
/// Grade map:
///   95–100 → A+
///   85–94  → A
///   70–84  → B
///   55–69  → C
///   40–54  → D
///    0–39  → Reject
///
/// Stateless, no I/O, safe to register as singleton.
/// </summary>
public class TradeGradeService : ITradeGradeService
{
    private const int StartingScore = 100;
    private const int WarningPenalty = 10;
    private const int FailurePenalty = 25;
    private const int NegativeEvPenalty = 20;
    private const int UnfavorableRrPenalty = 15;
    private const double FavorableRrThreshold = 2.0;

    public TradeGradeResult Grade(TradeGradeRequest request)
    {
        var score = StartingScore;
        var strengths = new List<string>();
        var weaknesses = new List<string>();

        // ── Filter penalties ─────────────────────────────────────────
        var warnings = request.FilterResults.Count(f => f.Status == TradeFilterStatus.Warning);
        var failures = request.FilterResults.Count(f => f.Status == TradeFilterStatus.Fail);

        if (warnings > 0)
        {
            score -= warnings * WarningPenalty;
            weaknesses.Add($"{warnings} filter warning(s) (-{warnings * WarningPenalty} pts).");
        }

        if (failures > 0)
        {
            score -= failures * FailurePenalty;
            weaknesses.Add($"{failures} filter failure(s) (-{failures * FailurePenalty} pts).");
        }

        var passed = request.FilterResults.Count(f => f.Status == TradeFilterStatus.Pass);
        if (passed > 0)
            strengths.Add($"{passed}/{request.FilterResults.Count} filters passed.");

        // ── Expected value ───────────────────────────────────────────
        if (request.EvResult is not null)
        {
            if (request.EvResult.PositiveExpectancy)
            {
                strengths.Add($"Positive expected value ({request.EvResult.ExpectedValue:F2}%).");
            }
            else
            {
                score -= NegativeEvPenalty;
                weaknesses.Add($"Negative expected value ({request.EvResult.ExpectedValue:F2}%) (-{NegativeEvPenalty} pts).");
            }
        }

        // ── Risk/reward ──────────────────────────────────────────────
        if (request.RrResult is not null && request.RrResult.ValidationError is null)
        {
            if (request.RrResult.RiskRewardRatio >= FavorableRrThreshold)
            {
                strengths.Add($"Favorable risk/reward ratio ({request.RrResult.RiskRewardRatio:F2}).");
            }
            else
            {
                score -= UnfavorableRrPenalty;
                weaknesses.Add($"Risk/reward ratio ({request.RrResult.RiskRewardRatio:F2}) below {FavorableRrThreshold} (-{UnfavorableRrPenalty} pts).");
            }
        }
        else if (request.RrResult?.ValidationError is not null)
        {
            weaknesses.Add($"Risk/reward could not be computed: {request.RrResult.ValidationError}");
        }

        // ── Clamp & map ─────────────────────────────────────────────
        score = Math.Clamp(score, 0, 100);
        var grade = MapScoreToGrade(score);

        var summary = grade switch
        {
            TradeGrade.APlus => "Exceptional setup — all signals aligned.",
            TradeGrade.A     => "Strong setup with minor concerns.",
            TradeGrade.B     => "Decent setup — proceed with standard sizing.",
            TradeGrade.C     => "Marginal setup — reduce exposure or wait for improvement.",
            TradeGrade.D     => "Weak setup — consider paper-only.",
            TradeGrade.Reject => "Setup does not meet minimum quality standards.",
            _                => "Grade could not be determined.",
        };

        return new TradeGradeResult
        {
            Grade = grade,
            Score = score,
            Summary = summary,
            Strengths = strengths,
            Weaknesses = weaknesses,
        };
    }

    private static TradeGrade MapScoreToGrade(int score) => score switch
    {
        >= 95 => TradeGrade.APlus,
        >= 85 => TradeGrade.A,
        >= 70 => TradeGrade.B,
        >= 55 => TradeGrade.C,
        >= 40 => TradeGrade.D,
        _     => TradeGrade.Reject,
    };
}
