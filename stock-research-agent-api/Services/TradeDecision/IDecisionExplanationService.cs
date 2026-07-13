using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Generates a deterministic, human-readable explanation of a trade decision.
/// Separates "why" (presentation) from "what" (business logic in the engine).
/// </summary>
public interface IDecisionExplanationService
{
    DecisionExplanation Explain(DecisionExplanationRequest request);
}
