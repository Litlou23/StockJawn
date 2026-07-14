using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

/// <summary>
/// Why we missed an opportunity — the root cause analysis.
/// Multiple reasons can apply to a single missed opportunity.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MissedOpportunityReason
{
    /// <summary>Ticker was never discovered by any provider.</summary>
    NeverDiscovered,

    /// <summary>Discovered but never entered the Research Universe.</summary>
    NotInResearchUniverse,

    /// <summary>In Research Universe but no prediction was generated.</summary>
    NoPredictionGenerated,

    /// <summary>Prediction generated but confidence was too low to act.</summary>
    LowConfidence,

    /// <summary>Prediction generated but risk was too high.</summary>
    HighRisk,

    /// <summary>No catalyst was detected to trigger a prediction.</summary>
    MissingCatalyst,

    /// <summary>No news coverage found for this ticker.</summary>
    MissingNews,

    /// <summary>Technical indicators didn't confirm the move.</summary>
    MissingTechnicalConfirmation,

    /// <summary>Volume signals were absent or insufficient.</summary>
    MissingVolume,

    /// <summary>Ticker wasn't on any watchlist or discovery source.</summary>
    MissingWatchlistEntry,

    /// <summary>Prediction was in the wrong direction.</summary>
    WrongDirection,

    /// <summary>Prediction was correct but timeframe was too late.</summary>
    TooLate,

    /// <summary>Asset was archived before the move happened.</summary>
    ArchivedTooEarly,

    /// <summary>Prediction was neutral/non-directional — no direction was expressed.</summary>
    NeutralPrediction,
}

/// <summary>
/// Which movement threshold tier this opportunity hit.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MovementTier
{
    /// <summary>Move exceeded the first threshold (e.g. 10%).</summary>
    Tier1,

    /// <summary>Move exceeded the second threshold (e.g. 20%).</summary>
    Tier2,

    /// <summary>Move exceeded the third threshold (e.g. 30%).</summary>
    Tier3,

    /// <summary>Move exceeded the fourth threshold (e.g. 50%).</summary>
    Tier4,
}

/// <summary>
/// Whether we caught (predicted correctly), partially caught, or completely missed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpportunityCaptureStatus
{
    /// <summary>We generated a correct directional prediction before the move.</summary>
    Captured,

    /// <summary>We had the ticker under investigation but didn't generate a prediction in time.</summary>
    PartiallyCaptured,

    /// <summary>We predicted the wrong direction.</summary>
    WrongDirection,

    /// <summary>We had no awareness of this ticker at all.</summary>
    CompletelyMissed,

    /// <summary>We had a neutral/non-directional prediction — direction was intentionally not expressed.</summary>
    NeutralPrediction,
}

/// <summary>
/// A record of a significant stock movement and how well our system anticipated it.
/// This is append-only — we never update these, only create.
/// </summary>
public record OpportunityLearningRecord
{
    /// <summary>Unique identifier (UUID).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Stock ticker symbol.</summary>
    public string Ticker { get; init; } = "";

    /// <summary>Date of the scan that detected this movement.</summary>
    public DateTimeOffset ScanDate { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The percent move that triggered this record (e.g. 12.5).</summary>
    public double PercentMove { get; init; }

    /// <summary>Direction of the move: "up" or "down".</summary>
    public string MoveDirection { get; init; } = "";

    /// <summary>Price at the start of the measurement period.</summary>
    public double StartPrice { get; init; }

    /// <summary>Price at the end of the measurement period.</summary>
    public double EndPrice { get; init; }

    /// <summary>Which threshold tier(s) this move exceeded.</summary>
    public MovementTier HighestTier { get; init; }

    /// <summary>The measurement period (e.g. "1_day", "1_week", "1_month").</summary>
    public string MeasurementPeriod { get; init; } = "";

    // ── Discovery awareness ────────────────────────────────────

    /// <summary>Was this ticker ever discovered by our discovery engine?</summary>
    public bool WasDiscovered { get; init; }

    /// <summary>When was it first discovered (null if never).</summary>
    public DateTimeOffset? DiscoveryDate { get; init; }

    /// <summary>How many days before the move was it discovered? Negative = after.</summary>
    public int? DaysBeforeMove { get; init; }

    /// <summary>Which provider discovered it first.</summary>
    public string? DiscoverySource { get; init; }

    // ── Research Universe awareness ────────────────────────────

    /// <summary>Was this ticker in the Research Universe at the time of the move?</summary>
    public bool WasInResearchUniverse { get; init; }

    /// <summary>What state was it in? (Discovered, Monitoring, BuildingThesis, etc.)</summary>
    public string? ResearchState { get; init; }

    /// <summary>Interest score at the time of the move.</summary>
    public int? InterestScoreAtMove { get; init; }

    /// <summary>Evidence count at the time of the move.</summary>
    public int? EvidenceCountAtMove { get; init; }

    // ── Prediction awareness ───────────────────────────────────

    /// <summary>Was a prediction generated for this ticker in the lookback window?</summary>
    public bool HadPrediction { get; init; }

    /// <summary>Was the prediction in the correct direction?</summary>
    public bool? PredictionCorrectDirection { get; init; }

    /// <summary>The prediction's confidence score (if one existed).</summary>
    public int? PredictionConfidence { get; init; }

    /// <summary>The prediction's risk score (if one existed).</summary>
    public int? PredictionRisk { get; init; }

    /// <summary>The prediction type (bullish/bearish/neutral).</summary>
    public string? PredictionType { get; init; }

    /// <summary>ID of the relevant prediction (if one existed).</summary>
    public string? PredictionId { get; init; }

    // ── Analysis ───────────────────────────────────────────────

    /// <summary>Overall capture status.</summary>
    public OpportunityCaptureStatus CaptureStatus { get; init; }

    /// <summary>Reasons we missed this opportunity (empty if captured).</summary>
    public List<string> MissReasons { get; init; } = [];

    /// <summary>Human-readable summary of what happened.</summary>
    public string Summary { get; init; } = "";

    /// <summary>When this record was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Configuration for the Opportunity Learning system.
/// All thresholds are configurable.
/// </summary>
public class OpportunityLearningConfig
{
    /// <summary>Movement thresholds in percent. Each is a tier boundary.</summary>
    public List<double> MovementThresholds { get; init; } = [10.0, 20.0, 30.0, 50.0];

    /// <summary>How many days back to look for predictions when evaluating a move.</summary>
    public int PredictionLookbackDays { get; init; } = 7;

    /// <summary>How many days back to look for discovery events.</summary>
    public int DiscoveryLookbackDays { get; init; } = 30;

    /// <summary>Measurement periods to check for significant moves.
    /// Each maps to a number of trading days to compare price change.</summary>
    public Dictionary<string, int> MeasurementPeriods { get; init; } = new()
    {
        ["1_day"] = 1,
        ["1_week"] = 5,
        ["1_month"] = 21,
    };

    /// <summary>Universe of tickers to scan for significant moves.
    /// If empty, uses a broad market scan approach.
    /// Can be set via OPPORTUNITY_SCAN_TICKERS env var (comma-separated).</summary>
    public List<string> ScanTickers { get; init; } = [];

    /// <summary>Maximum records to return from analytics queries.</summary>
    public int MaxAnalyticsResults { get; init; } = 500;
}

/// <summary>
/// Aggregated analytics from opportunity learning records.
/// </summary>
public record OpportunityAnalytics
{
    /// <summary>Total significant moves detected across all tiers.</summary>
    public int TotalOpportunities { get; init; }

    /// <summary>How many we captured (correct prediction before the move).</summary>
    public int Captured { get; init; }

    /// <summary>How many we partially captured (aware but no prediction).</summary>
    public int PartiallyCaptured { get; init; }

    /// <summary>How many we predicted wrong direction.</summary>
    public int WrongDirection { get; init; }

    /// <summary>How many we completely missed (zero awareness).</summary>
    public int CompletelyMissed { get; init; }

    /// <summary>How many had a neutral/non-directional prediction (direction not expressed).</summary>
    public int NeutralPrediction { get; init; }

    /// <summary>Capture rate: Captured / Total.</summary>
    public double CaptureRate { get; init; }

    /// <summary>Awareness rate: (Captured + PartiallyCaptured) / Total.</summary>
    public double AwarenessRate { get; init; }

    /// <summary>Breakdown by movement tier.</summary>
    public Dictionary<string, TierBreakdown> ByTier { get; init; } = new();

    /// <summary>Breakdown by measurement period.</summary>
    public Dictionary<string, TierBreakdown> ByPeriod { get; init; } = new();

    /// <summary>Most common miss reasons and their counts.</summary>
    public List<MissReasonCount> TopMissReasons { get; init; } = [];

    /// <summary>Average days-before-move for discovered opportunities.</summary>
    public double? AverageDiscoveryLeadDays { get; init; }

    /// <summary>Period this analytics covers.</summary>
    public DateTimeOffset? FromDate { get; init; }
    public DateTimeOffset? ToDate { get; init; }
}

/// <summary>
/// Breakdown stats for a single tier or period.
/// </summary>
public record TierBreakdown
{
    public int Total { get; init; }
    public int Captured { get; init; }
    public int Missed { get; init; }
    public double CaptureRate { get; init; }
}

/// <summary>
/// A miss reason with its occurrence count.
/// </summary>
public record MissReasonCount(string Reason, int Count);
