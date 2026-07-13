using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision.Filters;

/// <summary>
/// Evaluates whether the prediction's confidence score meets the
/// minimum threshold for trade consideration.
///
/// Current behaviour: always Pass (placeholder).
/// Future: configurable min-confidence gate, per-setup-type thresholds.
/// </summary>
public class ConfidenceTradeFilter : ITradeFilter
{
    public TradeFilterResult Evaluate(TradeDecisionContext context)
    {
        return new TradeFilterResult
        {
            FilterName = "Confidence",
            Status = TradeFilterStatus.Pass,
            Reason = "Placeholder — confidence filtering not yet implemented.",
        };
    }
}
