using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision.Filters;

/// <summary>
/// Evaluates whether current volatility conditions are suitable for
/// the trade's expected holding period and risk profile.
///
/// Current behaviour: always Pass (placeholder).
/// Future: check ATR-relative stop distance, IV rank, VIX regime.
/// </summary>
public class VolatilityTradeFilter : ITradeFilter
{
    public TradeFilterResult Evaluate(TradeDecisionContext context)
    {
        return new TradeFilterResult
        {
            FilterName = "Volatility",
            Status = TradeFilterStatus.Pass,
            Reason = "Placeholder — volatility filtering not yet implemented.",
        };
    }
}
