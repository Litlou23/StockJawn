using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.OpportunityLearning;

/// <summary>
/// Learns from opportunities we missed.
///
/// Every significant stock movement (configurable thresholds: 10%, 20%, 30%, 50%)
/// is evaluated against our pipeline:
///   - Was it discovered?
///   - Did it enter the Research Universe?
///   - Was a prediction generated?
///   - If not — why?
///
/// Results are persisted for analytics. No weight updates — observation only.
/// </summary>
public interface IOpportunityLearningService
{
    /// <summary>
    /// Scan a set of tickers for significant moves and evaluate each against our pipeline.
    /// Returns all new opportunity learning records created.
    /// </summary>
    Task<OpportunityScanResult> ScanForMissedOpportunitiesAsync(List<string>? tickersToScan = null);

    /// <summary>
    /// Evaluate a single ticker's recent move against our pipeline.
    /// </summary>
    Task<List<OpportunityLearningRecord>> EvaluateTickerAsync(
        string ticker, double percentMove, string direction,
        double startPrice, double endPrice, string measurementPeriod);

    /// <summary>
    /// Generate analytics from persisted opportunity learning records.
    /// </summary>
    Task<OpportunityAnalytics> GetAnalyticsAsync(DateTimeOffset? from = null, DateTimeOffset? to = null);

    /// <summary>
    /// Get the current configuration.
    /// </summary>
    OpportunityLearningConfig GetConfig();
}

/// <summary>
/// Result of a full opportunity scan.
/// </summary>
public record OpportunityScanResult
{
    public int TickersScanned { get; init; }
    public int SignificantMoversFound { get; init; }
    public int RecordsCreated { get; init; }
    public int Captured { get; init; }
    public int PartiallyCaptured { get; init; }
    public int CompletelyMissed { get; init; }
    public int WrongDirection { get; init; }
    public int NeutralPrediction { get; init; }
    public int Skipped { get; init; }
    public List<string> Errors { get; init; } = [];
    public string Summary { get; init; } = "";
}
