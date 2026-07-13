using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Evaluates one aspect of a potential trade.
/// Implementations must be stateless and side-effect-free.
///
/// The engine runs every registered filter on every decision — it never
/// short-circuits on the first Fail.  Filters should not depend on each
/// other's results; they read only from <see cref="TradeDecisionContext"/>.
/// </summary>
public interface ITradeFilter
{
    TradeFilterResult Evaluate(TradeDecisionContext context);
}
