using System.Collections.Concurrent;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.KnowledgeBase;

/// <summary>
/// Thread-safe in-memory knowledge base.
/// Upserts merge new evidence into existing entries.
/// Data lost on restart — future phase persists to Supabase.
/// </summary>
public class InMemoryKnowledgeBase : IKnowledgeBase
{
    private readonly ConcurrentDictionary<string, KnowledgeEntry> _entries = new();

    public void Record(KnowledgeEntry entry)
    {
        _entries.AddOrUpdate(
            entry.Key,
            entry,
            (_, existing) => MergeEntry(existing, entry));
    }

    public List<KnowledgeEntry> Query(KnowledgeBaseQuery query)
    {
        var results = _entries.Values.AsEnumerable();

        if (query.Category is not null)
            results = results.Where(e => e.Category == query.Category);

        if (query.MinConfidence > 0)
            results = results.Where(e => e.Confidence >= query.MinConfidence);

        if (query.Regime is not null)
            results = results.Where(e =>
                e.Conditions.Any(c =>
                    c.Type == LearningConditionType.MarketRegime &&
                    c.Value.Equals(query.Regime.ToString(), StringComparison.OrdinalIgnoreCase)));

        if (query.SignalName is not null)
            results = results.Where(e =>
                e.Key.Contains(query.SignalName, StringComparison.OrdinalIgnoreCase));

        if (query.Sector is not null)
            results = results.Where(e =>
                e.Conditions.Any(c =>
                    c.Type == LearningConditionType.Sector &&
                    c.Value.Equals(query.Sector, StringComparison.OrdinalIgnoreCase)));

        return results
            .OrderByDescending(e => e.Confidence)
            .Take(query.Limit)
            .ToList();
    }

    public List<KnowledgeEntry> GetAll(KnowledgeCategory? category = null)
    {
        var results = _entries.Values.AsEnumerable();
        if (category is not null)
            results = results.Where(e => e.Category == category);
        return results.OrderByDescending(e => e.Confidence).ToList();
    }

    public List<KnowledgeEntry> GetStrongest(int limit = 20)
    {
        return _entries.Values
            .OrderByDescending(e => e.Confidence)
            .ThenByDescending(e => e.SampleSize)
            .Take(limit)
            .ToList();
    }

    public KnowledgeBaseStats GetStats()
    {
        var all = _entries.Values.ToList();
        return new KnowledgeBaseStats
        {
            TotalEntries = all.Count,
            HighConfidenceEntries = all.Count(e => e.Confidence >= 0.7),
            EntriesByCategory = all.GroupBy(e => e.Category)
                .ToDictionary(g => g.Key, g => g.Count()),
            Summary = $"{all.Count} knowledge entries, {all.Count(e => e.Confidence >= 0.7)} high-confidence.",
        };
    }

    private static KnowledgeEntry MergeEntry(KnowledgeEntry existing, KnowledgeEntry incoming)
    {
        // Weighted average of stats based on sample sizes
        var totalN = existing.SampleSize + incoming.SampleSize;
        if (totalN == 0) totalN = 1;

        return existing with
        {
            SampleSize = totalN,
            WinRate = Math.Round(
                ((existing.WinRate * existing.SampleSize) + (incoming.WinRate * incoming.SampleSize)) / totalN, 4),
            AverageReturn = Math.Round(
                ((existing.AverageReturn * existing.SampleSize) + (incoming.AverageReturn * incoming.SampleSize)) / totalN, 4),
            Confidence = StatisticalConfidence.FromSampleSize(totalN),
            LastUpdated = DateTimeOffset.UtcNow,
            ConfirmationCount = existing.ConfirmationCount + 1,
        };
    }
}
