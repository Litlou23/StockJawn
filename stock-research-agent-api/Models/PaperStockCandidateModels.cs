using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

// -----------------------------------------------------------------------
// Paper Stock Candidate — parent record for a short-term stock pick.
// Wraps an existing prediction_candidates row with paper-trading metadata
// (timeframe, entry/stop, deterministic score, status). Linked option
// candidates reference paper_stock_candidate_id.
// -----------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StockTimeframe { one_day, two_day, three_day, one_week, one_month, three_month, six_month, one_year }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaperStockStatus { open, evaluated, expired, watch_only, unavailable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CandidateMode { learning, actionable_shadow, live_eligible }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityTier { very_weak, weak, medium, strong_paper, production_candidate }

public record PaperStockCandidate
{
    public string Id { get; init; } = "";
    public string? PredictionId { get; init; }
    public string? RunId { get; init; }

    public string Ticker { get; init; } = "";
    public PredictionType PredictionType { get; init; }
    public StockTimeframe Timeframe { get; init; } = StockTimeframe.one_day;

    // Entry snapshot — real data
    public double? EntryPrice { get; init; }
    public double? ReferencePrice { get; init; }
    public double? TargetPrice { get; init; }
    public double? StopPrice { get; init; }

    // Deterministic scoring (0..100)
    public double CatalystScore { get; init; }
    public double TrendScore { get; init; }
    public double VolumeScore { get; init; }
    public double MarketContextScore { get; init; }
    public double HistoricalAccuracyScore { get; init; }
    public double RiskPenalty { get; init; }
    public double MissingDataPenalty { get; init; }
    public double TotalScore { get; init; }

    public int ConfidenceScore { get; init; }
    public int RiskScore { get; init; }
    public string? CatalystType { get; init; }
    public string SelectionReason { get; init; } = "";
    public List<string> Warnings { get; init; } = [];
    public string DataAvailability { get; init; } = "real"; // real | partial | unavailable

    public CandidateMode CandidateMode { get; init; } = CandidateMode.learning;
    public QualityTier QualityTier { get; init; } = QualityTier.very_weak;
    public bool IsActionable { get; init; }
    public string ThresholdPolicyVersion { get; init; } = "learning_options_v1";
    public string InclusionReason { get; init; } = "";
    public string? ExclusionReason { get; init; }
    public double ScorePercentileInRun { get; init; }

    // Direction-neutral dual scores
    public double? BullishScore { get; init; }
    public double? BearishScore { get; init; }
    public string? WinningDirection { get; init; }

    public PaperStockStatus Status { get; init; } = PaperStockStatus.open;
    public bool QualifiesForOptions { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record PaperStockOutcome
{
    public string Id { get; init; } = "";
    public string PaperStockCandidateId { get; init; } = "";
    public string? PredictionId { get; init; }
    public string Ticker { get; init; } = "";
    public DateTimeOffset EvaluationTime { get; init; }

    public double? ExitPrice { get; init; }
    public double? HighAfter { get; init; }
    public double? LowAfter { get; init; }
    public double? PercentMove { get; init; }

    public bool? DirectionCorrect { get; init; }
    public bool? TargetHit { get; init; }
    public bool? StopHit { get; init; }
    public bool? InvalidationHit { get; init; }
    public double OutcomeScore { get; init; }

    public string OutcomeSummary { get; init; } = "";
    public string? Lesson { get; init; }
    public string? FailureReason { get; init; }
    public List<string> Warnings { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record StockLearningStat
{
    public string Id { get; init; } = "";
    public string StatType { get; init; } = "";
    public string StatKey { get; init; } = "";
    public int TotalCandidates { get; init; }
    public int CorrectCandidates { get; init; }
    public double Accuracy { get; init; }
    public double AveragePercentMove { get; init; }
    public double AverageOutcomeScore { get; init; }
    public DateTimeOffset LastUpdatedAt { get; init; }
}

// -----------------------------------------------------------------------
// Orchestrator response shapes
// -----------------------------------------------------------------------

public record DynamicMorningResult
{
    public string? RunId { get; init; }
    public int PredictionsGenerated { get; init; }
    public int StockCandidatesGenerated { get; init; }
    public int StockCandidatesQualifiedForOptions { get; init; }
    public int OptionCandidatesGenerated { get; init; }
    public string Report { get; init; } = "";
    public List<string> Errors { get; init; } = [];
    public List<PaperStockCandidate> StockCandidates { get; init; } = [];
}

public record DynamicEodResult
{
    public string? RunId { get; init; }
    public int StockOutcomesEvaluated { get; init; }
    public int OptionOutcomesEvaluated { get; init; }
    public string Report { get; init; } = "";
    public List<string> Errors { get; init; } = [];
}

public record DynamicLearningResult
{
    public string? RunId { get; init; }
    public int StockStatsUpdated { get; init; }
    public int OptionStatsUpdated { get; init; }
    public int WeightsAdjusted { get; init; }
    public int InsightsGenerated { get; init; }
    public string Report { get; init; } = "";
    public List<string> Errors { get; init; } = [];
}

public record DynamicDashboardSummary
{
    public int StockPicksToday { get; init; }
    public int OptionPicksToday { get; init; }
    public int OpenStockCandidates { get; init; }
    public int OpenOptionCandidates { get; init; }
    public int EvaluatedToday { get; init; }
    public string? BestSignalKey { get; init; }
    public double BestSignalAccuracy { get; init; }
    public string? WorstSignalKey { get; init; }
    public double WorstSignalAccuracy { get; init; }
    public string? InsightOfTheDay { get; init; }
    public DateTimeOffset? LatestRunStartedAt { get; init; }
    public string? LatestRunId { get; init; }
    public int LatestRunPredictionCandidatesGenerated { get; init; }
    public int LatestRunPaperStockCandidatesCreated { get; init; }
    public int LatestRunPaperOptionCandidatesCreated { get; init; }
    public int LatestRunBlockedOptionCandidates { get; init; }
    public string? LatestRunTopOptionBlockReason { get; init; }
    public int TotalStockOutcomes { get; init; }
    public int TotalOptionOutcomes { get; init; }
    public int StockOutcomesAddedToday { get; init; }
    public int OptionOutcomesAddedToday { get; init; }
    public int StockOutcomesAddedLast7Days { get; init; }
    public int OptionOutcomesAddedLast7Days { get; init; }
    public int CandidatesAwaitingEodEvaluation { get; init; }
    public double OutcomeCoverageRate { get; init; }
    public FunnelSummary Funnel { get; init; } = new();
    public List<BlockReasonCount> BlockReasonBreakdown { get; init; } = [];
    public List<QualityTierPerformance> QualityTierPerformance { get; init; } = [];
    public List<ConfidenceCalibrationBucket> ConfidenceCalibration { get; init; } = [];
    public PortfolioChallengeSummary? PortfolioChallenge { get; init; }
}

public record CandidateGenerationAuditEntry
{
    public string Id { get; init; } = "";
    public string? RunId { get; init; }
    public string Ticker { get; init; } = "";
    public string? PredictionCandidateId { get; init; }
    public string? PaperStockCandidateId { get; init; }
    public string? PaperOptionCandidateId { get; init; }
    public string PredictionType { get; init; } = "";
    public int ConfidenceScore { get; init; }
    public int RiskScore { get; init; }
    public double ScorePercentileInRun { get; init; }
    public bool StockCandidateCreated { get; init; }
    public bool OptionCandidateCreated { get; init; }
    public CandidateMode CandidateMode { get; init; } = CandidateMode.learning;
    public QualityTier QualityTier { get; init; } = QualityTier.very_weak;
    public string? OptionBlockReason { get; init; }
    public bool MarketDataAvailable { get; init; }
    public bool OptionChainAvailable { get; init; }
    public string ThresholdPolicyVersion { get; init; } = "learning_options_v1";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record BlockReasonCount(string Reason, int Count);

public record FunnelSummary
{
    public int PredictionCandidates { get; init; }
    public int StockCandidates { get; init; }
    public int OptionEligible { get; init; }
    public int OptionCreated { get; init; }
    public int Evaluated { get; init; }
    public int LearningStatsUpdated { get; init; }
}

public record QualityTierPerformance
{
    public string QualityTier { get; init; } = "";
    public int CandidateCount { get; init; }
    public double? WinRate { get; init; }
    public double? AverageReturn { get; init; }
    public double? MedianReturn { get; init; }
}

public record ConfidenceCalibrationBucket
{
    public string BucketLabel { get; init; } = "";
    public int CandidateCount { get; init; }
    public double? SuccessRate { get; init; }
}
