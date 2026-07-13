using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.MarketRegime;

/// <summary>
/// Classifies the current market environment into zero or more
/// simultaneously active regimes, each with a confidence level.
///
/// Stateless — classification is derived entirely from the
/// <see cref="MarketRegimeContext"/> input.
/// </summary>
public interface IMarketRegimeEngine
{
    MarketRegimeResult Classify(MarketRegimeContext context);
}
