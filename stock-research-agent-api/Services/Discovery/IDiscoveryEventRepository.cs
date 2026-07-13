using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// Persistence layer for discovery events.
/// Events are write-heavy, read-light — mainly used for audit trails
/// and debugging which providers surfaced which tickers.
/// </summary>
public interface IDiscoveryEventRepository
{
    Task PersistEventsAsync(List<DiscoveryEvent> events);
    Task<List<DiscoveryEvent>> GetRecentAsync(int limit = 100);
    Task<List<DiscoveryEvent>> GetByTickerAsync(string ticker, int limit = 50);
}
