using System.Text.Json;
using OpenAI.Chat;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Generates structured predictions from real market data.
///
/// Flow:
///   1. Rule-based engine scores technical signals + catalysts using
///      learning-adjusted weights from Supabase.
///   2. Direction, confidence, risk, and importance are determined by
///      the computed scores — never by OpenAI.
///   3. OpenAI (GPT-4.1-nano) receives the computed scores, signals,
///      and raw market data, then writes the explanation: thesis,
///      bull/bear cases, invalidation rule, and key levels.
///
/// If OpenAI is unavailable, the prediction still ships with a
/// generated explanation from the signal list.
/// No fake data. If data is unavailable, predictions are downgraded or skipped.
/// </summary>
public class PredictionGenerator
{
    private readonly MarketDataService _marketData;
    private readonly ResearchRepository _repo;
    private readonly ILogger<PredictionGenerator> _logger;
    private readonly ChatClient? _chatClient;

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
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var model = configuration["OPENAI_PREDICTION_MODEL"] ?? "gpt-4.1-nano";
            _chatClient = new ChatClient(model, apiKey);
        }
        else
        {
            _logger.LogWarning("[prediction] OPENAI_API_KEY not set — predictions will use signal-list explanations only");
        }
    }

    // -----------------------------------------------------------------------
    // Market snapshot builder
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
    // Prediction generation — signals first, AI explains
    // -----------------------------------------------------------------------

    public async Task<(PredictionCandidate? Prediction, List<PredictionInput> Inputs)>
        GeneratePredictionForTickerAsync(string ticker, string runId, MarketSnapshot snapshot)
    {
        // ── Step 1: Compute real signals and scores ──────────────────
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

        // ── Step 2: Build data-source metadata ──────────────────────
        var dataSources = new List<string>();
        var missingWarnings = new List<string>();

        if (snapshot.DataAvailability.MarketDataAvailable) dataSources.Add("twelve-data");
        else missingWarnings.Add("Market data unavailable — prediction based on news/catalysts only");

        if (snapshot.DataAvailability.NewsAvailable) dataSources.Add("rss-news");
        else missingWarnings.Add("No recent news/catalysts found");

        if (!snapshot.DataAvailability.OptionsChainAvailable)
            missingWarnings.Add("Options-chain data not connected — cannot confirm options setups");

        // ── Step 3: Ask OpenAI to explain the computed prediction ───
        var explanation = await GetAiExplanationAsync(
            ticker, snapshot, predType, totalScore, confidence, risk,
            allSignals, weights, lessons);

        if (explanation is not null)
            dataSources.Add("openai-analysis");

        // Fall back to signal-derived explanation if AI unavailable
        var bullishCase = explanation?.BullishCase
            ?? string.Join("; ", allSignals.Where(s => !s.Contains("bearish") && !s.Contains("negative") && !s.Contains("below")));
        var bearishCase = explanation?.BearishCase
            ?? string.Join("; ", allSignals.Where(s => s.Contains("bearish") || s.Contains("negative") || s.Contains("below")));
        var thesis = explanation?.Thesis
            ?? $"Score: {totalScore:F1}. Signals: {allSignals.Count}. {predType} stance based on {(dataSources.Count > 0 ? string.Join(" + ", dataSources) : "limited data")}.";
        var invalidation = explanation?.InvalidationRule
            ?? (predType == "bullish"
                ? "Invalidate if price drops >2% from entry or bearish catalyst emerges"
                : predType == "bearish"
                    ? "Invalidate if price rises >2% from entry or bullish catalyst emerges"
                    : "Invalidate if major catalyst changes thesis direction");

        // ── Step 4: Assemble prediction (scores from engine, text from AI) ──
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
            PredictionReason = thesis,
            InvalidationRule = invalidation,
            DataSourcesUsed = dataSources,
            MissingDataWarnings = missingWarnings,
            Status = "open",
        };

        var inputs = BuildInputs(ticker, snapshot, lessons);
        if (explanation is not null)
        {
            inputs.Add(new PredictionInput
            {
                PredictionId = "",
                InputType = "ai_explanation",
                SourceName = "openai-gpt4.1-nano",
                Summary = $"AI explanation of {predType} call (conf={confidence}, risk={risk}): {(thesis.Length > 120 ? thesis[..120] + "..." : thesis)}",
            });
        }

        _logger.LogInformation(
            "[prediction] {Ticker}: {Direction} (conf={Conf}, risk={Risk}, score={Score:F1}) — AI explanation: {HasAI}",
            ticker, predType, confidence, risk, totalScore, explanation is not null);

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
    // OpenAI call — explanation only, not decision-making
    // -----------------------------------------------------------------------

    private async Task<AiExplanationResponse?> GetAiExplanationAsync(
        string ticker,
        MarketSnapshot snapshot,
        string direction,
        double totalScore,
        int confidence,
        int risk,
        List<string> signals,
        Dictionary<string, double> weights,
        List<string> lessons)
    {
        if (_chatClient is null) return null;

        try
        {
            var systemPrompt = BuildExplanationSystemPrompt();
            var userPrompt = BuildExplanationUserPrompt(
                ticker, snapshot, direction, totalScore, confidence, risk, signals, weights, lessons);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt),
            };

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 400,
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
            };

            var completion = await _chatClient.CompleteChatAsync(messages, options);
            var text = completion.Value.Content.Count > 0 ? completion.Value.Content[0].Text : null;

            if (string.IsNullOrWhiteSpace(text)) return null;

            var result = JsonSerializer.Deserialize<AiExplanationResponse>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[prediction] OpenAI explanation call failed for {Ticker} — using signal-list fallback", ticker);
            return null;
        }
    }

    private static string BuildExplanationSystemPrompt()
    {
        return """
            You are a stock market analyst writing prediction explanations.

            IMPORTANT: You do NOT decide the prediction direction, confidence, or risk.
            Those have already been computed by the scoring engine from real market signals.
            Your job is to EXPLAIN WHY those signals led to this prediction.

            You MUST respond with valid JSON matching this schema:
            {
              "thesis": "<1-3 sentence explanation of why the computed signals support this direction>",
              "bullish_case": "<specific bullish factors from the provided signals and data>",
              "bearish_case": "<specific bearish factors from the provided signals and data>",
              "invalidation_rule": "<specific price level or condition that would invalidate this prediction>",
              "key_levels": { "support": <price or null>, "resistance": <price or null> }
            }

            Rules:
            - Reference ONLY the signals, scores, and data provided. Do NOT invent signals.
            - Be specific about price levels from the bars provided (support/resistance).
            - Explain the reasoning behind the computed direction — don't override it.
            - Keep thesis to 1-3 sentences. Be concise and insightful.
            - Invalidation rule should reference specific price levels when possible.
            """;
    }

    private static string BuildExplanationUserPrompt(
        string ticker,
        MarketSnapshot snapshot,
        string direction,
        double totalScore,
        int confidence,
        int risk,
        List<string> signals,
        Dictionary<string, double> weights,
        List<string> lessons)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Explain this prediction for {ticker}");
        sb.AppendLine();
        sb.AppendLine("### Computed prediction (from scoring engine — do NOT change these):");
        sb.AppendLine($"- Direction: {direction}");
        sb.AppendLine($"- Total score: {totalScore:F1}");
        sb.AppendLine($"- Confidence: {confidence}/100");
        sb.AppendLine($"- Risk: {risk}/100");
        sb.AppendLine();

        sb.AppendLine("### Signals that produced this score:");
        foreach (var signal in signals)
            sb.AppendLine($"- {signal}");
        sb.AppendLine();

        if (snapshot.Quote is not null)
        {
            var q = snapshot.Quote;
            sb.AppendLine($"### Current Quote: ${q.Price:F2} | Change: {(q.ChangePercent >= 0 ? "+" : "")}{q.ChangePercent:F2}% | Open: ${q.Open:F2} | High: ${q.High:F2} | Low: ${q.Low:F2} | Vol: {q.Volume:N0}");
        }

        if (snapshot.RecentBars.Count > 0)
        {
            sb.AppendLine("### Recent Price Bars (newest first):");
            foreach (var bar in snapshot.RecentBars.Take(10))
                sb.AppendLine($"  {bar.Date}: O={bar.Open:F2} H={bar.High:F2} L={bar.Low:F2} C={bar.Close:F2} V={bar.Volume:N0}");
        }

        if (snapshot.TechnicalContext is not null)
        {
            var t = snapshot.TechnicalContext;
            sb.AppendLine($"### Technical: Trend={t.TrendDirection} | MA={t.MovingAverageSummary} | Momentum={t.MomentumSummary} | Volume={t.VolumeSummary} | RSI={t.RelativeStrengthNote}");
        }

        if (snapshot.NewsContext.Count > 0)
        {
            sb.AppendLine("### News:");
            foreach (var n in snapshot.NewsContext.Take(5))
                sb.AppendLine($"  - [{n.CatalystType ?? "news"}] {n.Title} (sentiment: {n.Sentiment ?? "unknown"})");
        }

        if (weights.Count > 0)
        {
            var adjusted = weights.Where(w => Math.Abs(w.Value - 1.0) > 0.1).ToList();
            if (adjusted.Count > 0)
            {
                sb.AppendLine("### Learning-adjusted weights:");
                foreach (var w in adjusted)
                    sb.AppendLine($"  - {w.Key}: {w.Value:F2}x");
            }
        }

        if (lessons.Count > 0)
        {
            sb.AppendLine("### Prior lessons:");
            foreach (var lesson in lessons.Take(3))
                sb.AppendLine($"  - {lesson}");
        }

        return sb.ToString();
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

    // -----------------------------------------------------------------------
    // Rule-based scoring engine — the source of truth for all predictions
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
// OpenAI response DTO — explanation only, no scores or direction
// -----------------------------------------------------------------------

internal class AiExplanationResponse
{
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
