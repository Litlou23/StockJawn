using ResearchSignal = StockResearchAgent.Api.Models.ResearchSignal;

namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class ResearchSignalEvaluator : IResearchSignalEvaluator
{
    public EvaluatorKind Kind => EvaluatorKind.research_signal;

    public EvaluatorOutput Evaluate(EvaluationContext context)
    {
        var researchSignals = context.ResearchSignals;
        if (researchSignals.Count == 0)
        {
            return new EvaluatorOutput
            {
                Kind = Kind,
                DebugInformation = new EvaluatorReasoning
                {
                    EvaluatorName = nameof(ResearchSignalEvaluator),
                    Summary = "No active research-signal contribution.",
                },
            };
        }

        var weights = context.LearningData.Weights;
        var signals = new List<string>();
        double bull = 0, bear = 0;

        foreach (var sig in researchSignals)
        {
            var weightKey = $"research_{sig.SignalType}";
            var w = weights.GetValueOrDefault(weightKey, 1.0);
            var contribution = sig.Strength * sig.Confidence * 15 * w;

            if (sig.SignalCategory == "institutional")
            {
                if (sig.SignalType.Contains("buy") || sig.SignalType.Contains("cluster"))
                    bull += contribution;
                else if (sig.SignalType.Contains("sell"))
                    bear += contribution;
                else
                    bull += contribution * 0.5;
            }
            else
            {
                if (sig.Strength > 0) bull += contribution;
                else bear += Math.Abs(contribution);
            }

            signals.Add($"Research: {sig.Summary ?? sig.SignalType} (str={sig.Strength:F2}, conf={sig.Confidence:F2})");
        }

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, 20),
            BearishContribution = Math.Clamp(bear, 0, 20),
            DebugSignals = signals,
            DebugInformation = new EvaluatorReasoning
            {
                EvaluatorName = nameof(ResearchSignalEvaluator),
                Summary = "Research-signal contribution based on active external signal providers.",
                Reasons = signals,
                SupportingEvidenceIds = context.Intelligence.Evidence
                    .Where(e => e.EvidenceId.Contains("institution", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.EvidenceId)
                    .ToList(),
            },
        };
    }
}
