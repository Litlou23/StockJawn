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
    private static readonly TimeSpan LongCacheTtl = TimeSpan.FromHours(12); // fundamentals, EMA — change slowly

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

    /// <summary>
    /// Returns a quote, falling back to the latest daily bar's close price
    /// when the live quote API is unavailable (quota exhausted, after-hours
    /// errors, etc.). This prevents EOD evaluation from skipping every
    /// prediction when the daily API budget has been spent.
    /// </summary>
    public async Task<MarketSnapshotQuote?> GetQuoteWithFallbackAsync(string ticker)
    {
        var quote = await GetQuoteAsync(ticker);
        if (quote is not null) return quote;

        var bars = await GetRecentBarsAsync(ticker, 2);
        if (bars.Count == 0) return null;

        var latest = bars[0]; // most recent bar
        _logger.LogInformation("[market-data] {Ticker}: using bar fallback (close={Close}, date={Date}) — live quote unavailable",
            ticker, latest.Close, latest.Date);

        return new MarketSnapshotQuote
        {
            Price = latest.Close,
            Open = latest.Open,
            High = latest.High,
            Low = latest.Low,
            PreviousClose = bars.Count > 1 ? bars[1].Close : latest.Open,
            Volume = latest.Volume,
            Change = latest.Close - latest.Open,
            ChangePercent = latest.Open > 0 ? ((latest.Close - latest.Open) / latest.Open) * 100 : 0,
            Timestamp = latest.Date,
        };
    }

    public async Task<List<MarketSnapshotBar>> GetRecentBarsAsync(string ticker, int count = 20)
    {
        return await GetCachedAsync<List<MarketSnapshotBar>>($"bars:{ticker}:{count}",
            async () => await _provider.GetRecentBarsAsync(ticker, count)) ?? [];
    }

    /// <summary>
    /// Fetch historical daily candles for a date range. No caching — used for bulk backtest data loading.
    /// </summary>
    public async Task<List<MarketSnapshotBar>> GetHistoricalBarsAsync(
        string ticker, DateOnly startDate, DateOnly endDate)
    {
        return await _provider.GetHistoricalBarsAsync(ticker, startDate, endDate);
    }

    public async Task<MarketSnapshotTechnical?> GetTechnicalContextAsync(string ticker)
    {
        var bars = await GetRecentBarsAsync(ticker);
        return _provider.ComputeTechnicalContext(bars);
    }

    // -----------------------------------------------------------------------
    // API-sourced technical indicators (new signals not computable from bars)
    // -----------------------------------------------------------------------

    public async Task<(double MacdLine, double Signal, double Histogram)?> GetMacdAsync(string ticker)
    {
        return await GetCachedValueAsync($"macd:{ticker}",
            () => _provider.GetMacdAsync(ticker));
    }

    public async Task<(double? Ema12, double? Ema26, double? Ema50)> GetEmaAsync(string ticker)
    {
        // Cache the tuple as a boxed object — EMA changes daily, not intraday
        var cached = await GetCachedAsync($"ema:{ticker}",
            async () =>
            {
                var result = await _provider.GetEmaAsync(ticker);
                return new EmaResult { Ema12 = result.Ema12, Ema26 = result.Ema26, Ema50 = result.Ema50 };
            },
            LongCacheTtl);
        return cached is not null ? (cached.Ema12, cached.Ema26, cached.Ema50) : (null, null, null);
    }

    public async Task<double?> GetEma21Async(string ticker)
    {
        return await GetCachedValueAsync($"ema21:{ticker}",
            () => _provider.GetEma21Async(ticker));
    }

    // -----------------------------------------------------------------------
    // Fundamentals
    // -----------------------------------------------------------------------

    public async Task<FundamentalsContext?> GetFundamentalsAsync(string ticker)
    {
        return await GetCachedAsync($"fundamentals:{ticker}",
            () => _provider.GetFundamentalsAsync(ticker),
            LongCacheTtl);
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

    private async Task<T?> GetCachedAsync<T>(string key, Func<Task<T?>> factory, TimeSpan? ttl = null) where T : class
    {
        if (Cache.TryGetValue(key, out var entry) && entry.Expiry > DateTimeOffset.UtcNow)
            return (T?)entry.Value;

        var value = await factory();
        if (value is not null)
            Cache[key] = (value, DateTimeOffset.UtcNow + (ttl ?? CacheTtl));

        return value;
    }

    /// <summary>Cache helper for nullable value types (tuples, doubles).</summary>
    private async Task<T?> GetCachedValueAsync<T>(string key, Func<Task<T?>> factory) where T : struct
    {
        if (Cache.TryGetValue(key, out var entry) && entry.Expiry > DateTimeOffset.UtcNow)
            return (T)entry.Value;

        var value = await factory();
        if (value is not null)
            Cache[key] = (value, DateTimeOffset.UtcNow + CacheTtl);

        return value;
    }

    /// <summary>Wrapper to cache EMA results as a reference type.</summary>
    private class EmaResult
    {
        public double? Ema12 { get; init; }
        public double? Ema26 { get; init; }
        public double? Ema50 { get; init; }
    }
}
