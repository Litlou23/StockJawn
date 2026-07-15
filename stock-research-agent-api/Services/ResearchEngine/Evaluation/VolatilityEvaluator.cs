using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

/// <summary>
/// Scores the volatility bucket using the <see cref="VolatilityOpportunityAssessment"/>
/// produced by <see cref="VolatilityOpportunityEngine"/>.
///
/// Rewards: healthy directional volatility, confirmed breakouts,
///          controlled pullbacks, panic exhaustion with recovery signs.
/// Penalizes: chaotic volatility, thin-volume volatility,
///            volatility traps, failed bounces.
///
/// Max contribution: 10 per side (unchanged from original).
/// Bucket weight increase is a Phase 3 configuration change.
/// </summary>
public class VolatilityEvaluator : IVolatilityEvaluator
{
    private const double MaxContribution = 10;

    public EvaluatorKind Kind => EvaluatorKind.volatility;

    public EvaluatorOutput Evaluate(EvaluationContext context)
    {
        var assessment = context.VolatilityAssessment;
        var signals = new List<string>();
        double bull = 0, bear = 0;

        // Fallback: if VOE assessment is not available, use legacy Bollinger-only logic
        if (assessment is null)
            return EvaluateLegacy(context);

        // ── Opportunity type scoring ────────────────────────────
        ScoreOpportunityType(assessment, ref bull, ref bear, signals);

        // ── Volatility regime context ───────────────────────────
        ScoreVolatilityRegime(assessment, ref bull, ref bear, signals);

        // ── Gap context ─────────────────────────────────────────
        ScoreGapContext(assessment, ref bull, ref bear, signals);

        // ── Support / resistance proximity ──────────────────────
        ScoreSupportResistance(assessment, ref bull, ref bear, signals);

        // ── Volume persistence ──────────────────────────────────
        ScoreVolumePersistence(assessment, ref bull, ref bear, signals);

        // ── Catalyst freshness ──────────────────────────────────
        ScoreCatalystFreshness(assessment, ref bull, ref bear, signals);

        var summary = assessment.Opportunity != OpportunityType.None
            ? $"Volatility opportunity: {assessment.Opportunity} (regime={assessment.StockVolRegime})"
            : $"Volatility context: regime={assessment.StockVolRegime}, ATR pctile={assessment.AtrPercentile?.ToString("F0") ?? "n/a"}";

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, MaxContribution),
            BearishContribution = Math.Clamp(bear, 0, MaxContribution),
            DebugSignals = signals,
            ParticipatesInConfirmation = false,
            DebugInformation = new EvaluatorReasoning
            {
                EvaluatorName = nameof(VolatilityEvaluator),
                Summary = summary,
                Reasons = signals,
                SupportingFeatureIds = context.Intelligence.Features
                    .Where(f => f.FeatureId.Contains("volatility", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FeatureId)
                    .ToList(),
            },
        };
    }

    // ─────────────────────────────────────────────────────────────
    // Opportunity type → directional score
    // ─────────────────────────────────────────────────────────────

    private static void ScoreOpportunityType(
        VolatilityOpportunityAssessment a, ref double bull, ref double bear, List<string> signals)
    {
        switch (a.Opportunity)
        {
            case OpportunityType.DipAfterPanic:
                // Panic selling with recovery characteristics → bullish
                bull += 4;
                signals.Add($"+4 bull: DipAfterPanic — gap {a.GapPercent:F1}% down, oversold, volume confirmed");
                break;

            case OpportunityType.SqueezeBreakout:
                // Breakout from compressed range → direction of breakout
                if (a.GapDir == GapDirection.Up || a.DistanceFromResistance >= 0)
                { bull += 4; signals.Add("+4 bull: SqueezeBreakout — upside breakout from compressed range"); }
                else
                { bear += 4; signals.Add("+4 bear: SqueezeBreakout — downside breakout from compressed range"); }
                break;

            case OpportunityType.ExhaustionReversal:
                // Extreme volatility decelerating → counter-trend
                if (a.GapDir == GapDirection.Up || (a.DistanceFromResistance is not null && a.DistanceFromResistance >= 0))
                { bear += 3; signals.Add("+3 bear: ExhaustionReversal — overbought + ATR decelerating"); }
                else
                { bull += 3; signals.Add("+3 bull: ExhaustionReversal — oversold + ATR decelerating"); }
                break;

            case OpportunityType.MomentumContinuation:
                // Gap up with volume in expanding regime → trend following
                bull += 3;
                signals.Add($"+3 bull: MomentumContinuation — gap up + volume {a.VolumeRatioPersistence:F1}x + ATR pctile {a.AtrPercentile:F0}");
                break;

            case OpportunityType.FailedBounce:
                // Near support but no volume to hold → bearish
                bear += 4;
                signals.Add($"+4 bear: FailedBounce — near support, volume drying up, RSI weak");
                break;

            case OpportunityType.VolatilityTrap:
                // High vol + thin volume = unpredictable → penalize both sides
                bull += 1; bear += 3;
                signals.Add($"+3 bear/+1 bull: VolatilityTrap — ATR pctile {a.AtrPercentile:F0} but vol persistence only {a.VolumeRatioPersistence:F2}x");
                break;

            case OpportunityType.None:
            default:
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Volatility regime
    // ─────────────────────────────────────────────────────────────

    private static void ScoreVolatilityRegime(
        VolatilityOpportunityAssessment a, ref double bull, ref double bear, List<string> signals)
    {
        switch (a.StockVolRegime)
        {
            case StockVolatilityRegime.Squeeze:
                // Compressed range — breakout imminent, add to both sides
                bull += 1; bear += 1;
                signals.Add("+1 both: Squeeze regime — breakout expected");
                break;

            case StockVolatilityRegime.Expanding:
                // Expanding vol — directional moves more likely
                if (a.AtrAcceleration is > 0)
                { signals.Add($"+0: Expanding regime, ATR accelerating ({a.AtrAcceleration:F1}%) — directional context only"); }
                break;

            case StockVolatilityRegime.Extreme:
                // Extreme vol is risky — small penalty to confidence
                bear += 1;
                signals.Add("+1 bear: Extreme volatility regime — elevated risk");
                break;

            case StockVolatilityRegime.Normal:
            case StockVolatilityRegime.Unknown:
            default:
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Gap context
    // ─────────────────────────────────────────────────────────────

    private static void ScoreGapContext(
        VolatilityOpportunityAssessment a, ref double bull, ref double bear, List<string> signals)
    {
        // Only score significant gaps (>= 3%)
        if (a.GapClassification < GapType.Significant) return;

        if (a.GapDir == GapDirection.Up && a.GapWithVolume)
        {
            bull += 2;
            signals.Add($"+2 bull: Gap up {a.GapPercent:F1}% with volume confirmation");
        }
        else if (a.GapDir == GapDirection.Down && a.GapWithVolume)
        {
            // Gap down with volume is bearish pressure, but may be dip opportunity
            // (DipAfterPanic already scored separately if RSI confirms)
            bear += 2;
            signals.Add($"+2 bear: Gap down {a.GapPercent:F1}% with volume confirmation");
        }
        else if (a.GapClassification >= GapType.Large && !a.GapWithVolume)
        {
            // Large gap without volume — suspicious, mild caution
            bear += 1;
            signals.Add($"+1 bear: Large gap {a.GapPercent:F1}% without volume — thin-market move");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Support / resistance proximity
    // ─────────────────────────────────────────────────────────────

    private static void ScoreSupportResistance(
        VolatilityOpportunityAssessment a, ref double bull, ref double bear, List<string> signals)
    {
        // Near support (< 2% above Donchian low) in non-extreme vol → mild bullish
        if (a.DistanceFromSupport is not null && a.DistanceFromSupport < 2.0
            && a.StockVolRegime is not StockVolatilityRegime.Extreme)
        {
            bull += 1;
            signals.Add($"+1 bull: Near support ({a.DistanceFromSupport:F1}% from Donchian low)");
        }

        // At or above resistance (>= 0% above Donchian high) → mild bearish (potential reversal)
        if (a.DistanceFromResistance is not null && a.DistanceFromResistance >= 0
            && a.StockVolRegime is not StockVolatilityRegime.Squeeze) // squeeze breakout is good
        {
            bear += 1;
            signals.Add($"+1 bear: At/above resistance ({a.DistanceFromResistance:F1}% from Donchian high)");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Volume persistence
    // ─────────────────────────────────────────────────────────────

    private static void ScoreVolumePersistence(
        VolatilityOpportunityAssessment a, ref double bull, ref double bear, List<string> signals)
    {
        if (a.VolumeRatioPersistence is null) return;

        // Sustained high volume (3-bar avg > 1.5x) confirms directional moves
        if (a.VolumeRatioPersistence > 1.5 && a.Opportunity != OpportunityType.None)
        {
            // Amplify the dominant direction
            if (a.GapDir == GapDirection.Up)
            { bull += 1; signals.Add($"+1 bull: Sustained high volume ({a.VolumeRatioPersistence:F1}x avg)"); }
            else if (a.GapDir == GapDirection.Down)
            { bear += 1; signals.Add($"+1 bear: Sustained high volume ({a.VolumeRatioPersistence:F1}x avg)"); }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Catalyst freshness
    // ─────────────────────────────────────────────────────────────

    private static void ScoreCatalystFreshness(
        VolatilityOpportunityAssessment a, ref double bull, ref double bear, List<string> signals)
    {
        if (a.CatalystAgeHours is null) return;

        // Very fresh catalyst (< 6 hours) amplifies volatility signal slightly
        if (a.CatalystAgeHours < 6 && a.Opportunity != OpportunityType.None)
        {
            bull += 1; bear += 1;
            signals.Add($"+1 both: Fresh catalyst ({a.CatalystAgeHours:F0}h old) amplifies volatility signal");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Legacy fallback (no VOE assessment available)
    // ─────────────────────────────────────────────────────────────

    private EvaluatorOutput EvaluateLegacy(EvaluationContext context)
    {
        var ind = context.Indicators;
        var quote = context.Snapshot.Quote;
        var signals = new List<string> { "Volatility: legacy mode (no VOE assessment)" };
        double bull = 0, bear = 0;

        if (ind.BollingerBreakout == true && quote is not null)
        {
            if (quote.Price > (ind.BollingerUpper ?? 0))
            { bull += 5; signals.Add("Volatility: Bollinger upper breakout"); }
            else
            { bear += 5; signals.Add("Volatility: Bollinger lower breakdown"); }
        }

        if (ind.BollingerBandwidth is double bw)
        {
            if (bw < 3) { bull += 2; bear += 2; signals.Add($"Volatility: Bollinger squeeze ({bw:F1}%)"); }
            else if (bw > 10) { signals.Add($"Volatility: bands very wide ({bw:F1}%)"); }
        }

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, MaxContribution),
            BearishContribution = Math.Clamp(bear, 0, MaxContribution),
            DebugSignals = signals,
            ParticipatesInConfirmation = false,
            DebugInformation = new EvaluatorReasoning
            {
                EvaluatorName = nameof(VolatilityEvaluator),
                Summary = "Legacy volatility scoring — VOE assessment not available.",
                Reasons = signals,
            },
        };
    }
}
