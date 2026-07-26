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

        // Multi-day SPY trend (EMA-based) — strongest market signal.
        // This captures whether the market has been trending up/down over weeks,
        // not just today's noise. Heavily weighted because counter-trend predictions
        // have near-zero win rates historically.
        if (ctx.SpyMultiDayTrend is not null)
        {
            if (ctx.SpyMultiDayTrend == "bullish") { bull += 8; signals.Add($"Market: SPY above 20-EMA (ratio {ctx.SpyEmaRatio:F4}) — multi-day uptrend"); }
            else if (ctx.SpyMultiDayTrend == "bearish") { bear += 8; signals.Add($"Market: SPY below 20-EMA (ratio {ctx.SpyEmaRatio:F4}) — multi-day downtrend"); }
        }

        // Sector ETF momentum — is the stock's sector trending with or against it?
        // A stock in a sector whose ETF is above its EMA has tailwinds; below = headwinds.
        // Worth ~5 pts — meaningful but not dominant over broad market.
        if (ctx.SectorEtfTrend is not null && ctx.SectorEtf is not null)
        {
            if (ctx.SectorEtfTrend == "bullish")
            {
                bull += 5;
                signals.Add($"Sector: {ctx.SectorEtf} above EMA (ratio {ctx.SectorEtfEmaRatio:F4}) — sector uptrend");
            }
            else if (ctx.SectorEtfTrend == "bearish")
            {
                bear += 5;
                signals.Add($"Sector: {ctx.SectorEtf} below EMA (ratio {ctx.SectorEtfEmaRatio:F4}) — sector downtrend");
            }
        }

        // Intraday SPY trend (today's change) — lighter weight, noisy
        if (ctx.SpyTrend is not null)
        {
            if (ctx.SpyTrend == "bullish") { bull += 2; signals.Add("Market: SPY today bullish"); }
            else if (ctx.SpyTrend == "bearish") { bear += 2; signals.Add("Market: SPY today bearish"); }
        }
        if (ctx.QqqTrend is not null)
        {
            if (ctx.QqqTrend == "bullish") { bull += 2; signals.Add("Market: QQQ trend bullish"); }
            else if (ctx.QqqTrend == "bearish") { bear += 2; signals.Add("Market: QQQ trend bearish"); }
        }

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, 25),
            BearishContribution = Math.Clamp(bear, 0, 25),
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
