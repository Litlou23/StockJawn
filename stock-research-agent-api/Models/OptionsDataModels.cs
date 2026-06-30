using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

// ---------------------------------------------------------------------------
// MarketData.app raw API response — parallel arrays
// ---------------------------------------------------------------------------

public class MarketDataApiResponse
{
    [JsonPropertyName("s")]
    public string Status { get; set; } = "";

    [JsonPropertyName("optionSymbol")]
    public string[] OptionSymbol { get; set; } = [];

    [JsonPropertyName("underlying")]
    public string[] Underlying { get; set; } = [];

    [JsonPropertyName("expiration")]
    public long[] Expiration { get; set; } = [];

    [JsonPropertyName("side")]
    public string[] Side { get; set; } = [];

    [JsonPropertyName("strike")]
    public double[] Strike { get; set; } = [];

    [JsonPropertyName("firstTraded")]
    public long[] FirstTraded { get; set; } = [];

    [JsonPropertyName("dte")]
    public int[] Dte { get; set; } = [];

    [JsonPropertyName("updated")]
    public long[] Updated { get; set; } = [];

    [JsonPropertyName("bid")]
    public double[] Bid { get; set; } = [];

    [JsonPropertyName("bidSize")]
    public int[] BidSize { get; set; } = [];

    [JsonPropertyName("mid")]
    public double[] Mid { get; set; } = [];

    [JsonPropertyName("ask")]
    public double[] Ask { get; set; } = [];

    [JsonPropertyName("askSize")]
    public int[] AskSize { get; set; } = [];

    [JsonPropertyName("last")]
    public double[] Last { get; set; } = [];

    [JsonPropertyName("openInterest")]
    public int[] OpenInterest { get; set; } = [];

    [JsonPropertyName("volume")]
    public int[] Volume { get; set; } = [];

    [JsonPropertyName("inTheMoney")]
    public bool[] InTheMoney { get; set; } = [];

    [JsonPropertyName("intrinsicValue")]
    public double[] IntrinsicValue { get; set; } = [];

    [JsonPropertyName("extrinsicValue")]
    public double[] ExtrinsicValue { get; set; } = [];

    [JsonPropertyName("underlyingPrice")]
    public double[] UnderlyingPrice { get; set; } = [];

    [JsonPropertyName("iv")]
    public double[] Iv { get; set; } = [];

    [JsonPropertyName("delta")]
    public double[] Delta { get; set; } = [];

    [JsonPropertyName("gamma")]
    public double[] Gamma { get; set; } = [];

    [JsonPropertyName("theta")]
    public double[] Theta { get; set; } = [];

    [JsonPropertyName("vega")]
    public double[] Vega { get; set; } = [];
}

// ---------------------------------------------------------------------------
// Normalized option contract (one per array index)
// ---------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OptionSide { call, put }

public record OptionContract
{
    public string OptionSymbol { get; init; } = "";
    public string Underlying { get; init; } = "";
    public DateTimeOffset Expiration { get; init; }
    public OptionSide Side { get; init; }
    public double Strike { get; init; }
    public int Dte { get; init; }
    public DateTimeOffset Updated { get; init; }

    // Pricing
    public double Bid { get; init; }
    public int BidSize { get; init; }
    public double Mid { get; init; }
    public double Ask { get; init; }
    public int AskSize { get; init; }
    public double Last { get; init; }

    // Interest & volume
    public int OpenInterest { get; init; }
    public int Volume { get; init; }
    public bool InTheMoney { get; init; }

    // Value decomposition
    public double IntrinsicValue { get; init; }
    public double ExtrinsicValue { get; init; }
    public double UnderlyingPrice { get; init; }

    // Greeks
    public double Iv { get; init; }
    public double Delta { get; init; }
    public double Gamma { get; init; }
    public double Theta { get; init; }
    public double Vega { get; init; }

    // Computed
    public double BidAskSpread => Ask - Bid;
    public double BidAskSpreadPercent => Mid > 0 ? (Ask - Bid) / Mid * 100 : 0;
}

// ---------------------------------------------------------------------------
// Options chain — full normalized result
// ---------------------------------------------------------------------------

public record OptionsChain
{
    public string Underlying { get; init; } = "";
    public double UnderlyingPrice { get; init; }
    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<OptionContract> Contracts { get; init; } = [];
    public int TotalContracts => Contracts.Count;
    public List<string> Warnings { get; init; } = [];
}

// ---------------------------------------------------------------------------
// Contract filter criteria
// ---------------------------------------------------------------------------

public class OptionContractFilter
{
    public OptionSide? Side { get; set; }
    public int? MinDte { get; set; }
    public int? MaxDte { get; set; }
    public double? MinStrike { get; set; }
    public double? MaxStrike { get; set; }
    public double? MinIv { get; set; }
    public double? MaxIv { get; set; }
    public int? MinOpenInterest { get; set; }
    public int? MinVolume { get; set; }
    public double? MaxBidAskSpreadPercent { get; set; }
    public bool? InTheMoney { get; set; }
    public double? MinDelta { get; set; }
    public double? MaxDelta { get; set; }
}

// ---------------------------------------------------------------------------
// Contract score (for ranking best candidates)
// ---------------------------------------------------------------------------

public record ContractScore
{
    public OptionContract Contract { get; init; } = null!;
    public double LiquidityScore { get; init; }
    public double SpreadScore { get; init; }
    public double IvScore { get; init; }
    public double DteScore { get; init; }
    public double OverallScore { get; init; }
    public string ScoreExplanation { get; init; } = "";
}

// ---------------------------------------------------------------------------
// Paper option candidate — a real contract selected for paper tracking
// ---------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaperCandidateStatus { open, closed, expired, evaluated }

public record PaperOptionCandidate
{
    public string Id { get; init; } = "";
    public string? PredictionId { get; init; }
    public string Ticker { get; init; } = "";
    public string OptionSymbol { get; init; } = "";
    public OptionSide Side { get; init; }
    public double Strike { get; init; }
    public DateTimeOffset Expiration { get; init; }
    public int DteAtEntry { get; init; }

    // Entry snapshot
    public double EntryUnderlyingPrice { get; init; }
    public double EntryBid { get; init; }
    public double EntryAsk { get; init; }
    public double EntryMid { get; init; }
    public double EntryIv { get; init; }
    public double EntryDelta { get; init; }
    public int EntryOpenInterest { get; init; }
    public int EntryVolume { get; init; }

    // Scoring
    public double ContractScore { get; init; }
    public string SelectionReason { get; init; } = "";

    // Status
    public PaperCandidateStatus Status { get; init; } = PaperCandidateStatus.open;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

// ---------------------------------------------------------------------------
// Paper option outcome — what happened to the paper candidate
// ---------------------------------------------------------------------------

public record PaperOptionOutcome
{
    public string Id { get; init; } = "";
    public string PaperCandidateId { get; init; } = "";
    public DateTimeOffset EvaluationTime { get; init; }

    // Current snapshot
    public double CurrentUnderlyingPrice { get; init; }
    public double CurrentBid { get; init; }
    public double CurrentAsk { get; init; }
    public double CurrentMid { get; init; }
    public double CurrentIv { get; init; }
    public double CurrentDelta { get; init; }
    public int CurrentOpenInterest { get; init; }
    public int CurrentVolume { get; init; }

    // P&L
    public double PaperPnlPerContract { get; init; }
    public double PaperPnlPercent { get; init; }
    public double UnderlyingMovePercent { get; init; }

    // IV change
    public double IvChange { get; init; }

    public string OutcomeSummary { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

// ---------------------------------------------------------------------------
// API response DTOs
// ---------------------------------------------------------------------------

public record OptionsChainResponse
{
    public string Underlying { get; init; } = "";
    public double UnderlyingPrice { get; init; }
    public int TotalContracts { get; init; }
    public List<OptionContract> Contracts { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public DateTimeOffset RetrievedAt { get; init; }
}

public record TopContractsResponse
{
    public string Underlying { get; init; } = "";
    public double UnderlyingPrice { get; init; }
    public List<ContractScore> TopContracts { get; init; } = [];
    public OptionContractFilter FilterUsed { get; init; } = new();
    public List<string> Warnings { get; init; } = [];
}

public record PaperCandidateResponse
{
    public PaperOptionCandidate Candidate { get; init; } = null!;
    public PredictionCandidate? LinkedPrediction { get; init; }
}

public record PaperTrackingStatusResponse
{
    public int TotalCandidates { get; init; }
    public int OpenCandidates { get; init; }
    public int ClosedCandidates { get; init; }
    public int ExpiredCandidates { get; init; }
    public List<PaperCandidateWithOutcome> Candidates { get; init; } = [];
}

public record PaperCandidateWithOutcome
{
    public PaperOptionCandidate Candidate { get; init; } = null!;
    public PaperOptionOutcome? LatestOutcome { get; init; }
}

// ---------------------------------------------------------------------------
// MarketData.app Stock Quote — parallel arrays (single element for single symbol)
// ---------------------------------------------------------------------------

public class MarketDataStockQuoteResponse
{
    [JsonPropertyName("s")]
    public string Status { get; set; } = "";

    [JsonPropertyName("symbol")]
    public string[] Symbol { get; set; } = [];

    [JsonPropertyName("ask")]
    public double[] Ask { get; set; } = [];

    [JsonPropertyName("askSize")]
    public int[] AskSize { get; set; } = [];

    [JsonPropertyName("bid")]
    public double[] Bid { get; set; } = [];

    [JsonPropertyName("bidSize")]
    public int[] BidSize { get; set; } = [];

    [JsonPropertyName("mid")]
    public double[] Mid { get; set; } = [];

    [JsonPropertyName("last")]
    public double[] Last { get; set; } = [];

    [JsonPropertyName("change")]
    public double[] Change { get; set; } = [];

    [JsonPropertyName("changepct")]
    public double[] ChangePct { get; set; } = [];

    [JsonPropertyName("volume")]
    public long[] Volume { get; set; } = [];

    [JsonPropertyName("updated")]
    public long[] Updated { get; set; } = [];
}

public record StockQuote
{
    public string Symbol { get; init; } = "";
    public double Ask { get; init; }
    public int AskSize { get; init; }
    public double Bid { get; init; }
    public int BidSize { get; init; }
    public double Mid { get; init; }
    public double Last { get; init; }
    public double Change { get; init; }
    public double ChangePct { get; init; }
    public long Volume { get; init; }
    public DateTimeOffset Updated { get; init; }
}

// ---------------------------------------------------------------------------
// MarketData.app Stock Candles — parallel arrays
// ---------------------------------------------------------------------------

public class MarketDataCandlesResponse
{
    [JsonPropertyName("s")]
    public string Status { get; set; } = "";

    [JsonPropertyName("t")]
    public long[] Timestamps { get; set; } = [];

    [JsonPropertyName("o")]
    public double[] Open { get; set; } = [];

    [JsonPropertyName("h")]
    public double[] High { get; set; } = [];

    [JsonPropertyName("l")]
    public double[] Low { get; set; } = [];

    [JsonPropertyName("c")]
    public double[] Close { get; set; } = [];

    [JsonPropertyName("v")]
    public long[] Volume { get; set; } = [];
}

public record StockCandle
{
    public DateTimeOffset Timestamp { get; init; }
    public double Open { get; init; }
    public double High { get; init; }
    public double Low { get; init; }
    public double Close { get; init; }
    public long Volume { get; init; }
}

// ---------------------------------------------------------------------------
// Paper Options V2 — Enhanced types for generate/evaluate flow
// ---------------------------------------------------------------------------

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DurationPreference { system_recommended, one_week, two_week }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PriceBucket { lotto, speculative, main_research, expensive }

public class GenerateCandidatesRequest
{
    public string PredictionId { get; set; } = "";
    public DurationPreference DurationPreference { get; set; } = DurationPreference.system_recommended;
    public bool AutoSave { get; set; } = false;
    /// <summary>
    /// Optional link to the paper_stock_candidates row that triggered this
    /// generation. Persisted on the option candidate when AutoSave is true.
    /// </summary>
    public string? PaperStockCandidateId { get; set; }
}

public record PaperCandidateEnhanced
{
    public string Id { get; init; } = "";
    public string? PredictionId { get; init; }
    public string? PaperStockCandidateId { get; init; }
    public string Ticker { get; init; } = "";
    public string OptionSymbol { get; init; } = "";
    public OptionSide Side { get; init; }
    public double Strike { get; init; }
    public DateTimeOffset Expiration { get; init; }
    public int DteAtEntry { get; init; }
    public double EntryUnderlyingPrice { get; init; }
    public double EntryBid { get; init; }
    public double EntryAsk { get; init; }
    public double EntryMid { get; init; }
    public double EntryIv { get; init; }
    public double EntryDelta { get; init; }
    public int EntryOpenInterest { get; init; }
    public int EntryVolume { get; init; }
    public double ContractScore { get; init; }
    public string SelectionReason { get; init; } = "";
    public PaperCandidateStatus Status { get; init; } = PaperCandidateStatus.open;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Enhanced fields
    public string Provider { get; init; } = "marketdata";
    public double EntryLast { get; init; }
    public double EntryGamma { get; init; }
    public double EntryTheta { get; init; }
    public double EntryVega { get; init; }
    public double EstimatedContractCost { get; init; }
    public double SpreadPercent { get; init; }
    public string DurationBucket { get; init; } = "system_recommended";
    public string? PriceBucket { get; init; }
    public string? DataDelayLabel { get; init; }
    public int Rank { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public record GenerateCandidatesResponse
{
    public string PredictionId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public string PredictionType { get; init; } = "";
    public double UnderlyingPrice { get; init; }
    public string DurationBucket { get; init; } = "";
    public int TargetDte { get; init; }
    public List<PaperCandidateEnhanced> Candidates { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public record PaperOutcomeEnhanced
{
    public string Id { get; init; } = "";
    public string PaperCandidateId { get; init; } = "";
    public DateTimeOffset EvaluationTime { get; init; }
    public double CurrentUnderlyingPrice { get; init; }
    public double CurrentBid { get; init; }
    public double CurrentAsk { get; init; }
    public double CurrentMid { get; init; }
    public double CurrentIv { get; init; }
    public double CurrentDelta { get; init; }
    public int CurrentOpenInterest { get; init; }
    public int CurrentVolume { get; init; }
    public double PaperPnlPerContract { get; init; }
    public double PaperPnlPercent { get; init; }
    public double UnderlyingMovePercent { get; init; }
    public double IvChange { get; init; }
    public string OutcomeSummary { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Enhanced fields
    public string? PredictionId { get; init; }
    public string Ticker { get; init; } = "";
    public string OptionSymbol { get; init; } = "";
    public double CurrentLast { get; init; }
    public bool DirectionCorrect { get; init; }
    public bool ContractProfitable { get; init; }
    public bool SpreadStillAcceptable { get; init; }
    public bool VolumeStillAcceptable { get; init; }
    public double OutcomeScore { get; init; }
    public string? Lesson { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public record OptionLearningStat
{
    public string Id { get; init; } = "";
    public string StatType { get; init; } = "";
    public string StatKey { get; init; } = "";
    public int TotalCandidates { get; init; }
    public int ProfitableCandidates { get; init; }
    public double WinRate { get; init; }
    public double AverageOptionMovePercent { get; init; }
    public double AverageUnderlyingMovePercent { get; init; }
    public double AverageOutcomeScore { get; init; }
    public DateTimeOffset LastUpdatedAt { get; init; }
}

public class EvaluateCandidateRequest
{
    public string PaperCandidateId { get; set; } = "";
}

public class SaveCandidateRequest
{
    public string PredictionId { get; set; } = "";
    public PaperCandidateEnhanced Candidate { get; set; } = null!;
}

public record PaperOptionsDebugResponse
{
    public int TotalCandidates { get; init; }
    public int OpenCandidates { get; init; }
    public int EvaluatedCandidates { get; init; }
    public int TotalOutcomes { get; init; }
    public List<OptionLearningStat> LearningStats { get; init; } = [];
    public bool MarketDataConfigured { get; init; }
}
