using System.Collections.Concurrent;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.AdaptiveLearning;

/// <summary>
/// Thread-safe in-memory store for conditional performance stats.
/// Data is lost on restart — future phase will persist to Supabase.
/// </summary>
public class InMemoryAdaptiveLearningRepository : IAdaptiveLearningRepository
{
    private readonly ConcurrentDictionary<string, ConditionalSignalPerformance> _store = new();

    public Task UpsertPerformanceAsync(ConditionalSignalPerformance performance)
    {
        var key = BuildKey(performance.SignalName, performance.Conditions);
        _store[key] = performance;
        return Task.CompletedTask;
    }

    public Task<List<ConditionalSignalPerformance>> QueryAsync(ConditionalPerformanceQuery query)
    {
        var results = _store.Values.AsEnumerable();

        if (query.SignalName is not null)
            results = results.Where(p =>
                p.SignalName.Equals(query.SignalName, StringComparison.OrdinalIgnoreCase));

        if (query.Conditions.Count > 0)
        {
            results = results.Where(p =>
                query.Conditions.All(qc =>
                    p.Conditions.Any(pc =>
                        pc.Type == qc.Type &&
                        pc.Value.Equals(qc.Value, StringComparison.OrdinalIgnoreCase))));
        }

        results = results.Where(p => p.SampleSize >= query.MinSampleSize);

        return Task.FromResult(results.ToList());
    }

    public Task<List<ConditionalSignalPerformance>> GetBySignalAsync(string signalName)
    {
        var results = _store.Values
            .Where(p => p.SignalName.Equals(signalName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult(results);
    }

    private static string BuildKey(string signal, List<LearningCondition> conditions)
    {
        var condPart = string.Join("|",
            conditions.OrderBy(c => c.Type).ThenBy(c => c.Value)
                .Select(c => $"{c.Type}:{c.Value}"));
        return $"{signal.ToLowerInvariant()}::{condPart}";
    }
}
