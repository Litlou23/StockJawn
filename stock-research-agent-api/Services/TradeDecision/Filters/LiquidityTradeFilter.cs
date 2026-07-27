using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision.Filters;

/// <summary>
/// Evaluates whether the underlying instrument has sufficient liquidity
/// for safe trade execution.
///
/// Checks:
/// - Price &lt; $2 → Fail (penny stock, wide spreads, unreliable fills)
/// - Price &lt; $5 → Warning (low-priced, may have liquidity issues)
/// - R/R validation: if risk/reward couldn't be computed, flag as warning
///   (often indicates thin/gapped markets where fills are unreliable)
/// </summary>
public class LiquidityTradeFilter : ITradeFilter
{
    private const double PennyStockThreshold = 2.0;
    private const double LowPriceThreshold = 5.0;

    public TradeFilterResult Evaluate(TradeDecisionContext context)
    {
        var pred = context.Prediction;
        var price = pred.EntryReferencePrice;

        // ── Penny stock filter ──
        if (price is > 0 and < PennyStockThreshold)
        {
            return new TradeFilterResult
            {
                FilterName = "Liquidity",
                Status = TradeFilterStatus.Fail,
                Reason = $"Entry price ${price:F2} is below ${PennyStockThreshold} — penny stock territory with unreliable fills and wide spreads.",
            };
        }

        // ── Low-price warning ──
        if (price is > 0 and < LowPriceThreshold)
        {
            return new TradeFilterResult
            {
                FilterName = "Liquidity",
                Status = TradeFilterStatus.Warning,
                Reason = $"Low entry price (${price:F2}). May have wider spreads and lower institutional participation.",
            };
        }

        // ── R/R validation error = market data quality concern ──
        if (context.RrResult?.ValidationError is not null)
        {
            return new TradeFilterResult
            {
                FilterName = "Liquidity",
                Status = TradeFilterStatus.Warning,
                Reason = $"Risk/reward could not be validated — potential market data or pricing issue.",
            };
        }

        // ── No entry price at all ──
        if (price is null or <= 0)
        {
            return new TradeFilterResult
            {
                FilterName = "Liquidity",
                Status = TradeFilterStatus.Warning,
                Reason = "No entry reference price available — cannot verify pricing quality.",
            };
        }

        return new TradeFilterResult
        {
            FilterName = "Liquidity",
            Status = TradeFilterStatus.Pass,
            Reason = $"Entry price ${price:F2} passes liquidity check.",
        };
    }
}
