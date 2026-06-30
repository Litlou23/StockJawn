using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

// -----------------------------------------------------------------------
// Paper Stock Candidate — parent record for a short-term stock pick.
// Wraps an existing prediction_candidates row with paper-trading metadata
// (timeframe, entry/stop, deterministic score, status). Linked option
// candidates reference paper_stock_candidate_id.
// -----------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StockTimeframe { one_day, two_day, one_week }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaperStockStatus { open, evaluated, expired, watch_only, unavailable }

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
}
