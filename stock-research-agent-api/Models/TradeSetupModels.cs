using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

// ---------------------------------------------------------------------------
// Trade Setup — the core concept of STOCKJAWN's setup detection philosophy.
//
// A trade setup is a specific combination of signal conditions that has
// historically produced measurable outcomes. Instead of asking "will this
// stock go up tomorrow?", the system asks "is this a historically favorable
// setup that justifies taking a position?"
//
// Each setup has:
//   - A fingerprint (which signal categories are active and in what direction)
//   - Entry/target/stop/invalidation parameters
//   - An expected holding period
//   - Historical performance metrics for setups matching this fingerprint
// ---------------------------------------------------------------------------

/// <summary>
/// A setup fingerprint captures which signal buckets are active and aligned
/// for a given prediction. Two predictions with the same fingerprint share
/// the same "type of setup" and can be compared historically.
///
/// Format: sorted, pipe-delimited active signals with direction qualifiers.
/// Example: "bullish_catalyst|bullish_momentum|bullish_trend|bull_market"
/// </summary>
public record SetupFingerprint
{
    /// <summary>
    /// Canonical string form: sorted, pipe-delimited active components.
    /// This is the primary key for setup-level learning.
    /// </summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>
    /// Individual signal components that make up this fingerprint.
    /// </summary>
    public List<string> Components { get; init; } = [];

    /// <summary>
    /// How many independent signal categories are aligned.
    /// Higher confirmation = higher historical reliability (in theory).
    /// </summary>
    public int ConfirmationCount { get; init; }

    /// <summary>
    /// Human-readable description: "Bullish trend + positive news + strong volume"
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// The dominant direction of the setup (bullish/bearish/mixed).
    /// </summary>
    public string Direction { get; init; } = "neutral";
}

/// <summary>
/// A complete trade setup attached to a prediction/candidate.
/// This replaces the concept of "prediction + paper stock candidate"
/// as the atomic unit the system learns from.
/// </summary>
public record TradeSetup
{
    public string Id { get; init; } = "";
    public string PredictionId { get; init; } = "";
    public string? PaperStockCandidateId { get; init; }
    public string RunId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // --- Setup Identity ---

    /// <summary>
    /// The fingerprint that identifies what kind of setup this is.
    /// </summary>
    public SetupFingerprint Fingerprint { get; init; } = new();

    /// <summary>
    /// The specific signal strengths at entry time, keyed by bucket name.
    /// Preserved for learning — the fingerprint is the category, these are the details.
    /// </summary>
    public Dictionary<string, BucketEvidence> SignalEvidence { get; init; } = new();

    // --- Trade Parameters ---

    public string Direction { get; init; } = "neutral"; // bullish, bearish, neutral
    public double? EntryPrice { get; init; }
    public double? TargetPrice { get; init; }
    public double? StopPrice { get; init; }
    public double? InvalidationPrice { get; init; }
    public double? RiskRewardRatio { get; init; }

    /// <summary>
    /// Expected holding period based on setup characteristics.
    /// Setups with strong catalysts may resolve faster; range-bound setups take longer.
    /// </summary>
    public SetupHoldingPeriod ExpectedHoldingPeriod { get; init; } = SetupHoldingPeriod.one_to_three_days;

    /// <summary>
    /// Maximum number of trading days before the setup is considered expired
    /// regardless of whether target or stop was hit.
    /// </summary>
    public int MaxHoldingDays { get; init; } = 5;

    // --- Historical Context at Entry ---

    /// <summary>
    /// How this fingerprint has performed historically. Null if no history exists.
    /// </summary>
    public SetupPerformance? HistoricalPerformance { get; init; }

    /// <summary>
    /// The market regime at entry time (bull_trend, bear_trend, sideways, high_volatility).
    /// </summary>
    public string? MarketRegime { get; init; }

    // --- Scoring ---

    /// <summary>
    /// Setup quality score (0-100) based on signal confirmation,
    /// historical performance, and risk/reward.
    /// </summary>
    public double SetupScore { get; init; }

    /// <summary>
    /// Whether this setup clears the bar for historical favorability.
    /// A setup is "favorable" when it has positive expected value
    /// with sufficient sample size.
    /// </summary>
    public bool IsHistoricallyFavorable { get; init; }

    // --- Lifecycle Tracking ---

    public SetupStatus Status { get; init; } = SetupStatus.active;

    /// <summary>
    /// How the setup resolved. Null while active.
    /// </summary>
    public SetupOutcome? Outcome { get; init; }
}

/// <summary>
/// Evidence from one signal bucket contributing to a setup.
/// </summary>
public record BucketEvidence
{
    public string BucketName { get; init; } = "";
    public double BullishScore { get; init; }
    public double BearishScore { get; init; }
    public double NetScore { get; init; }
    public string DominantDirection { get; init; } = "neutral"; // bullish, bearish, neutral
    public bool IsActive { get; init; } // Whether this bucket contributed meaningfully
    public List<string> Signals { get; init; } = []; // The specific signal descriptions
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupHoldingPeriod
{
    intraday,
    one_to_three_days,
    one_week,
    two_weeks,
    one_month,
    multi_month,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupStatus
{
    active,           // Setup is live, being tracked
    target_hit,       // Target was reached before invalidation
    stop_hit,         // Stop was hit
    invalidated,      // Invalidation condition was met
    expired,          // Max holding period elapsed without resolution
    evaluated,        // Legacy: one-time evaluation completed
}

/// <summary>
/// How a setup resolved — the answer to "did the thesis succeed?"
/// </summary>
public record SetupOutcome
{
    /// <summary>
    /// Did the target get hit before the stop/invalidation/expiry?
    /// This is the primary success metric, replacing "was direction correct after 1 day."
    /// </summary>
    public bool SetupSucceeded { get; init; }

    public SetupStatus Resolution { get; init; }
    public DateTimeOffset ResolvedAt { get; init; }
    public int DaysHeld { get; init; }

    // Price journey
    public double? ExitPrice { get; init; }
    public double? MaxFavorablePrice { get; init; }
    public double? MaxAdversePrice { get; init; }
    public double? MaxFavorablePercent { get; init; }
    public double? MaxAdversePercent { get; init; }
    public double? ReturnPercent { get; init; }

    // Was the thesis validated even if the trade didn't hit target?
    public bool? DirectionCorrect { get; init; }
    public bool? TargetHit { get; init; }
    public bool? StopHit { get; init; }
    public bool? InvalidationHit { get; init; }

    public string OutcomeSummary { get; init; } = "";
    public string? Lesson { get; init; }
}

// ---------------------------------------------------------------------------
// Setup Performance — historical stats for a given setup fingerprint.
// This is what the learning engine builds and the scoring engine consults.
// ---------------------------------------------------------------------------

/// <summary>
/// Aggregated performance metrics for setups matching a given fingerprint.
/// This is the core data structure the system uses to decide:
/// "Is this a historically favorable setup?"
/// </summary>
public record SetupPerformance
{
    public string Id { get; init; } = "";

    /// <summary>
    /// The fingerprint this performance record describes.
    /// </summary>
    public string SetupFingerprint { get; init; } = "";

    /// <summary>
    /// Human-readable setup description.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// The direction this setup represents.
    /// </summary>
    public string Direction { get; init; } = "";

    // --- Core Metrics ---

    /// <summary>
    /// How many times this exact setup has occurred.
    /// </summary>
    public int SampleSize { get; init; }

    /// <summary>
    /// Percentage of times the setup's target was hit before invalidation.
    /// </summary>
    public double WinRate { get; init; }

    /// <summary>
    /// Average return on winning setups.
    /// </summary>
    public double AverageWinPercent { get; init; }

    /// <summary>
    /// Average return on losing setups.
    /// </summary>
    public double AverageLossPercent { get; init; }

    /// <summary>
    /// Expected value per setup: (WinRate × AvgWin) + ((1-WinRate) × AvgLoss).
    /// Positive = historically profitable setup. This is the north star metric.
    /// </summary>
    public double ExpectedValuePercent { get; init; }

    /// <summary>
    /// Average number of days setups of this type take to resolve.
    /// </summary>
    public double AverageHoldingDays { get; init; }

    /// <summary>
    /// How many independent signal categories typically confirm this setup.
    /// </summary>
    public double AverageConfirmationCount { get; init; }

    // --- Confidence & Trust ---

    /// <summary>
    /// Statistical confidence in the performance metrics (0-1).
    /// Low sample size → low confidence → the system should weight
    /// this setup's history less heavily.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Risk rating based on variance of outcomes (0-100).
    /// High variance even with positive EV = higher risk.
    /// </summary>
    public int RiskRating { get; init; }

    /// <summary>
    /// Is this setup currently trusted based on recent performance?
    /// A setup can be historically good but recently degraded.
    /// </summary>
    public bool IsTrusted { get; init; } = true;

    /// <summary>
    /// Recent win rate (last 30 days) vs all-time.
    /// Used to detect setup degradation.
    /// </summary>
    public double? RecentWinRate { get; init; }

    // --- Market Regime Breakdown ---

    /// <summary>
    /// Performance broken down by market regime.
    /// Key = regime name, Value = performance in that regime.
    /// </summary>
    public Dictionary<string, RegimePerformance> ByRegime { get; init; } = new();

    public DateTimeOffset LastUpdatedAt { get; init; }
}

/// <summary>
/// Performance of a setup within a specific market regime.
/// </summary>
public record RegimePerformance
{
    public int SampleSize { get; init; }
    public double WinRate { get; init; }
    public double ExpectedValuePercent { get; init; }
}

// ---------------------------------------------------------------------------
// Setup Learning Stats — what the learning engine discovers and persists.
// ---------------------------------------------------------------------------

/// <summary>
/// A row in the setup_performance table. One per unique fingerprint.
/// The learning engine upserts these after each evaluation cycle.
/// </summary>
public record SetupLearningStat
{
    public string Id { get; init; } = "";
    public string SetupFingerprint { get; init; } = "";
    public string Description { get; init; } = "";
    public string Direction { get; init; } = "";
    public int TotalOccurrences { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public double WinRate { get; init; }
    public double AverageWinPercent { get; init; }
    public double AverageLossPercent { get; init; }
    public double ExpectedValuePercent { get; init; }
    public double AverageHoldingDays { get; init; }
    public int AverageConfirmationCount { get; init; }
    public double Confidence { get; init; }
    public int RiskRating { get; init; }
    public bool IsTrusted { get; init; } = true;
    public string? MarketRegimeBreakdownJson { get; init; }
    public DateTimeOffset LastUpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
