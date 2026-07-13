using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision.Filters;

/// <summary>
/// Evaluates whether the underlying instrument has sufficient liquidity
/// (volume, spread, market cap) for the intended position size.
///
/// Current behaviour: always Pass (placeholder).
/// Future: check average volume, bid-ask spread, relative volume ratio.
/// </summary>
public class LiquidityTradeFilter : ITradeFilter
{
    public TradeFilterResult Evaluate(TradeDecisionContext context)
    {
        return new TradeFilterResult
        {
            FilterName = "Liquidity",
            Status = TradeFilterStatus.Pass,
            Reason = "Placeholder — liquidity filtering not yet implemented.",
        };
    }
}
