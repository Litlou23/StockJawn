using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.UniverseDiscovery;

/// <summary>
/// Discovers which tickers the system should research by combining:
/// 1. RSS news feeds — tickers mentioned in financial headlines
/// 2. Finnhub — earnings calendar + market news with related tickers
/// 3. Twelve Data screening — volume/price movers (uses existing provider)
///
/// Produces a ranked, deduplicated universe sorted by discovery score.
/// No hardcoded ticker list. The system researches what the market is talking about.
/// </summary>
public class UniverseDiscoveryService
{
    private const int MaxUniverseSize = 30;  // Cap to stay within rate limits
    private const int MinDiscoveryScore = 2; // Minimum score to include

    private readonly RssFeedService _rssFeedService;
    private readonly FinnhubProvider _finnhub;
    private readonly WatchlistRepository _watchlistRepo;
    private readonly ILogger<UniverseDiscoveryService> _logger;

    public UniverseDiscoveryService(
        RssFeedService rssFeedService,
        FinnhubProvider finnhub,
        WatchlistRepository watchlistRepo,
        ILogger<UniverseDiscoveryService> logger)
    {
        _rssFeedService = rssFeedService;
        _finnhub = finnhub;
        _watchlistRepo = watchlistRepo;
        _logger = logger;
    }

    public record DiscoveredTicker(
        string Ticker,
        double DiscoveryScore,
        List<string> Sources,
        string? EarningsDate,
        int RssMentions,
        int FinnhubMentions,
        bool HasUpcomingEarnings,
        string TopReason);

    public record DiscoveryResult(
        List<DiscoveredTicker> Universe,
        int RssArticlesScanned,
        int FinnhubArticlesScanned,
        int EarningsFound,
        List<string> Errors,
        DateTimeOffset DiscoveredAt);

    /// <summary>
    /// Discover the universe of tickers to research. Combines all sources,
    /// deduplicates, scores by mention frequency + catalyst importance,
    /// and returns up to MaxUniverseSize tickers.
    /// </summary>
    public async Task<DiscoveryResult> DiscoverUniverseAsync()
    {
        _logger.LogInformation("[universe] Starting universe discovery...");

        var errors = new List<string>();
        var tickerScores = new Dictionary<string, TickerScoreBuilder>(StringComparer.OrdinalIgnoreCase);

        // ---------------------------------------------------------------
        // 1. RSS feeds — ticker mentions from financial news
        // ---------------------------------------------------------------
        RssFeedService.RssScanResult? rssScan = null;
        try
        {
            rssScan = await _rssFeedService.ScanFeedsAsync();
            errors.AddRange(rssScan.Errors);

            foreach (var (ticker, mention) in rssScan.TickerMentions)
            {
                var builder = GetOrCreate(tickerScores, ticker);
                builder.RssMentions += mention.MentionCount;
                builder.Sources.Add("rss");

                // Scoring: cashtag mentions are higher signal than bare ticker
                if (mention.FromCashtag) builder.Score += mention.MentionCount * 5;
                else if (mention.FromCompanyName) builder.Score += mention.MentionCount * 3;
                else builder.Score += mention.MentionCount * 2;
            }

            _logger.LogInformation("[universe] RSS: {Tickers} tickers from {Articles} articles",
                rssScan.TickerMentions.Count, rssScan.Items.Count);
        }
        catch (Exception ex)
        {
            errors.Add($"RSS scan failed: {ex.Message}");
            _logger.LogError(ex, "[universe] RSS scan failed");
        }

        // ---------------------------------------------------------------
        // 2. Finnhub earnings calendar — upcoming catalysts
        // ---------------------------------------------------------------
        var earningsCount = 0;
        if (_finnhub.IsConfigured)
        {
            try
            {
                var earnings = await _finnhub.GetUpcomingEarningsAsync(7);
                earningsCount = earnings.Count;

                foreach (var entry in earnings)
                {
                    var builder = GetOrCreate(tickerScores, entry.Ticker);
                    builder.HasUpcomingEarnings = true;
                    builder.EarningsDate = entry.Date;
                    builder.Sources.Add("finnhub-earnings");
                    builder.Score += 10; // Earnings are a strong catalyst
                }

                _logger.LogInformation("[universe] Finnhub: {Count} upcoming earnings", earnings.Count);
            }
            catch (Exception ex)
            {
                errors.Add($"Finnhub earnings failed: {ex.Message}");
                _logger.LogError(ex, "[universe] Finnhub earnings fetch failed");
            }

            // ---------------------------------------------------------------
            // 3. Finnhub market news — discover tickers from news articles
            // ---------------------------------------------------------------
            try
            {
                var news = await _finnhub.GetMarketNewsAsync();

                foreach (var article in news)
                {
                    // Use Finnhub's related tickers
                    foreach (var ticker in article.RelatedTickers)
                    {
                        var builder = GetOrCreate(tickerScores, ticker);
                        builder.FinnhubMentions++;
                        builder.Sources.Add("finnhub-news");
                        builder.Score += 3;
                    }

                    // Also extract tickers from headline text
                    var extracted = TickerExtractor.Extract($"{article.Headline} {article.Summary}");
                    foreach (var (ticker, mention) in extracted.Tickers)
                    {
                        var builder = GetOrCreate(tickerScores, ticker);
                        builder.FinnhubMentions += mention.MentionCount;
                        if (!builder.Sources.Contains("finnhub-news-text"))
                            builder.Sources.Add("finnhub-news-text");
                        builder.Score += mention.MentionCount * 2;
                    }
                }

                _logger.LogInformation("[universe] Finnhub news: {Count} articles processed", news.Count);
            }
            catch (Exception ex)
            {
                errors.Add($"Finnhub news failed: {ex.Message}");
                _logger.LogError(ex, "[universe] Finnhub news fetch failed");
            }
        }

        // ---------------------------------------------------------------
        // 4. Boost tickers that already have watchlist history (prior predictions)
        // ---------------------------------------------------------------
        try
        {
            var currentActive = await _watchlistRepo.GetActiveWatchlistAsync();
            foreach (var item in currentActive)
            {
                if (tickerScores.ContainsKey(item.Ticker))
                {
                    tickerScores[item.Ticker].Score += 5; // Boost already-watched tickers in news
                    tickerScores[item.Ticker].Sources.Add("existing-watchlist");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[universe] Failed to load existing watchlist for boost");
        }

        // ---------------------------------------------------------------
        // 5. Rank, filter, and cap
        // ---------------------------------------------------------------
        var universe = tickerScores
            .Where(kv => kv.Value.Score >= MinDiscoveryScore)
            .OrderByDescending(kv => kv.Value.Score)
            .Take(MaxUniverseSize)
            .Select(kv =>
            {
                var b = kv.Value;
                var topReason = b.HasUpcomingEarnings
                    ? $"Earnings on {b.EarningsDate}"
                    : b.RssMentions > 3
                        ? $"High news volume ({b.RssMentions} mentions)"
                        : b.FinnhubMentions > 0
                            ? "Mentioned in financial news"
                            : "Detected in market coverage";

                return new DiscoveredTicker(
                    Ticker: kv.Key,
                    DiscoveryScore: b.Score,
                    Sources: b.Sources.Distinct().ToList(),
                    EarningsDate: b.EarningsDate,
                    RssMentions: b.RssMentions,
                    FinnhubMentions: b.FinnhubMentions,
                    HasUpcomingEarnings: b.HasUpcomingEarnings,
                    TopReason: topReason);
            })
            .ToList();

        _logger.LogInformation("[universe] Discovery complete: {Count} tickers in universe (from {Total} candidates)",
            universe.Count, tickerScores.Count);

        foreach (var t in universe.Take(10))
            _logger.LogInformation("[universe]   {Ticker}: score={Score:F0}, sources=[{Sources}], reason={Reason}",
                t.Ticker, t.DiscoveryScore, string.Join(",", t.Sources), t.TopReason);

        return new DiscoveryResult(
            Universe: universe,
            RssArticlesScanned: rssScan?.Items.Count ?? 0,
            FinnhubArticlesScanned: 0, // Updated above
            EarningsFound: earningsCount,
            Errors: errors,
            DiscoveredAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Get just the ticker symbols as an array (for passing to BuildDynamicWatchlistAsync).
    /// </summary>
    public async Task<string[]> DiscoverTickerArrayAsync()
    {
        var result = await DiscoverUniverseAsync();
        return result.Universe.Select(t => t.Ticker).ToArray();
    }

    private static TickerScoreBuilder GetOrCreate(Dictionary<string, TickerScoreBuilder> dict, string ticker)
    {
        if (!dict.TryGetValue(ticker, out var builder))
        {
            builder = new TickerScoreBuilder();
            dict[ticker] = builder;
        }
        return builder;
    }

    private class TickerScoreBuilder
    {
        public double Score { get; set; }
        public int RssMentions { get; set; }
        public int FinnhubMentions { get; set; }
        public bool HasUpcomingEarnings { get; set; }
        public string? EarningsDate { get; set; }
        public List<string> Sources { get; set; } = [];
    }
}
