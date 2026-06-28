using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Generates structured predictions from real market data and news.
/// Uses rule-based scoring with adjustable weights from Supabase.
/// No fake data. If data is unavailable, predictions are downgraded or skipped.
/// </summary>
public class PredictionGenerator
{
    private readonly MarketDataService _marketData;
    private readonly ResearchRepository _repo;
    private readonly ILogger<PredictionGenerator> _logger;

    public PredictionGenerator(
        MarketDataService marketData,
        ResearchRepository repo,
        ILogger<PredictionGenerator> logger)
    {
        _marketData = marketData;
        _repo = repo;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Market snapshot builder
    // -----------------------------------------------------------------------

    public async Task<MarketSnapshot> BuildMarketSnapshotAsync(string ticker, string runId)
    {
        var (quote, bars, technical, warnings) = await _marketData.GetFullContextAsync(ticker);

        // TODO: Integrate RSS news feed when available in .NET API
        var newsContext = new List<MarketSnapshotNews>();

        var availability = new MarketSnapshotAvailability
        {
            MarketDataAvailable = quote is not null,
            NewsAvailable = newsContext.Count > 0,
            OptionsChainAvailable = false,
            Warnings = warnings,
        };

        var recentBars = bars.Select(b => new MarketSnapshotBar
        {
            Date = b.Date, Open = b.Open, High = b.High,
            Low = b.Low, Close = b.Close, Volume = b.Volume,
        }).ToList();

        return new MarketSnapshot
        {
            Id = "",
            RunId = runId,
            Ticker = ticker,
            Quote = quote,
            RecentBars = recentBars,
            TechnicalContext = technical,
            NewsContext = newsContext,
            DataAvailability = availability,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    // -----------------------------------------------------------------------
    // Prediction scoring
    // -----------------------------------------------------------------------

    public async Task<(PredictionCandidate? Prediction, List<PredictionInput> Inputs)>
        GeneratePredictionForTickerAsync(string ticker, string runId, MarketSnapshot snapshot)
    {
        var weights = (await _repo.GetScoringWeightsAsync())
            .ToDictionary(w => w.SignalName, w => w.Weight);
        var lessons = (await _repo.GetRecentLearningInsightsAsync(10))
            .Select(i => i.Summary).ToList();

        var (techScore, techSignals) = ScoreTechnicalSignals(snapshot, weights);
        var (catScore, catSignals) = ScoreCatalystSignals(snapshot, weights);
        var totalScore = techScore + catScore;
        var allSignals = techSignals.Concat(catSignals).ToList();

        var predType = DeterminePredictionType(totalScore);
        var confidence = CalculateConfidence(snapshot, totalScore);
        var risk = CalculateRisk(snapshot, predType);

        if (confidence < 5 && predType == "watch_only") return (null, []);

        var dataSources = new List<string>();
        var missingWarnings = new List<string>();

        if (snapshot.DataAvailability.MarketDataAvailable) dataSources.Add("twelve-data");
        else missingWarnings.Add("Market data unavailable -- prediction based on news/catalysts only");

        if (snapshot.DataAvailability.NewsAvailable) dataSources.Add("rss-news");
        else missingWarnings.Add("No recent news/catalysts found");

        if (!snapshot.DataAvailability.OptionsChainAvailable)
            missingWarnings.Add("Options-chain data not connected -- cannot confirm options setups");

        var bullishCase = string.Join("; ",
            allSignals.Where(s => !s.Contains("bearish") && !s.Contains("negative") && !s.Contains("below")));
        var bearishCase = string.Join("; ",
            allSignals.Where(s => s.Contains("bearish") || s.Contains("negative") || s.Contains("below")));

        var prediction = new PredictionCandidate
        {
            RunId = runId,
            Ticker = ticker,
            PredictionType = Enum.TryParse<PredictionType>(predType, out var pt) ? pt : PredictionType.neutral,
            AssetType = PredictionAssetType.stock,
            TimeWindow = "1_day",
            ConfidenceScore = confidence,
            ImportanceScore = Math.Min(Math.Abs((int)totalScore), 100),
            RiskScore = risk,
            EntryReferencePrice = snapshot.Quote?.Price,
            BullishCase = string.IsNullOrEmpty(bullishCase) ? "No strong bullish signals" : bullishCase,
            BearishCase = string.IsNullOrEmpty(bearishCase) ? "No strong bearish signals identified" : bearishCase,
            PredictionReason = $"Score: {totalScore:F1}. Signals: {allSignals.Count}. {predType} stance based on {(dataSources.Count > 0 ? string.Join(" + ", dataSources) : "limited data")}.",
            InvalidationRule = predType == "bullish"
                ? "Invalidate if price drops >2% from entry or bearish catalyst emerges"
                : predType == "bearish"
                    ? "Invalidate if price rises >2% from entry or bullish catalyst emerges"
                    : "Invalidate if major catalyst changes thesis direction",
            DataSourcesUsed = dataSources,
            MissingDataWarnings = missingWarnings,
            Status = "open",
        };

        var inputs = new List<PredictionInput>();

        if (snapshot.Quote is not null)
        {
            inputs.Add(new PredictionInput
            {
                PredictionId = "",
                InputType = "market_data",
                SourceName = "twelve-data",
                Summary = $"{ticker} @ ${snapshot.Quote.Price:F2} ({(snapshot.Quote.ChangePercent > 0 ? "+" : "")}{snapshot.Quote.ChangePercent:F2}%)",
            });
        }

        if (snapshot.TechnicalContext is not null)
        {
            inputs.Add(new PredictionInput
            {
                PredictionId = "",
                InputType = "technical",
                SourceName = "twelve-data-computed",
                Summary = $"Trend: {snapshot.TechnicalContext.TrendDirection}. {snapshot.TechnicalContext.MomentumSummary}",
            });
        }

        foreach (var news in snapshot.NewsContext.Take(3))
        {
            inputs.Add(new PredictionInput
            {
                PredictionId = "",
                InputType = news.CatalystType is not null ? "catalyst" : "news",
                SourceName = news.SourceName,
                SourceUrl = news.Url,
                Summary = news.Title,
            });
        }

        if (lessons.Count > 0)
        {
            inputs.Add(new PredictionInput
            {
                PredictionId = "",
                InputType = "prior_lesson",
                SourceName = "learning-engine",
                Summary = $"{lessons.Count} prior lessons considered: {lessons[0][..Math.Min(100, lessons[0].Length)]}...",
            });
        }

        return (prediction, inputs);
    }

    public async Task<(List<PredictionCandidate> Predictions, List<PredictionInput> AllInputs)>
        GeneratePredictionsForWatchlistAsync(string[] watchlist, string runId, List<MarketSnapshot> snapshots)
    {
        var predictions = new List<PredictionCandidate>();
        var allInputs = new List<PredictionInput>();

        foreach (var snapshot in snapshots)
        {
            var (pred, inputs) = await GeneratePredictionForTickerAsync(snapshot.Ticker, runId, snapshot);
            if (pred is not null)
            {
                predictions.Add(pred);
                allInputs.AddRange(inputs);
            }
        }

        predictions.Sort((a, b) => b.ConfidenceScore.CompareTo(a.ConfidenceScore));
        return (predictions, allInputs);
    }

    // -----------------------------------------------------------------------
    // Scoring internals
    // -----------------------------------------------------------------------

    private static (double Score, List<string> Signals) ScoreTechnicalSignals(
        MarketSnapshot snapshot, Dictionary<string, double> weights)
    {
        var tech = snapshot.TechnicalContext;
        if (tech is null) return (0, ["No technical data available"]);

        double score = 0;
        var signals = new List<string>();

        var trendW = weights.GetValueOrDefault("technical_trend", 1.0);
        if (tech.TrendDirection == "bullish") { score += 20 * trendW; signals.Add("Trend: bullish"); }
        else if (tech.TrendDirection == "bearish") { score -= 15 * trendW; signals.Add("Trend: bearish"); }
        else signals.Add("Trend: neutral/unknown");

        var momW = weights.GetValueOrDefault("technical_momentum", 1.0);
        if (tech.MomentumSummary.Contains("up", StringComparison.OrdinalIgnoreCase))
        { score += 10 * momW; signals.Add("Momentum: positive"); }
        else if (tech.MomentumSummary.Contains("down", StringComparison.OrdinalIgnoreCase))
        { score -= 10 * momW; signals.Add("Momentum: negative"); }

        var volW = weights.GetValueOrDefault("technical_volume", 1.0);
        if (tech.VolumeSummary.Contains("elevated", StringComparison.OrdinalIgnoreCase))
        { score += 10 * volW; signals.Add("Volume: elevated"); }
        else if (tech.VolumeSummary.Contains("below", StringComparison.OrdinalIgnoreCase))
        { score -= 5 * volW; signals.Add("Volume: below average"); }

        return (score, signals);
    }

    private static (double Score, List<string> Signals) ScoreCatalystSignals(
        MarketSnapshot snapshot, Dictionary<string, double> weights)
    {
        var news = snapshot.NewsContext;
        if (news.Count == 0) return (0, ["No recent news/catalysts"]);

        double score = 0;
        var signals = new List<string>();

        var volW = weights.GetValueOrDefault("news_volume", 1.0);
        if (news.Count >= 3) { score += 10 * volW; signals.Add($"High news volume: {news.Count} items"); }

        foreach (var item in news)
        {
            var catKey = item.CatalystType is not null ? $"catalyst_{item.CatalystType}" : null;
            var catW = catKey is not null ? weights.GetValueOrDefault(catKey, 1.0) : 1.0;

            var impactScore = item.ImportanceScore * catW * 5;
            score += item.Sentiment == "bearish" ? -impactScore : impactScore;

            var sentW = item.Sentiment == "bearish"
                ? weights.GetValueOrDefault("news_sentiment_bearish", 1.0)
                : weights.GetValueOrDefault("news_sentiment_bullish", 1.0);
            score += (item.Sentiment == "bullish" ? 5 : item.Sentiment == "bearish" ? -5 : 0) * sentW;

            var titlePreview = item.Title.Length > 60 ? item.Title[..60] : item.Title;
            signals.Add($"{item.CatalystType ?? "news"}: \"{titlePreview}\" ({item.Sentiment ?? "neutral"}, imp={item.ImportanceScore})");
        }

        return (score, signals);
    }

    private static string DeterminePredictionType(double totalScore) =>
        totalScore >= 30 ? "bullish"
        : totalScore <= -20 ? "bearish"
        : Math.Abs(totalScore) >= 10 ? "neutral"
        : "watch_only";

    private static int CalculateConfidence(MarketSnapshot snapshot, double totalScore)
    {
        double confidence = Math.Min(Math.Abs(totalScore), 100);
        if (!snapshot.DataAvailability.MarketDataAvailable) confidence *= 0.5;
        if (!snapshot.DataAvailability.NewsAvailable) confidence *= 0.7;
        if (!snapshot.DataAvailability.OptionsChainAvailable) confidence *= 0.9;
        return (int)Math.Round(confidence);
    }

    private static int CalculateRisk(MarketSnapshot snapshot, string predictionType)
    {
        int risk = 50;
        if (snapshot.TechnicalContext is not null)
        {
            if (predictionType == "bullish" && snapshot.TechnicalContext.TrendDirection == "bearish") risk += 20;
            if (predictionType == "bearish" && snapshot.TechnicalContext.TrendDirection == "bullish") risk += 20;
        }
        if (!snapshot.DataAvailability.MarketDataAvailable) risk += 15;
        if (!snapshot.DataAvailability.NewsAvailable) risk += 10;
        return Math.Min(risk, 100);
    }
}
