namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class VolumeEvaluator : IVolumeEvaluator
{
    public EvaluatorKind Kind => EvaluatorKind.volume;

    public EvaluatorOutput Evaluate(EvaluationContext context)
    {
        var ind = context.Indicators;
        var signals = new List<string>();
        double bull = 0, bear = 0;

        if (ind.VolumeRatio is double vr)
        {
            if (vr > 2.0) { bull += 3; bear += 3; signals.Add($"Volume: very elevated ({vr:F1}x avg)"); }
            else if (vr > 1.3) { bull += 2; bear += 2; signals.Add($"Volume: above average ({vr:F1}x avg)"); }
            else if (vr < 0.5) { signals.Add($"Volume: very low ({vr:F1}x avg)"); }
            else if (vr < 0.7) { signals.Add($"Volume: below average ({vr:F1}x avg)"); }
        }

        if (ind.ObvSlope is double obvS)
        {
            if (obvS > 0) { bull += 5; signals.Add("Volume: OBV trending up (accumulation)"); }
            else if (obvS < 0) { bear += 5; signals.Add("Volume: OBV trending down (distribution)"); }
        }

        if (ind.PriceVolumeConfirmation is bool pvc)
        {
            if (pvc) { bull += 4; signals.Add("Volume: price-volume confirmed"); }
            else { bear += 4; signals.Add("Volume: price-volume divergence"); }
        }

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, 15),
            BearishContribution = Math.Clamp(bear, 0, 15),
            DebugSignals = signals,
            DebugInformation = new EvaluatorReasoning
            {
                EvaluatorName = nameof(VolumeEvaluator),
                Summary = "Volume contribution based on relative volume, OBV, and price-volume confirmation.",
                Reasons = signals,
                SupportingFeatureIds = context.Intelligence.Features
                    .Where(f => f.FeatureId.Contains("volume", StringComparison.OrdinalIgnoreCase)
                        || f.FeatureId.Contains("institutional", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FeatureId)
                    .ToList(),
            },
        };
    }
}
