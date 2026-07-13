using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.UniverseDiscovery;

namespace StockResearchAgent.Api.Services.Discovery.Providers;

/// <summary>
/// Discovers tickers from Finnhub news and upcoming earnings.
/// Emits two event categories: News and Earnings.
///
/// News: scans general market news for tickers mentioned in headlines.
/// Earnings: surfaces companies reporting in the next 7 days.
/// </summary>
public class FinnhubDiscoveryProvider : IDiscoveryProvider
{
    private readonly FinnhubProvider _finnhub;
    private readonly ILogger<FinnhubDiscoveryProvider> _logger;

    public string ProviderId => "finnhub";
    public bool IsConfigured => _finnhub.IsConfigured;

    public FinnhubDiscoveryProvider(
        FinnhubProvider finnhub,
        ILogger<FinnhubDiscoveryProvider> logger)
    {
        _finnhub = finnhub;
        _logger = logger;
    }

    public async Task<List<DiscoveryEvent>> ScanAsync()
    {
        if (!IsConfigured) return [];

        var events = new List<DiscoveryEvent>();

        // ── News-driven discoveries ─────────────────────────────
        try
        {
            var articles = await _finnhub.GetMarketNewsAsync("general", 50);
            var tickerGroups = articles
                .Where(a => a.RelatedTickers.Count > 0)
                .SelectMany(a => a.RelatedTickers.Select(t => (Ticker: t.ToUpperInvariant(), Article: a)))
                .GroupBy(x => x.Ticker)
                .Where(g => g.Key.Length >= 1 && g.Key.Length <= 5);

            foreach (var group in tickerGroups)
            {
                var articleCount = group.Count();
                var latest = group.OrderByDescending(x => x.Article.Datetime).First().Article;
                var importance = Math.Clamp(articleCount * 20, 10, 80);
                var confidence = Math.Clamp(articleCount * 0.2, 0.3, 0.9);

                events.Add(new DiscoveryEvent
                {
                    Ticker = group.Key,
                    Timestamp = latest.Datetime,
                    Source = "finnhub-news",
                    Reason = articleCount == 1
                        ? $"News: {latest.Headline}"
                        : $"{articleCount} recent news articles — latest: {latest.Headline}",
                    Importance = importance,
                    Category = DiscoveryCategory.News,
                    Confidence = confidence,
                });
            }

            _logger.LogInformation(
                "[discovery:finnhub] News scan: {Articles} articles → {Events} ticker events",
                articles.Count, events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[discovery:finnhub] News scan failed");
        }

        // ── Earnings-driven discoveries ─────────────────────────
        try
        {
            var earnings = await _finnhub.GetUpcomingEarningsAsync(7);
            foreach (var entry in earnings)
            {
                if (string.IsNullOrEmpty(entry.Ticker) || entry.Ticker.Length > 5) continue;

                var daysUntil = (DateTimeOffset.Parse(entry.Date) - DateTimeOffset.UtcNow).Days;
                var importance = daysUntil switch
                {
                    <= 1 => 70,
                    <= 3 => 50,
                    _ => 30,
                };

                events.Add(new DiscoveryEvent
                {
                    Ticker = entry.Ticker.ToUpperInvariant(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Source = "finnhub-earnings",
                    Reason = $"Earnings {(entry.Hour == "bmo" ? "before market open" : "after close")} on {entry.Date}" +
                             (entry.EstimateEps is not null ? $" (est EPS: {entry.EstimateEps:F2})" : ""),
                    Importance = importance,
                    Category = DiscoveryCategory.Earnings,
                    Confidence = 0.9, // Earnings dates are high-confidence facts
                });
            }

            _logger.LogInformation(
                "[discovery:finnhub] Earnings scan: {Count} upcoming reports", earnings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[discovery:finnhub] Earnings scan failed");
        }

        return events;
    }
}
