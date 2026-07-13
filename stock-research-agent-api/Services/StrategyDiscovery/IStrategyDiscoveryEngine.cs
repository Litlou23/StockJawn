using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.StrategyDiscovery;

/// <summary>
/// Searches historical prediction outcomes for recurring combinations
/// of conditions that statistically outperform baseline.
///
/// Uses deterministic statistical analysis — no AI, no embeddings.
/// Algorithm is swappable: implement this interface with a different
/// search strategy without changing consumers.
/// </summary>
public interface IStrategyDiscoveryEngine
{
    /// <summary>
    /// Run discovery across all recorded observations.
    /// Returns strategies that pass significance and performance thresholds.
    /// </summary>
    Task<StrategyDiscoveryResult> DiscoverAsync(StrategyDiscoveryRequest request);

    /// <summary>Record an observation for future discovery runs.</summary>
    Task RecordObservationAsync(StrategyObservationInput input);

    /// <summary>Get all currently discovered strategies.</summary>
    Task<List<DiscoveredStrategy>> GetDiscoveredStrategiesAsync();
}

/// <summary>
/// Configuration for a discovery run.
/// </summary>
public record StrategyDiscoveryRequest
{
    /// <summary>Minimum observations required for a pattern to be considered.</summary>
    public int MinSampleSize { get; init; } = 20;
    /// <summary>Minimum win rate to qualify as a strategy.</summary>
    public double MinWinRate { get; init; } = 0.60;
    /// <summary>Minimum average return (percent) to qualify.</summary>
    public double MinAverageReturn { get; init; } = 3.0;
    /// <summary>Maximum number of conditions in a combination to search.</summary>
    public int MaxCombinationDepth { get; init; } = 4;
}

/// <summary>
/// Input for recording an observation into the discovery engine.
/// </summary>
public record StrategyObservationInput
{
    public string PredictionId { get; init; } = "";
    public string? Ticker { get; init; }
    public DateTimeOffset Date { get; init; }
    public bool IsWin { get; init; }
    public double ReturnPercent { get; init; }
    public int HoldingDays { get; init; }
    public List<LearningCondition> Conditions { get; init; } = [];
}
