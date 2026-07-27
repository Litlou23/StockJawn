using StockResearchAgent.Api.Models;

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

        // ── Overbought exhaustion guard ──
        // Data shows momentum has -0.24 correlation with bullish returns.
        // High momentum bullish picks = buying the top. UNLESS there's a real
        // catalyst (earnings, FDA, merger, etc.) sustaining the move.
        // If ROC is strong up but no high-velocity catalyst → penalize bullish,
        // the run is likely exhausted. If catalyst IS present → momentum is
        // justified, keep the score.
        bool strongMomentumUp = (ind.Roc5 is double r5Up && r5Up > 2) || (ind.Roc10 is double r10Up && r10Up > 3);
        bool overboughtRsi = ind.Rsi14 is double rsiOB && rsiOB > 65;

        if (strongMomentumUp || overboughtRsi)
        {
            var news = context.Snapshot.NewsContext;
            bool catalystJustified = HasFundamentalCatalyst(news, bullish: true);

            if (!catalystJustified)
            {
                // No real catalyst — stock ran on pure momentum, likely exhausted
                double penalty = strongMomentumUp && overboughtRsi ? 8 : 5;
                bull -= penalty;
                bear += 3;
                signals.Add($"Momentum: OVERBOUGHT EXHAUSTION — strong momentum without catalyst, penalizing bullish by {penalty}");
            }
            else
            {
                signals.Add("Momentum: strong momentum WITH catalyst support — no exhaustion penalty");
            }
        }

        // ── Oversold exhaustion guard (bearish mirror) ──
        // Strong downward momentum without a bearish catalyst = drop already happened.
        bool strongMomentumDown = (ind.Roc5 is double r5Dn && r5Dn < -2) || (ind.Roc10 is double r10Dn && r10Dn < -3);
        bool oversoldRsi = ind.Rsi14 is double rsiOS && rsiOS < 35;

        if (strongMomentumDown || oversoldRsi)
        {
            var news = context.Snapshot.NewsContext;
            bool catalystJustified = HasFundamentalCatalyst(news, bullish: false);

            if (!catalystJustified)
            {
                double penalty = strongMomentumDown && oversoldRsi ? 6 : 4;
                bear -= penalty;
                bull += 2;
                signals.Add($"Momentum: OVERSOLD EXHAUSTION — strong downward momentum without bearish catalyst, penalizing bearish by {penalty}");
            }
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

    /// <summary>
    /// Checks whether the news has a fundamental catalyst justifying the momentum.
    /// Prefers LLM classification (CatalystQuality) when available; falls back to
    /// keyword-based catalyst type + sentiment checks.
    /// </summary>
    private static bool HasFundamentalCatalyst(List<MarketSnapshotNews> news, bool bullish)
    {
        if (news.Count == 0) return false;

        // ── LLM-classified articles (preferred) ──
        var classified = news.Where(n => n.CatalystQuality is not null).ToList();
        if (classified.Count > 0)
        {
            // If LLM says any article is a fundamental catalyst with decent confidence, it's justified
            bool llmSaysFundamental = classified.Any(n =>
                n.CatalystQuality == "fundamental_catalyst" && (n.CatalystConfidence ?? 0) >= 50);

            if (llmSaysFundamental) return true;

            // If LLM classified articles but found NO fundamental catalyst, trust it
            // (don't fall back to keyword check — LLM had richer context)
            return false;
        }

        // ── Fallback: keyword-based check (no LLM classification available) ──
        var highVelocityCatalysts = new HashSet<string>
        {
            "earnings", "merger", "acquisition", "fda", "guidance",
            "buyback", "8k_filing", "quarterly_report", "annual_report",
            "insider_transaction", "beneficial_ownership_change"
        };

        bool hasCatalystType = news.Any(n =>
            n.CatalystType is not null && highVelocityCatalysts.Contains(n.CatalystType)
            && n.ImportanceScore >= 60);

        var sentimentMatch = bullish ? "bullish" : "bearish";
        bool hasStrongSentiment = news.Any(n =>
            n.Sentiment == sentimentMatch && n.ImportanceScore >= 70);

        return hasCatalystType || hasStrongSentiment;
    }
}
