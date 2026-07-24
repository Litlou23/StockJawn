namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class MomentumEvaluator : IMomentumEvaluator
{
    public EvaluatorKind Kind => EvaluatorKind.momentum;

    public EvaluatorOutput Evaluate(EvaluationContext context)
    {
        var ind = context.Indicators;
        var signals = new List<string>();
        double bull = 0, bear = 0;

        if (ind.Roc5 is double roc5)
        {
            if (roc5 > 2) { bull += 5; signals.Add($"Momentum: ROC5 strong up ({roc5:F1}%)"); }
            else if (roc5 > 0.5) { bull += 2; signals.Add($"Momentum: ROC5 up ({roc5:F1}%)"); }
            else if (roc5 < -2) { bear += 5; signals.Add($"Momentum: ROC5 strong down ({roc5:F1}%)"); }
            else if (roc5 < -0.5) { bear += 2; signals.Add($"Momentum: ROC5 down ({roc5:F1}%)"); }
        }

        if (ind.Roc10 is double roc10)
        {
            if (roc10 > 3) { bull += 4; signals.Add($"Momentum: ROC10 strong up ({roc10:F1}%)"); }
            else if (roc10 > 1) { bull += 2; signals.Add($"Momentum: ROC10 up ({roc10:F1}%)"); }
            else if (roc10 < -3) { bear += 4; signals.Add($"Momentum: ROC10 strong down ({roc10:F1}%)"); }
            else if (roc10 < -1) { bear += 2; signals.Add($"Momentum: ROC10 down ({roc10:F1}%)"); }
        }

        if (ind.Rsi14 is double rsi)
        {
            if (rsi > 70) { bear += 3; signals.Add($"Momentum: RSI overbought ({rsi:F0})"); }
            else if (rsi > 55) { bull += 4; signals.Add($"Momentum: RSI bullish ({rsi:F0})"); }
            else if (rsi < 30)
            {
                // Oversold — contrarian bullish signal, but also a mean-reversion trap
                // for bearish predictions. Data shows bearish calls on oversold stocks
                // are the worst performers (stocks bounce back).
                bull += 5; // stronger contrarian signal (was 3)
                bear -= 3; // penalize bearish on oversold — chasing the bottom
                signals.Add($"Momentum: RSI oversold ({rsi:F0}) — mean-reversion risk for bearish");
            }
            else if (rsi < 45) { bear += 4; signals.Add($"Momentum: RSI bearish ({rsi:F0})"); }
        }

        // ── Mean-reversion guard ──
        // When RSI is near oversold AND rate-of-change is deeply negative,
        // the drop has already happened. Bearish predictions here are chasing
        // the end of a move, not the beginning. Data: strong_trend + strong_momentum
        // bearish = only 38% accuracy with +4.29% avg move against.
        if (ind.Rsi14 is double rsiMR && rsiMR < 35
            && ((ind.Roc5 is double roc5MR && roc5MR < -2) || (ind.Roc10 is double roc10MR && roc10MR < -3)))
        {
            bear -= 4;
            bull += 2;
            signals.Add($"Momentum: mean-reversion guard — RSI {rsiMR:F0} + deep negative ROC suggests drop already priced in");
        }

        if (ind.StochasticCloseLocation is double stoch)
        {
            if (stoch > 80) { bull += 3; signals.Add($"Momentum: close near highs ({stoch:F0}%)"); }
            else if (stoch < 20) { bear += 3; signals.Add($"Momentum: close near lows ({stoch:F0}%)"); }
        }

        // MACD (API-sourced) — crossover direction and histogram strength
        if (ind.MacdBullishCrossover is bool macdCross)
        {
            if (macdCross) { bull += 4; signals.Add("Momentum: MACD bullish crossover"); }
            else { bear += 4; signals.Add("Momentum: MACD bearish crossover"); }
        }

        if (ind.MacdHistogram is double hist)
        {
            // Histogram magnitude confirms momentum strength
            if (hist > 0.5) { bull += 2; signals.Add($"Momentum: MACD histogram strong positive ({hist:F2})"); }
            else if (hist < -0.5) { bear += 2; signals.Add($"Momentum: MACD histogram strong negative ({hist:F2})"); }
        }

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, 25),
            BearishContribution = Math.Clamp(bear, 0, 25),
            DebugSignals = signals,
            DebugInformation = new EvaluatorReasoning
            {
                EvaluatorName = nameof(MomentumEvaluator),
                Summary = "Momentum contribution based on ROC, RSI, and close-location behavior.",
                Reasons = signals,
                SupportingFeatureIds = context.Intelligence.Features
                    .Where(f => f.FeatureId.Contains("momentum", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FeatureId)
                    .ToList(),
            },
        };
    }
}
