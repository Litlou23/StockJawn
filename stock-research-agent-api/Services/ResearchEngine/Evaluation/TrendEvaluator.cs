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

        // EMA alignment (API-sourced) — exponential MAs respond faster than SMAs
        if (ind.Ema12 is double ema12 && ind.Ema26 is double ema26)
        {
            if (ema12 > ema26) { bull += 4; signals.Add($"Trend: EMA12 above EMA26 (bullish alignment)"); }
            else { bear += 4; signals.Add($"Trend: EMA12 below EMA26 (bearish alignment)"); }
        }

        // Price vs EMA50 — long-term trend anchor
        if (ind.Ema50 is double ema50 && context.Snapshot.Quote is not null)
        {
            var price = context.Snapshot.Quote.Price;
            if (price > ema50 * 1.02) { bull += 3; signals.Add($"Trend: price above EMA50 (${price:F2} vs ${ema50:F2})"); }
            else if (price < ema50 * 0.98) { bear += 3; signals.Add($"Trend: price below EMA50 (${price:F2} vs ${ema50:F2})"); }
        }

        // 52-week range position — confirms long-term trend strength
        var fundamentals = context.Snapshot.Fundamentals;
        if (fundamentals?.FiftyTwoWeekHigh is double high52 && fundamentals?.FiftyTwoWeekLow is double low52
            && context.Snapshot.Quote is not null && high52 > low52)
        {
            var currentPrice = context.Snapshot.Quote.Price;
            var range = high52 - low52;
            var position = (currentPrice - low52) / range; // 0.0 = at 52w low, 1.0 = at 52w high

            if (position >= 0.95) { bull += 3; signals.Add($"Trend: within 5% of 52-week high (${currentPrice:F2} vs ${high52:F2})"); }
            else if (position >= 0.80) { bull += 2; signals.Add($"Trend: in upper 20% of 52-week range ({position:P0})"); }
            else if (position <= 0.05) { bear += 3; signals.Add($"Trend: within 5% of 52-week low (${currentPrice:F2} vs ${low52:F2})"); }
            else if (position <= 0.20) { bear += 2; signals.Add($"Trend: in lower 20% of 52-week range ({position:P0})"); }
        }

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, 30),
            BearishContribution = Math.Clamp(bear, 0, 30),
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
