using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Portfolio-level trade selection.
///
/// Phase 1 rules (deterministic, no optimisation):
///   1. Reject any trade with grade Reject or decision Reject.
///   2. Rank remaining by Grade (desc) → EV (desc) → R/R (desc).
///   3. Accept the top N until MaxPositions is reached.
///   4. Defer everything else.
///   5. Generate portfolio warnings and a summary.
///
/// Future phases will add sector exposure, correlation, position
/// replacement, and proper capital allocation. The interface stays
/// the same — only internal logic changes.
///
/// Stateless, no I/O, safe to register as singleton.
/// </summary>
public class PortfolioDecisionEngine : IPortfolioDecisionEngine
{
    private readonly IHistoricalSimilarityEngine _similarityEngine;

    public PortfolioDecisionEngine(IHistoricalSimilarityEngine similarityEngine)
    {
        _similarityEngine = similarityEngine;
    }

    public PortfolioRecommendation Evaluate(PortfolioEvaluationRequest request)
    {
        var accepted = new List<PortfolioTradeEntry>();
        var deferred = new List<PortfolioTradeEntry>();
        var rejected = new List<PortfolioTradeEntry>();
        var warnings = new List<string>();
        var allocation = new Dictionary<string, double>();

        // ── 1. Reject low-quality trades ─────────────────────────────
        var viable = new List<Models.TradeDecision>();

        foreach (var trade in request.Opportunities)
        {
            if (trade.TradeGrade == TradeGrade.Reject ||
                trade.Decision == TradeDecisionType.Reject)
            {
                rejected.Add(new PortfolioTradeEntry
                {
                    Trade = trade,
                    Disposition = PortfolioDisposition.Rejected,
                    Reason = trade.TradeGrade == TradeGrade.Reject
                        ? "Trade grade is Reject — does not meet minimum quality."
                        : "Trade decision is Reject.",
                });
            }
            else
            {
                viable.Add(trade);
            }
        }

        // ── 2. Rank: Grade desc → EV desc → R/R desc ────────────────
        var ranked = viable
            .OrderByDescending(t => (int)t.TradeGrade)
            .ThenByDescending(t => t.ExpectedValue ?? double.MinValue)
            .ThenByDescending(t => t.RiskRewardRatio ?? 0.0)
            .ToList();

        // ── 3. Accept top N, defer the rest ──────────────────────────
        var slotsRemaining = Math.Max(0, request.MaxPositions);
        var capitalRemaining = request.AvailableBuyingPower;

        foreach (var trade in ranked)
        {
            if (slotsRemaining <= 0)
            {
                deferred.Add(new PortfolioTradeEntry
                {
                    Trade = trade,
                    Disposition = PortfolioDisposition.Deferred,
                    Reason = "Maximum positions reached.",
                });
                continue;
            }

            if (capitalRemaining <= 0)
            {
                deferred.Add(new PortfolioTradeEntry
                {
                    Trade = trade,
                    Disposition = PortfolioDisposition.Deferred,
                    Reason = "Buying power exhausted.",
                });
                continue;
            }

            accepted.Add(new PortfolioTradeEntry
            {
                Trade = trade,
                Disposition = PortfolioDisposition.Accepted,
                Reason = $"Ranked #{accepted.Count + 1} — grade {trade.TradeGrade}, EV {trade.ExpectedValue ?? 0:F2}%, R/R {trade.RiskRewardRatio ?? 0:F2}.",
            });

            slotsRemaining--;
        }

        // ── 4. Placeholder capital allocation (uniform) ──────────────
        if (accepted.Count > 0 && request.AvailableBuyingPower > 0)
        {
            var perTrade = request.AvailableBuyingPower / accepted.Count;
            foreach (var entry in accepted)
            {
                var ticker = entry.Trade.Ticker ?? entry.Trade.PredictionId;
                allocation[ticker] = Math.Round(perTrade, 2);
            }
        }

        // ── 5. Portfolio warnings ────────────────────────────────────
        if (deferred.Count > 0 && deferred.Any(d => d.Reason == "Maximum positions reached."))
            warnings.Add($"Maximum positions ({request.MaxPositions}) reached — {deferred.Count(d => d.Reason == "Maximum positions reached.")} trade(s) deferred.");

        if (deferred.Count > 0 && deferred.Any(d => d.Reason == "Buying power exhausted."))
            warnings.Add("Buying power exhausted — some trades could not be allocated capital.");

        if (rejected.Count > 0)
            warnings.Add($"{rejected.Count} trade(s) rejected due to low quality.");

        if (accepted.Count > 0 && accepted.Count == request.MaxPositions)
            warnings.Add("Portfolio is at full capacity.");

        // Placeholder concentration warning
        if (accepted.Count > 0)
        {
            var tickers = accepted
                .Where(a => a.Trade.Ticker is not null)
                .GroupBy(a => a.Trade.Ticker)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in tickers)
                warnings.Add($"Duplicate ticker: {group.Key} appears {group.Count()} times in accepted trades.");
        }

        // ── 6. Summary ──────────────────────────────────────────────
        var summaryParts = new List<string>();
        summaryParts.Add($"{accepted.Count} trade(s) accepted");

        if (deferred.Count > 0)
            summaryParts.Add($"{deferred.Count} deferred due to portfolio limits");
        if (rejected.Count > 0)
            summaryParts.Add($"{rejected.Count} rejected");

        var summary = string.Join(". ", summaryParts) + ".";

        // ── 7. Historical similarity context (does not affect ranking) ──
        Dictionary<string, HistoricalSimilarityResult>? historicalContext = null;
        if (accepted.Count > 0)
        {
            historicalContext = new Dictionary<string, HistoricalSimilarityResult>();
            foreach (var entry in accepted)
            {
                var key = entry.Trade.Ticker ?? entry.Trade.PredictionId;
                if (!historicalContext.ContainsKey(key))
                {
                    historicalContext[key] = _similarityEngine.FindSimilar(
                        new HistoricalSimilarityRequest { Trade = entry.Trade });
                }
            }
        }

        return new PortfolioRecommendation
        {
            AcceptedTrades = accepted,
            DeferredTrades = deferred,
            RejectedTrades = rejected,
            RecommendedCapitalAllocation = allocation,
            PortfolioWarnings = warnings,
            Summary = summary,
            HistoricalContext = historicalContext,
        };
    }
}
