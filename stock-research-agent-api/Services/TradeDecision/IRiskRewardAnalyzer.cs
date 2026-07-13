using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Calculates risk/reward metrics for a potential trade.
/// Stateless, pure calculation — no database access, no side effects.
/// Designed to be consumed by <see cref="ITradeDecisionEngine"/> and
/// reusable anywhere else in the system (options lab, paper trading, etc.).
/// </summary>
public interface IRiskRewardAnalyzer
{
    RiskRewardResult Analyze(RiskRewardRequest request);
}
