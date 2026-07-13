namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Configurable thresholds that gate weight updates in the learning loop.
/// Bind from appsettings "LearningGuardrails" section.
/// </summary>
public class LearningGuardrailOptions
{
    public const string SectionName = "LearningGuardrails";

    // ── Sample size ──────────────────────────────────────────────────
    /// <summary>Min observations before a signal weight can be adjusted.</summary>
    public int MinSampleSize { get; set; } = 50;

    /// <summary>Min observations for calibration factor updates.</summary>
    public int MinCalibrationSample { get; set; } = 30;

    /// <summary>Min observations for risk-cap-boost updates.</summary>
    public int MinCapBoostSample { get; set; } = 15;

    /// <summary>Min evidence count for pattern-based recommendations.</summary>
    public int MinPatternEvidence { get; set; } = 10;

    // ── Maximum adjustment per cycle ─────────────────────────────────
    /// <summary>Max daily weight movement (fraction). Default 1%.</summary>
    public double MaxDailyMovement { get; set; } = 0.01;

    /// <summary>Max cumulative adjustment from base (fraction). Default ±20%.</summary>
    public double MaxCumulativeAdjustment { get; set; } = 0.20;

    /// <summary>Max daily calibration factor movement. Default 0.01.</summary>
    public double MaxCalibrationMovement { get; set; } = 0.01;

    /// <summary>Max daily risk-cap-boost movement (points). Default 2.</summary>
    public double MaxCapBoostMovement { get; set; } = 2.0;

    // ── Confidence interval ──────────────────────────────────────────
    /// <summary>
    /// Require accuracy to be statistically different from 50% at this
    /// confidence level (z-score). 1.645 = 90%, 1.96 = 95%. Default 1.645.
    /// </summary>
    public double RequiredZScore { get; set; } = 1.645;

    /// <summary>Enable/disable the confidence interval check entirely.</summary>
    public bool EnforceConfidenceInterval { get; set; } = true;

    // ── Accuracy trend ───────────────────────────────────────────────
    /// <summary>
    /// Compare recent-window accuracy to full-window accuracy.
    /// Block weight increases if recent accuracy is declining.
    /// </summary>
    public bool EnforceAccuracyTrend { get; set; } = true;

    /// <summary>Days for the "recent" accuracy window.</summary>
    public int RecentWindowDays { get; set; } = 30;

    /// <summary>Min observations in the recent window to evaluate trend.</summary>
    public int MinRecentSample { get; set; } = 10;

    /// <summary>
    /// Max allowable drop in recent accuracy vs overall before blocking
    /// a weight increase. E.g. 0.10 = if recent is 10%+ worse, block upward moves.
    /// </summary>
    public double MaxAccuracyDecline { get; set; } = 0.10;

    // ── Regime consistency ───────────────────────────────────────────
    /// <summary>
    /// If a signal performs well in one regime but poorly in another,
    /// block aggressive adjustments. Requires observations across
    /// multiple regimes.
    /// </summary>
    public bool EnforceRegimeConsistency { get; set; } = true;

    /// <summary>Min observations per regime to include in consistency check.</summary>
    public int MinRegimeSample { get; set; } = 10;

    /// <summary>
    /// Max spread between best and worst regime accuracy before
    /// throttling adjustment magnitude. E.g. 0.25 = if best regime is
    /// 25%+ better than worst, cut daily movement in half.
    /// </summary>
    public double MaxRegimeSpread { get; set; } = 0.25;

    /// <summary>
    /// Factor applied to daily movement when regime spread exceeds
    /// MaxRegimeSpread. Default 0.5 (halve the movement).
    /// </summary>
    public double RegimeThrottleFactor { get; set; } = 0.5;

    // ── Global kill switch ───────────────────────────────────────────
    /// <summary>Set to true to block ALL weight updates (read-only mode).</summary>
    public bool Frozen { get; set; } = false;
}
