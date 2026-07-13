using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

/// <summary>
/// Configuration for continuous (incremental) discovery runs.
/// Discovery becomes a lightweight background process that keeps
/// the Research Universe current throughout the trading day.
/// </summary>
public class ContinuousDiscoveryConfig
{
    /// <summary>Minutes between discovery cycles. Default: 60.
    /// Supported: 30, 60, 120.</summary>
    public int DiscoveryIntervalMinutes { get; init; } = 60;

    /// <summary>Discovery schedule mode.</summary>
    public DiscoveryScheduleMode ScheduleMode { get; init; } = DiscoveryScheduleMode.TradingHoursOnly;

    /// <summary>US market open hour (ET). Default: 9.</summary>
    public int MarketOpenHourET { get; init; } = 9;

    /// <summary>US market close hour (ET). Default: 16.</summary>
    public int MarketCloseHourET { get; init; } = 16;

    /// <summary>Maximum events to process per cycle to stay lightweight.</summary>
    public int MaxEventsPerCycle { get; init; } = 500;

    /// <summary>Whether to build HistoricalResearchProfile on first discovery.</summary>
    public bool BuildHistoricalProfileOnDiscovery { get; init; } = true;

    /// <summary>Days between automatic profile refreshes. Default: 90.
    /// Set to 0 to disable scheduled refresh (corporate-event triggers still fire).</summary>
    public int ProfileRefreshIntervalDays { get; init; } = 90;

    /// <summary>Discovery categories that trigger an immediate profile refresh
    /// regardless of the scheduled interval. These represent significant
    /// corporate events that materially change a stock's profile.</summary>
    public DiscoveryCategory[] ProfileRefreshTriggerCategories { get; init; } =
    [
        DiscoveryCategory.Earnings,
        DiscoveryCategory.Filing,
        DiscoveryCategory.RegulatoryEvent,
        DiscoveryCategory.InsiderActivity,
    ];
}

/// <summary>
/// When continuous discovery should run.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiscoveryScheduleMode
{
    /// <summary>Only during US market hours (9:30 AM – 4:00 PM ET).</summary>
    TradingHoursOnly,
    /// <summary>24/7 continuous scanning.</summary>
    Always,
}

/// <summary>
/// Result of a single continuous discovery cycle.
/// Lighter than <see cref="DiscoveryRunResult"/> — tracks incremental delta only.
/// </summary>
public record ContinuousDiscoveryResult
{
    public DateTimeOffset CycleStart { get; init; }
    public DateTimeOffset CycleEnd { get; init; }
    public DateTimeOffset? CheckpointUsed { get; init; }
    public int NewEventsFound { get; init; }
    public int NewAssetsCreated { get; init; }
    public int ExistingAssetsUpdated { get; init; }
    public int EvidenceRecordsCreated { get; init; }
    public int TimelineEventsCreated { get; init; }
    public int HistoricalProfilesBuilt { get; init; }
    public int HistoricalProfilesRefreshed { get; init; }
    public int ProvidersScanned { get; init; }
    public int ProvidersSkipped { get; init; }
    public int ProvidersFailed { get; init; }
    public TimeSpan Duration { get; init; }
    public string Summary { get; init; } = "";
    public bool WasSkipped { get; init; }
    public string? SkipReason { get; init; }
}

/// <summary>
/// Immutable timeline event — "Git history" for a stock's research journey.
///
/// Every time new evidence is added to a Research Asset, one of these
/// is appended. The Learning Engine can reconstruct how the thesis
/// evolved, not just whether the final prediction was right.
///
/// These are NEVER modified or deleted.
/// </summary>
public record ResearchTimelineEvent
{
    /// <summary>Unique identifier (UUID).</summary>
    public string Id { get; init; } = "";

    /// <summary>Stock ticker symbol.</summary>
    public string Ticker { get; init; } = "";

    /// <summary>When this event occurred.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Category of this timeline entry.</summary>
    public TimelineEventType EventType { get; init; } = TimelineEventType.EvidenceAdded;

    /// <summary>Human-readable description of what happened.
    /// Example: "3 analyst upgrades in 48 hours",
    /// "Morning Scan generated bullish prediction",
    /// "Prediction succeeded: +4.2% in 3 days".</summary>
    public string Description { get; init; } = "";

    /// <summary>Which system produced this event.
    /// Examples: "finnhub", "congress-signals", "morning-scan", "learning-engine".</summary>
    public string Source { get; init; } = "";

    /// <summary>Optional link to the related evidence record, discovery event, or prediction.</summary>
    public string? RelatedEntityId { get; init; }

    /// <summary>Optional link to the related entity type for cross-referencing.
    /// Examples: "evidence", "discovery_event", "prediction", "outcome".</summary>
    public string? RelatedEntityType { get; init; }

    /// <summary>Interest score at the time of this event.</summary>
    public int? InterestScoreSnapshot { get; init; }

    /// <summary>Research state at the time of this event.</summary>
    public string? ResearchStateSnapshot { get; init; }

    /// <summary>Current thesis at the time of this event.</summary>
    public string? ThesisSnapshot { get; init; }

    /// <summary>When this record was persisted.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Types of timeline events.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelineEventType
{
    /// <summary>New evidence was added (news, filing, signal, etc.).</summary>
    EvidenceAdded,
    /// <summary>Asset was first discovered and entered the Research Universe.</summary>
    Discovered,
    /// <summary>Asset was promoted to a new lifecycle state.</summary>
    StatePromotion,
    /// <summary>The thesis was updated or refined.</summary>
    ThesisUpdated,
    /// <summary>A prediction was generated for this ticker.</summary>
    PredictionGenerated,
    /// <summary>A prediction outcome was evaluated.</summary>
    PredictionOutcome,
    /// <summary>Interest score changed significantly (±10 or more).</summary>
    ScoreChange,
    /// <summary>Asset was archived.</summary>
    Archived,
    /// <summary>Volume spike or unusual activity detected.</summary>
    VolumeSpike,
    /// <summary>Catalyst event (earnings, FDA, product launch).</summary>
    CatalystEvent,
}

/// <summary>
/// Historical profile built when a stock first enters the Research Universe.
/// Provides persistent context for future scoring. Refreshable on a
/// configurable schedule (default 90 days) or after significant corporate events.
/// </summary>
public record HistoricalResearchProfile
{
    /// <summary>Unique identifier (UUID).</summary>
    public string Id { get; init; } = "";

    /// <summary>Stock ticker symbol.</summary>
    public string Ticker { get; init; } = "";

    /// <summary>Link to the ResearchAsset this profile belongs to.</summary>
    public string ResearchAssetId { get; init; } = "";

    /// <summary>When this profile was built.</summary>
    public DateTimeOffset BuiltAt { get; init; } = DateTimeOffset.UtcNow;

    // ── Volatility & Price History ──────────────────────────────

    /// <summary>Historical volatility (annualized, from daily returns). Null if insufficient data.</summary>
    public double? HistoricalVolatility { get; init; }

    /// <summary>Average True Range (14-day) as a percentage of price.</summary>
    public double? AtrPercent { get; init; }

    /// <summary>52-week high.</summary>
    public decimal? High52Week { get; init; }

    /// <summary>52-week low.</summary>
    public decimal? Low52Week { get; init; }

    /// <summary>Current price relative to 52-week range (0.0 = at low, 1.0 = at high).</summary>
    public double? PricePositionIn52WeekRange { get; init; }

    // ── Catalyst Reaction History ───────────────────────────────

    /// <summary>Average absolute move (%) on earnings days, last 4 quarters.</summary>
    public double? AvgEarningsMovePercent { get; init; }

    /// <summary>Average move (%) after analyst upgrades (next 5 days).</summary>
    public double? AvgAnalystUpgradeMovePercent { get; init; }

    /// <summary>Average move (%) after SEC filings (next 5 days).</summary>
    public double? AvgSecFilingMovePercent { get; init; }

    // ── Volume Profile ─────────────────────────────────────────

    /// <summary>Average daily volume over the last 30 days.</summary>
    public long? AvgDailyVolume30D { get; init; }

    /// <summary>Average daily volume over the last 90 days.</summary>
    public long? AvgDailyVolume90D { get; init; }

    // ── Sector & Relative Strength ─────────────────────────────

    /// <summary>Sector classification (e.g. "Technology", "Healthcare").</summary>
    public string? Sector { get; init; }

    /// <summary>Industry classification.</summary>
    public string? Industry { get; init; }

    /// <summary>Relative strength vs. S&P 500 over 30 days.</summary>
    public double? RelativeStrength30D { get; init; }

    // ── Learning History ───────────────────────────────────────

    /// <summary>Number of previous predictions STOCKJAWN has made for this ticker.</summary>
    public int PreviousPredictionCount { get; init; }

    /// <summary>Accuracy of previous predictions (0.0–1.0). Null if no history.</summary>
    public double? PreviousPredictionAccuracy { get; init; }

    /// <summary>Average confidence score of previous predictions.</summary>
    public double? AvgPreviousConfidence { get; init; }

    // ── Pattern Summary ────────────────────────────────────────

    /// <summary>Free-text summary of historical patterns.
    /// Example: "High earnings volatility stock. Tends to gap up on upgrades.
    /// Previous predictions 3/5 correct, overconfident on bearish calls."</summary>
    public string? PatternSummary { get; init; }

    /// <summary>When this profile was last refreshed (usually same as BuiltAt).</summary>
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>How many times this profile has been refreshed (0 = original build only).</summary>
    public int RefreshCount { get; init; }

    /// <summary>What triggered the last refresh, if any.
    /// Examples: "scheduled_90d", "corporate_event:Earnings", null (original build).</summary>
    public string? LastRefreshReason { get; init; }
}
