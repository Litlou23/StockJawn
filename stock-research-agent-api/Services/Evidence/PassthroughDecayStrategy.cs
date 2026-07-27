using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Evidence;

/// <summary>
/// Exponential decay strategy — evidence loses weight over time based on
/// configurable half-life per evidence type.
///
/// Formula: decayedWeight = originalWeight × 2^(-ageDays / halfLifeDays)
///
/// Examples with default configs:
///   - News (half-life 1d): 50% weight after 1 day, 25% after 2 days
///   - SEC filings (half-life 45d): 50% weight after 45 days
///   - Congress trades (half-life 14d): 50% weight after 2 weeks
///
/// Evidence is considered "effectively expired" when its decayed weight
/// drops below 5% of the original — at that point it no longer
/// meaningfully contributes to the interest score.
/// </summary>
public class PassthroughDecayStrategy : IEvidenceDecayStrategy
{
    private static readonly Dictionary<EvidenceType, EvidenceDecayConfig> _configs = new()
    {
        [EvidenceType.News] = new()
        {
            EvidenceType = EvidenceType.News,
            DefaultTtlDays = 3,
            HalfLifeDays = 1,
        },
        [EvidenceType.Technical] = new()
        {
            EvidenceType = EvidenceType.Technical,
            DefaultTtlDays = 5,
            HalfLifeDays = 2,
        },
        [EvidenceType.Congress] = new()
        {
            EvidenceType = EvidenceType.Congress,
            DefaultTtlDays = 30,
            HalfLifeDays = 14,
        },
        [EvidenceType.SEC] = new()
        {
            EvidenceType = EvidenceType.SEC,
            DefaultTtlDays = 90,
            HalfLifeDays = 45,
        },
        [EvidenceType.Learning] = new()
        {
            EvidenceType = EvidenceType.Learning,
            DefaultTtlDays = 60,
            HalfLifeDays = 30,
        },
        [EvidenceType.MarketRegime] = new()
        {
            EvidenceType = EvidenceType.MarketRegime,
            DefaultTtlDays = 7,
            HalfLifeDays = 3,
        },
        [EvidenceType.Options] = new()
        {
            EvidenceType = EvidenceType.Options,
            DefaultTtlDays = 5,
            HalfLifeDays = 2,
        },
        [EvidenceType.Volume] = new()
        {
            EvidenceType = EvidenceType.Volume,
            DefaultTtlDays = 3,
            HalfLifeDays = 1,
        },
        [EvidenceType.Momentum] = new()
        {
            EvidenceType = EvidenceType.Momentum,
            DefaultTtlDays = 7,
            HalfLifeDays = 3,
        },
        [EvidenceType.Research] = new()
        {
            EvidenceType = EvidenceType.Research,
            DefaultTtlDays = 30,
            HalfLifeDays = 14,
        },
        [EvidenceType.Catalyst] = new()
        {
            EvidenceType = EvidenceType.Catalyst,
            DefaultTtlDays = 14,
            HalfLifeDays = 7,
        },
    };

    private const double MinWeightThreshold = 0.05;

    /// <summary>
    /// Applies exponential decay: weight × 2^(-ageDays / halfLifeDays).
    /// Returns 0.0 if the record is effectively expired.
    /// </summary>
    public double ApplyDecay(EvidenceRecord record, DateTimeOffset asOf)
    {
        if (IsEffectivelyExpired(record, asOf))
            return 0.0;

        var config = GetConfig(record.EvidenceType);
        var ageDays = (asOf - record.Timestamp).TotalDays;

        if (ageDays <= 0)
            return record.Weight;

        var decayFactor = Math.Pow(0.5, ageDays / (config.HalfLifeDays ?? 3));
        var decayed = record.Weight * decayFactor;

        // Clamp to zero if below minimum threshold (relative to original)
        return Math.Abs(decayed) < MinWeightThreshold * Math.Abs(record.Weight)
            ? 0.0
            : decayed;
    }

    public EvidenceDecayConfig GetConfig(EvidenceType type)
    {
        return _configs.TryGetValue(type, out var config)
            ? config
            : new EvidenceDecayConfig { EvidenceType = type, DefaultTtlDays = 7, HalfLifeDays = 3 };
    }

    public bool IsEffectivelyExpired(EvidenceRecord record, DateTimeOffset asOf)
    {
        // Explicit expiration takes priority
        if (record.Expiration.HasValue && record.Expiration.Value <= asOf)
            return true;

        // Check if age exceeds TTL
        var config = GetConfig(record.EvidenceType);
        var ageDays = (asOf - record.Timestamp).TotalDays;
        if (ageDays >= config.DefaultTtlDays)
            return true;

        // Check if decayed weight is below minimum threshold
        if (ageDays > 0)
        {
            var decayFactor = Math.Pow(0.5, ageDays / (config.HalfLifeDays ?? 3));
            if (Math.Abs(decayFactor) < MinWeightThreshold)
                return true;
        }

        return false;
    }
}
