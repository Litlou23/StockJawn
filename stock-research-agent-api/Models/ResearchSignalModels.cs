namespace StockResearchAgent.Api.Models;

/// <summary>
/// A normalized piece of research evidence attached to a ticker.
/// Any provider can emit these. The scoring and learning engines
/// consume them without knowing which provider created them.
/// </summary>
public record ResearchSignal
{
    public string Id { get; init; } = "";
    public string Ticker { get; init; } = "";

    // Determines scoring bucket weight key and learning key
    public string SignalType { get; init; } = "";

    // Coarse grouping for scoring caps: institutional, flow, sentiment, catalyst
    public string SignalCategory { get; init; } = "";

    // Which provider emitted this signal
    public string Provider { get; init; } = "";

    // Directional: positive = bullish, negative = bearish
    public double Strength { get; init; }

    // Reliability of this individual instance
    public double Confidence { get; init; }

    // When the underlying event occurred
    public DateTimeOffset EventTimestamp { get; init; }

    // When we detected it
    public DateTimeOffset DetectedAt { get; init; }

    // When this signal stops influencing scores
    public DateTimeOffset? ExpiresAt { get; init; }

    public bool Active { get; init; } = true;

    // Human-readable summary for UI and prediction context
    public string Summary { get; init; } = "";

    // Provider-specific details stored as JSONB
    public object? Metadata { get; init; }
}

/// <summary>
/// Declares a signal type a provider can emit. Used to auto-seed
/// scoring weights when a new provider is registered.
/// </summary>
public record SignalTypeDefinition(
    string SignalType,
    string SignalCategory,
    double DefaultWeight,
    string Description);

/// <summary>
/// Contract for any system that produces research signals.
/// </summary>
public interface IResearchSignalProvider
{
    string ProviderId { get; }
    bool IsConfigured { get; }
    Task<List<ResearchSignal>> CollectSignalsAsync();
    IReadOnlyList<SignalTypeDefinition> SignalTypes { get; }
}

public record SignalCollectionResult(int Persisted, int Expired, List<string> Errors);
