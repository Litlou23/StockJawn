namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class MarketContextEvaluator : IMarketContextEvaluator
{
    public EvaluatorKind Kind => EvaluatorKind.market_context;

    public EvaluatorOutput Evaluate(EvaluationContext context)
    {
        var ctx = context.Benchmark;
        var signals = new List<string>();
        double bull = 0, bear = 0;

        if (ctx.RelativeStrengthVsSpy is double relSpy)
        {
            if (relSpy > 1.5) { bull += 6; signals.Add($"Market: outperforming SPY (+{relSpy:F1}%)"); }
            else if (relSpy > 0.5) { bull += 3; signals.Add($"Market: beating SPY (+{relSpy:F1}%)"); }
            else if (relSpy < -1.5) { bear += 6; signals.Add($"Market: lagging SPY ({relSpy:F1}%)"); }
            else if (relSpy < -0.5) { bear += 3; signals.Add($"Market: trailing SPY ({relSpy:F1}%)"); }
        }

        if (ctx.RelativeStrengthVsQqq is double relQqq)
        {
            if (relQqq > 1.5) { bull += 4; signals.Add($"Market: outperforming QQQ (+{relQqq:F1}%)"); }
            else if (relQqq < -1.5) { bear += 4; signals.Add($"Market: lagging QQQ ({relQqq:F1}%)"); }
        }

        if (ctx.SpyTrend is not null)
        {
            if (ctx.SpyTrend == "bullish") { bull += 3; signals.Add("Market: SPY trend bullish"); }
            else if (ctx.SpyTrend == "bearish") { bear += 3; signals.Add("Market: SPY trend bearish"); }
        }
        if (ctx.QqqTrend is not null)
        {
            if (ctx.QqqTrend == "bullish") { bull += 2; signals.Add("Market: QQQ trend bullish"); }
            else if (ctx.QqqTrend == "bearish") { bear += 2; signals.Add("Market: QQQ trend bearish"); }
        }

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, 15),
            BearishContribution = Math.Clamp(bear, 0, 15),
            DebugSignals = signals,
            DebugInformation = new EvaluatorReasoning
            {
                EvaluatorName = nameof(MarketContextEvaluator),
                Summary = "Market-context contribution based on relative strength and benchmark trend.",
                Reasons = signals,
                SupportingFeatureIds = context.Intelligence.Features
                    .Where(f => f.FeatureId.Contains("sector", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FeatureId)
                    .ToList(),
            },
        };
    }
}
