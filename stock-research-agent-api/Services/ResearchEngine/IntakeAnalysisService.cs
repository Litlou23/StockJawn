using System.Text.Json;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.UniverseDiscovery;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Migrated from Next.js learningAnalysisService.ts + rssPickGenerator.ts.
/// Analyzes RSS intake data: ticker clustering, sentiment breakdown,
/// catalyst analysis, and auto-pick generation.
/// </summary>
public class IntakeAnalysisService
{
    private readonly RssFeedService _rssFeed;
    private readonly ResearchRepository _repo;
    private readonly IOpenAiCompletionService _ai;
    private readonly ILogger<IntakeAnalysisService> _logger;

    private static readonly Dictionary<string, string> TickerCompany = new()
    {
        ["SPY"] = "SPDR S&P 500 ETF", ["QQQ"] = "Invesco QQQ Trust",
        ["AAPL"] = "Apple Inc.", ["MSFT"] = "Microsoft Corp.",
        ["NVDA"] = "NVIDIA Corp.", ["AMD"] = "Advanced Micro Devices",
        ["TSLA"] = "Tesla Inc.", ["AMZN"] = "Amazon.com Inc.",
        ["META"] = "Meta Platforms Inc.", ["GOOGL"] = "Alphabet Inc.",
        ["PLTR"] = "Palantir Technologies", ["AVGO"] = "Broadcom Inc.",
        ["NFLX"] = "Netflix Inc.", ["COIN"] = "Coinbase Global Inc.",
    };

    private static readonly Dictionary<string, string> TickerSector = new()
    {
        ["SPY"] = "Index", ["QQQ"] = "Index",
        ["AAPL"] = "Technology", ["MSFT"] = "Technology",
        ["NVDA"] = "Semiconductors", ["AMD"] = "Semiconductors",
        ["TSLA"] = "Consumer Discretionary", ["AMZN"] = "Consumer Discretionary",
        ["META"] = "Communication Services", ["GOOGL"] = "Communication Services",
        ["PLTR"] = "Technology", ["AVGO"] = "Semiconductors",
        ["NFLX"] = "Communication Services", ["COIN"] = "Financials",
    };

    public IntakeAnalysisService(
        RssFeedService rssFeed, ResearchRepository repo,
        IOpenAiCompletionService ai, ILogger<IntakeAnalysisService> logger)
    {
        _rssFeed = rssFeed;
        _repo = repo;
        _ai = ai;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Models
    // -----------------------------------------------------------------------

    public record IntakeAnalysis
    {
        public string FeedStatus { get; init; } = "ok";
        public int ItemsFetched { get; init; }
        public Dictionary<string, int> TickerMentions { get; init; } = [];
        public Dictionary<string, int> CatalystBreakdown { get; init; } = [];
        public Dictionary<string, int> SentimentBreakdown { get; init; } = [];
        public Dictionary<string, int> SourceBreakdown { get; init; } = [];
        public int HighImportanceCount { get; init; }
        public List<TopItem> TopItems { get; init; } = [];
        public List<TrendingTicker> TrendingTickers { get; init; } = [];
        public List<DominantCatalyst> DominantCatalysts { get; init; } = [];
        public OverallSentiment Sentiment { get; init; } = new();
    }

    public record TopItem(string Title, string Source, List<string> Tickers,
        string Sentiment, double Importance, string Url, string CatalystType);

    public record TrendingTicker(string Ticker, int Mentions,
        int AvgImportance, string NetSentiment);

    public record DominantCatalyst(string Type, int Count, int PctOfTotal);

    public record OverallSentiment
    {
        public string Label { get; init; } = "Mixed";
        public double Score { get; init; }
        public int BullishPct { get; init; }
        public int BearishPct { get; init; }
    }

    public record AutoPick
    {
        public string Ticker { get; init; } = "";
        public string CompanyName { get; init; } = "";
        public string Sector { get; init; } = "";
        public int Score { get; init; }
        public string MainReason { get; init; } = "";
        public string RiskLevel { get; init; } = "medium";
        public string ConvictionLevel { get; init; } = "watchlist";
        public string BearishCounterpoint { get; init; } = "";
        public List<PickSignal> Signals { get; init; } = [];
        public List<SourceItem> SourceItems { get; init; } = [];
    }

    public record PickSignal(string Name, double Value, double WeightApplied, string Note);
    public record SourceItem(string Title, string Url, string Sentiment, double Importance);

    public record FullIntakeAnalysis
    {
        public IntakeAnalysis Intake { get; init; } = new();
        public List<AutoPick> AutoPicks { get; init; } = [];
        public string? AiBriefing { get; init; }
    }

    // -----------------------------------------------------------------------
    // Main Analysis
    // -----------------------------------------------------------------------

    public async Task<FullIntakeAnalysis> RunIntakeAnalysisAsync()
    {
        RssFeedService.RssScanResult scan;
        try
        {
            scan = await _rssFeed.ScanFeedsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[intake] RSS scan failed");
            return new FullIntakeAnalysis
            {
                Intake = new IntakeAnalysis { FeedStatus = $"error: {ex.Message}" },
            };
        }

        var analysis = AnalyzeItems(scan);
        var autoPicks = GenerateAutoPicks(scan);

        _logger.LogInformation("[intake] {Items} items, {Tickers} ticker mentions, {Picks} auto-picks",
            scan.Items.Count, scan.TickerMentions.Count, autoPicks.Count);

        // AI briefing
        string? briefing = null;
        if (_ai.IsConfigured && analysis.ItemsFetched > 0)
        {
            briefing = await GenerateAiBriefingAsync(analysis, autoPicks);
        }

        return new FullIntakeAnalysis
        {
            Intake = analysis,
            AutoPicks = autoPicks,
            AiBriefing = briefing,
        };
    }

    // -----------------------------------------------------------------------
    // RSS Item Analysis (ported from analyzeIntakeItems in TS)
    // -----------------------------------------------------------------------

    private static IntakeAnalysis AnalyzeItems(RssFeedService.RssScanResult scan)
    {
        var items = scan.Items;
        if (items.Count == 0)
            return new IntakeAnalysis { ItemsFetched = 0 };

        var tickerMentions = new Dictionary<string, int>();
        var tickerImportance = new Dictionary<string, List<double>>();
        var tickerSentiment = new Dictionary<string, List<double>>();
        var catalystBreakdown = new Dictionary<string, int>();
        var sentimentBreakdown = new Dictionary<string, int>();
        var sourceBreakdown = new Dictionary<string, int>();

        foreach (var item in items)
        {
            // Source tracking
            sourceBreakdown.TryGetValue(item.SourceName, out var sc);
            sourceBreakdown[item.SourceName] = sc + 1;

            // Extract tickers mentioned in this item
            var extracted = TickerExtractor.Extract($"{item.Title} {item.Summary}");
            foreach (var (ticker, _) in extracted.Tickers)
            {
                tickerMentions.TryGetValue(ticker, out var mc);
                tickerMentions[ticker] = mc + 1;

                if (!tickerImportance.ContainsKey(ticker))
                    tickerImportance[ticker] = [];
                tickerImportance[ticker].Add(item.SourceReliability * 100);

                if (!tickerSentiment.ContainsKey(ticker))
                    tickerSentiment[ticker] = [];
                // Simple heuristic sentiment from title keywords
                tickerSentiment[ticker].Add(SimpleSentiment(item.Title));
            }
        }

        // Trending tickers
        var trending = tickerMentions
            .OrderByDescending(kv => kv.Value).Take(10)
            .Select(kv =>
            {
                var avgImp = tickerImportance.GetValueOrDefault(kv.Key, [50])
                    .DefaultIfEmpty(50).Average();
                var avgSent = tickerSentiment.GetValueOrDefault(kv.Key, [0])
                    .DefaultIfEmpty(0).Average();
                return new TrendingTicker(kv.Key, kv.Value, (int)avgImp,
                    avgSent > 0.2 ? "bullish" : avgSent < -0.2 ? "bearish" : "neutral");
            }).ToList();

        // Top items by source reliability
        var topItems = items
            .OrderByDescending(i => i.SourceReliability)
            .Take(10)
            .Select(i =>
            {
                var extracted = TickerExtractor.Extract($"{i.Title} {i.Summary}");
                return new TopItem(i.Title, i.SourceName,
                    extracted.Tickers.Keys.ToList(),
                    SimpleSentimentLabel(i.Title),
                    i.SourceReliability * 100, i.Url, "news");
            }).ToList();

        // Overall sentiment
        var totalSent = items.Sum(i => SimpleSentiment(i.Title));
        var avgSentiment = totalSent / items.Count;
        var bullCount = items.Count(i => SimpleSentiment(i.Title) > 0.2);
        var bearCount = items.Count(i => SimpleSentiment(i.Title) < -0.2);

        return new IntakeAnalysis
        {
            FeedStatus = scan.Errors.Count == 0 ? "ok" : $"{scan.Errors.Count} feed error(s)",
            ItemsFetched = items.Count,
            TickerMentions = tickerMentions,
            CatalystBreakdown = catalystBreakdown,
            SentimentBreakdown = sentimentBreakdown,
            SourceBreakdown = sourceBreakdown,
            HighImportanceCount = items.Count(i => i.SourceReliability >= 0.75),
            TopItems = topItems,
            TrendingTickers = trending,
            Sentiment = new OverallSentiment
            {
                Label = avgSentiment > 0.15 ? "Bullish" : avgSentiment < -0.15 ? "Bearish" : "Mixed",
                Score = Math.Round(avgSentiment, 2),
                BullishPct = items.Count > 0 ? (int)(bullCount * 100.0 / items.Count) : 0,
                BearishPct = items.Count > 0 ? (int)(bearCount * 100.0 / items.Count) : 0,
            },
        };
    }

    // -----------------------------------------------------------------------
    // Auto-Pick Generation (ported from rssPickGenerator.ts)
    // -----------------------------------------------------------------------

    private static List<AutoPick> GenerateAutoPicks(RssFeedService.RssScanResult scan)
    {
        // Cluster items by watchlist ticker
        var clusters = new Dictionary<string, List<RssFeedService.FeedItem>>();
        foreach (var item in scan.Items)
        {
            var extracted = TickerExtractor.Extract($"{item.Title} {item.Summary}");
            foreach (var ticker in extracted.Tickers.Keys)
            {
                if (!TickerCompany.ContainsKey(ticker)) continue;
                if (!clusters.ContainsKey(ticker)) clusters[ticker] = [];
                clusters[ticker].Add(item);
            }
        }

        var picks = new List<AutoPick>();
        foreach (var (ticker, tickerItems) in clusters)
        {
            // Minimum threshold: 2+ articles or 1 high-reliability article
            if (tickerItems.Count < 2 && !tickerItems.Any(i => i.SourceReliability >= 0.75))
                continue;

            var volumeScore = Math.Min(tickerItems.Count * 8, 30);
            var avgReliability = tickerItems.Average(i => i.SourceReliability) * 100;
            var importanceScore = avgReliability * 0.4;
            var sourceCount = tickerItems.Select(i => i.SourceId).Distinct().Count();
            var diversityScore = Math.Min(sourceCount * 5, 15);
            var highImpBonus = Math.Min(tickerItems.Count(i => i.SourceReliability >= 0.75) * 5, 15);

            var score = (int)Math.Min(100, Math.Round(volumeScore + importanceScore + diversityScore + highImpBonus));

            var avgSent = tickerItems.Average(i => SimpleSentiment(i.Title));
            var sentimentLabel = avgSent > 0.2 ? "bullish" : avgSent < -0.2 ? "bearish" : "mixed";
            var riskLevel = avgSent < -0.3 ? "high" : Math.Abs(avgSent) < 0.1 ? "medium" : "low";

            var freshest = tickerItems.OrderByDescending(i => i.PublishedAt).First();
            var mainReason = $"{tickerItems.Count} news item(s) with {sentimentLabel} sentiment. " +
                             $"Top headline: \"{freshest.Title}\"";

            picks.Add(new AutoPick
            {
                Ticker = ticker,
                CompanyName = TickerCompany.GetValueOrDefault(ticker, ticker),
                Sector = TickerSector.GetValueOrDefault(ticker, "Unknown"),
                Score = score,
                MainReason = mainReason,
                RiskLevel = riskLevel,
                ConvictionLevel = score >= 65 && tickerItems.Count >= 3 ? "higher_conviction" : "watchlist",
                BearishCounterpoint = tickerItems.Count < 3
                    ? "Low article volume — could be noise"
                    : "No specific bearish catalysts identified",
                Signals =
                [
                    new PickSignal("news_volume", tickerItems.Count,
                        Math.Min(tickerItems.Count * 8, 30) / 100.0,
                        $"{tickerItems.Count} article(s) mentioning {ticker}"),
                    new PickSignal("source_reliability", Math.Round(avgReliability),
                        0.4, $"Average reliability {avgReliability:F0}/100"),
                    new PickSignal("sentiment_direction", Math.Round(avgSent * 100),
                        0.15, sentimentLabel),
                ],
                SourceItems = tickerItems.Take(5).Select(i => new SourceItem(
                    i.Title, i.Url, SimpleSentimentLabel(i.Title),
                    i.SourceReliability * 100)).ToList(),
            });
        }

        return picks.OrderByDescending(p => p.Score).ToList();
    }

    // -----------------------------------------------------------------------
    // AI Briefing
    // -----------------------------------------------------------------------

    private async Task<string?> GenerateAiBriefingAsync(IntakeAnalysis analysis, List<AutoPick> picks)
    {
        try
        {
            var trendingStr = string.Join(", ",
                analysis.TrendingTickers.Take(5)
                    .Select(t => $"{t.Ticker} ({t.Mentions} mentions, {t.NetSentiment})"));

            var topPicksStr = string.Join("\n",
                picks.Take(5).Select(p => $"- {p.Ticker} (score {p.Score}, {p.RiskLevel} risk): {p.MainReason}"));

            var prompt = $@"Analyze this RSS news data and auto-generated pick candidates. Provide a concise 3-5 sentence market briefing covering: (1) what the news cycle is focused on, (2) which tickers deserve attention and why, (3) key risks or caution areas. Be direct and specific.

RSS Analysis:
- {analysis.ItemsFetched} articles from {analysis.SourceBreakdown.Count} sources
- Overall sentiment: {analysis.Sentiment.Label} ({analysis.Sentiment.BullishPct}% bullish, {analysis.Sentiment.BearishPct}% bearish)
- Trending tickers: {trendingStr}

Auto-Generated Picks (top {Math.Min(picks.Count, 5)}):
{topPicksStr}

Respond with ONLY the briefing text, no JSON.";

            var result = await _ai.CompleteAsync(new AiCompletionRequest
            {
                Messages =
                [
                    new() { Role = "system", Content = "You are a concise stock research analyst. No disclaimers, no filler." },
                    new() { Role = "user", Content = prompt },
                ],
                MaxOutputTokens = 400,
            }, CancellationToken.None);

            return result.Text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[intake] AI briefing generation failed");
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static readonly string[] BullishWords =
        ["surge", "soar", "rally", "gain", "jump", "rise", "beat", "upgrade",
         "bullish", "record", "high", "growth", "strong", "outperform", "boost"];
    private static readonly string[] BearishWords =
        ["plunge", "crash", "drop", "fall", "decline", "miss", "downgrade",
         "bearish", "low", "weak", "underperform", "risk", "loss", "cut", "fear"];

    private static double SimpleSentiment(string text)
    {
        var lower = text.ToLowerInvariant();
        var bullHits = BullishWords.Count(w => lower.Contains(w));
        var bearHits = BearishWords.Count(w => lower.Contains(w));
        var total = bullHits + bearHits;
        if (total == 0) return 0;
        return (double)(bullHits - bearHits) / total;
    }

    private static string SimpleSentimentLabel(string text)
    {
        var score = SimpleSentiment(text);
        return score > 0.2 ? "positive" : score < -0.2 ? "negative" : "neutral";
    }
}
