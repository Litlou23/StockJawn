using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.StrategyDiscovery;

/// <summary>
/// Deterministic strategy discovery via combinatorial condition mining.
///
/// Algorithm:
///   1. Maintain an in-memory observation log.
///   2. On Discover(), enumerate all 2-to-N condition combinations
///      observed across the log.
///   3. For each combination with sufficient sample size, compute
///      win rate, average return, and confidence.
///   4. Return combinations that exceed performance thresholds.
///
/// Thread-safe. Singleton-safe.
/// </summary>
public class StrategyDiscoveryEngine : IStrategyDiscoveryEngine
{
    private readonly ConcurrentBag<StrategyObservationInput> _observations = [];
    private readonly ConcurrentDictionary<string, DiscoveredStrategy> _discovered = new();
    private readonly IStrategyDiscoveryRepository? _repository;
    private readonly ILogger<StrategyDiscoveryEngine>? _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private bool _loaded;

    public StrategyDiscoveryEngine() { }

    public StrategyDiscoveryEngine(
        IStrategyDiscoveryRepository repository,
        ILogger<StrategyDiscoveryEngine> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Load persisted observations and strategies from the repository.
    /// Called once on first use (lazy init to avoid blocking startup).
    /// Thread-safe via SemaphoreSlim.
    /// </summary>
    private async Task EnsureLoadedAsync()
    {
        if (_loaded || _repository is null) return;

        await _loadLock.WaitAsync();
        try
        {
            if (_loaded) return; // double-check after acquiring lock
            _loaded = true;

            var observations = await _repository.GetAllObservationsAsync();
            foreach (var obs in observations) _observations.Add(obs);

            var strategies = await _repository.GetAllStrategiesAsync();
            foreach (var s in strategies) _discovered[s.StrategyId] = s;

            _logger?.LogInformation(
                "[strategy-discovery] Loaded {Obs} observations and {Strat} strategies from persistence",
                observations.Count, strategies.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[strategy-discovery] Failed to load persisted data — starting empty");
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task RecordObservationAsync(StrategyObservationInput input)
    {
        _observations.Add(input);

        // Persist (non-blocking failure — observation is already in memory)
        if (_repository is not null)
        {
            try
            {
                await _repository.StoreObservationAsync(input);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[strategy-discovery] Failed to persist observation");
            }
        }
    }

    public async Task<StrategyDiscoveryResult> DiscoverAsync(StrategyDiscoveryRequest request)
    {
        // Ensure persisted data is loaded before discovery
        await EnsureLoadedAsync();

        var observations = _observations.ToList();
        if (observations.Count < request.MinSampleSize)
        {
            return new StrategyDiscoveryResult
            {
                Summary = $"Insufficient data — {observations.Count} observations, need {request.MinSampleSize}.",
            };
        }

        var candidateMap = new Dictionary<string, List<StrategyObservationInput>>();

        // Group observations by condition combinations (2..MaxDepth)
        foreach (var obs in observations)
        {
            var conditions = obs.Conditions
                .OrderBy(c => c.Type).ThenBy(c => c.Value)
                .ToList();

            // Generate all combinations of size 2..MaxDepth
            for (var depth = 2; depth <= Math.Min(request.MaxCombinationDepth, conditions.Count); depth++)
            {
                foreach (var combo in Combinations(conditions, depth))
                {
                    var key = BuildPatternKey(combo);
                    if (!candidateMap.ContainsKey(key))
                        candidateMap[key] = [];
                    candidateMap[key].Add(obs);
                }
            }
        }

        // Evaluate candidates
        var strategies = new List<DiscoveredStrategy>();
        var evaluated = 0;

        foreach (var (key, obs) in candidateMap)
        {
            if (obs.Count < request.MinSampleSize) continue;
            evaluated++;

            var wins = obs.Count(o => o.IsWin);
            var winRate = (double)wins / obs.Count;
            var avgReturn = obs.Average(o => o.ReturnPercent);
            var returns = obs.Select(o => o.ReturnPercent).OrderBy(r => r).ToList();
            var medianReturn = Median(returns);

            if (winRate < request.MinWinRate || avgReturn < request.MinAverageReturn)
                continue;

            // Only keep conditions that are in this specific combo
            var comboConditions = ParseKeyToConditions(key);

            var confidence = ClassifyConfidence(obs.Count, winRate);
            var label = string.Join(" + ", comboConditions.Select(c => $"{c.Type}:{c.Value}"));

            var strategy = new DiscoveredStrategy
            {
                StrategyId = key,
                Pattern = new StrategyPattern
                {
                    PatternId = key,
                    Conditions = comboConditions,
                    Label = label,
                },
                SampleSize = obs.Count,
                WinRate = Math.Round(winRate, 4),
                AverageReturn = Math.Round(avgReturn, 2),
                MedianReturn = Math.Round(medianReturn, 2),
                Confidence = confidence,
                Summary = $"{label}: {winRate:P0} win rate over {obs.Count} trades, avg return {avgReturn:F1}%.",
            };

            strategies.Add(strategy);
            _discovered[key] = strategy;

            // Persist discovered strategy (non-blocking failure)
            if (_repository is not null)
            {
                try
                {
                    await _repository.StoreStrategyAsync(strategy);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[strategy-discovery] Failed to persist strategy {Id}", key);
                }
            }
        }

        strategies = strategies
            .OrderByDescending(s => s.WinRate)
            .ThenByDescending(s => s.AverageReturn)
            .ToList();

        return new StrategyDiscoveryResult
        {
            Strategies = strategies,
            CandidatesEvaluated = evaluated,
            StrategiesDiscovered = strategies.Count,
            Summary = $"Evaluated {evaluated} condition combinations. Discovered {strategies.Count} strategies above thresholds.",
        };
    }

    public async Task<List<DiscoveredStrategy>> GetDiscoveredStrategiesAsync()
    {
        await EnsureLoadedAsync();
        return _discovered.Values
            .OrderByDescending(s => s.WinRate)
            .ThenByDescending(s => s.SampleSize)
            .ToList();
    }

    // ══════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════

    private static string BuildPatternKey(List<LearningCondition> conditions)
    {
        return string.Join("|",
            conditions.OrderBy(c => c.Type).ThenBy(c => c.Value)
                .Select(c => $"{c.Type}:{c.Value}"));
    }

    private static List<LearningCondition> ParseKeyToConditions(string key)
    {
        return key.Split('|').Select(part =>
        {
            var split = part.Split(':', 2);
            return new LearningCondition
            {
                Type = Enum.Parse<LearningConditionType>(split[0]),
                Value = split[1],
            };
        }).ToList();
    }

    private static StrategyConfidence ClassifyConfidence(int sampleSize, double winRate)
    {
        // Higher sample + higher win rate deviation from 50% = more confident
        if (sampleSize >= 100 && winRate >= 0.70) return StrategyConfidence.VeryHigh;
        if (sampleSize >= 50 && winRate >= 0.65) return StrategyConfidence.High;
        if (sampleSize >= 30 && winRate >= 0.60) return StrategyConfidence.Medium;
        if (sampleSize >= 20) return StrategyConfidence.Low;
        return StrategyConfidence.Insufficient;
    }

    private static double Median(List<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    /// <summary>
    /// Generate all k-combinations from a list.
    /// </summary>
    private static IEnumerable<List<T>> Combinations<T>(List<T> source, int k)
    {
        if (k == 0) { yield return []; yield break; }
        for (var i = 0; i <= source.Count - k; i++)
        {
            foreach (var tail in Combinations(source.Skip(i + 1).ToList(), k - 1))
            {
                var combo = new List<T> { source[i] };
                combo.AddRange(tail);
                yield return combo;
            }
        }
    }
}
