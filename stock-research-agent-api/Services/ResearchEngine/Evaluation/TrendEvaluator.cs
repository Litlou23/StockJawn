using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class TrendEvaluator : ITrendEvaluator
{
    public EvaluatorKind Kind => EvaluatorKind.trend;

    public EvaluatorOutput Evaluate(EvaluationContext context)
    {
        var ind = context.Indicators;
        var signals = new List<string>();
        double bull = 0, bear = 0;

        if (ind.Sma5 is not null && ind.Sma20 is not null)
        {
            if (ind.Sma5AboveSma20) { bull += 8; signals.Add("Trend: SMA5 above SMA20"); }
            else { bear += 8; signals.Add("Trend: SMA5 below SMA20"); }
        }

        if (ind.Sma20 is not null)
        {
            if (ind.CloseAboveSma20) { bull += 6; signals.Add("Trend: close above SMA20"); }
            else { bear += 6; signals.Add("Trend: close below SMA20"); }
        }

        if (ind.LinearRegressionSlope is double slope)
        {
            if (slope > 0.5) { bull += 6; signals.Add($"Trend: strong upslope ({slope:F2})"); }
            else if (slope > 0.1) { bull += 3; signals.Add($"Trend: mild upslope ({slope:F2})"); }
            else if (slope < -0.5) { bear += 6; signals.Add($"Trend: strong downslope ({slope:F2})"); }
            else if (slope < -0.1) { bear += 3; signals.Add($"Trend: mild downslope ({slope:F2})"); }
        }

        if (ind.DonchianBreakout == true) { bull += 5; signals.Add("Trend: Donchian 20 breakout"); }
        else if (ind.DonchianBreakdown == true) { bear += 5; signals.Add("Trend: Donchian 20 breakdown"); }

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, 25),
            BearishContribution = Math.Clamp(bear, 0, 25),
            DebugSignals = signals,
            DebugInformation = new EvaluatorReasoning
            {
                EvaluatorName = nameof(TrendEvaluator),
                Summary = "Trend contribution based on moving-average alignment, slope, and Donchian structure.",
                Reasons = signals,
                SupportingFeatureIds = context.Intelligence.Features
                    .Where(f => f.FeatureId.Contains("trend", StringComparison.OrdinalIgnoreCase)
                        || f.FeatureId.Contains("support", StringComparison.OrdinalIgnoreCase)
                        || f.FeatureId.Contains("resistance", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FeatureId)
                    .ToList(),
            },
        };
    }
}
