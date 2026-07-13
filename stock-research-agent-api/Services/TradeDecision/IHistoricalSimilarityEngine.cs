using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Given a trade decision, identifies similar historical cases and
/// summarises their outcomes.  Provides decision-support context —
/// does not make decisions or modify rankings.
/// </summary>
public interface IHistoricalSimilarityEngine
{
    HistoricalSimilarityResult FindSimilar(HistoricalSimilarityRequest request);
}
