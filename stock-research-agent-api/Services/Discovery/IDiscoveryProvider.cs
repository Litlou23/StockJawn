using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// A pluggable source of stock discovery events.
///
/// Each provider scans one external data source and emits
/// <see cref="DiscoveryEvent"/> instances for tickers that deserve
/// investigation. Providers are stateless and idempotent — calling
/// <see cref="ScanAsync"/> multiple times should not produce duplicate
/// side effects.
///
/// Current providers:
///   - FinnhubDiscoveryProvider (news + earnings)
///   - TwelveDataDiscoveryProvider (price movers + volume spikes)
///   - CongressDiscoveryProvider (congressional trading activity)
///   - MarketIntelligenceDiscoveryProvider (thesis-driven signals)
///
/// Future providers (implement this interface to add):
///   - SEC filing scanner
///   - Press release scanner
///   - Insider buying tracker
///   - Analyst upgrade/downgrade feed
///   - Options flow detector
///   - Earnings calendar enricher
///   - FDA event tracker
/// </summary>
public interface IDiscoveryProvider
{
    /// <summary>Unique identifier for this provider (e.g. "finnhub-news").</summary>
    string ProviderId { get; }

    /// <summary>Whether this provider is configured and ready to scan.</summary>
    bool IsConfigured { get; }

    /// <summary>Scan the source and return discovery events.
    /// Must be idempotent and safe to call repeatedly.</summary>
    Task<List<DiscoveryEvent>> ScanAsync();
}
