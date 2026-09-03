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

        // ── Macro sentiment from SPY news headlines (AI-classified) ──
        // This is the strongest directional signal: geopolitical events, Fed policy,
        // macro shocks. When the AI classifies risk_off with high confidence, this
        // heavily penalizes bullish predictions and boosts bearish ones system-wide.
        // Scaled by confidence: weak signal (conf 30) ≈ 3-4 pts, strong signal (conf 80+) ≈ 10-12 pts.
        if (ctx.MacroSentiment is not null && ctx.MacroSentimentConfidence is int macroConf and > 20)
        {
            // Scale contribution by confidence: (confidence / 100) * max_points
            // Max 12 points — comparable to multi-day SPY trend (8pts) but can exceed it
            // for high-confidence macro events
            var scale = Math.Clamp(macroConf / 100.0, 0.2, 1.0);
            var macroPoints = Math.Round(12.0 * scale, 1);
            var themes = ctx.MacroThemes is not null ? string.Join(", ", ctx.MacroThemes) : "unknown";
            var daysNote = ctx.MacroImpactDays is int days ? $", est. {days}d impact" : "";

            if (ctx.MacroSentiment == "risk_off")
            {
                bear += macroPoints;
                signals.Add($"Macro: RISK-OFF ({themes}, conf {macroConf}{daysNote}) → +{macroPoints:F1} bearish");
            }
            else if (ctx.MacroSentiment == "risk_on")
            {
                bull += macroPoints;
                signals.Add($"Macro: RISK-ON ({themes}, conf {macroConf}{daysNote}) → +{macroPoints:F1} bullish");
            }
        }

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, 35),
            BearishContribution = Math.Clamp(bear, 0, 35),
            DebugSignals = signals,
            DebugInformation = new EvaluatorReasoning
            {
                EvaluatorName = nameof(MarketContextEvaluator),
                Summary = "Market-context contribution based on relative strength, benchmark trend, and macro news sentiment.",
                Reasons = signals,
                SupportingFeatureIds = context.Intelligence.Features
                    .Where(f => f.FeatureId.Contains("sector", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FeatureId)
                    .ToList(),
            },
        };
    }
}
