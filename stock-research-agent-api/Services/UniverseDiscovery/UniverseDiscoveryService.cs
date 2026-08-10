using StockResearchAgent.Api.Services.Broker;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.UniverseDiscovery;

/// <summary>
/// Discovers which tickers the system should research by combining:
/// 1. RSS news feeds — tickers mentioned in financial headlines
/// 2. Finnhub — earnings calendar + market news with related tickers
/// 3. Twelve Data screening — volume/price movers (uses existing provider)
/// 4. TwelveData market movers — real-time top gainers &amp; losers (DYNAMIC)
/// 5. Alpaca screener — top movers + most active by volume (FREE, DYNAMIC)
/// 6. Base universe — safety net of ~60 high-liquidity large-caps
///
/// Sources 4 &amp; 5 are the key dynamic discovery — they catch whatever stocks are
/// making big moves TODAY without needing a static list to be manually updated.
/// The base universe (6) acts as a safety net for names that should always be
/// scanned even on quiet days.
///
/// Produces a ranked, deduplicated universe sorted by discovery score.
/// </summary>
public class UniverseDiscoveryService
{
    private const int MaxUniverseSize = 60;  // Bumped from 30 — base universe + news-discovered
    private const int MinDiscoveryScore = 2; // Minimum score to include

    /// <summary>
    /// High-liquidity large-cap stocks that are ALWAYS included in the scan universe.
    /// These are where earnings beats drive 20-40% weekly moves. Without this list,
    /// the system only finds obscure small-caps from RSS headline parsing and misses
    /// SHOP +41%, PLTR +38%, AXON +31% etc.
    /// Refreshed periodically — covers S&amp;P 500 top components, high-growth tech,
    /// and stocks with historically large earnings moves.
    /// </summary>
    private static readonly string[] BaseUniverse =
    [
        // Mega-cap tech — where the biggest dollar moves happen
        "AAPL", "MSFT", "AMZN", "GOOGL", "META", "NVDA", "TSLA", "AVGO", "ORCL", "CRM",
        // High-growth tech — big earnings movers
        "SHOP", "PLTR", "TTD", "NFLX", "AMD", "UBER", "SQ", "SNOW", "DDOG", "NET",
        "CRWD", "ZS", "PANW", "FTNT", "MDB", "COIN", "RBLX", "PINS", "SNAP", "APP",
        // Industrials / defense with big moves
        "AXON", "CAT", "DE", "GE", "HON", "LMT", "RTX",
        // Consumer / retail momentum
        "COST", "WMT", "TGT", "NKE", "SBUX", "MCD", "CMG",
        // Biotech / pharma with catalyst moves
        "LLY", "MRNA", "ABBV", "BMY", "GILD",
        // Semis — earnings season movers
        "MU", "QCOM", "MRVL", "KLAC", "LRCX", "AMAT",
        // Financials with volume
        "JPM", "GS", "MS", "V", "MA",
    ];

    private readonly RssFeedService _rssFeedService;
    private readonly FinnhubProvider _finnhub;
    private readonly TwelveDataProvider _twelveData;
    private readonly AlpacaBrokerAdapter _alpaca;
    private readonly WatchlistRepository _watchlistRepo;
    private readonly ILogger<UniverseDiscoveryService> _logger;

    public UniverseDiscoveryService(
        RssFeedService rssFeedService,
        FinnhubProvider finnhub,
        TwelveDataProvider twelveData,
        AlpacaBrokerAdapter alpaca,
        WatchlistRepository watchlistRepo,
        ILogger<UniverseDiscoveryService> logger)
    {
        _rssFeedService = rssFeedService;
        _finnhub = finnhub;
        _twelveData = twelveData;
        _alpaca = alpaca;
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
        // 4. TwelveData market movers — real-time top gainers & losers
        // ---------------------------------------------------------------
        // This is the key dynamic source — catches stocks making big moves
        // TODAY without needing a static list. Costs 200 API credits total.
        try
        {
            var movers = await _twelveData.GetMarketMoversAsync(30);
            foreach (var mover in movers)
            {
                if (IsLikelyWarrantOrSPAC(mover.Ticker)) continue;
                var builder = GetOrCreate(tickerScores, mover.Ticker);
                // Big movers get high scores — this is what the system was missing
                var absChange = Math.Abs(mover.PercentChange);
                var moverScore = absChange >= 10 ? 15 : absChange >= 5 ? 12 : 8;
                builder.Score += moverScore;
                if (!builder.Sources.Contains("twelvedata-movers"))
                    builder.Sources.Add("twelvedata-movers");
            }
            _logger.LogInformation("[universe] TwelveData movers: {Count} stocks (gainers + losers)", movers.Count);
        }
        catch (Exception ex)
        {
            errors.Add($"TwelveData movers failed: {ex.Message}");
            _logger.LogWarning(ex, "[universe] TwelveData market movers failed");
        }

        // ---------------------------------------------------------------
        // 5. Alpaca screener — top movers + most active by volume (FREE)
        // ---------------------------------------------------------------
        try
        {
            var alpacaMovers = await _alpaca.GetTopMoversAsync(20);
            foreach (var mover in alpacaMovers)
            {
                if (IsLikelyWarrantOrSPAC(mover.Ticker)) continue;
                var builder = GetOrCreate(tickerScores, mover.Ticker);
                var absChange = Math.Abs(mover.PercentChange);
                var moverScore = absChange >= 10 ? 12 : absChange >= 5 ? 10 : 6;
                builder.Score += moverScore;
                if (!builder.Sources.Contains("alpaca-movers"))
                    builder.Sources.Add("alpaca-movers");
            }
            _logger.LogInformation("[universe] Alpaca movers: {Count} stocks", alpacaMovers.Count);
        }
        catch (Exception ex)
        {
            errors.Add($"Alpaca movers failed: {ex.Message}");
            _logger.LogWarning(ex, "[universe] Alpaca movers failed");
        }

        try
        {
            var actives = await _alpaca.GetMostActivesAsync(20);
            foreach (var active in actives)
            {
                if (IsLikelyWarrantOrSPAC(active.Ticker)) continue;
                var builder = GetOrCreate(tickerScores, active.Ticker);
                builder.Score += 5; // High volume = institutional interest
                if (!builder.Sources.Contains("alpaca-actives"))
                    builder.Sources.Add("alpaca-actives");
            }
            _logger.LogInformation("[universe] Alpaca most active: {Count} stocks", actives.Count);
        }
        catch (Exception ex)
        {
            errors.Add($"Alpaca actives failed: {ex.Message}");
            _logger.LogWarning(ex, "[universe] Alpaca most actives failed");
        }

        // ---------------------------------------------------------------
        // 6. Base universe — safety net for high-liquidity large-caps
        // ---------------------------------------------------------------
        // Without this, discovery is entirely news-driven and misses the stocks
        // where earnings beats create 20-40% weekly moves. A real trader always
        // watches the big names.
        foreach (var ticker in BaseUniverse)
        {
            var builder = GetOrCreate(tickerScores, ticker);
            // Base universe tickers that ALSO have news/earnings get a big boost —
            // news + liquidity = highest catalyst potential (e.g. PLTR earnings beat)
            if (builder.Score > 0 && !builder.Sources.Contains("base-universe"))
                builder.Score += 8; // News-confirmed large-cap — prioritize
            if (builder.Score < MinDiscoveryScore + 1)
            {
                // Guarantee inclusion — base universe tickers always meet the minimum
                builder.Score = Math.Max(builder.Score, MinDiscoveryScore + 1);
            }
            if (!builder.Sources.Contains("base-universe"))
                builder.Sources.Add("base-universe");
        }

        _logger.LogInformation("[universe] Base universe: {Count} high-liquidity tickers always included", BaseUniverse.Length);

        // ---------------------------------------------------------------
        // 7. Boost tickers that already have watchlist history (prior predictions)
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
        // 8. Rank, filter, and cap
        // ---------------------------------------------------------------
        var universe = tickerScores
            .Where(kv => kv.Value.Score >= MinDiscoveryScore)
            .OrderByDescending(kv => kv.Value.Score)
            .Take(MaxUniverseSize)
            .Select(kv =>
            {
                var b = kv.Value;
                var isBase = b.Sources.Contains("base-universe");
                var isMover = b.Sources.Contains("twelvedata-movers") || b.Sources.Contains("alpaca-movers");
                var isActive = b.Sources.Contains("alpaca-actives");
                var topReason = b.HasUpcomingEarnings
                    ? $"Earnings on {b.EarningsDate}"
                    : isMover
                        ? "Top market mover today"
                        : b.RssMentions > 3
                            ? $"High news volume ({b.RssMentions} mentions)"
                            : isActive
                                ? "High volume — most active today"
                                : b.FinnhubMentions > 0
                                    ? "Mentioned in financial news"
                                    : isBase
                                        ? "High-liquidity large-cap (base universe)"
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

    /// <summary>
    /// Filters out warrants (W, WS), SPAC units (U), rights (R, RT), and other
    /// non-common-stock tickers that show up on screeners as big movers but are
    /// illiquid and untradeable. Only applied to screener/mover sources — RSS,
    /// Finnhub, and BaseUniverse bypass this filter.
    ///
    /// Conservative: only targets 5+ char tickers with known suffixes.
    /// A legit 5-char ticker filtered here can still enter via news or base universe.
    /// </summary>
    private static bool IsLikelyWarrantOrSPAC(string ticker)
    {
        if (string.IsNullOrEmpty(ticker)) return true;

        // Tickers > 5 chars are almost always warrants/units/rights (e.g., HGTXUW, SRZNWS)
        if (ticker.Length > 5) return true;

        // 5-char tickers with warrant/SPAC suffixes (catches HGTXU, SRZNW, HUBCZ, BGLWW, etc.)
        // Safe: GOOGL (ends L), legit 5-char tickers don't typically end in W/U/Z
        if (ticker.Length == 5)
        {
            if (ticker.EndsWith("W") || ticker.EndsWith("U") || ticker.EndsWith("Z"))
                return true;
            if (ticker.EndsWith("WS") || ticker.EndsWith("RT"))
                return true;
        }

        return false;
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
