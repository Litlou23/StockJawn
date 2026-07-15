namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class LearningAdjustmentEvaluator : ILearningAdjustmentEvaluator
{
    public EvaluatorKind Kind => EvaluatorKind.learning;

    public EvaluatorOutput Evaluate(EvaluationContext context)
    {
        var weights = context.LearningData.Weights;
        var lessons = context.LearningData.Lessons;
        var signals = new List<string>();
        double bull = 0, bear = 0;

        var bullishWeights = weights
            .Where(w => w.Key.EndsWith("_bullish") && Math.Abs(w.Value - 1.0) > 0.15)
            .ToList();
        var bearishWeights = weights
            .Where(w => w.Key.EndsWith("_bearish") && Math.Abs(w.Value - 1.0) > 0.15)
            .ToList();

        if (bullishWeights.Count > 0)
        {
            var avgWeight = bullishWeights.Average(w => w.Value);
            bull = Math.Clamp((avgWeight - 1.0) * 30, 0, 15);
            signals.Add($"Learning: {bullishWeights.Count} bullish weights adjusted (avg {avgWeight:F2}x)");
        }
        if (bearishWeights.Count > 0)
        {
            var avgWeight = bearishWeights.Average(w => w.Value);
            bear = Math.Clamp((avgWeight - 1.0) * 30, 0, 15);
            signals.Add($"Learning: {bearishWeights.Count} bearish weights adjusted (avg {avgWeight:F2}x)");
        }

        var genericWeights = weights
            .Where(w => w.Key != "calibration_factor"
                && !w.Key.StartsWith("min_") // exclude decision thresholds
                && !w.Key.EndsWith("_bullish") && !w.Key.EndsWith("_bearish")
                && Math.Abs(w.Value - 1.0) > 0.15)
            .ToList();
        if (genericWeights.Count > 0)
        {
            var avgWeight = genericWeights.Average(w => w.Value);
            var contribution = Math.Clamp((avgWeight - 1.0) * 15, -7, 7);
            if (contribution > 0) bull += contribution;
            else bear += Math.Abs(contribution);
            signals.Add($"Learning: {genericWeights.Count} generic weights (avg {avgWeight:F2}x)");
        }

        if (lessons.Count > 0)
            signals.Add($"Learning: {lessons.Count} prior lessons considered");

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, 15),
            BearishContribution = Math.Clamp(bear, 0, 15),
            DebugSignals = signals,
            DebugInformation = new EvaluatorReasoning
            {
                EvaluatorName = nameof(LearningAdjustmentEvaluator),
                Summary = "Learning contribution based on persisted weight overrides and prior lessons.",
                Reasons = signals,
            },
        };
    }
}
