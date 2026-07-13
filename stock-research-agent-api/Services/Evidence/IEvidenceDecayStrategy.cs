using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Evidence;

/// <summary>
/// Defines how evidence weight decays over time.
///
/// Different evidence types decay at different rates:
///   - News decays quickly (hours to days)
///   - Technical signals decay moderately (days)
///   - SEC filings barely decay (weeks to months)
///   - Congressional trades decay slowly (weeks)
///   - Catalyst events decay based on proximity to the event
///
/// Implementations can use exponential decay, linear decay,
/// step functions, or any custom curve. The framework is
/// intentionally open — swap strategies without changing
/// the aggregator or service.
///
/// NOTE: Actual decay formulas are NOT implemented yet.
/// The default implementation passes through the original weight.
/// This is architecture-only — formulas will be added later.
/// </summary>
public interface IEvidenceDecayStrategy
{
    /// <summary>
    /// Apply time-based decay to an evidence record's weight.
    /// Returns the effective weight at the given point in time.
    /// </summary>
    /// <param name="record">The evidence record to decay.</param>
    /// <param name="asOf">The point in time to compute decay for (usually now).</param>
    /// <returns>Decayed weight. 0 if the evidence has fully decayed.</returns>
    double ApplyDecay(EvidenceRecord record, DateTimeOffset asOf);

    /// <summary>
    /// Get the decay configuration for a specific evidence type.
    /// Used for introspection and configuration display.
    /// </summary>
    EvidenceDecayConfig GetConfig(EvidenceType type);

    /// <summary>
    /// Check whether an evidence record should be considered expired
    /// based on decay (weight fallen below minimum threshold).
    /// Separate from the explicit Expiration field on the record.
    /// </summary>
    bool IsEffectivelyExpired(EvidenceRecord record, DateTimeOffset asOf);
}
