using System.Text.Json.Nodes;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// Supabase-backed repository for immutable research timeline events.
/// Table: research_timeline_events
/// </summary>
public class SupabaseResearchTimelineRepository : IResearchTimelineRepository
{
    private readonly SupabaseClient _db;
    private const string Table = "research_timeline_events";

    public SupabaseResearchTimelineRepository(SupabaseClient db) => _db = db;

    public async Task AppendAsync(ResearchTimelineEvent evt)
    {
        await AppendManyAsync([evt]);
    }

    public async Task AppendManyAsync(List<ResearchTimelineEvent> events)
    {
        if (events.Count == 0) return;

        var rows = events.Select(evt => new JsonObject
        {
            ["id"] = string.IsNullOrEmpty(evt.Id) ? Guid.NewGuid().ToString() : evt.Id,
            ["ticker"] = evt.Ticker,
            ["timestamp"] = evt.Timestamp.ToString("o"),
            ["event_type"] = evt.EventType.ToString(),
            ["description"] = TruncateString(evt.Description, 2000),
            ["source"] = evt.Source,
            ["related_entity_id"] = evt.RelatedEntityId,
            ["related_entity_type"] = evt.RelatedEntityType,
            ["interest_score_snapshot"] = evt.InterestScoreSnapshot,
            ["research_state_snapshot"] = evt.ResearchStateSnapshot,
            ["thesis_snapshot"] = TruncateString(evt.ThesisSnapshot, 1000),
        }).ToList();

        await _db.InsertAsync(Table, rows, returnRows: false);
    }

    public async Task<List<ResearchTimelineEvent>> GetTimelineAsync(string ticker, int limit = 100)
    {
        var filter = $"ticker=eq.{ticker.ToUpperInvariant()}";
        var rows = await _db.SelectAsync(Table, filter, order: "timestamp.desc", limit: limit);
        return rows.Select(MapEvent).ToList();
    }

    public async Task<List<ResearchTimelineEvent>> GetRecentAsync(DateTimeOffset since, int limit = 200)
    {
        var filter = $"timestamp=gte.{since:o}";
        var rows = await _db.SelectAsync(Table, filter, order: "timestamp.desc", limit: limit);
        return rows.Select(MapEvent).ToList();
    }

    public async Task<List<ResearchTimelineEvent>> GetByTypeAsync(
        string ticker, TimelineEventType eventType, int limit = 50)
    {
        var filter = $"ticker=eq.{ticker.ToUpperInvariant()}&event_type=eq.{eventType}";
        var rows = await _db.SelectAsync(Table, filter, order: "timestamp.desc", limit: limit);
        return rows.Select(MapEvent).ToList();
    }

    private static ResearchTimelineEvent MapEvent(JsonObject row)
    {
        _ = Enum.TryParse<TimelineEventType>(row["event_type"]?.ToString(), out var eventType);

        return new ResearchTimelineEvent
        {
            Id = row["id"]?.ToString() ?? "",
            Ticker = row["ticker"]?.ToString() ?? "",
            Timestamp = DateTimeOffset.TryParse(row["timestamp"]?.ToString(), out var ts)
                ? ts : DateTimeOffset.UtcNow,
            EventType = eventType,
            Description = row["description"]?.ToString() ?? "",
            Source = row["source"]?.ToString() ?? "",
            RelatedEntityId = row["related_entity_id"]?.ToString(),
            RelatedEntityType = row["related_entity_type"]?.ToString(),
            InterestScoreSnapshot = int.TryParse(row["interest_score_snapshot"]?.ToString(), out var score)
                ? score : null,
            ResearchStateSnapshot = row["research_state_snapshot"]?.ToString(),
            ThesisSnapshot = row["thesis_snapshot"]?.ToString(),
            CreatedAt = DateTimeOffset.TryParse(row["created_at"]?.ToString(), out var ca)
                ? ca : DateTimeOffset.UtcNow,
        };
    }

    private static string? TruncateString(string? s, int max)
        => s is null ? null : s.Length <= max ? s : s[..max];
}
