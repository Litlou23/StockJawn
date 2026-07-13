using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.AdaptiveLearning;

/// <summary>
/// Learns conditional signal performance: how each signal performs
/// under different market regimes, sectors, volatility conditions, etc.
///
/// Complements (does not replace) the existing <see cref="ResearchEngine.LearningEngine"/>
/// which tracks unconditional signal accuracy.
/// </summary>
public interface IAdaptiveLearningEngine
{
    /// <summary>Record a completed prediction outcome with full context.</summary>
    Task RecordOutcomeAsync(AdaptiveLearningObservation observation);

    /// <summary>Query conditional performance for a signal under specific conditions.</summary>
    Task<ConditionalPerformanceResult> QueryAsync(ConditionalPerformanceQuery query);

    /// <summary>Get all conditional stats for a given signal name.</summary>
    Task<List<ConditionalSignalPerformance>> GetSignalProfileAsync(string signalName);

    /// <summary>Get the best-performing conditions for a given signal.</summary>
    Task<ConditionalSignalPerformance?> GetBestConditionAsync(string signalName, int minSampleSize = 10);
}
