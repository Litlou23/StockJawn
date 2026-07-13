using System.Collections.Concurrent;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.CaseRepository;

/// <summary>
/// In-memory implementation of <see cref="IHistoricalCaseRepository"/>.
/// Thread-safe. Data lost on restart — future phase persists to Supabase.
///
/// Can be seeded from the existing <see cref="Knowledge.IKnowledgeRepository"/>
/// case store during startup without modifying that system.
/// </summary>
public class InMemoryHistoricalCaseRepository : IHistoricalCaseRepository
{
    private readonly ConcurrentDictionary<string, HistoricalCase> _cases = new();

    public Task StoreCaseAsync(HistoricalCase @case)
    {
        _cases[@case.CaseId] = @case;
        return Task.CompletedTask;
    }

    public Task<List<HistoricalCase>> FindSimilarCasesAsync(CaseSearchQuery query)
    {
        var results = _cases.Values.AsEnumerable();

        if (query.Ticker is not null)
            results = results.Where(c =>
                c.Ticker.Equals(query.Ticker, StringComparison.OrdinalIgnoreCase));

        if (query.Direction is not null)
            results = results.Where(c =>
                c.Prediction?.PredictionType.ToString()
                    .Equals(query.Direction, StringComparison.OrdinalIgnoreCase) == true);

        if (query.Regime is not null)
            results = results.Where(c =>
                c.MarketRegime.Equals(query.Regime.ToString(), StringComparison.OrdinalIgnoreCase));

        if (query.MinGrade is not null)
            results = results.Where(c =>
                c.Tags.Contains(query.MinGrade.ToString()!));

        if (query.RequiredFeatures.Count > 0)
            results = results.Where(c =>
                query.RequiredFeatures.All(f =>
                    c.Features.Any(feat =>
                        feat.FeatureId.Equals(f, StringComparison.OrdinalIgnoreCase))));

        if (query.RequiredEvidence.Count > 0)
            results = results.Where(c =>
                query.RequiredEvidence.All(e =>
                    c.Evidence.Any(ev =>
                        ev.EvidenceId.Equals(e, StringComparison.OrdinalIgnoreCase))));

        if (query.RequiredConcepts.Count > 0)
            results = results.Where(c =>
                query.RequiredConcepts.All(con =>
                    c.Concepts.Contains(con, StringComparer.OrdinalIgnoreCase)));

        return Task.FromResult(results.Take(query.Limit).ToList());
    }

    public Task<List<HistoricalCase>> FindCasesByRegimeAsync(MarketRegimeType regime, int limit = 50)
    {
        return Task.FromResult(_cases.Values
            .Where(c => c.MarketRegime.Equals(regime.ToString(), StringComparison.OrdinalIgnoreCase))
            .Take(limit).ToList());
    }

    public Task<List<HistoricalCase>> FindCasesByPatternAsync(string patternType, int limit = 50)
    {
        return Task.FromResult(_cases.Values
            .Where(c => c.Tags.Contains(patternType, StringComparer.OrdinalIgnoreCase))
            .Take(limit).ToList());
    }

    public Task<List<HistoricalCase>> FindWinningCasesAsync(int limit = 50)
    {
        return Task.FromResult(_cases.Values
            .Where(c => c.Outcome?.Outcome == "win")
            .OrderByDescending(c => c.Outcome?.ReturnPercent ?? 0)
            .Take(limit).ToList());
    }

    public Task<List<HistoricalCase>> FindLosingCasesAsync(int limit = 50)
    {
        return Task.FromResult(_cases.Values
            .Where(c => c.Outcome?.Outcome == "loss")
            .OrderBy(c => c.Outcome?.ReturnPercent ?? 0)
            .Take(limit).ToList());
    }

    public Task<List<HistoricalCase>> FindHighestReturnCasesAsync(int limit = 20)
    {
        return Task.FromResult(_cases.Values
            .Where(c => c.Outcome is not null)
            .OrderByDescending(c => c.Outcome!.ReturnPercent ?? 0)
            .Take(limit).ToList());
    }

    public Task<List<HistoricalCase>> FindCasesByTickerAsync(string ticker, int limit = 50)
    {
        return Task.FromResult(_cases.Values
            .Where(c => c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Date)
            .Take(limit).ToList());
    }

    public Task<CaseLibraryStats> GetStatsAsync()
    {
        var all = _cases.Values.ToList();
        var wins = all.Count(c => c.Outcome?.Outcome == "win");
        var losses = all.Count(c => c.Outcome?.Outcome == "loss");
        var total = all.Count;

        return Task.FromResult(new CaseLibraryStats
        {
            TotalCases = total,
            WinningCases = wins,
            LosingCases = losses,
            OverallWinRate = total > 0 ? Math.Round((double)wins / total, 4) : 0,
            AverageReturn = total > 0
                ? Math.Round(all.Where(c => c.Outcome?.ReturnPercent != null)
                    .Average(c => c.Outcome!.ReturnPercent!.Value), 2)
                : 0,
            AverageHoldingDays = total > 0
                ? Math.Round(all.Where(c => c.Outcome?.HoldingPeriodDays != null)
                    .DefaultIfEmpty()
                    .Average(c => c?.Outcome?.HoldingPeriodDays ?? 0), 1)
                : 0,
            DistinctTickers = all.Select(c => c.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            CasesByRegime = all.GroupBy(c => c.MarketRegime)
                .ToDictionary(g => g.Key, g => g.Count()),
        });
    }
}
