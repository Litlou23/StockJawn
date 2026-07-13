using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.CaseRepository;

/// <summary>
/// Structured retrieval interface for the historical case library.
///
/// Every completed prediction becomes a searchable case containing
/// facts, features, evidence, thesis, regime, trade decision, outcome,
/// MFE/MAE, holding period, and lessons learned.
///
/// This interface extends the existing <see cref="Knowledge.IKnowledgeRepository"/>
/// case storage with purpose-built query methods.
/// </summary>
public interface IHistoricalCaseRepository
{
    Task StoreCaseAsync(HistoricalCase @case);
    Task<List<HistoricalCase>> FindSimilarCasesAsync(CaseSearchQuery query);
    Task<List<HistoricalCase>> FindCasesByRegimeAsync(MarketRegimeType regime, int limit = 50);
    Task<List<HistoricalCase>> FindCasesByPatternAsync(string patternType, int limit = 50);
    Task<List<HistoricalCase>> FindWinningCasesAsync(int limit = 50);
    Task<List<HistoricalCase>> FindLosingCasesAsync(int limit = 50);
    Task<List<HistoricalCase>> FindHighestReturnCasesAsync(int limit = 20);
    Task<List<HistoricalCase>> FindCasesByTickerAsync(string ticker, int limit = 50);
    Task<CaseLibraryStats> GetStatsAsync();
}

/// <summary>
/// Search criteria for finding similar cases.
/// All fields optional — provides flexible multi-dimensional querying.
/// </summary>
public record CaseSearchQuery
{
    public string? Ticker { get; init; }
    public string? Direction { get; init; }
    public MarketRegimeType? Regime { get; init; }
    public TradeGrade? MinGrade { get; init; }
    public List<string> RequiredFeatures { get; init; } = [];
    public List<string> RequiredEvidence { get; init; } = [];
    public List<string> RequiredConcepts { get; init; } = [];
    public int Limit { get; init; } = 25;
}

/// <summary>
/// Summary statistics for the case library.
/// </summary>
public record CaseLibraryStats
{
    public int TotalCases { get; init; }
    public int WinningCases { get; init; }
    public int LosingCases { get; init; }
    public double OverallWinRate { get; init; }
    public double AverageReturn { get; init; }
    public double AverageHoldingDays { get; init; }
    public int DistinctTickers { get; init; }
    public Dictionary<string, int> CasesByRegime { get; init; } = [];
}
