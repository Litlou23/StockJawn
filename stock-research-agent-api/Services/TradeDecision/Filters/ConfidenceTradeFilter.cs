using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision.Filters;

/// <summary>
/// Evaluates whether the prediction's confidence score meets the
/// minimum threshold for trade consideration.
///
/// Fail:    confidence &lt; 20 — too speculative for any action.
/// Warning: confidence &lt; 35 — low confidence, proceed with caution.
/// Pass:    confidence &gt;= 35 — meets minimum quality bar.
///
/// Also checks risk/confidence alignment: high risk + low confidence
/// is a dangerous combination that gets a warning.
/// </summary>
public class ConfidenceTradeFilter : ITradeFilter
{
    private const int FailThreshold = 20;
    private const int WarnThreshold = 35;

    public TradeFilterResult Evaluate(TradeDecisionContext context)
    {
        var confidence = context.Prediction.ConfidenceScore;
        var risk = context.Prediction.RiskScore;

        if (confidence < FailThreshold)
        {
            return new TradeFilterResult
            {
                FilterName = "Confidence",
                Status = TradeFilterStatus.Fail,
                Reason = $"Confidence {confidence} is below minimum threshold ({FailThreshold}). Too speculative.",
            };
        }

        if (confidence < WarnThreshold)
        {
            return new TradeFilterResult
            {
                FilterName = "Confidence",
                Status = TradeFilterStatus.Warning,
                Reason = $"Low confidence ({confidence} < {WarnThreshold}). Consider smaller position or skip.",
            };
        }

        // High risk + mediocre confidence = dangerous combo
        if (risk >= 70 && confidence < 50)
        {
            return new TradeFilterResult
            {
                FilterName = "Confidence",
                Status = TradeFilterStatus.Warning,
                Reason = $"High risk ({risk}) with moderate confidence ({confidence}). Risk/confidence misalignment.",
            };
        }

        return new TradeFilterResult
        {
            FilterName = "Confidence",
            Status = TradeFilterStatus.Pass,
            Reason = $"Confidence {confidence} passes minimum threshold.",
        };
    }
}
