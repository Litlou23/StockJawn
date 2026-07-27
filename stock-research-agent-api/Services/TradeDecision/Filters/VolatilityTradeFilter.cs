using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision.Filters;

/// <summary>
/// Evaluates whether current volatility conditions are suitable for
/// the trade's expected holding period and risk profile.
///
/// Uses ATR% from the prediction (computed at scan time) to check:
/// - Extreme volatility (&gt; 8% ATR) → Fail for day trades, Warning for swings
/// - High volatility (&gt; 5% ATR) → Warning for day trades
/// - Stop distance vs ATR: if stop is tighter than 0.5 ATR → likely to get stopped out
/// </summary>
public class VolatilityTradeFilter : ITradeFilter
{
    private const double ExtremeAtrPct = 8.0;  // ATR% above this = extremely volatile
    private const double HighAtrPct = 5.0;     // ATR% above this = high volatility
    private const double MinStopAtrRatio = 0.5; // Stop should be at least 0.5× ATR away

    public TradeFilterResult Evaluate(TradeDecisionContext context)
    {
        var pred = context.Prediction;
        var atrPct = pred.AtrPercent;

        // No ATR data — can't evaluate, pass with note
        if (atrPct is null or <= 0)
        {
            return new TradeFilterResult
            {
                FilterName = "Volatility",
                Status = TradeFilterStatus.Pass,
                Reason = "No ATR data available — volatility check skipped.",
            };
        }

        var isDayTrade = pred.TimeWindow is "1_day" or "intraday";

        // ── Extreme volatility check ──
        if (atrPct >= ExtremeAtrPct)
        {
            if (isDayTrade)
            {
                return new TradeFilterResult
                {
                    FilterName = "Volatility",
                    Status = TradeFilterStatus.Fail,
                    Reason = $"Extreme volatility (ATR {atrPct:F1}% >= {ExtremeAtrPct}%) on a day trade. Too risky for short timeframe.",
                };
            }

            return new TradeFilterResult
            {
                FilterName = "Volatility",
                Status = TradeFilterStatus.Warning,
                Reason = $"Extreme volatility (ATR {atrPct:F1}% >= {ExtremeAtrPct}%). Widen stops or reduce position.",
            };
        }

        // ── High volatility warning for day trades ──
        if (atrPct >= HighAtrPct && isDayTrade)
        {
            return new TradeFilterResult
            {
                FilterName = "Volatility",
                Status = TradeFilterStatus.Warning,
                Reason = $"High volatility (ATR {atrPct:F1}%) on a day trade. Position sizing should be reduced.",
            };
        }

        // ── Stop distance vs ATR check ──
        // If stop is set too tight relative to the stock's natural movement range,
        // it's likely to get triggered by noise rather than real adverse moves.
        if (pred.EntryReferencePrice is > 0 && pred.StopPrice is > 0 && pred.Atr14 is > 0)
        {
            var stopDistance = Math.Abs(pred.EntryReferencePrice.Value - pred.StopPrice.Value);
            var stopAtrRatio = stopDistance / pred.Atr14.Value;

            if (stopAtrRatio < MinStopAtrRatio)
            {
                return new TradeFilterResult
                {
                    FilterName = "Volatility",
                    Status = TradeFilterStatus.Warning,
                    Reason = $"Stop distance (${stopDistance:F2}) is only {stopAtrRatio:F2}× ATR (${pred.Atr14:F2}). Likely to be stopped out by noise.",
                };
            }
        }

        return new TradeFilterResult
        {
            FilterName = "Volatility",
            Status = TradeFilterStatus.Pass,
            Reason = $"Volatility acceptable (ATR {atrPct:F1}%).",
        };
    }
}
