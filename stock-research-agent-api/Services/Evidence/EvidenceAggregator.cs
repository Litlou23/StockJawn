using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Evidence;

/// <summary>
/// Default evidence aggregator. Filters expired records, applies decay,
/// computes interest score, builds timeline, generates thesis.
/// </summary>
public class EvidenceAggregator : IEvidenceAggregator
{
    private readonly IEvidenceDecayStrategy _decay;

    public EvidenceAggregator(IEvidenceDecayStrategy decay)
    {
        _decay = decay;
    }

    public EvidenceSnapshot Aggregate(string ticker, List<EvidenceRecord> allRecords)
    {
        var now = DateTimeOffset.UtcNow;

        // Partition into active vs expired
        var active = allRecords
            .Where(r => r.Expiration is null || r.Expiration > now)
            .ToList();

        // Apply decay to get effective weights
        var decayed = active
            .Select(r => (Record: r, EffectiveWeight: _decay.ApplyDecay(r, now)))
            .Where(x => Math.Abs(x.EffectiveWeight) >= 0.01) // drop negligible evidence
            .ToList();

        // Count by type
        var countByType = decayed
            .GroupBy(x => x.Record.EvidenceType)
            .ToDictionary(g => g.Key, g => g.Count());

        // Weight by type
        var weightByType = decayed
            .GroupBy(x => x.Record.EvidenceType)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.EffectiveWeight));

        // Timeline — most recent first, capped at 50
        var timeline = decayed
            .OrderByDescending(x => x.Record.Timestamp)
            .Take(50)
            .Select(x => x.Record)
            .ToList();

        var interestScore = ComputeInterestScoreInternal(decayed);
        var thesis = GenerateThesis(ticker, active);

        return new EvidenceSnapshot
        {
            Ticker = ticker,
            InterestScore = interestScore,
            EvidenceCount = decayed.Count,
            TotalEvidenceCount = allRecords.Count,
            CountByType = countByType,
            WeightByType = weightByType,
            Timeline = timeline,
            CurrentThesis = thesis,
            LastEvidenceAt = timeline.Count > 0 ? timeline[0].Timestamp : null,
            ComputedAt = now,
        };
    }

    public int ComputeInterestScore(List<EvidenceRecord> activeRecords)
    {
        var now = DateTimeOffset.UtcNow;
        var decayed = activeRecords
            .Select(r => (Record: r, EffectiveWeight: _decay.ApplyDecay(r, now)))
            .Where(x => Math.Abs(x.EffectiveWeight) >= 0.01)
            .ToList();

        return ComputeInterestScoreInternal(decayed);
    }

    public string GenerateThesis(string ticker, List<EvidenceRecord> activeRecords)
    {
        if (activeRecords.Count == 0)
            return $"No active evidence for {ticker}.";

        // Group by type and find dominant evidence categories
        var byType = activeRecords
            .GroupBy(r => r.EvidenceType)
            .OrderByDescending(g => g.Sum(r => r.Importance))
            .ToList();

        var dominant = byType.First();
        var typeCount = byType.Count;
        var totalImportance = activeRecords.Sum(r => r.Importance);
        var avgWeight = activeRecords.Average(r => r.Weight);
        var direction = avgWeight > 0.1 ? "bullish" : avgWeight < -0.1 ? "bearish" : "neutral";

        var parts = new List<string>();

        // Lead with the dominant evidence type
        parts.Add($"{ticker}: {dominant.Count()} {dominant.Key} signal{(dominant.Count() != 1 ? "s" : "")}");

        // Add supporting types
        if (typeCount > 1)
        {
            var supporting = byType.Skip(1).Take(3)
                .Select(g => $"{g.Count()} {g.Key}")
                .ToList();
            parts.Add($"supported by {string.Join(", ", supporting)}");
        }

        // Add sentiment direction
        parts.Add($"({direction}, {activeRecords.Count} total evidence points)");

        return string.Join(" — ", parts);
    }

    // ── Internal ────────────────────────────────────────────────

    private static int ComputeInterestScoreInternal(
        List<(EvidenceRecord Record, double EffectiveWeight)> decayed)
    {
        if (decayed.Count == 0) return 0;

        // Interest score components:
        // 1. Sum of absolute effective weights (evidence volume)
        // 2. Max importance (peak signal strength)
        // 3. Type diversity bonus (convergence from multiple sources)

        var totalAbsWeight = decayed.Sum(x => Math.Abs(x.EffectiveWeight));
        var maxImportance = decayed.Max(x => x.Record.Importance);
        var typeCount = decayed.Select(x => x.Record.EvidenceType).Distinct().Count();

        // Weight volume: each 1.0 of absolute weight = ~15 points
        var volumeScore = totalAbsWeight * 15.0;

        // Peak importance: scale to 0–30 range
        var peakScore = maxImportance * 0.3;

        // Diversity bonus: each distinct type beyond 1 adds 5 points
        var diversityBonus = (typeCount - 1) * 5.0;

        var raw = volumeScore + peakScore + diversityBonus;
        return Math.Clamp((int)raw, 0, 100);
    }
}
