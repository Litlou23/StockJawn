using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Evidence;

/// <summary>
/// Placeholder decay strategy — no decay applied.
/// Returns the original weight unchanged.
///
/// This exists so the architecture compiles and runs end-to-end.
/// Replace with a real decay implementation (exponential, half-life, etc.)
/// when ready to tune evidence aging.
///
/// Default TTL configs are set here for future use — they define
/// how long each evidence type should live before being considered
/// stale, even though decay math isn't applied yet.
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

    /// <summary>
    /// No decay — returns original weight.
    /// Future: apply exponential decay based on HalfLifeDays.
    /// </summary>
    public double ApplyDecay(EvidenceRecord record, DateTimeOffset asOf)
    {
        // Passthrough — no decay applied yet
        return record.Weight;
    }

    public EvidenceDecayConfig GetConfig(EvidenceType type)
    {
        return _configs.TryGetValue(type, out var config)
            ? config
            : new EvidenceDecayConfig { EvidenceType = type, DefaultTtlDays = 7, HalfLifeDays = 3 };
    }

    public bool IsEffectivelyExpired(EvidenceRecord record, DateTimeOffset asOf)
    {
        // Only check explicit expiration for now
        // Future: also check if decayed weight < MinWeight
        return record.Expiration.HasValue && record.Expiration.Value <= asOf;
    }
}
