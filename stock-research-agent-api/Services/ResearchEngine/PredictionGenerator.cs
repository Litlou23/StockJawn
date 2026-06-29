using System.Text.Json;
using OpenAI.Chat;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Generates structured predictions by sending real market data to OpenAI.
/// Uses GPT-4.1-nano for cost-effective analysis (~$0.10/month at 10 tickers/day).
/// Historical outcomes and learning insights are fed as in-context learning.
/// No fake data. If data is unavailable, predictions are downgraded or skipped.
/// </summary>
public class PredictionGenerator
{
    private readonly MarketDataService _marketData;
    private readonly ResearchRepository _repo;
    private readonly ILogger<PredictionGenerator> _logger;
    private readonly ChatClient _chatClient;

    public PredictionGenerator(
        MarketDataService marketData,
        ResearchRepository repo,
        IConfiguration configuration,
        ILogger<PredictionGenerator> logger)
    {
        _marketData = marketData;
        _repo = repo;
        _logger = logger;

        var apiKey = configuration["OPENAI_API_KEY"];
        var model = configuration["OPENAI_PREDICTION_MODEL"] ?? "gpt-4.1-nano";
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not configured.");
        _chatClient = new ChatClient(model, apiKey);
    }

    // -----------------------------------------------------------------------
    // Market snapshot builder (unchanged)
    // -----------------------------------------------------------------------

    public async Task<MarketSnapshot> BuildMarketSnapshotAsync(string ticker, string runId)
    {
        var (quote, bars, technical, warnings) = await _marketData.GetFullContextAsync(ticker);

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
    // AI-powered prediction generation
    // -----------------------------------------------------------------------

    public async Task<(PredictionCandidate? Prediction, List<PredictionInput> Inputs)>
        GeneratePredictionForTickerAsync(string ticker, string runId, MarketSnapshot snapshot)
    {
        if (!snapshot.DataAvailability.MarketDataAvailable)
        {
            _logger.LogWarning("[prediction-ai] No market data for {Ticker}, skipping", ticker);
            return (null, []);
        }

        var outcomes = await _repo.GetRecentOutcomesAsync(20);
        var lessons = (await _repo.GetRecentLearningInsightsAsync(10))
            .Select(i => i.Summary).ToList();
        var weights = (await _repo.GetScoringWeightsAsync())
            .ToDictionary(w => w.SignalName, w => w.Weight);

        try
        {
            var aiResult = await CallOpenAiForPredictionAsync(ticker, snapshot, outcomes, lessons, weights);
            if (aiResult is null)
            {
                _logger.LogWarning("[prediction-ai] OpenAI returned no actionable prediction for {Ticker}", ticker);
                return (null, []);
            }

            var dataSources = new List<string> { "twelve-data", "openai-analysis" };
            var missingWarnings = new List<string>();
            if (!snapshot.DataAvailability.NewsAvailable)
                missingWarnings.Add("No recent news/catalysts found");
            if (!snapshot.DataAvailability.OptionsChainAvailable)
                missingWarnings.Add("Options-chain data not connected");

            var prediction = new PredictionCandidate
            {
                RunId = runId,
                Ticker = ticker,
                PredictionType = ParsePredictionType(aiResult.Direction),
                AssetType = PredictionAssetType.stock,
                TimeWindow = aiResult.TimeWindow ?? "1_day",
                ConfidenceScore = Math.Clamp(aiResult.Confidence, 1, 100),
                ImportanceScore = Math.Clamp(aiResult.Importance, 1, 100),
                RiskScore = Math.Clamp(aiResult.Risk, 1, 100),
                EntryReferencePrice = snapshot.Quote?.Price,
                BullishCase = aiResult.BullishCase ?? "No strong bullish signals",
                BearishCase = aiResult.BearishCase ?? "No strong bearish signals",
                PredictionReason = aiResult.Thesis ?? "AI analysis completed",
                InvalidationRule = aiResult.InvalidationRule ?? "Invalidate if major catalyst changes thesis direction",
                DataSourcesUsed = dataSources,
                MissingDataWarnings = missingWarnings,
                Status = "open",
            };

            var inputs = BuildInputs(ticker, snapshot, lessons);
            inputs.Add(new PredictionInput
            {
                PredictionId = "",
                InputType = "ai_analysis",
                SourceName = "openai-gpt4.1-nano",
                Summary = $"AI thesis: {(aiResult.Thesis?.Length > 150 ? aiResult.Thesis[..150] + "..." : aiResult.Thesis)}",
            });

            _logger.LogInformation(
                "[prediction-ai] {Ticker}: {Direction} (conf={Conf}, risk={Risk}) — {Thesis}",
                ticker, aiResult.Direction, aiResult.Confidence, aiResult.Risk,
                aiResult.Thesis?[..Math.Min(80, aiResult.Thesis.Length)]);

            return (prediction, inputs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[prediction-ai] OpenAI call failed for {Ticker}, falling back to rule-based", ticker);
            return FallbackRuleBased(ticker, runId, snapshot, weights, lessons);
        }
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
    // OpenAI call
    // -----------------------------------------------------------------------

    private async Task<AiPredictionResponse?> CallOpenAiForPredictionAsync(
        string ticker,
        MarketSnapshot snapshot,
        List<PredictionOutcome> recentOutcomes,
        List<string> lessons,
        Dictionary<string, double> weights)
    {
        var systemPrompt = BuildSystemPrompt(lessons, recentOutcomes);
        var userPrompt = BuildUserPrompt(ticker, snapshot, weights);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt),
        };

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 500,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        };

        var completion = await _chatClient.CompleteChatAsync(messages, options);
        var text = completion.Value.Content.Count > 0 ? completion.Value.Content[0].Text : null;

        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            var result = JsonSerializer.Deserialize<AiPredictionResponse>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (result is null || string.IsNullOrWhiteSpace(result.Direction)) return null;
            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[prediction-ai] Failed to parse OpenAI response for {Ticker}: {Text}", ticker, text?[..Math.Min(200, text.Length)]);
            return null;
        }
    }

    private static string BuildSystemPrompt(List<string> lessons, List<PredictionOutcome> recentOutcomes)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a stock market technical analyst. Analyze the provided market data and produce a short-term prediction.");
        sb.AppendLine("You MUST respond with valid JSON matching this exact schema:");
        sb.AppendLine("""
{
  "direction": "bullish" | "bearish" | "neutral",
  "confidence": <1-100>,
  "importance": <1-100>,
  "risk": <1-100>,
  "time_window": "intraday" | "1_day" | "3_day" | "1_week",
  "thesis": "<1-3 sentence analysis explaining your reasoning>",
  "bullish_case": "<key bullish factors>",
  "bearish_case": "<key bearish factors>",
  "invalidation_rule": "<specific condition that would invalidate this prediction>",
  "key_levels": { "support": <price>, "resistance": <price> }
}
""");
        sb.AppendLine("Rules:");
        sb.AppendLine("- Base your analysis on price action, trend, momentum, volume, and moving averages.");
        sb.AppendLine("- Be specific about support/resistance levels from the price bars provided.");
        sb.AppendLine("- If the data is insufficient for a strong call, set direction to neutral and confidence low.");
        sb.AppendLine("- Do NOT invent data. Only reference what is provided.");
        sb.AppendLine("- Keep thesis concise (1-3 sentences) but insightful — explain WHY, not just WHAT.");
        sb.AppendLine("- Consider recent win/loss patterns in your confidence calibration.");

        if (lessons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Lessons from prior predictions (use to calibrate):");
            foreach (var lesson in lessons.Take(5))
                sb.AppendLine($"- {lesson}");
        }

        if (recentOutcomes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Recent outcome history (for calibration):");
            var correct = recentOutcomes.Count(o => o.DirectionCorrect == true);
            var total = recentOutcomes.Count(o => o.DirectionCorrect.HasValue);
            var avgMove = recentOutcomes.Where(o => o.PercentMove.HasValue).Select(o => o.PercentMove!.Value).DefaultIfEmpty(0).Average();
            sb.AppendLine($"- Recent accuracy: {correct}/{total} ({(total > 0 ? correct * 100.0 / total : 0):F0}%)");
            sb.AppendLine($"- Average move: {avgMove:F2}%");
            if (total > 0 && correct * 100.0 / total < 50)
                sb.AppendLine("- NOTE: Accuracy is below 50%. Consider being more conservative with confidence scores.");
        }

        return sb.ToString();
    }

    private static string BuildUserPrompt(string ticker, MarketSnapshot snapshot, Dictionary<string, double> weights)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Analyze: {ticker}");

        if (snapshot.Quote is not null)
        {
            var q = snapshot.Quote;
            sb.AppendLine($"**Current Quote:** ${q.Price:F2} | Change: {(q.ChangePercent >= 0 ? "+" : "")}{q.ChangePercent:F2}% | Open: ${q.Open:F2} | High: ${q.High:F2} | Low: ${q.Low:F2} | Vol: {q.Volume:N0}");
        }

        if (snapshot.RecentBars.Count > 0)
        {
            sb.AppendLine("**Recent Price Bars (newest first):**");
            foreach (var bar in snapshot.RecentBars.Take(10))
                sb.AppendLine($"  {bar.Date}: O={bar.Open:F2} H={bar.High:F2} L={bar.Low:F2} C={bar.Close:F2} V={bar.Volume:N0}");
        }

        if (snapshot.TechnicalContext is not null)
        {
            var t = snapshot.TechnicalContext;
            sb.AppendLine($"**Technical Summary:** Trend={t.TrendDirection} | MA={t.MovingAverageSummary} | Momentum={t.MomentumSummary} | Volume={t.VolumeSummary} | RSI note={t.RelativeStrengthNote}");
        }

        if (snapshot.NewsContext.Count > 0)
        {
            sb.AppendLine("**Recent News:**");
            foreach (var n in snapshot.NewsContext.Take(5))
                sb.AppendLine($"  - [{n.CatalystType ?? "news"}] {n.Title} (sentiment: {n.Sentiment ?? "unknown"}, importance: {n.ImportanceScore})");
        }

        if (weights.Count > 0)
        {
            var significant = weights.Where(w => Math.Abs(w.Value - 1.0) > 0.1).ToList();
            if (significant.Count > 0)
            {
                sb.AppendLine("**Adjusted signal weights (from learning engine):**");
                foreach (var w in significant)
                    sb.AppendLine($"  - {w.Key}: {w.Value:F2}x");
            }
        }

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Fallback rule-based scoring (if OpenAI fails)
    // -----------------------------------------------------------------------

    private (PredictionCandidate? Prediction, List<PredictionInput> Inputs) FallbackRuleBased(
        string ticker, string runId, MarketSnapshot snapshot,
        Dictionary<string, double> weights, List<string> lessons)
    {
        _logger.LogInformation("[prediction-ai] Using fallback rule-based scoring for {Ticker}", ticker);

        var (techScore, techSignals) = ScoreTechnicalSignals(snapshot, weights);
        var (catScore, catSignals) = ScoreCatalystSignals(snapshot, weights);
        var totalScore = techScore + catScore;
        var allSignals = techSignals.Concat(catSignals).ToList();

        var predType = DeterminePredictionType(totalScore);
        var confidence = CalculateConfidence(snapshot, totalScore);
        var risk = CalculateRisk(snapshot, predType);

        if (confidence < 5 && predType == "watch_only") return (null, []);

        var dataSources = new List<string>();
        var missingWarnings = new List<string> { "AI analysis unavailable — using rule-based fallback" };

        if (snapshot.DataAvailability.MarketDataAvailable) dataSources.Add("twelve-data");
        if (snapshot.DataAvailability.NewsAvailable) dataSources.Add("rss-news");
        if (!snapshot.DataAvailability.OptionsChainAvailable)
            missingWarnings.Add("Options-chain data not connected");

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
            PredictionReason = $"[Fallback] Score: {totalScore:F1}. Signals: {allSignals.Count}. {predType} stance.",
            InvalidationRule = predType == "bullish"
                ? "Invalidate if price drops >2% from entry or bearish catalyst emerges"
                : predType == "bearish"
                    ? "Invalidate if price rises >2% from entry or bullish catalyst emerges"
                    : "Invalidate if major catalyst changes thesis direction",
            DataSourcesUsed = dataSources,
            MissingDataWarnings = missingWarnings,
            Status = "open",
        };

        var inputs = BuildInputs(ticker, snapshot, lessons);
        return (prediction, inputs);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static List<PredictionInput> BuildInputs(string ticker, MarketSnapshot snapshot, List<string> lessons)
    {
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

        return inputs;
    }

    private static PredictionType ParsePredictionType(string? direction) =>
        direction?.ToLowerInvariant() switch
        {
            "bullish" => PredictionType.bullish,
            "bearish" => PredictionType.bearish,
            "neutral" => PredictionType.neutral,
            _ => PredictionType.neutral,
        };

    // -----------------------------------------------------------------------
    // Rule-based scoring (kept as fallback)
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

// -----------------------------------------------------------------------
// OpenAI response DTO (internal)
// -----------------------------------------------------------------------

internal class AiPredictionResponse
{
    public string? Direction { get; set; }
    public int Confidence { get; set; }
    public int Importance { get; set; }
    public int Risk { get; set; }
    public string? TimeWindow { get; set; }
    public string? Thesis { get; set; }
    public string? BullishCase { get; set; }
    public string? BearishCase { get; set; }
    public string? InvalidationRule { get; set; }
    public AiKeyLevels? KeyLevels { get; set; }
}

internal class AiKeyLevels
{
    public double? Support { get; set; }
    public double? Resistance { get; set; }
}
