using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Discovery;

public class SupabaseDiscoveryEventRepository : IDiscoveryEventRepository
{
    private readonly SupabaseClient _db;

    public SupabaseDiscoveryEventRepository(SupabaseClient db)
    {
        _db = db;
    }

    public async Task PersistEventsAsync(List<DiscoveryEvent> events)
    {
        if (events.Count == 0) return;

        // Batch insert: one HTTP call instead of N
        var rows = events.Select(evt => new JsonObject
        {
            ["ticker"] = evt.Ticker,
            ["timestamp"] = evt.Timestamp.ToString("o"),
            ["source"] = evt.Source,
            ["reason"] = evt.Reason.Length > 1000 ? evt.Reason[..1000] : evt.Reason,
            ["importance"] = evt.Importance,
            ["category"] = evt.Category.ToString(),
            ["confidence"] = evt.Confidence,
        }).ToList();

        await _db.InsertAsync("discovery_events", rows, returnRows: false);
    }

    public async Task<List<DiscoveryEvent>> GetRecentAsync(int limit = 100)
    {
        var rows = await _db.SelectAsync("discovery_events", "", order: "timestamp.desc", limit: limit);
        return rows.Select(MapEvent).ToList();
    }

    public async Task<List<DiscoveryEvent>> GetByTickerAsync(string ticker, int limit = 50)
    {
        var filter = $"ticker=eq.{ticker.ToUpperInvariant()}";
        var rows = await _db.SelectAsync("discovery_events", filter, order: "timestamp.desc", limit: limit);
        return rows.Select(MapEvent).ToList();
    }

    private static DiscoveryEvent MapEvent(JsonObject row)
    {
        _ = Enum.TryParse<DiscoveryCategory>(row["category"]?.ToString(), out var category);

        return new DiscoveryEvent
        {
            Id = row["id"]?.ToString() ?? "",
            Ticker = row["ticker"]?.ToString() ?? "",
            Timestamp = DateTimeOffset.TryParse(row["timestamp"]?.ToString(), out var ts) ? ts : DateTimeOffset.UtcNow,
            Source = row["source"]?.ToString() ?? "",
            Reason = row["reason"]?.ToString() ?? "",
            Importance = int.TryParse(row["importance"]?.ToString(), out var imp) ? imp : 0,
            Category = category,
            Confidence = double.TryParse(row["confidence"]?.ToString(), out var conf) ? conf : 0.5,
        };
    }
}
