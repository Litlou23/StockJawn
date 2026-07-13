using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Evidence;

/// <summary>
/// Aggregates raw evidence records into a computed snapshot.
///
/// Responsible for:
///   - Filtering out expired evidence
///   - Applying decay weights (via <see cref="IEvidenceDecayStrategy"/>)
///   - Computing InterestScore from weighted evidence
///   - Building an evidence timeline
///   - Generating a thesis from the evidence pattern
///
/// Stateless — given the same evidence records, always produces
/// the same snapshot.
/// </summary>
public interface IEvidenceAggregator
{
    /// <summary>
    /// Compute an evidence snapshot from a list of evidence records.
    /// Handles expiration filtering and decay internally.
    /// </summary>
    EvidenceSnapshot Aggregate(string ticker, List<EvidenceRecord> allRecords);

    /// <summary>
    /// Compute interest score only (lightweight, no full snapshot).
    /// </summary>
    int ComputeInterestScore(List<EvidenceRecord> activeRecords);

    /// <summary>
    /// Generate a thesis string from evidence patterns.
    /// </summary>
    string GenerateThesis(string ticker, List<EvidenceRecord> activeRecords);
}
