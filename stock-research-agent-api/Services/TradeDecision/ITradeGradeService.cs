using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Assigns a deterministic letter grade to a trade opportunity
/// based on EV, risk/reward, and filter outcomes.
/// </summary>
public interface ITradeGradeService
{
    TradeGradeResult Grade(TradeGradeRequest request);
}
