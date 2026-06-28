using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Analyzes prediction outcomes, updates signal performance stats,
/// adjusts scoring weights, and generates insights. This is a feedback
/// loop, not model fine-tuning: outcomes -> stats -> weights -> insights.
/// </summary>
public class LearningEngine
{
    private const int MinPredictionsForAdjustment = 5;
    private const double MaxWeightChange = 0.3;

    private readonly ResearchRepository _repo;
    private readonly ILogger<LearningEngine> _logger;

    public LearningEngine(ResearchRepository repo, ILogger<LearningEngine> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Signal Performance Tracking
    // -----------------------------------------------------------------------

    public async Task<(int Updated, List<ResearchSignalPerformance> Signals)> UpdateSignalPerformanceAsync()
    {
        var predictions = await _repo.GetRecentPredictionsAsync(200);
        var outcomes = await _repo.GetRecentOutcomesAsync(200);

        var outcomeMap = outcomes.ToDictionary(o => o.PredictionId);
        var tallies = new Dictionary<string, (int Total, int Correct, double TotalScore)>();

        foreach (var pred in predictions)
        {
            if (!outcomeMap.TryGetValue(pred.Id, out var outcome) || outcome.DirectionCorrect is null)
                continue;

            var signals = ExtractSignalsFromPrediction(pred);
            foreach (var signalName in signals)
            {
                var (total, correct, totalScore) = tallies.GetValueOrDefault(signalName);
                total++;
                if (outcome.DirectionCorrect == true) correct++;
                totalScore += outcome.OutcomeScore ?? 50;
                tallies[signalName] = (total, correct, totalScore);
            }
        }

        var results = new List<ResearchSignalPerformance>();
        foreach (var (signalName, (total, correct, totalScore)) in tallies)
        {
            await _repo.UpsertSignalPerformanceAsync(new
            {
                signal_name = signalName,
                signal_type = CategorizeSignal(signalName),
                total_predictions = total,
                correct_predictions = correct,
                accuracy = total > 0 ? (double)correct / total : 0,
                average_outcome_score = total > 0 ? totalScore / total : 0,
                last_updated_at = DateTimeOffset.UtcNow.ToString("o"),
            });
            results.Add(new ResearchSignalPerformance
            {
                SignalName = signalName,
                SignalType = CategorizeSignal(signalName),
                TotalPredictions = total,
                CorrectPredictions = correct,
                Accuracy = total > 0 ? (double)correct / total : 0,
                AverageOutcomeScore = total > 0 ? totalScore / total : 0,
                LastUpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        return (results.Count, results);
    }

    // -----------------------------------------------------------------------
    // Scoring Weight Adjustment
    // -----------------------------------------------------------------------

    public record WeightChange(string Signal, double OldWeight, double NewWeight, string Reason);

    public async Task<(int Adjusted, List<WeightChange> Changes)> UpdateScoringWeightsFromOutcomesAsync()
    {
        var perfStats = await _repo.GetAllSignalPerformanceAsync();
        var currentWeights = await _repo.GetScoringWeightsAsync();
        var weightMap = currentWeights.ToDictionary(w => w.SignalName, w => w.Weight);
        var changes = new List<WeightChange>();

        foreach (var perf in perfStats)
        {
            if (perf.TotalPredictions < MinPredictionsForAdjustment) continue;

            var oldWeight = weightMap.GetValueOrDefault(perf.SignalName, 1.0);
            var accuracyDelta = perf.Accuracy - 0.5;
            var adjustment = Math.Clamp(accuracyDelta * MaxWeightChange * 2, -MaxWeightChange, MaxWeightChange);
            var newWeight = Math.Clamp(oldWeight + adjustment, 0.1, 3.0);

            if (Math.Abs(newWeight - oldWeight) < 0.05) continue;

            newWeight = Math.Round(newWeight, 2);
            var reason = $"Accuracy: {perf.Accuracy * 100:F1}% over {perf.TotalPredictions} predictions. Avg score: {perf.AverageOutcomeScore:F1}.";
            await _repo.UpdateScoringWeightAsync(perf.SignalName, newWeight, reason);
            changes.Add(new WeightChange(perf.SignalName, oldWeight, newWeight, reason));
        }

        return (changes.Count, changes);
    }

    // -----------------------------------------------------------------------
    // Learning Insights Generation
    // -----------------------------------------------------------------------

    public async Task<List<object>> GenerateLearningInsightsAsync()
    {
        var perfStats = await _repo.GetAllSignalPerformanceAsync();
        var outcomes = await _repo.GetRecentOutcomesAsync(100);
        var predictions = await _repo.GetRecentPredictionsAsync(100);
        var insights = new List<object>();

        // 1. Reliable signals
        var reliable = perfStats.Where(s => s.TotalPredictions >= MinPredictionsForAdjustment && s.Accuracy > 0.6).ToList();
        if (reliable.Count > 0)
        {
            insights.Add(new
            {
                insight_type = "signal",
                summary = $"Reliable signals: {string.Join(", ", reliable.Select(s => $"{s.SignalName} ({s.Accuracy * 100:F0}% accuracy, n={s.TotalPredictions})"))}",
                evidence = $"Based on {reliable.Sum(s => s.TotalPredictions)} total predictions.",
                action_recommendation = "Increase weight on these signals in future predictions.",
                confidence = Math.Min((double)reliable[0].TotalPredictions / 20, 1),
            });
        }

        // 2. Unreliable signals
        var unreliable = perfStats.Where(s => s.TotalPredictions >= MinPredictionsForAdjustment && s.Accuracy < 0.4).ToList();
        if (unreliable.Count > 0)
        {
            insights.Add(new
            {
                insight_type = "signal",
                summary = $"Unreliable signals: {string.Join(", ", unreliable.Select(s => $"{s.SignalName} ({s.Accuracy * 100:F0}% accuracy, n={s.TotalPredictions})"))}",
                evidence = $"Based on {unreliable.Sum(s => s.TotalPredictions)} total predictions.",
                action_recommendation = "Decrease weight on these signals. Consider whether they are noise.",
                confidence = Math.Min((double)unreliable[0].TotalPredictions / 20, 1),
            });
        }

        // 3. Per-ticker patterns
        var outcomeMap = outcomes.ToDictionary(o => o.PredictionId);
        var tickerStats = new Dictionary<string, (int Correct, int Wrong, int Total)>();

        foreach (var pred in predictions)
        {
            if (!outcomeMap.TryGetValue(pred.Id, out var outcome) || outcome.DirectionCorrect is null) continue;
            var (correct, wrong, total) = tickerStats.GetValueOrDefault(pred.Ticker);
            total++;
            if (outcome.DirectionCorrect == true) correct++; else wrong++;
            tickerStats[pred.Ticker] = (correct, wrong, total);
        }

        foreach (var (ticker, (correct, wrong, total)) in tickerStats)
        {
            if (total < 3) continue;
            var accuracy = (double)correct / total;
            if (accuracy < 0.3)
            {
                insights.Add(new
                {
                    insight_type = "ticker",
                    summary = $"{ticker} predictions have been unreliable: {correct}/{total} correct ({accuracy * 100:F0}%).",
                    evidence = $"{wrong} wrong predictions vs {correct} correct.",
                    action_recommendation = $"Consider requiring higher confidence threshold for {ticker} or investigating what makes it unpredictable.",
                    confidence = Math.Min((double)total / 10, 1),
                });
            }
            else if (accuracy > 0.7 && total >= 5)
            {
                insights.Add(new
                {
                    insight_type = "ticker",
                    summary = $"{ticker} predictions have been reliable: {correct}/{total} correct ({accuracy * 100:F0}%).",
                    evidence = $"Consistent across {total} predictions.",
                    action_recommendation = $"{ticker} may be a good candidate for higher-confidence predictions.",
                    confidence = Math.Min((double)total / 10, 1),
                });
            }
        }

        // 4. Missing data impact
        var missingDataPreds = predictions.Where(p => p.MissingDataWarnings.Count > 0).ToList();
        if (missingDataPreds.Count > 0)
        {
            var withOutcome = missingDataPreds.Where(p => outcomeMap.ContainsKey(p.Id)).ToList();
            var missingCorrect = withOutcome.Count(p => outcomeMap[p.Id].DirectionCorrect == true);
            var missingTotal = withOutcome.Count;

            if (missingTotal >= 3)
            {
                var missingAcc = (double)missingCorrect / missingTotal;
                var allWarnings = missingDataPreds.SelectMany(p => p.MissingDataWarnings).Distinct().Take(3);
                insights.Add(new
                {
                    insight_type = "risk_rule",
                    summary = $"Predictions with missing data: {missingAcc * 100:F0}% accuracy ({missingCorrect}/{missingTotal}).",
                    evidence = "Common missing data: " + string.Join("; ", allWarnings),
                    action_recommendation = missingAcc < 0.4
                        ? "Missing data significantly hurts accuracy. Require more data before generating predictions."
                        : "Missing data has moderate impact. Continue but flag low-data predictions clearly.",
                    confidence = Math.Min((double)missingTotal / 10, 1),
                });
            }
        }

        if (insights.Count > 0)
            await _repo.SaveLearningInsightsAsync(insights);

        return insights;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static List<string> ExtractSignalsFromPrediction(PredictionCandidate pred)
    {
        var signals = new List<string>();
        foreach (var src in pred.DataSourcesUsed)
        {
            if (src == "twelve-data")
                signals.AddRange(["technical_trend", "technical_momentum", "technical_volume", "technical_ma_position"]);
            else if (src == "rss-news")
                signals.AddRange(["news_sentiment_bullish", "news_sentiment_bearish", "news_volume"]);
        }
        return signals;
    }

    private static string CategorizeSignal(string name) =>
        name.StartsWith("technical_") ? "technical"
        : name.StartsWith("news_") ? "news_sentiment"
        : name.StartsWith("catalyst_") ? "catalyst"
        : name.StartsWith("volume") ? "volume"
        : "market_context";
}
