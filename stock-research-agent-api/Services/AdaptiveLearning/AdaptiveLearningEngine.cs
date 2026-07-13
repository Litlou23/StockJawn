using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

namespace StockResearchAgent.Api.Services.AdaptiveLearning;

/// <summary>
/// Learns conditional signal performance by recording outcomes
/// and maintaining running statistics per (signal, condition-set) pair.
///
/// After each completed prediction, call <see cref="RecordOutcomeAsync"/>
/// to update all relevant conditional stats.
///
/// Does NOT replace the existing LearningEngine — it extends the
/// learning surface to include regime-conditional behaviour.
/// </summary>
public class AdaptiveLearningEngine : IAdaptiveLearningEngine
{
    private readonly IAdaptiveLearningRepository _repository;

    /// <summary>
    /// Signal bucket names that map to EvaluatorKind.
    /// </summary>
    private static readonly string[] SignalBuckets =
    [
        "Trend", "Momentum", "Volume", "Volatility",
        "MarketContext", "Catalyst", "Learning",
    ];

    public AdaptiveLearningEngine(IAdaptiveLearningRepository repository)
    {
        _repository = repository;
    }

    public async Task RecordOutcomeAsync(AdaptiveLearningObservation observation)
    {
        var prediction = observation.Prediction;
        var outcome = observation.Outcome;
        var isWin = outcome.Outcome == "win";
        var returnPct = outcome.ReturnPercent ?? 0;
        var holdingDays = outcome.HoldingPeriodDays ?? 0;

        // Build all condition axes from the observation
        var conditionAxes = BuildConditionAxes(observation);

        // For each signal bucket, update stats for each individual condition
        // and for meaningful combinations (signal × single condition).
        // Full N-way combinations are deferred to StrategyDiscoveryEngine.
        foreach (var signal in SignalBuckets)
        {
            // Unconditional (signal alone — already tracked by LearningEngine,
            // but we maintain here too for consistent querying)
            await UpdateStatAsync(signal, [], isWin, returnPct, holdingDays);

            // Single-condition slices
            foreach (var condition in conditionAxes)
            {
                await UpdateStatAsync(signal, [condition], isWin, returnPct, holdingDays);
            }
        }
    }

    public async Task<ConditionalPerformanceResult> QueryAsync(ConditionalPerformanceQuery query)
    {
        var performances = await _repository.QueryAsync(query);

        var best = performances
            .Where(p => p.SampleSize >= query.MinSampleSize)
            .OrderByDescending(p => p.WinRate)
            .ThenByDescending(p => p.AverageReturn)
            .FirstOrDefault();

        var worst = performances
            .Where(p => p.SampleSize >= query.MinSampleSize)
            .OrderBy(p => p.WinRate)
            .ThenBy(p => p.AverageReturn)
            .FirstOrDefault();

        var summary = performances.Count > 0
            ? $"{performances.Count} conditional stats found for {query.SignalName ?? "all signals"}."
            : "No conditional performance data available yet.";

        return new ConditionalPerformanceResult
        {
            Performances = performances,
            BestCondition = best,
            WorstCondition = worst,
            Summary = summary,
        };
    }

    public async Task<List<ConditionalSignalPerformance>> GetSignalProfileAsync(string signalName)
    {
        return await _repository.GetBySignalAsync(signalName);
    }

    public async Task<ConditionalSignalPerformance?> GetBestConditionAsync(string signalName, int minSampleSize = 10)
    {
        var stats = await _repository.GetBySignalAsync(signalName);
        return stats
            .Where(p => p.SampleSize >= minSampleSize && p.Conditions.Count > 0)
            .OrderByDescending(p => p.WinRate)
            .ThenByDescending(p => p.AverageReturn)
            .FirstOrDefault();
    }

    // ══════════════════════════════════════════════════════════════
    // Internals
    // ══════════════════════════════════════════════════════════════

    private async Task UpdateStatAsync(
        string signalName,
        List<LearningCondition> conditions,
        bool isWin,
        double returnPct,
        int holdingDays)
    {
        // Retrieve existing or create new
        var query = new ConditionalPerformanceQuery
        {
            SignalName = signalName,
            Conditions = conditions,
            MinSampleSize = 0,
        };

        var results = await _repository.QueryAsync(query);
        var existing = results
            .FirstOrDefault(p =>
                p.Conditions.Count == conditions.Count &&
                conditions.All(c => p.Conditions.Any(pc =>
                    pc.Type == c.Type && pc.Value == c.Value)));

        var oldN = existing?.SampleSize ?? 0;
        var oldWins = (int)Math.Round((existing?.WinRate ?? 0) * oldN);
        var oldTotalReturn = (existing?.AverageReturn ?? 0) * oldN;
        var oldTotalHolding = (existing?.AverageHoldingDays ?? 0) * oldN;

        var newN = oldN + 1;
        var newWins = oldWins + (isWin ? 1 : 0);
        var newTotalReturn = oldTotalReturn + returnPct;
        var newTotalHolding = oldTotalHolding + holdingDays;

        var updated = new ConditionalSignalPerformance
        {
            SignalName = signalName,
            Conditions = conditions,
            SampleSize = newN,
            WinRate = Math.Round((double)newWins / newN, 4),
            AverageReturn = Math.Round(newTotalReturn / newN, 4),
            MedianReturn = returnPct, // approximation — proper median requires storing all values
            AverageHoldingDays = Math.Round(newTotalHolding / newN, 1),
            Confidence = StatisticalConfidence.FromSampleSize(newN),
            LastUpdated = DateTimeOffset.UtcNow,
        };

        await _repository.UpsertPerformanceAsync(updated);
    }

    private static List<LearningCondition> BuildConditionAxes(
        AdaptiveLearningObservation obs)
    {
        var conditions = new List<LearningCondition>();

        // Market regime(s)
        if (obs.RegimeAtPrediction?.ActiveRegimes is { Count: > 0 } regimes)
        {
            foreach (var regime in regimes)
            {
                conditions.Add(new LearningCondition
                {
                    Type = LearningConditionType.MarketRegime,
                    Value = regime.Type.ToString(),
                });
            }
        }

        // Direction
        var direction = obs.Prediction.WinningDirection
                     ?? obs.Prediction.PredictionType.ToString();
        if (!string.IsNullOrEmpty(direction))
        {
            conditions.Add(new LearningCondition
            {
                Type = LearningConditionType.Direction,
                Value = direction,
            });
        }

        // Trade grade
        if (obs.TradeGrade != TradeGrade.Unspecified)
        {
            conditions.Add(new LearningCondition
            {
                Type = LearningConditionType.TradeGrade,
                Value = obs.TradeGrade.ToString(),
            });
        }

        // Sector
        if (!string.IsNullOrEmpty(obs.Sector))
        {
            conditions.Add(new LearningCondition
            {
                Type = LearningConditionType.Sector,
                Value = obs.Sector,
            });
        }

        // Market cap
        if (!string.IsNullOrEmpty(obs.MarketCap))
        {
            conditions.Add(new LearningCondition
            {
                Type = LearningConditionType.MarketCap,
                Value = obs.MarketCap,
            });
        }

        // Confidence band
        var conf = obs.Prediction.ConfidenceScore;
        if (conf is not null)
        {
            var band = conf switch
            {
                >= 80 => "VeryHigh",
                >= 60 => "High",
                >= 40 => "Medium",
                >= 20 => "Low",
                _ => "VeryLow",
            };
            conditions.Add(new LearningCondition
            {
                Type = LearningConditionType.ConfidenceBand,
                Value = band,
            });
        }

        // Risk band
        var risk = obs.Prediction.RiskScore;
        if (risk is not null)
        {
            var band = risk switch
            {
                >= 75 => "High",
                >= 50 => "Medium",
                _ => "Low",
            };
            conditions.Add(new LearningCondition
            {
                Type = LearningConditionType.RiskBand,
                Value = band,
            });
        }

        return conditions;
    }
}
