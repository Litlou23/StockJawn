using System.Xml.Linq;

namespace StockResearchAgent.Api.Services.UniverseDiscovery;

/// <summary>
/// Fetches and parses RSS/Atom feeds from financial news sources.
/// Extracts ticker mentions from headlines and summaries.
/// No external NuGet dependency — uses System.Xml.Linq directly.
/// </summary>
public class RssFeedService
{
    private readonly HttpClient _http;
    private readonly ILogger<RssFeedService> _logger;

    // Same feeds as the Next.js sourceRegistry
    private static readonly FeedSource[] Feeds =
    [
        new("yahoo-finance", "Yahoo Finance", "https://finance.yahoo.com/news/rssindex", 0.75),
        new("cnbc-top", "CNBC Top News", "https://www.cnbc.com/id/100003114/device/rss/rss.html", 0.80),
        new("marketwatch-top", "MarketWatch", "http://feeds.marketwatch.com/marketwatch/topstories/", 0.75),
        new("cnbc-tech", "CNBC Technology", "https://www.cnbc.com/id/19854910/device/rss/rss.html", 0.78),
        new("investing-com", "Investing.com", "https://www.investing.com/rss/news.rss", 0.60),
    ];

    public RssFeedService(ILogger<RssFeedService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; StockResearchAgent/1.0)");
    }

    public record FeedSource(string Id, string Name, string Url, double Reliability);

    public record FeedItem(
        string SourceId,
        string SourceName,
        string Title,
        string Summary,
        string Url,
        DateTimeOffset PublishedAt,
        double SourceReliability);

    public record RssScanResult(
        List<FeedItem> Items,
        Dictionary<string, TickerExtractor.TickerMention> TickerMentions,
        List<string> Errors);

    /// <summary>
    /// Fetch all configured RSS feeds and extract ticker mentions.
    /// Each feed is fetched independently — one failure doesn't block others.
    /// </summary>
    public async Task<RssScanResult> ScanFeedsAsync()
    {
        _logger.LogInformation("[rss] Scanning {Count} RSS feeds for ticker mentions...", Feeds.Length);

        var allItems = new List<FeedItem>();
        var errors = new List<string>();
        var aggregatedTickers = new Dictionary<string, TickerExtractor.TickerMention>(StringComparer.OrdinalIgnoreCase);

        var tasks = Feeds.Select(async feed =>
        {
            try
            {
                var items = await FetchFeedAsync(feed);
                return (Feed: feed, Items: items, Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (Feed: feed, Items: new List<FeedItem>(), Error: $"{feed.Name}: {ex.Message}");
            }
        });

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            if (result.Error is not null)
            {
                errors.Add(result.Error);
                _logger.LogWarning("[rss] Feed failed: {Error}", result.Error);
                continue;
            }

            allItems.AddRange(result.Items);
            _logger.LogInformation("[rss] {Feed}: {Count} items", result.Feed.Name, result.Items.Count);

            // Extract tickers from each item
            foreach (var item in result.Items)
            {
                var extracted = TickerExtractor.Extract($"{item.Title} {item.Summary}");
                foreach (var (ticker, mention) in extracted.Tickers)
                {
                    if (aggregatedTickers.TryGetValue(ticker, out var existing))
                    {
                        aggregatedTickers[ticker] = existing with
                        {
                            MentionCount = existing.MentionCount + mention.MentionCount,
                            FromCashtag = existing.FromCashtag || mention.FromCashtag,
                            FromCompanyName = existing.FromCompanyName || mention.FromCompanyName,
                            FromBareTicker = existing.FromBareTicker || mention.FromBareTicker,
                        };
                    }
                    else
                    {
                        aggregatedTickers[ticker] = mention;
                    }
                }
            }
        }

        _logger.LogInformation("[rss] Scan complete: {Items} items, {Tickers} unique tickers, {Errors} feed errors",
            allItems.Count, aggregatedTickers.Count, errors.Count);

        return new RssScanResult(allItems, aggregatedTickers, errors);
    }

    private async Task<List<FeedItem>> FetchFeedAsync(FeedSource feed)
    {
        var xml = await _http.GetStringAsync(feed.Url);
        var doc = XDocument.Parse(xml);
        var items = new List<FeedItem>();

        // RSS 2.0 format
        var rssItems = doc.Descendants("item");
        foreach (var item in rssItems)
        {
            var title = item.Element("title")?.Value ?? "(untitled)";
            var description = item.Element("description")?.Value ?? "";
            var link = item.Element("link")?.Value ?? feed.Url;
            var pubDate = item.Element("pubDate")?.Value;

            // Strip HTML from description
            description = StripHtml(description);
            if (description.Length > 500) description = description[..500];

            var publishedAt = DateTimeOffset.TryParse(pubDate, out var dt)
                ? dt : DateTimeOffset.UtcNow;

            items.Add(new FeedItem(feed.Id, feed.Name, title, description, link, publishedAt, feed.Reliability));
        }

        // Atom format fallback
        if (!items.Any())
        {
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var atomEntries = doc.Descendants(atom + "entry");
            foreach (var entry in atomEntries)
            {
                var title = entry.Element(atom + "title")?.Value ?? "(untitled)";
                var summary = entry.Element(atom + "summary")?.Value ?? entry.Element(atom + "content")?.Value ?? "";
                var link = entry.Element(atom + "link")?.Attribute("href")?.Value ?? feed.Url;
                var updated = entry.Element(atom + "updated")?.Value ?? entry.Element(atom + "published")?.Value;

                summary = StripHtml(summary);
                if (summary.Length > 500) summary = summary[..500];

                var publishedAt = DateTimeOffset.TryParse(updated, out var dt)
                    ? dt : DateTimeOffset.UtcNow;

                items.Add(new FeedItem(feed.Id, feed.Name, title, summary, link, publishedAt, feed.Reliability));
            }
        }

        return items;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        // Simple regex-free HTML strip
        var inTag = false;
        var result = new System.Text.StringBuilder(html.Length);
        foreach (var c in html)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) result.Append(c);
        }
        return result.ToString().Trim();
    }
}
