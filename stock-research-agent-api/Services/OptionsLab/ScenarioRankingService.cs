using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.OptionsLab;

/// <summary>
/// Ranks auto-generated scenarios and selects a recommended one.
/// Scoring factors: direction match, confidence fit, risk/reward ratio,
/// breakeven distance, payoff potential, and historical accuracy.
/// </summary>
public static class ScenarioRankingService
{
    public static List<OptionsScenarioCard> RankAndRecommend(
        List<OptionsScenarioCard> scenarios,
        PredictionCandidate pred,
        double currentPrice,
        double? endingPrice,
        double historicalAccuracy)
    {
        if (scenarios.Count == 0) return scenarios;

        foreach (var s in scenarios)
        {
            s.Recommended = false;
            s.RecommendationReason = null;
        }

        var scored = scenarios
            .Select(s => (Scenario: s, Score: ComputeScore(s, pred, currentPrice, endingPrice, historicalAccuracy)))
            .OrderByDescending(x => x.Score)
            .ToList();

        // Mark top scorer as recommended
        var best = scored[0];
        best.Scenario.Recommended = true;
        best.Scenario.RecommendationReason = BuildRecommendationReason(best.Scenario, pred);

        return scored.Select(x => x.Scenario).ToList();
    }

    private static double ComputeScore(
        OptionsScenarioCard s,
        PredictionCandidate pred,
        double currentPrice,
        double? endingPrice,
        double historicalAccuracy)
    {
        double score = 0;

        // 1. Confidence fit (0-30 pts)
        score += s.ConfidenceFitScore * 0.3;

        // 2. Risk/reward ratio (0-25 pts) — higher maxProfit/maxLoss = better
        if (s.MaxLoss > 0 && s.MaxProfit > 0)
        {
            var rr = s.MaxProfit == -1 ? 5.0 : s.MaxProfit / s.MaxLoss; // unlimited profit gets 5x
            score += Math.Min(rr * 5, 25);
        }

        // 3. Breakeven distance (0-20 pts) — closer breakeven = easier to reach
        if (s.Breakevens.Count > 0 && currentPrice > 0)
        {
            var nearestBe = s.Breakevens.Min(b => Math.Abs(b - currentPrice));
            var bePercent = nearestBe / currentPrice * 100;
            // Closer = better: 0% distance = 20 pts, 10%+ = 0 pts
            score += Math.Max(0, 20 - bePercent * 2);
        }

        // 4. Estimated return if prediction hits (0-15 pts)
        if (s.EstimatedReturnPercent > 0)
            score += Math.Min(s.EstimatedReturnPercent / 10, 15);

        // 5. Spread bonus for moderate confidence (0-10 pts)
        if (pred.ConfidenceScore < 65 &&
            s.StrategyType is OptionsStrategyType.bull_call_spread_proxy or OptionsStrategyType.bear_put_spread_proxy)
        {
            score += 10; // spreads are more appropriate for moderate confidence
        }

        // 6. Duration preference — 2-week sweet spot
        if (s.Duration == "2_week") score += 5;
        else if (s.Duration == "1_week") score += 2;
        // 3-week gets no bonus

        return score;
    }

    private static string BuildRecommendationReason(OptionsScenarioCard s, PredictionCandidate pred)
    {
        var parts = new List<string>();

        if (s.StrategyType is OptionsStrategyType.bull_call_spread_proxy or OptionsStrategyType.bear_put_spread_proxy)
            parts.Add("defined risk with capped loss");
        else if (s.StrategyType is OptionsStrategyType.iron_condor_proxy)
            parts.Add("profits from low movement matching neutral prediction");
        else
            parts.Add("directional exposure matching prediction");

        if (pred.ConfidenceScore >= 60)
            parts.Add($"prediction confidence ({pred.ConfidenceScore}%) supports this strategy");
        else
            parts.Add($"moderate confidence ({pred.ConfidenceScore}%) favors defined-risk strategies");

        if (s.EstimatedReturnPercent > 50)
            parts.Add($"strong theoretical return potential ({s.EstimatedReturnPercent:F0}%)");

        return "System-recommended: " + string.Join("; ", parts) + ".";
    }
}
