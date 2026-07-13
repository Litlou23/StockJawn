using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

/// <summary>
/// Category of catalyst or event that triggered a discovery.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiscoveryCategory
{
    /// <summary>Market-moving news coverage.</summary>
    News,
    /// <summary>Upcoming or recent earnings report.</summary>
    Earnings,
    /// <summary>Congressional or institutional trading activity.</summary>
    InstitutionalActivity,
    /// <summary>Significant price or volume movement.</summary>
    PriceAction,
    /// <summary>SEC filing, press release, or corporate event.</summary>
    Filing,
    /// <summary>Analyst upgrade/downgrade or price target change.</summary>
    AnalystAction,
    /// <summary>Unusual options flow or positioning.</summary>
    OptionsFlow,
    /// <summary>FDA approval, trial data, or regulatory event.</summary>
    RegulatoryEvent,
    /// <summary>Insider buying or selling.</summary>
    InsiderActivity,
    /// <summary>Sector or industry momentum.</summary>
    SectorMomentum,
    /// <summary>Multiple catalysts accumulating over time.</summary>
    CatalystAccumulation,
    /// <summary>General or uncategorized discovery.</summary>
    General,
}

/// <summary>
/// A single discovery event emitted by a <see cref="Services.Discovery.IDiscoveryProvider"/>.
///
/// Discovery events describe WHY a ticker deserves attention.
/// They do NOT predict direction — they identify opportunity for investigation.
/// Multiple events for the same ticker accumulate evidence on the
/// corresponding <see cref="ResearchAsset"/>.
/// </summary>
public record DiscoveryEvent
{
    /// <summary>Unique event identifier (UUID, assigned on persist).</summary>
    public string Id { get; init; } = "";

    /// <summary>Stock ticker symbol.</summary>
    public string Ticker { get; init; } = "";

    /// <summary>When the event occurred or was detected.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Which provider surfaced this event.
    /// Examples: "finnhub-news", "finnhub-earnings", "congress-signals",
    /// "twelvedata-movers", "market-intelligence".</summary>
    public string Source { get; init; } = "";

    /// <summary>Human-readable explanation of why this ticker was surfaced.
    /// Example: "3 analyst upgrades in 48 hours" or "Earnings in 2 days, implied move 8%".</summary>
    public string Reason { get; init; } = "";

    /// <summary>How important this event is, 1–100. Higher = more urgent.
    /// Used to compute the ResearchAsset's InterestScore.</summary>
    public int Importance { get; init; }

    /// <summary>What type of catalyst this event represents.</summary>
    public DiscoveryCategory Category { get; init; } = DiscoveryCategory.General;

    /// <summary>Provider's confidence that this event is meaningful, 0.0–1.0.
    /// Low confidence events still create research assets but rank lower.</summary>
    public double Confidence { get; init; } = 0.5;
}

/// <summary>
/// Result of a single discovery provider scan.
/// </summary>
public record DiscoveryProviderResult
{
    public string ProviderId { get; init; } = "";
    public List<DiscoveryEvent> Events { get; init; } = [];
    public bool Success { get; init; } = true;
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Aggregate result of running all discovery providers.
/// </summary>
public record DiscoveryRunResult
{
    public int TotalEventsDiscovered { get; init; }
    public int NewAssetsCreated { get; init; }
    public int ExistingAssetsUpdated { get; init; }
    public int ProvidersSucceeded { get; init; }
    public int ProvidersFailed { get; init; }
    public List<DiscoveryProviderResult> ProviderResults { get; init; } = [];
    public TimeSpan TotalDuration { get; init; }
    public string Summary { get; init; } = "";
}
