using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// Orchestrates all <see cref="IDiscoveryProvider"/> instances to discover
/// tickers that deserve investigation.
///
/// The engine runs all providers, deduplicates events by ticker,
/// and creates or updates Research Assets idempotently. It never
/// generates predictions — it only feeds the Research Universe.
/// </summary>
public interface IDiscoveryEngine
{
    /// <summary>Run all configured discovery providers and process results.</summary>
    Task<DiscoveryRunResult> RunDiscoveryAsync();

    /// <summary>Run a single provider by ID (for testing/debugging).</summary>
    Task<DiscoveryProviderResult> RunProviderAsync(string providerId);
}
