using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

/// <summary>
/// Outcome evaluation for neutral predictions (high_volatility, no_edge, range_bound).
/// These are NOT graded as correct/incorrect — instead they measure whether the
/// neutral classification was justified, and what opportunity (if any) was missed.
/// </summary>
public record NeutralPredictionOutcome
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string PredictionId { get; init; } = "";
    public string? PaperStockCandidateId { get; init; }
    public string Ticker { get; init; } = "";
    public string PredictionType { get; init; } = ""; // neutral_high_volatility, neutral_no_edge, neutral_range_bound
    public string TimeWindow { get; init; } = "";

    // --- Price data at evaluation ---
    public double? EntryPrice { get; init; }
    public double? ExitPrice { get; init; }
    public double? HighAfter { get; init; }
    public double? LowAfter { get; init; }

    // --- Core metrics ---
    public double RealizedMovePercent { get; init; }
    public double AbsoluteMovePercent { get; init; }
    public double MaxRunUp { get; init; }        // MFE — max favorable excursion (always positive)
    public double MaxDrawdown { get; init; }      // MAE — max adverse excursion (always positive)
    public double RealizedVolatility { get; init; } // std dev of daily returns over the window

    // --- Type-specific accuracy ---
    /// <summary>0-100: how well did the neutral classification match reality?</summary>
    public double NeutralAccuracyScore { get; init; }

    /// <summary>For high_volatility: was volatility actually high? (0-100)</summary>
    public double? VolatilityPredictionAccuracy { get; init; }

    /// <summary>For range_bound: % of time price stayed inside predicted range</summary>
    public double? RangeAdherencePercent { get; init; }

    /// <summary>For range_bound: did support break?</summary>
    public bool? SupportBroken { get; init; }

    /// <summary>For range_bound: did resistance break?</summary>
    public bool? ResistanceBroken { get; init; }

    /// <summary>For range_bound: max excursion outside the expected range (%)</summary>
    public double? MaxRangeExcursionPercent { get; init; }

    /// <summary>For no_edge: did a meaningful breakout occur?</summary>
    public bool? BreakoutOccurred { get; init; }

    /// <summary>For no_edge: how persistent was the directional move? (0-1)</summary>
    public double? DirectionalPersistence { get; init; }

    // --- Counterfactual analysis ---
    /// <summary>What direction would the system have picked? (bullish/bearish)</summary>
    public string? CounterfactualDirection { get; init; }

    /// <summary>Would that direction have been correct?</summary>
    public bool? CounterfactualCorrect { get; init; }

    /// <summary>0-100: how significant was the missed opportunity?</summary>
    public double OpportunityMissedScore { get; init; }

    /// <summary>Bull score from the original prediction</summary>
    public double? OriginalBullScore { get; init; }

    /// <summary>Bear score from the original prediction</summary>
    public double? OriginalBearScore { get; init; }

    // --- Summary ---
    public string OutcomeSummary { get; init; } = "";
    public string? Lesson { get; init; }

    public DateTimeOffset EvaluationTime { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
