using System.Collections.Concurrent;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.MarketData;

/// <summary>
/// Facade over TwelveDataProvider with a 5-minute in-memory cache.
/// Returns null when API key is missing -- never produces fake data.
/// </summary>
public class MarketDataService
{
    private readonly TwelveDataProvider _provider;
    private readonly ILogger<MarketDataService> _logger;

    // Simple in-memory cache: key -> (value, expiry)
    private static readonly ConcurrentDictionary<string, (object Value, DateTimeOffset Expiry)> Cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public MarketDataService(TwelveDataProvider provider, ILogger<MarketDataService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public bool IsConfigured => _provider.IsConfigured;

    public async Task<MarketSnapshotQuote?> GetQuoteAsync(string ticker)
    {
        return await GetCachedAsync($"quote:{ticker}",
            () => _provider.GetQuoteAsync(ticker));
    }

    public async Task<List<MarketSnapshotBar>> GetRecentBarsAsync(string ticker, int count = 20)
    {
        return await GetCachedAsync<List<MarketSnapshotBar>>($"bars:{ticker}:{count}",
            async () => await _provider.GetRecentBarsAsync(ticker, count)) ?? [];
    }

    public async Task<MarketSnapshotTechnical?> GetTechnicalContextAsync(string ticker)
    {
        var bars = await GetRecentBarsAsync(ticker);
        return _provider.ComputeTechnicalContext(bars);
    }

    public async Task<object> GetProviderHealthAsync()
    {
        return await _provider.GetProviderHealthAsync();
    }

    /// <summary>
    /// Gathers all market data context for a ticker (quote, bars, technical).
    /// Returns with warnings if data is unavailable -- never fakes anything.
    /// </summary>
    public async Task<(MarketSnapshotQuote? Quote, List<MarketSnapshotBar> Bars, MarketSnapshotTechnical? Technical, List<string> Warnings)>
        GetFullContextAsync(string ticker)
    {
        var warnings = new List<string>();

        if (!_provider.IsConfigured)
        {
            warnings.Add("TWELVE_DATA_API_KEY not configured -- no market data available");
            return (null, [], null, warnings);
        }

        var quote = await GetQuoteAsync(ticker);
        var bars = await GetRecentBarsAsync(ticker);
        var technical = _provider.ComputeTechnicalContext(bars);

        if (quote is null) warnings.Add($"Could not fetch quote for {ticker}");
        if (bars.Count == 0) warnings.Add($"Could not fetch price bars for {ticker}");
        if (technical is null) warnings.Add($"Insufficient data for technical context on {ticker}");

        return (quote, bars, technical, warnings);
    }

    // -----------------------------------------------------------------------
    // Cache helper
    // -----------------------------------------------------------------------

    private async Task<T?> GetCachedAsync<T>(string key, Func<Task<T?>> factory) where T : class
    {
        if (Cache.TryGetValue(key, out var entry) && entry.Expiry > DateTimeOffset.UtcNow)
            return (T?)entry.Value;

        var value = await factory();
        if (value is not null)
            Cache[key] = (value, DateTimeOffset.UtcNow + CacheTtl);

        return value;
    }
}
