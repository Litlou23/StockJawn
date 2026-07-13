using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// Persistence for immutable research timeline events.
/// Append-only — events are never modified or deleted.
/// Think of it as Git history for a stock's research journey.
/// </summary>
public interface IResearchTimelineRepository
{
    /// <summary>Append a single timeline event.</summary>
    Task AppendAsync(ResearchTimelineEvent evt);

    /// <summary>Append multiple timeline events in one batch.</summary>
    Task AppendManyAsync(List<ResearchTimelineEvent> events);

    /// <summary>Get the full timeline for a ticker, most recent first.</summary>
    Task<List<ResearchTimelineEvent>> GetTimelineAsync(string ticker, int limit = 100);

    /// <summary>Get timeline events across all tickers since a timestamp.</summary>
    Task<List<ResearchTimelineEvent>> GetRecentAsync(DateTimeOffset since, int limit = 200);

    /// <summary>Get timeline events by type for a ticker.</summary>
    Task<List<ResearchTimelineEvent>> GetByTypeAsync(
        string ticker, TimelineEventType eventType, int limit = 50);
}
