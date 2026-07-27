using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

public interface ITradeDecisionEngine
{
    /// <summary>
    /// Evaluate a prediction and produce a capital-allocation decision
    /// using real historical performance statistics.
    /// </summary>
    Task<Models.TradeDecision> DecideAsync(PredictionCandidate prediction);
}
