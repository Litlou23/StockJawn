using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchUniverse;

/// <summary>
/// Configuration for the Research Universe Engine.
/// All thresholds and rules are configurable — no hardcoded values.
/// Load from environment variables or appsettings; defaults are sensible starting points.
/// </summary>
public class ResearchUniverseConfig
{
    // ── Staleness ───────────────────────────────────────────────

    /// <summary>Days without activity before a Discovered asset is archived.</summary>
    public int StaleDiscoveredDays { get; init; } = 3;

    /// <summary>Days without activity before a Monitoring asset is archived.</summary>
    public int StaleMonitoringDays { get; init; } = 7;

    /// <summary>Days without activity before a BuildingThesis asset is archived.</summary>
    public int StaleBuildingThesisDays { get; init; } = 14;

    /// <summary>Days without activity before a ReadyForEvaluation asset is archived.</summary>
    public int StaleReadyForEvaluationDays { get; init; } = 21;

    // ── State promotion thresholds ──────────────────────────────

    /// <summary>Minimum evidence count to promote Discovered → Monitoring.</summary>
    public int PromoteToMonitoringEvidenceCount { get; init; } = 2;

    /// <summary>Minimum interest score to promote Discovered → Monitoring.</summary>
    public int PromoteToMonitoringMinScore { get; init; } = 15;

    /// <summary>Minimum evidence count to promote Monitoring → BuildingThesis.</summary>
    public int PromoteToBuildingThesisEvidenceCount { get; init; } = 5;

    /// <summary>Minimum interest score to promote Monitoring → BuildingThesis.</summary>
    public int PromoteToBuildingThesisMinScore { get; init; } = 30;

    /// <summary>Minimum distinct evidence types to promote Monitoring → BuildingThesis.</summary>
    public int PromoteToBuildingThesisMinTypes { get; init; } = 2;

    /// <summary>Minimum evidence count to promote BuildingThesis → ReadyForEvaluation.</summary>
    public int PromoteToReadyEvidenceCount { get; init; } = 8;

    /// <summary>Minimum interest score to promote BuildingThesis → ReadyForEvaluation.</summary>
    public int PromoteToReadyMinScore { get; init; } = 50;

    /// <summary>Thesis must be non-empty to promote to ReadyForEvaluation.</summary>
    public bool RequireThesisForReady { get; init; } = true;

    // ── Interest score adjustments ──────────────────────────────

    /// <summary>Score boost per new evidence item recorded.</summary>
    public int ScorePerEvidence { get; init; } = 5;

    /// <summary>Extra score boost when a repeated catalyst hits the same ticker.</summary>
    public int ScoreBoostRepeatedCatalyst { get; init; } = 10;

    /// <summary>Score penalty per day of inactivity beyond the grace period.</summary>
    public int ScoreDecayPerStaleDay { get; init; } = 3;

    /// <summary>Days of inactivity before score decay begins.</summary>
    public int ScoreDecayGraceDays { get; init; } = 2;

    /// <summary>Initial interest score for newly discovered assets.</summary>
    public int InitialInterestScore { get; init; } = 10;

    /// <summary>Maximum interest score (hard cap).</summary>
    public int MaxInterestScore { get; init; } = 100;

    /// <summary>Minimum interest score — below this, asset may be archived.</summary>
    public int MinInterestScoreForActive { get; init; } = 5;

    // ── Holding window rules ────────────────────────────────────

    /// <summary>Default expected holding window for newly discovered assets.</summary>
    public string DefaultHoldingWindow { get; init; } = "2_5_days";

    /// <summary>Holding window overrides by dominant evidence type.
    /// Key = EvidenceType name, Value = holding window string.</summary>
    public Dictionary<string, string> HoldingWindowByEvidenceType { get; init; } = new()
    {
        ["News"] = "1_day",
        ["Technical"] = "1_day",
        ["Congress"] = "1_2_weeks",
        ["SEC"] = "1_2_weeks",
        ["Catalyst"] = "2_5_days",
        ["Options"] = "1_day",
        ["Volume"] = "1_day",
        ["Momentum"] = "2_5_days",
        ["Learning"] = "2_5_days",
        ["MarketRegime"] = "2_5_days",
        ["Research"] = "2_5_days",
    };

    // ── Archive rules ───────────────────────────────────────────

    /// <summary>Whether to auto-archive assets whose interest score drops below minimum.</summary>
    public bool ArchiveOnLowScore { get; init; } = true;

    /// <summary>Archive reason template for stale assets. {0} = days inactive.</summary>
    public string StaleArchiveReasonTemplate { get; init; } = "Stale: no activity for {0} days";

    /// <summary>Archive reason template for low-score assets. {0} = current score.</summary>
    public string LowScoreArchiveReasonTemplate { get; init; } = "Low interest: score dropped to {0}";

    /// <summary>
    /// Get the staleness threshold for a given state.
    /// </summary>
    public int GetStaleDaysForState(ResearchState state) => state switch
    {
        ResearchState.Discovered => StaleDiscoveredDays,
        ResearchState.Monitoring => StaleMonitoringDays,
        ResearchState.BuildingThesis => StaleBuildingThesisDays,
        ResearchState.ReadyForEvaluation => StaleReadyForEvaluationDays,
        _ => StaleMonitoringDays,
    };
}
