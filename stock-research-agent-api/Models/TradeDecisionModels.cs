using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

/// <summary>
/// What the engine recommends doing with a prediction.
/// Ordered from least to most committed — numeric comparison is intentional.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TradeDecisionType
{
    /// <summary>No action — monitor only.</summary>
    Watch = 0,
    /// <summary>Meets minimum criteria but not compelling enough to size.</summary>
    Consider = 1,
    /// <summary>Execute a paper trade with the recommended position size.</summary>
    PaperTrade = 2,
    /// <summary>Would qualify for live execution (future phase).</summary>
    LiveEligible = 3,
    /// <summary>Explicitly rejected — do not trade even if other signals flip.</summary>
    Reject = -1,
}

/// <summary>
/// Letter-grade quality rating for the trade setup.
/// Assigned by <see cref="Services.TradeDecision.ITradeGradeService"/>
/// based on a deterministic scoring formula.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TradeGrade
{
    Unspecified = 0,
    Reject = 1,
    D = 2,
    C = 3,
    B = 4,
    A = 5,
    APlus = 6,
}

/// <summary>
/// Broad market environment classification.
/// Used by the Trade Decision layer to contextualise opportunities.
/// Maps to the string-based regime values already produced by
/// <see cref="Services.ResearchEngine.TradeSetupEngine.DetectMarketRegime"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarketRegimeType
{
    Unknown = 0,
    BullTrend,
    BearTrend,
    Sideways,
    HighVolatility,
    LowVolatility,
    RiskOn,
    RiskOff,
    MomentumMarket,
    MeanReversionMarket,
    Recovery,
    Distribution,
    Accumulation,
    Expansion,
    Contraction,
}

/// <summary>
/// The output of <see cref="Services.TradeDecision.ITradeDecisionEngine"/>.
/// Represents a capital-allocation decision derived from a prediction.
/// This model is NOT persisted — it is computed on the fly.
/// </summary>
public record TradeDecision
{
    // ── Identity ──────────────────────────────────────────────────────
    public string PredictionId { get; init; } = "";
    public string? Ticker { get; init; }
    public DateTimeOffset DecidedAt { get; init; } = DateTimeOffset.UtcNow;

    // ── Core decision ─────────────────────────────────────────────────
    public TradeDecisionType Decision { get; init; } = TradeDecisionType.Watch;
    public TradeGrade TradeGrade { get; init; } = TradeGrade.Unspecified;

    // ── Quantitative fields (null = not yet computed) ─────────────────
    public double? ExpectedValue { get; init; }
    public double? RiskRewardRatio { get; init; }
    public double? RecommendedPositionSize { get; init; }

    // ── Reasoning (human-readable) ────────────────────────────────────
    public List<string> Reasons { get; init; } = [];
    public List<string> Warnings { get; init; } = [];

    // ── Expected value breakdown ────────────────────────────────────
    public ExpectedValueResult? ExpectedValueResult { get; init; }

    // ── Risk/reward breakdown ─────────────────────────────────────
    public RiskRewardResult? RiskRewardResult { get; init; }

    // ── Filter results ────────────────────────────────────────────
    public List<TradeFilterResult> FilterResults { get; init; } = [];

    // ── Grade breakdown ───────────────────────────────────────────
    public TradeGradeResult? GradeResult { get; init; }

    // ── Explanation ──────────────────────────────────────────────
    public DecisionExplanation? Explanation { get; init; }

    // ── Source context (carry-forward from prediction for downstream use)
    public int? ConfidenceScore { get; init; }
    public int? RiskScore { get; init; }
    public string? Direction { get; init; }
    public string? SetupFingerprint { get; init; }
}

// ─────────────────────────────────────────────────────────────────────
// Expected Value models
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Input to <see cref="Services.TradeDecision.IExpectedValueCalculator"/>.
/// All rates are decimals (0.65 = 65%), percents are raw (12.0 = 12%).
/// </summary>
public record ExpectedValueRequest
{
    /// <summary>Historical win rate as a decimal (0.0–1.0).</summary>
    public double WinRate { get; init; }
    /// <summary>Average gain on winning trades (percent, e.g. 12.0 = 12%).</summary>
    public double AverageWinPercent { get; init; }
    /// <summary>Average loss on losing trades (percent, positive number, e.g. 6.0 = 6%).</summary>
    public double AverageLossPercent { get; init; }
}

/// <summary>
/// Output of <see cref="Services.TradeDecision.IExpectedValueCalculator"/>.
/// Captures the EV calculation result plus the inputs that produced it.
/// </summary>
public record ExpectedValueResult
{
    /// <summary>
    /// Expected value per trade in percent.
    /// Formula: (WinRate * AverageWinPercent) - ((1 - WinRate) * AverageLossPercent)
    /// </summary>
    public double ExpectedValue { get; init; }
    public double WinRate { get; init; }
    public double AverageWinPercent { get; init; }
    public double AverageLossPercent { get; init; }
    /// <summary>True when ExpectedValue > 0 — the setup has a positive edge.</summary>
    public bool PositiveExpectancy { get; init; }
}

// ─────────────────────────────────────────────────────────────────────
// Risk/Reward models
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Input to <see cref="Services.TradeDecision.IRiskRewardAnalyzer"/>.
/// All prices are absolute dollar values.
/// </summary>
public record RiskRewardRequest
{
    public double EntryPrice { get; init; }
    public double TargetPrice { get; init; }
    public double StopLossPrice { get; init; }
    /// <summary>
    /// True for long/bullish trades, false for short/bearish.
    /// Determines which direction risk and reward are measured.
    /// </summary>
    public bool IsBullish { get; init; } = true;
}

/// <summary>
/// Output of <see cref="Services.TradeDecision.IRiskRewardAnalyzer"/>.
/// </summary>
public record RiskRewardResult
{
    /// <summary>Dollar amount at risk per share (always positive when valid).</summary>
    public double RiskAmount { get; init; }
    /// <summary>Dollar amount of potential reward per share (always positive when valid).</summary>
    public double RewardAmount { get; init; }
    /// <summary>RewardAmount / RiskAmount.  0 when risk is zero or negative.</summary>
    public double RiskRewardRatio { get; init; }
    /// <summary>True when RiskRewardRatio >= 2.0 (configurable in future phases).</summary>
    public bool IsFavorable { get; init; }
    /// <summary>Non-null when the inputs were invalid (e.g. zero/negative prices).</summary>
    public string? ValidationError { get; init; }
}

// ─────────────────────────────────────────────────────────────────────
// Trade Filter models
// ─────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TradeFilterStatus
{
    Pass,
    Warning,
    Fail,
}

/// <summary>
/// The outcome of a single <see cref="Services.TradeDecision.ITradeFilter"/>.
/// </summary>
public record TradeFilterResult
{
    /// <summary>Human-readable filter name (e.g. "Confidence", "Liquidity").</summary>
    public string FilterName { get; init; } = "";
    public TradeFilterStatus Status { get; init; } = TradeFilterStatus.Pass;
    /// <summary>Short explanation of why this status was returned.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Immutable bag of everything a trade filter might need to make its decision.
/// Designed for extension — future phases add fields here (market regime,
/// portfolio state, open positions, learning data, pattern matches) without
/// changing <see cref="Services.TradeDecision.ITradeFilter"/>.
/// </summary>
public record TradeDecisionContext
{
    // ── Always available ──────────────────────────────────────────
    public required PredictionCandidate Prediction { get; init; }
    public required ExpectedValueResult? EvResult { get; init; }
    public required RiskRewardResult? RrResult { get; init; }

    // ── Extensions ─────────────────────────────────────────────────
    public MarketRegimeType? Regime { get; init; }
    // public PortfolioChallenge? Portfolio { get; init; }
    // public List<PortfolioPosition>? OpenPositions { get; init; }
    // public SetupLearningStat? SetupHistory { get; init; }
    // public List<KnowledgeRule>? MatchedPatterns { get; init; }
}

// ─────────────────────────────────────────────────────────────────────
// Trade Grade models
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Input to <see cref="Services.TradeDecision.ITradeGradeService"/>.
/// Designed for extension — add fields here as new scoring dimensions
/// come online without breaking existing callers.
/// </summary>
public record TradeGradeRequest
{
    public ExpectedValueResult? EvResult { get; init; }
    public RiskRewardResult? RrResult { get; init; }
    public List<TradeFilterResult> FilterResults { get; init; } = [];

    // Future extensions:
    // public int? ConfidenceScore { get; init; }
    // public int? RiskScore { get; init; }
    // public SetupLearningStat? SetupHistory { get; init; }
}

/// <summary>
/// Output of <see cref="Services.TradeDecision.ITradeGradeService"/>.
/// </summary>
public record TradeGradeResult
{
    public TradeGrade Grade { get; init; } = TradeGrade.Unspecified;
    /// <summary>Numeric score 0–100 that the grade was derived from.</summary>
    public int Score { get; init; }
    /// <summary>One-sentence deterministic summary of the opportunity quality.</summary>
    public string Summary { get; init; } = "";
    public List<string> Strengths { get; init; } = [];
    public List<string> Weaknesses { get; init; } = [];
}

// ─────────────────────────────────────────────────────────────────────
// Decision Explanation models
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Input to <see cref="Services.TradeDecision.IDecisionExplanationService"/>.
/// Aggregates every computed result so the explanation can be
/// generated from structured data — no string parsing.
/// </summary>
public record DecisionExplanationRequest
{
    public required TradeDecision Decision { get; init; }
    public TradeGradeResult? GradeResult { get; init; }
    public ExpectedValueResult? EvResult { get; init; }
    public RiskRewardResult? RrResult { get; init; }
    public List<TradeFilterResult> FilterResults { get; init; } = [];
}

/// <summary>
/// Human-readable, fully deterministic explanation of a trade decision.
/// Designed to be the single source of truth for any UI, report,
/// or future AI consumer that needs to understand "why this decision."
/// </summary>
public record DecisionExplanation
{
    /// <summary>Short headline (e.g. "High Quality Bullish Opportunity").</summary>
    public string Headline { get; init; } = "";
    /// <summary>One-to-two sentence narrative summary.</summary>
    public string Summary { get; init; } = "";
    /// <summary>Positive factors that drove the decision.</summary>
    public List<string> Reasons { get; init; } = [];
    /// <summary>Non-blocking concerns worth noting.</summary>
    public List<string> Warnings { get; init; } = [];
    /// <summary>Filters or checks that outright failed.</summary>
    public List<string> FailedChecks { get; init; } = [];
    /// <summary>Quantitative evidence supporting the trade.</summary>
    public List<string> SupportingEvidence { get; init; } = [];
    /// <summary>Qualitative strengths of the setup.</summary>
    public List<string> TradeStrengths { get; init; } = [];
    /// <summary>Qualitative weaknesses or risks.</summary>
    public List<string> TradeWeaknesses { get; init; } = [];
    /// <summary>Actionable one-liner (e.g. "Suitable for consideration.").</summary>
    public string Recommendation { get; init; } = "";
}

// ─────────────────────────────────────────────────────────────────────
// Portfolio Decision models
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Disposition assigned to each <see cref="TradeDecision"/> by the
/// <see cref="Services.TradeDecision.IPortfolioDecisionEngine"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PortfolioDisposition
{
    /// <summary>Trade accepted — within position and risk limits.</summary>
    Accepted,
    /// <summary>Trade deferred — would exceed position count or buying power.</summary>
    Deferred,
    /// <summary>Trade rejected — grade too low or explicitly blocked.</summary>
    Rejected,
}

/// <summary>
/// A <see cref="TradeDecision"/> paired with its portfolio-level disposition
/// and the reason it was placed in that bucket.
/// </summary>
public record PortfolioTradeEntry
{
    public required TradeDecision Trade { get; init; }
    public PortfolioDisposition Disposition { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// Input to <see cref="Services.TradeDecision.IPortfolioDecisionEngine"/>.
/// Designed for future expansion — add fields (current positions,
/// sector exposure, correlation data) without changing the interface.
/// </summary>
public record PortfolioEvaluationRequest
{
    /// <summary>All individual trade decisions to evaluate together.</summary>
    public List<TradeDecision> Opportunities { get; init; } = [];

    // ── Portfolio state (placeholders for future phases) ──────────
    // public List<PortfolioPosition>? CurrentPositions { get; init; }

    // ── Constraints ──────────────────────────────────────────────
    /// <summary>Cash available to deploy (dollars).</summary>
    public double AvailableBuyingPower { get; init; }
    /// <summary>Maximum number of concurrent open positions.</summary>
    public int MaxPositions { get; init; } = 10;
    /// <summary>Maximum percentage of portfolio to risk on a single trade (0.0–1.0).</summary>
    public double MaxRiskPerTrade { get; init; } = 0.02;
    /// <summary>Maximum percentage of portfolio at risk across all positions (0.0–1.0).</summary>
    public double MaxPortfolioRisk { get; init; } = 0.10;

    // ── Future extensions ────────────────────────────────────────
    // public double? MaxSectorConcentration { get; init; }
    // public double? MaxCorrelation { get; init; }
}

/// <summary>
/// Output of <see cref="Services.TradeDecision.IPortfolioDecisionEngine"/>.
/// Pure recommendation — does not execute anything.
/// </summary>
public record PortfolioRecommendation
{
    /// <summary>Trades that passed portfolio-level screening.</summary>
    public List<PortfolioTradeEntry> AcceptedTrades { get; init; } = [];
    /// <summary>Trades deferred due to capacity/risk limits.</summary>
    public List<PortfolioTradeEntry> DeferredTrades { get; init; } = [];
    /// <summary>Trades explicitly rejected at portfolio level.</summary>
    public List<PortfolioTradeEntry> RejectedTrades { get; init; } = [];
    /// <summary>
    /// Recommended capital per accepted trade (ticker → dollars).
    /// Placeholder — uniform allocation for now.
    /// </summary>
    public Dictionary<string, double> RecommendedCapitalAllocation { get; init; } = [];
    /// <summary>Portfolio-level warnings (max positions, buying power, concentration).</summary>
    public List<string> PortfolioWarnings { get; init; } = [];
    /// <summary>Deterministic one-liner summary of the recommendation.</summary>
    public string Summary { get; init; } = "";

    /// <summary>
    /// Historical similarity context per accepted trade (ticker → result).
    /// Populated by <see cref="Services.TradeDecision.IHistoricalSimilarityEngine"/>.
    /// Null when historical analysis has not been requested or no trades accepted.
    /// </summary>
    public Dictionary<string, HistoricalSimilarityResult>? HistoricalContext { get; init; }
}

// ─────────────────────────────────────────────────────────────────────
// Historical Similarity models
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Input to <see cref="Services.TradeDecision.IHistoricalSimilarityEngine"/>.
/// Contains everything needed to find and score similar historical cases.
/// </summary>
public record HistoricalSimilarityRequest
{
    public required TradeDecision Trade { get; init; }

    /// <summary>Maximum number of similar cases to return.</summary>
    public int MaxResults { get; init; } = 25;
    /// <summary>Minimum similarity score (0–100) to include a case.</summary>
    public double MinSimilarityScore { get; init; } = 40.0;

    // ── Future inputs ────────────────────────────────────────────
    public MarketRegimeType? CurrentRegime { get; init; }
    // public List<string>? SectorFilter { get; init; }
}

/// <summary>
/// One historical case that resembles the current opportunity.
/// </summary>
public record HistoricalCaseSummary
{
    public string CaseId { get; init; } = "";
    public string? Ticker { get; init; }
    public DateTimeOffset Date { get; init; }
    public string? PredictionDirection { get; init; }
    public TradeGrade TradeGrade { get; init; } = TradeGrade.Unspecified;
    public MarketRegimeType MarketRegime { get; init; } = MarketRegimeType.Unknown;
    /// <summary>"win", "loss", or "pending".</summary>
    public string Outcome { get; init; } = "";
    public double ReturnPercent { get; init; }
    /// <summary>Trading days held.</summary>
    public int HoldingPeriod { get; init; }
    /// <summary>0–100 score reflecting how similar this case is to the query.</summary>
    public double SimilarityScore { get; init; }
}

/// <summary>
/// Aggregated output of <see cref="Services.TradeDecision.IHistoricalSimilarityEngine"/>.
/// Provides decision-support context — does not make decisions itself.
/// </summary>
public record HistoricalSimilarityResult
{
    public List<HistoricalCaseSummary> MatchingCases { get; init; } = [];
    public double AverageReturn { get; init; }
    public double MedianReturn { get; init; }
    /// <summary>Fraction of matching cases that were wins (0.0–1.0).</summary>
    public double WinRate { get; init; }
    /// <summary>Average holding period in trading days.</summary>
    public double AverageHoldingPeriod { get; init; }
    /// <summary>Deterministic lessons extracted from matching cases.</summary>
    public List<string> TopLessons { get; init; } = [];
    /// <summary>One-liner summary of the historical context.</summary>
    public string Summary { get; init; } = "";
}
