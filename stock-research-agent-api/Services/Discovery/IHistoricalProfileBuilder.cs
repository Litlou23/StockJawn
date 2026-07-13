using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// Builds and maintains historical research profiles for stocks
/// in the Research Universe.
///
/// Profiles are created when a stock first enters the universe and
/// refreshed on a configurable schedule (default 90 days) or after
/// significant corporate events (earnings, major filings, etc.).
/// </summary>
public interface IHistoricalProfileBuilder
{
    /// <summary>Build and persist a historical profile for a ticker.
    /// Returns the existing profile if one already exists (use RefreshProfileAsync to force rebuild).
    /// Returns null if insufficient data is available.</summary>
    Task<HistoricalResearchProfile?> BuildProfileAsync(string ticker, string researchAssetId);

    /// <summary>Force-refresh an existing profile with current market data.
    /// Creates a new profile if none exists. Returns null if insufficient data.</summary>
    Task<HistoricalResearchProfile?> RefreshProfileAsync(string ticker, string researchAssetId, string reason);

    /// <summary>Check if a profile needs refresh and refresh it if so.
    /// Considers both the scheduled interval and whether the discovery event
    /// category is a corporate event trigger. Returns true if refreshed.</summary>
    Task<bool> RefreshIfNeededAsync(string ticker, string researchAssetId, DiscoveryCategory eventCategory);

    /// <summary>Get an existing profile for a ticker.</summary>
    Task<HistoricalResearchProfile?> GetProfileAsync(string ticker);

    /// <summary>Check if a profile already exists for a ticker.</summary>
    Task<bool> HasProfileAsync(string ticker);
}
