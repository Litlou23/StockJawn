using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.CaseRepository;

/// <summary>
/// Supabase-backed implementation of <see cref="IHistoricalCaseRepository"/>.
/// Complex nested objects (Facts, Features, Evidence, Prediction, Outcome)
/// are stored as JSONB columns. Thread-safe. Persists across restarts.
/// </summary>
public class SupabaseHistoricalCaseRepository : IHistoricalCaseRepository
{
    private const string Table = "historical_cases";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SupabaseClient _db;
    private readonly ILogger<SupabaseHistoricalCaseRepository> _logger;

    public SupabaseHistoricalCaseRepository(
        SupabaseClient db,
        ILogger<SupabaseHistoricalCaseRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task StoreCaseAsync(HistoricalCase @case)
    {
        var row = new
        {
            case_id = @case.CaseId,
            ticker = @case.Ticker,
            case_date = @case.Date.ToString("o"),
            market_regime = @case.MarketRegime,
            facts = JsonSerializer.SerializeToNode(@case.Facts, JsonOpts),
            features = JsonSerializer.SerializeToNode(@case.Features, JsonOpts),
            evidence = JsonSerializer.SerializeToNode(@case.Evidence, JsonOpts),
            market_thesis = JsonSerializer.SerializeToNode(@case.MarketThesis, JsonOpts),
            prediction = JsonSerializer.SerializeToNode(@case.Prediction, JsonOpts),
            outcome = JsonSerializer.SerializeToNode(@case.Outcome, JsonOpts),
            mfe = @case.MaximumFavorableExcursion,
            mae = @case.MaximumAdverseExcursion,
            lessons_learned = JsonSerializer.SerializeToNode(@case.LessonsLearned, JsonOpts),
            concepts = JsonSerializer.SerializeToNode(@case.Concepts, JsonOpts),
            tags = JsonSerializer.SerializeToNode(@case.Tags, JsonOpts),
        };

        var ok = await _db.UpsertAsync(Table, row, "case_id");
        if (!ok)
            _logger.LogWarning("[case-repo] Upsert failed for case {Id}", @case.CaseId);
    }

    public async Task<List<HistoricalCase>> FindSimilarCasesAsync(CaseSearchQuery query)
    {
        var filters = new List<string>();

        if (query.Ticker is not null)
            filters.Add($"ticker=ilike.{query.Ticker}");

        if (query.Direction is not null)
            // Direction is inside the prediction JSONB — filter in-memory
            { }

        if (query.Regime is not null)
            filters.Add($"market_regime=ilike.{query.Regime}");

        var filter = filters.Count > 0 ? string.Join("&", filters) : null;
        var rows = await _db.SelectAsync(Table, filter, limit: query.Limit * 3); // over-fetch for in-memory filtering
        var cases = rows.Select(MapCase).ToList();

        // Apply in-memory filters for complex criteria
        if (query.Direction is not null)
            cases = cases.Where(c =>
                c.Prediction?.PredictionType.ToString()
                    .Equals(query.Direction, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

        if (query.MinGrade is not null)
            cases = cases.Where(c =>
                c.Tags.Contains(query.MinGrade.ToString()!))
                .ToList();

        if (query.RequiredFeatures.Count > 0)
            cases = cases.Where(c =>
                query.RequiredFeatures.All(f =>
                    c.Features.Any(feat =>
                        feat.FeatureId.Equals(f, StringComparison.OrdinalIgnoreCase))))
                .ToList();

        if (query.RequiredEvidence.Count > 0)
            cases = cases.Where(c =>
                query.RequiredEvidence.All(e =>
                    c.Evidence.Any(ev =>
                        ev.EvidenceId.Equals(e, StringComparison.OrdinalIgnoreCase))))
                .ToList();

        if (query.RequiredConcepts.Count > 0)
            cases = cases.Where(c =>
                query.RequiredConcepts.All(con =>
                    c.Concepts.Contains(con, StringComparer.OrdinalIgnoreCase)))
                .ToList();

        return cases.Take(query.Limit).ToList();
    }

    public async Task<List<HistoricalCase>> FindCasesByRegimeAsync(MarketRegimeType regime, int limit = 50)
    {
        var rows = await _db.SelectAsync(Table,
            $"market_regime=ilike.{regime}", limit: limit);
        return rows.Select(MapCase).ToList();
    }

    public async Task<List<HistoricalCase>> FindCasesByPatternAsync(string patternType, int limit = 50)
    {
        // tags is JSONB array — use PostgREST containment operator
        var rows = await _db.SelectAsync(Table,
            $"tags=cs.[\"{patternType}\"]", limit: limit);
        return rows.Select(MapCase).ToList();
    }

    public async Task<List<HistoricalCase>> FindWinningCasesAsync(int limit = 50)
    {
        // outcome is JSONB — filter in-memory
        var rows = await _db.SelectAsync(Table, limit: limit * 3);
        return rows.Select(MapCase)
            .Where(c => c.Outcome?.Outcome == "win")
            .OrderByDescending(c => c.Outcome?.ReturnPercent ?? 0)
            .Take(limit).ToList();
    }

    public async Task<List<HistoricalCase>> FindLosingCasesAsync(int limit = 50)
    {
        var rows = await _db.SelectAsync(Table, limit: limit * 3);
        return rows.Select(MapCase)
            .Where(c => c.Outcome?.Outcome == "loss")
            .OrderBy(c => c.Outcome?.ReturnPercent ?? 0)
            .Take(limit).ToList();
    }

    public async Task<List<HistoricalCase>> FindHighestReturnCasesAsync(int limit = 20)
    {
        var rows = await _db.SelectAsync(Table, limit: limit * 3);
        return rows.Select(MapCase)
            .Where(c => c.Outcome is not null)
            .OrderByDescending(c => c.Outcome!.ReturnPercent ?? 0)
            .Take(limit).ToList();
    }

    public async Task<List<HistoricalCase>> FindCasesByTickerAsync(string ticker, int limit = 50)
    {
        var rows = await _db.SelectAsync(Table,
            $"ticker=ilike.{ticker}", order: "case_date.desc", limit: limit);
        return rows.Select(MapCase).ToList();
    }

    public async Task<CaseLibraryStats> GetStatsAsync()
    {
        var rows = await _db.SelectAsync(Table);
        var all = rows.Select(MapCase).ToList();
        var wins = all.Count(c => c.Outcome?.Outcome == "win");
        var losses = all.Count(c => c.Outcome?.Outcome == "loss");
        var total = all.Count;

        return new CaseLibraryStats
        {
            TotalCases = total,
            WinningCases = wins,
            LosingCases = losses,
            OverallWinRate = total > 0 ? Math.Round((double)wins / total, 4) : 0,
            AverageReturn = total > 0
                ? Math.Round(all.Where(c => c.Outcome?.ReturnPercent != null)
                    .DefaultIfEmpty()
                    .Average(c => c?.Outcome?.ReturnPercent ?? 0), 2)
                : 0,
            AverageHoldingDays = total > 0
                ? Math.Round(all.Where(c => c.Outcome?.HoldingPeriodDays != null)
                    .DefaultIfEmpty()
                    .Average(c => c?.Outcome?.HoldingPeriodDays ?? 0), 1)
                : 0,
            DistinctTickers = all.Select(c => c.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            CasesByRegime = all.GroupBy(c => c.MarketRegime)
                .ToDictionary(g => g.Key, g => g.Count()),
        };
    }

    // ══════════════════════════════════════════════════════════════
    // Mapping
    // ══════════════════════════════════════════════════════════════

    private static HistoricalCase MapCase(JsonObject r)
    {
        return new HistoricalCase
        {
            CaseId = r["case_id"]?.ToString() ?? "",
            Ticker = r["ticker"]?.ToString() ?? "",
            Date = GetDateTimeOffset(r, "case_date"),
            MarketRegime = r["market_regime"]?.ToString() ?? "unknown",
            Facts = DeserializeList<MarketFact>(r["facts"]),
            Features = DeserializeList<MarketFeature>(r["features"]),
            Evidence = DeserializeList<MarketEvidence>(r["evidence"]),
            MarketThesis = Deserialize<MarketThesis>(r["market_thesis"]) ?? new(),
            Prediction = Deserialize<PredictionCandidate>(r["prediction"])!,
            Outcome = Deserialize<PredictionOutcome>(r["outcome"])!,
            MaximumFavorableExcursion = GetNullableDouble(r, "mfe"),
            MaximumAdverseExcursion = GetNullableDouble(r, "mae"),
            LessonsLearned = DeserializeStringList(r["lessons_learned"]),
            Concepts = DeserializeStringList(r["concepts"]),
            Tags = DeserializeStringList(r["tags"]),
        };
    }

    private static T? Deserialize<T>(JsonNode? node) where T : class
    {
        if (node is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(node.ToJsonString(), JsonOpts);
        }
        catch { return null; }
    }

    private static List<T> DeserializeList<T>(JsonNode? node)
    {
        if (node is null) return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(node.ToJsonString(), JsonOpts) ?? [];
        }
        catch { return []; }
    }

    private static List<string> DeserializeStringList(JsonNode? node)
    {
        if (node is not JsonArray arr) return [];
        return arr.Select(n => n?.ToString() ?? "").Where(s => s != "").ToList();
    }

    private static double? GetNullableDouble(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null || node.GetValueKind() == JsonValueKind.Null) return null;
        if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), out var parsed) ? parsed : null;
    }

    private static DateTimeOffset GetDateTimeOffset(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(node.ToString(), out var dt) ? dt : DateTimeOffset.MinValue;
    }
}
