using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

public interface ITradeDecisionEngine
{
    Models.TradeDecision Decide(PredictionCandidate prediction);
}
