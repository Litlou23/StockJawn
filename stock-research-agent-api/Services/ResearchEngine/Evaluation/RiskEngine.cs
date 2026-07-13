namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class RiskEngine : IRiskEngine
{
    public RiskAssessment Evaluate(EvaluationContext context, string predictionType)
    {
        var signals = new List<string>();
        var penalty = ScoreRiskPenalty(context, signals);
        var risk = ComputeRisk(context, predictionType, penalty);
        var earningsNear = HasNearEarnings(context.Snapshot);

        if (earningsNear)
        {
            risk = Math.Max(risk, 70);
            signals.Add("Risk: earnings imminent — directional prediction unreliable");
        }

        return new RiskAssessment
        {
            RiskScore = risk,
            RiskPenalty = penalty,
            EarningsNear = earningsNear,
            DebugSignals = signals,
        };
    }

    private static double ScoreRiskPenalty(EvaluationContext context, List<string> signals)
    {
        var ind = context.Indicators;
        var ctx = context.Benchmark;
        var snapshot = context.Snapshot;

        double penalty = 0;

        if (ind.Atr14 is double atr && snapshot.Quote is not null && snapshot.Quote.Price > 0)
        {
            var atrPct = (atr / snapshot.Quote.Price) * 100;
            if (atrPct > 8) { penalty -= 10; signals.Add($"Risk: ATR very high ({atrPct:F1}%)"); }
            else if (atrPct > 5) { penalty -= 5; signals.Add($"Risk: ATR elevated ({atrPct:F1}%)"); }
        }

        if (ctx.SpyTrend == "bearish" && ctx.QqqTrend == "bearish")
        { penalty -= 8; signals.Add("Risk: both SPY and QQQ bearish"); }

        if (!snapshot.DataAvailability.MarketDataAvailable)
        { penalty -= 15; signals.Add("Risk: no market data available"); }

        if (ind.BarsAvailable < 10)
        { penalty -= 5; signals.Add($"Risk: only {ind.BarsAvailable} bars available"); }

        if (ind.Rsi14 is double rsi && (rsi > 80 || rsi < 20))
        { penalty -= 5; signals.Add($"Risk: RSI at extreme ({rsi:F0})"); }

        return Math.Clamp(penalty, -30, 0);
    }

    private static int ComputeRisk(EvaluationContext context, string predType, double riskPenalty)
    {
        var snapshot = context.Snapshot;
        var ind = context.Indicators;
        var ctx = context.Benchmark;

        int risk = 40;

        if (ind.Atr14 is double atr && snapshot.Quote is not null && snapshot.Quote.Price > 0)
        {
            var atrPct = (atr / snapshot.Quote.Price) * 100;
            if (atrPct > 5) risk += 15;
            else if (atrPct > 3) risk += 5;
        }

        if (predType == "bullish" && !ind.Sma5AboveSma20) risk += 10;
        if (predType == "bearish" && ind.Sma5AboveSma20) risk += 10;

        if (ctx.SpyTrend == "bearish" && predType == "bullish") risk += 10;
        if (ctx.SpyTrend == "bullish" && predType == "bearish") risk += 10;

        if (!snapshot.DataAvailability.MarketDataAvailable) risk += 15;
        if (!snapshot.DataAvailability.NewsAvailable) risk += 5;

        risk += (int)Math.Abs(riskPenalty / 2);

        return Math.Clamp(risk, 0, 100);
    }

    private static bool HasNearEarnings(MarketSnapshot snapshot) =>
        snapshot.NewsContext.Any(n =>
            n.CatalystType == "earnings"
            && n.Title.Contains("Earnings in", StringComparison.OrdinalIgnoreCase)
            && (n.Title.Contains("0d") || n.Title.Contains("1d") || n.Title.Contains("2d") || n.Title.Contains("3d")));
}
