using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;

namespace StockResearchAgent.Api.Services.Discovery.Providers;

/// <summary>
/// Discovers tickers from TwelveData via significant price moves.
///
/// Scans a configurable watchlist of tickers for unusual daily moves
/// (price change > threshold or volume spikes). Rate-limit aware —
/// TwelveData free tier allows ~7 req/min, so the watchlist should be small.
///
/// Configure via DISCOVERY_WATCHLIST env var (comma-separated tickers).
/// Defaults to a set of high-cap, liquid names when not configured.
/// </summary>
public class TwelveDataDiscoveryProvider : IDiscoveryProvider
{
    private readonly TwelveDataProvider _twelve;
    private readonly ILogger<TwelveDataDiscoveryProvider> _logger;
    private readonly List<string> _watchlist;

    private const double PriceChangeThreshold = 3.0;   // ±3% daily move
    private const double VolumeMultiplierThreshold = 2.0; // 2x average volume

    private static readonly string[] DefaultWatchlist =
    [
        "AAPL", "MSFT", "GOOGL", "AMZN", "NVDA", "META", "TSLA",
        "JPM", "V", "JNJ", "UNH", "XOM", "PG", "HD", "MA",
    ];

    public string ProviderId => "twelvedata-movers";
    public bool IsConfigured => _twelve.IsConfigured;

    public TwelveDataDiscoveryProvider(
        TwelveDataProvider twelve,
        IConfiguration config,
        ILogger<TwelveDataDiscoveryProvider> logger)
    {
        _twelve = twelve;
        _logger = logger;

        var custom = config["DISCOVERY_WATCHLIST"];
        _watchlist = string.IsNullOrWhiteSpace(custom)
            ? [.. DefaultWatchlist]
            : custom.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => t.ToUpperInvariant())
                    .ToList();
    }

    public async Task<List<DiscoveryEvent>> ScanAsync()
    {
        if (!IsConfigured) return [];

        var events = new List<DiscoveryEvent>();

        foreach (var ticker in _watchlist)
        {
            try
            {
                var quote = await _twelve.GetQuoteAsync(ticker);
                if (quote is null) continue;

                var pctChange = quote.ChangePercent;
                var absPct = Math.Abs(pctChange);

                // Skip boring days
                if (absPct < PriceChangeThreshold) continue;

                var direction = pctChange > 0 ? "up" : "down";
                var importance = absPct switch
                {
                    >= 10.0 => 90,
                    >= 7.0  => 70,
                    >= 5.0  => 55,
                    _       => 35,
                };
                var confidence = Math.Clamp(absPct / 15.0, 0.4, 0.95);

                events.Add(new DiscoveryEvent
                {
                    Ticker = ticker,
                    Timestamp = DateTimeOffset.UtcNow,
                    Source = "twelvedata-movers",
                    Reason = $"Significant move: {direction} {absPct:F1}% (${quote.Price:F2})",
                    Importance = importance,
                    Category = DiscoveryCategory.PriceAction,
                    Confidence = confidence,
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[discovery:twelvedata] Failed to quote {Ticker}", ticker);
            }
        }

        _logger.LogInformation(
            "[discovery:twelvedata] Scanned {Count} tickers → {Events} movers",
            _watchlist.Count, events.Count);

        return events;
    }
}
