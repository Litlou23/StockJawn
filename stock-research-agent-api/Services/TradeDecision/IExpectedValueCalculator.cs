using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Computes expected value for a trade setup based on historical win/loss statistics.
/// Designed to be consumed by <see cref="ITradeDecisionEngine"/>.
///
/// Future phases will add overloads that accept a setup fingerprint and
/// pull historical stats from <c>setup_learning_stats</c> / <c>stock_learning_stats</c>.
/// </summary>
public interface IExpectedValueCalculator
{
    ExpectedValueResult Calculate(ExpectedValueRequest request);
}
