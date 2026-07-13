using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.StrategyDiscovery;

/// <summary>
/// Persistence for strategy discovery observations and discovered strategies.
/// Separates storage from the discovery algorithm so in-memory and Supabase
/// implementations are swappable.
/// </summary>
public interface IStrategyDiscoveryRepository
{
    Task StoreObservationAsync(StrategyObservationInput observation);
    Task<List<StrategyObservationInput>> GetAllObservationsAsync();
    Task StoreStrategyAsync(DiscoveredStrategy strategy);
    Task<List<DiscoveredStrategy>> GetAllStrategiesAsync();
}
