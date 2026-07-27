using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Portfolio-level trade selection with Kelly-proportional capital allocation.
///
/// Rules:
///   1. Reject any trade with grade Reject or decision Reject.
///   2. Rank remaining by Grade (desc) → EV (desc) → R/R (desc).
///   3. Accept the top N until MaxPositions is reached.
///   4. Allocate capital proportional to each trade's Kelly-derived edge.
///   5. Generate portfolio warnings and a summary.
///
/// Kelly allocation: each accepted trade gets a raw Kelly fraction
/// f* = (p × b − q) / b, scaled by KellyFraction (quarter-Kelly default)
/// and modulated by the trade's confidence. Capital is then distributed
/// proportionally across trades based on their individual Kelly fractions.
/// When real stats aren't available (<30 outcomes), falls back to
/// confidence-proportional allocation.
///
/// Singleton — TradeStatsProvider is also singleton with its own cache.
/// </summary>
public class PortfolioDecisionEngine : IPortfolioDecisionEngine
{
    private readonly IHistoricalSimilarityEngine _similarityEngine;
    private readonly TradeStatsProvider _tradeStats;
    private readonly ILogger<PortfolioDecisionEngine> _logger;

    /// <summary>Quarter-Kelly default — matches PositionSizingConfig.</summary>
    private const double KellyFraction = 0.25;
    /// <summary>Minimum outcomes before Kelly kicks in.</summary>
    private const int KellyMinSamples = 30;
    /// <summary>Confidence floor for scaling.</summary>
    private const double ConfFloor = 35;
    /// <summary>Confidence ceiling for scaling.</summary>
    private const double ConfCeiling = 85;

    public PortfolioDecisionEngine(
        IHistoricalSimilarityEngine similarityEngine,
        TradeStatsProvider tradeStats,
        ILogger<PortfolioDecisionEngine> logger)
    {
        _similarityEngine = similarityEngine;
        _tradeStats = tradeStats;
        _logger = logger;
    }

    public async Task<PortfolioRecommendation> EvaluateAsync(PortfolioEvaluationRequest request)
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

        // ── 4. Kelly-proportional capital allocation ─────────────────
        // Each trade gets a raw "edge weight" based on Kelly fraction
        // modulated by confidence, then capital is divided proportionally.
        if (accepted.Count > 0 && request.AvailableBuyingPower > 0)
        {
            var stats = await _tradeStats.GetStatsAsync();
            var useKelly = stats.IsReal && stats.SampleSize >= KellyMinSamples;

            // Compute per-trade raw Kelly weight
            double fullKelly = 0;
            if (useKelly && stats.AverageLossPercent > 0)
            {
                var p = stats.WinRate;
                var q = 1.0 - p;
                var b = stats.AverageWinPercent / stats.AverageLossPercent;
                fullKelly = b > 0 ? (p * b - q) / b : 0;
            }

            var rawWeights = new List<(PortfolioTradeEntry Entry, string Key, double Weight)>();
            var method = useKelly && fullKelly > 0 ? "kelly" : "confidence";

            foreach (var entry in accepted)
            {
                var ticker = entry.Trade.Ticker ?? entry.Trade.PredictionId;
                var confidence = entry.Trade.ConfidenceScore ?? 50;
                var ev = entry.Trade.ExpectedValue ?? 0;

                double weight;
                if (useKelly && fullKelly > 0)
                {
                    // Confidence modulates the Kelly fraction per trade:
                    // high confidence → full fractional Kelly, low → half
                    var clampedConf = Math.Clamp(confidence, ConfFloor, ConfCeiling);
                    var confT = (ConfCeiling - ConfFloor) > 0
                        ? (clampedConf - ConfFloor) / (ConfCeiling - ConfFloor)
                        : 0.5;
                    var confScale = 0.5 + 0.5 * confT; // 0.5 to 1.0

                    weight = fullKelly * KellyFraction * confScale;

                    // EV bonus: trades with strong EV get a boost
                    if (ev > 5.0) weight *= 1.2;
                    else if (ev < 0) weight *= 0.6;
                }
                else
                {
                    // Fallback: confidence-proportional allocation
                    // Higher confidence trades get more capital
                    var clampedConf = Math.Clamp(confidence, ConfFloor, ConfCeiling);
                    weight = clampedConf;

                    // EV adjustment
                    if (ev > 5.0) weight *= 1.2;
                    else if (ev < 0) weight *= 0.6;
                }

                // Floor: every accepted trade gets at least weight 1.0
                weight = Math.Max(weight, 1.0);

                rawWeights.Add((entry, ticker, weight));
            }

            // Normalize to sum to 1, then multiply by buying power
            var totalWeight = rawWeights.Sum(w => w.Weight);

            foreach (var (entry, ticker, weight) in rawWeights)
            {
                var share = totalWeight > 0 ? weight / totalWeight : 1.0 / accepted.Count;
                var dollars = Math.Round(request.AvailableBuyingPower * share, 2);
                allocation[ticker] = dollars;
            }

            _logger.LogInformation(
                "[portfolio-decision] Capital allocation: method={Method}, fullKelly={FullKelly:F3}, trades={Count}, buyingPower=${BuyingPower:F2}",
                method, fullKelly, accepted.Count, request.AvailableBuyingPower);

            foreach (var (entry, ticker, weight) in rawWeights)
            {
                _logger.LogDebug(
                    "[portfolio-decision] {Ticker}: rawWeight={Weight:F3}, allocated=${Dollars:F2} ({Pct:P1})",
                    ticker, weight, allocation[ticker], allocation[ticker] / request.AvailableBuyingPower);
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

        // Concentration warning
        if (accepted.Count > 0)
        {
            var tickers = accepted
                .Where(a => a.Trade.Ticker is not null)
                .GroupBy(a => a.Trade.Ticker)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in tickers)
                warnings.Add($"Duplicate ticker: {group.Key} appears {group.Count()} times in accepted trades.");

            // Warn if any single trade gets >40% of capital
            foreach (var kvp in allocation)
            {
                var pct = request.AvailableBuyingPower > 0
                    ? kvp.Value / request.AvailableBuyingPower
                    : 0;
                if (pct > 0.40)
                    warnings.Add($"High concentration: {kvp.Key} allocated {pct:P0} of capital.");
            }
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
