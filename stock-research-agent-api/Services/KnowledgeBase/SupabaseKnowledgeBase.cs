using System.Text.Json;
using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.KnowledgeBase;

/// <summary>
/// Supabase-backed implementation of <see cref="IKnowledgeBase"/>.
/// Persists knowledge entries across restarts.
/// Upserts merge new evidence via weighted averaging.
/// </summary>
public class SupabaseKnowledgeBase : IKnowledgeBase
{
    private const string Table = "knowledge_entries";
    private readonly SupabaseClient _db;
    private readonly ILogger<SupabaseKnowledgeBase> _logger;

    public SupabaseKnowledgeBase(
        SupabaseClient db,
        ILogger<SupabaseKnowledgeBase> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordAsync(KnowledgeEntry entry)
    {
        // Try to fetch existing entry for merge
        var existing = await _db.SelectSingleAsync(Table, $"key=eq.{Uri.EscapeDataString(entry.Key)}");

        KnowledgeEntry merged;
        if (existing is not null)
        {
            var existingEntry = MapEntry(existing);
            merged = MergeEntry(existingEntry, entry);
        }
        else
        {
            merged = entry;
        }

        var row = new
        {
            key = merged.Key,
            category = merged.Category.ToString(),
            statement = merged.Statement,
            conditions = merged.Conditions
                .Select(c => new { type = c.Type.ToString(), value = c.Value }).ToArray(),
            sample_size = merged.SampleSize,
            win_rate = merged.WinRate,
            average_return = merged.AverageReturn,
            confidence = merged.Confidence,
            first_observed = merged.FirstObserved.ToString("o"),
            last_updated = DateTimeOffset.UtcNow.ToString("o"),
            confirmation_count = merged.ConfirmationCount,
        };

        var ok = await _db.UpsertAsync(Table, row, "key");
        if (!ok)
            _logger.LogWarning("[knowledge-base] Upsert failed for key {Key}", entry.Key);
    }

    public async Task<List<KnowledgeEntry>> QueryAsync(KnowledgeBaseQuery query)
    {
        var filters = new List<string>();

        if (query.Category is not null)
            filters.Add($"category=eq.{query.Category}");

        if (query.MinConfidence > 0)
            filters.Add($"confidence=gte.{query.MinConfidence}");

        if (query.SignalName is not null)
            filters.Add($"key=ilike.*{Uri.EscapeDataString(query.SignalName)}*");

        var filter = filters.Count > 0 ? string.Join("&", filters) : null;
        var rows = await _db.SelectAsync(Table, filter, order: "confidence.desc", limit: query.Limit);
        var results = rows.Select(MapEntry).ToList();

        // Apply in-memory filters that are hard to express in PostgREST
        if (query.Regime is not null)
        {
            results = results.Where(e =>
                e.Conditions.Any(c =>
                    c.Type == LearningConditionType.MarketRegime &&
                    c.Value.Equals(query.Regime.ToString(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        if (query.Sector is not null)
        {
            results = results.Where(e =>
                e.Conditions.Any(c =>
                    c.Type == LearningConditionType.Sector &&
                    c.Value.Equals(query.Sector, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return results;
    }

    public async Task<List<KnowledgeEntry>> GetAllAsync(KnowledgeCategory? category = null)
    {
        var filter = category is not null ? $"category=eq.{category}" : null;
        var rows = await _db.SelectAsync(Table, filter, order: "confidence.desc");
        return rows.Select(MapEntry).ToList();
    }

    public async Task<List<KnowledgeEntry>> GetStrongestAsync(int limit = 20)
    {
        var rows = await _db.SelectAsync(Table, order: "confidence.desc,sample_size.desc", limit: limit);
        return rows.Select(MapEntry).ToList();
    }

    public async Task<KnowledgeBaseStats> GetStatsAsync()
    {
        var all = await _db.SelectAsync(Table);
        var entries = all.Select(MapEntry).ToList();

        return new KnowledgeBaseStats
        {
            TotalEntries = entries.Count,
            HighConfidenceEntries = entries.Count(e => e.Confidence >= 0.7),
            EntriesByCategory = entries.GroupBy(e => e.Category)
                .ToDictionary(g => g.Key, g => g.Count()),
            Summary = $"{entries.Count} knowledge entries, {entries.Count(e => e.Confidence >= 0.7)} high-confidence.",
        };
    }

    // ══════════════════════════════════════════════════════════════
    // Merge
    // ══════════════════════════════════════════════════════════════

    private static KnowledgeEntry MergeEntry(KnowledgeEntry existing, KnowledgeEntry incoming)
    {
        var totalN = existing.SampleSize + incoming.SampleSize;
        if (totalN == 0) totalN = 1;

        return existing with
        {
            SampleSize = totalN,
            WinRate = Math.Round(
                ((existing.WinRate * existing.SampleSize) + (incoming.WinRate * incoming.SampleSize)) / totalN, 4),
            AverageReturn = Math.Round(
                ((existing.AverageReturn * existing.SampleSize) + (incoming.AverageReturn * incoming.SampleSize)) / totalN, 4),
            Confidence = StatisticalConfidence.FromSampleSize(totalN),
            LastUpdated = DateTimeOffset.UtcNow,
            ConfirmationCount = existing.ConfirmationCount + 1,
        };
    }

    // ══════════════════════════════════════════════════════════════
    // Mapping
    // ══════════════════════════════════════════════════════════════

    private static KnowledgeEntry MapEntry(JsonObject r) => new()
    {
        Key = r["key"]?.ToString() ?? "",
        Category = Enum.TryParse<KnowledgeCategory>(r["category"]?.ToString(), out var cat)
            ? cat : KnowledgeCategory.General,
        Statement = r["statement"]?.ToString() ?? "",
        Conditions = ParseConditions(r["conditions"]),
        SampleSize = GetInt(r, "sample_size"),
        WinRate = GetDouble(r, "win_rate"),
        AverageReturn = GetDouble(r, "average_return"),
        Confidence = GetDouble(r, "confidence"),
        FirstObserved = GetDateTimeOffset(r, "first_observed"),
        LastUpdated = GetDateTimeOffset(r, "last_updated"),
        ConfirmationCount = GetInt(r, "confirmation_count"),
    };

    private static List<LearningCondition> ParseConditions(JsonNode? node)
    {
        if (node is not JsonArray arr) return [];
        var results = new List<LearningCondition>();
        foreach (var item in arr)
        {
            if (item is not JsonObject obj) continue;
            var typeStr = obj["type"]?.ToString() ?? "";
            if (Enum.TryParse<LearningConditionType>(typeStr, out var condType))
            {
                results.Add(new LearningCondition
                {
                    Type = condType,
                    Value = obj["value"]?.ToString() ?? "",
                });
            }
        }
        return results;
    }

    private static int GetInt(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return 0;
        if (node is JsonValue jv && jv.TryGetValue<int>(out var i)) return i;
        return int.TryParse(node.ToString(), out var parsed) ? parsed : 0;
    }

    private static double GetDouble(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return 0;
        if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
        return double.TryParse(node.ToString(), out var parsed) ? parsed : 0;
    }

    private static DateTimeOffset GetDateTimeOffset(JsonObject r, string key)
    {
        var node = r[key];
        if (node is null) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(node.ToString(), out var dt) ? dt : DateTimeOffset.MinValue;
    }
}
