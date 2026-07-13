namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class VolatilityEvaluator : IVolatilityEvaluator
{
    public EvaluatorKind Kind => EvaluatorKind.volatility;

    public EvaluatorOutput Evaluate(EvaluationContext context)
    {
        var ind = context.Indicators;
        var quote = context.Snapshot.Quote;
        var signals = new List<string>();
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
            BullishContribution = Math.Clamp(bull, 0, 10),
            BearishContribution = Math.Clamp(bear, 0, 10),
            DebugSignals = signals,
            ParticipatesInConfirmation = false,
            DebugInformation = new EvaluatorReasoning
            {
                EvaluatorName = nameof(VolatilityEvaluator),
                Summary = "Volatility setup contribution based on Bollinger structure.",
                Reasons = signals,
                SupportingFeatureIds = context.Intelligence.Features
                    .Where(f => f.FeatureId.Contains("volatility", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FeatureId)
                    .ToList(),
            },
        };
    }
}
