using System.Text.Json;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Classifies news articles as fundamental_catalyst, technical_momentum, or noise
/// using the existing OpenAI connection (gpt-4.1-mini). Called during snapshot
/// building for the top N articles by importance. Best-effort: if OpenAI is
/// unavailable or errors, articles remain unclassified and downstream logic
/// falls back to keyword-based checks.
/// </summary>
public class NewsCatalystClassifier
{
    private readonly IOpenAiCompletionService _openAi;
    private readonly ILogger<NewsCatalystClassifier> _logger;

    /// <summary>Max articles to classify per ticker. Config-driven via scoring_weight_overrides.</summary>
    public int MaxArticlesPerTicker { get; set; } = 8;

    public NewsCatalystClassifier(IOpenAiCompletionService openAi, ILogger<NewsCatalystClassifier> logger)
    {
        _openAi = openAi;
        _logger = logger;
    }

    /// <summary>
    /// Classifies the top N news articles for a ticker. Mutates the CatalystQuality,
    /// CatalystConfidence, and CatalystReasoning fields in-place on the news items.
    /// </summary>
    public async Task ClassifyAsync(string ticker, List<MarketSnapshotNews> newsItems, CancellationToken ct = default)
    {
        if (!_openAi.IsConfigured || newsItems.Count == 0)
            return;

        // Pick top N by importance score
        var toClassify = newsItems
            .OrderByDescending(n => n.ImportanceScore)
            .Take(MaxArticlesPerTicker)
            .ToList();

        if (toClassify.Count == 0) return;

        try
        {
            var articleBlock = string.Join("\n", toClassify.Select((n, i) =>
            {
                var summary = !string.IsNullOrWhiteSpace(n.Summary) ? $" | Summary: {n.Summary}" : "";
                return $"[{i}] Title: {n.Title}{summary} | Source: {n.SourceName} | Type: {n.CatalystType ?? "unknown"} | Sentiment: {n.Sentiment ?? "unknown"}";
            }));

            var prompt = $$"""
                You are a financial news classifier for {{ticker}}. For each article below, classify whether
                the news represents a FUNDAMENTAL CATALYST (real event driving price: earnings, FDA approval,
                merger, acquisition, guidance change, analyst upgrade/downgrade, major contract, legal ruling,
                regulatory action, dividend change, buyback announcement, insider buying, etc.) or
                TECHNICAL MOMENTUM (article just describes price action, chart patterns, momentum, "stock is
                up X%", trading volume, without identifying a fundamental reason) or NOISE (irrelevant,
                generic market commentary, clickbait, old news rehashed).

                Articles:
                {{articleBlock}}

                Respond with a JSON object: {"classifications": [{"index": 0, "quality": "fundamental_catalyst"|"technical_momentum"|"noise", "confidence": 0-100, "reason": "brief reason"}]}
                """;

            var result = await _openAi.CompleteAsync(new AiCompletionRequest
            {
                Messages =
                [
                    new AiChatMessageDto { Role = "system", Content = "You are a concise financial news classifier. Respond only with valid JSON." },
                    new AiChatMessageDto { Role = "user", Content = prompt }
                ],
                MaxOutputTokens = 500,
                ResponseFormatJson = true,
            }, ct);

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                _logger.LogWarning("[news-classifier] Empty response from OpenAI for {Ticker}", ticker);
                return;
            }

            var parsed = JsonSerializer.Deserialize<ClassificationResponse>(result.Text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed?.Classifications is null)
            {
                _logger.LogWarning("[news-classifier] Could not parse response for {Ticker}: {Text}", ticker, result.Text[..Math.Min(200, result.Text.Length)]);
                return;
            }

            foreach (var c in parsed.Classifications)
            {
                if (c.Index < 0 || c.Index >= toClassify.Count) continue;
                var news = toClassify[c.Index];
                news.CatalystQuality = c.Quality;
                news.CatalystConfidence = c.Confidence;
                news.CatalystReasoning = c.Reason;
            }

            var fundamentalCount = parsed.Classifications.Count(c => c.Quality == "fundamental_catalyst");
            var momentumCount = parsed.Classifications.Count(c => c.Quality == "technical_momentum");
            var noiseCount = parsed.Classifications.Count(c => c.Quality == "noise");
            _logger.LogInformation(
                "[news-classifier] {Ticker}: classified {Total} articles — {Fundamental} fundamental, {Momentum} technical, {Noise} noise",
                ticker, toClassify.Count, fundamentalCount, momentumCount, noiseCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[news-classifier] Classification failed for {Ticker} — proceeding without", ticker);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // Macro sentiment classification — fetches SPY news headlines and
    // classifies the broad market environment as risk_on / risk_off / neutral.
    // Called once per scan run and cached; the result feeds into
    // BenchmarkContext → MarketContextEvaluator for every ticker.
    // ───────────────────────────────────────────────────────────────────

    private MacroSentimentResult? _cachedMacroSentiment;
    private DateTimeOffset? _macroSentimentFetchedAt;

    /// <summary>
    /// Classifies broad market sentiment from SPY news headlines.
    /// Cached for 2 hours so multiple tickers in the same scan reuse the result.
    /// </summary>
    public async Task<MacroSentimentResult?> ClassifyMacroSentimentAsync(
        List<MarketSnapshotNews> spyNews, CancellationToken ct = default)
    {
        // Return cache if fresh (within 2 hours)
        if (_cachedMacroSentiment is not null && _macroSentimentFetchedAt is not null
            && (DateTimeOffset.UtcNow - _macroSentimentFetchedAt.Value).TotalHours < 2)
        {
            return _cachedMacroSentiment;
        }

        if (!_openAi.IsConfigured || spyNews.Count == 0)
            return null;

        try
        {
            var headlines = string.Join("\n", spyNews
                .OrderByDescending(n => n.ImportanceScore)
                .Take(12)
                .Select((n, i) =>
                {
                    var summary = !string.IsNullOrWhiteSpace(n.Summary)
                        ? $" | {n.Summary[..Math.Min(120, n.Summary.Length)]}"
                        : "";
                    return $"[{i}] {n.Title}{summary} ({n.SourceName})";
                }));

            var prompt = $$"""
                You are a macro market sentiment classifier. Given these recent market/SPY news headlines,
                classify the BROAD MARKET environment.

                Headlines:
                {{headlines}}

                Respond with a JSON object:
                {
                  "sentiment": "risk_on" | "risk_off" | "neutral",
                  "confidence": 0-100,
                  "impact_days": 1-20,
                  "themes": ["theme1", "theme2"],
                  "reasoning": "one sentence explaining why"
                }

                Guidelines:
                - "risk_off": war, geopolitical crisis, rate hikes, recession fears, major selloff, sanctions, oil shock, bank failures, tariffs escalation
                - "risk_on": peace deals, rate cuts, strong earnings season, stimulus, trade deals, positive economic data
                - "neutral": mixed signals, routine news, no clear macro driver
                - impact_days: how many TRADING days this macro environment is likely to persist (1=today only, 5=about a week, 10-20=structural shift)
                - themes: 1-3 short labels (e.g., "geopolitical_conflict", "fed_hawkish", "earnings_season", "oil_shock", "trade_war")
                - confidence: how confident you are in this classification (higher = clearer signal in the headlines)
                """;

            var result = await _openAi.CompleteAsync(new AiCompletionRequest
            {
                Messages =
                [
                    new AiChatMessageDto { Role = "system", Content = "You are a concise macro market classifier. Respond only with valid JSON." },
                    new AiChatMessageDto { Role = "user", Content = prompt }
                ],
                MaxOutputTokens = 300,
                ResponseFormatJson = true,
            }, ct);

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                _logger.LogWarning("[macro-classifier] Empty response from OpenAI");
                return null;
            }

            var parsed = JsonSerializer.Deserialize<MacroSentimentResponse>(result.Text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed is null)
            {
                _logger.LogWarning("[macro-classifier] Could not parse response: {Text}",
                    result.Text[..Math.Min(200, result.Text.Length)]);
                return null;
            }

            var macro = new MacroSentimentResult
            {
                Sentiment = parsed.Sentiment ?? "neutral",
                Confidence = Math.Clamp(parsed.Confidence, 0, 100),
                ImpactDays = Math.Clamp(parsed.ImpactDays, 1, 20),
                Themes = parsed.Themes ?? [],
                Reasoning = parsed.Reasoning,
            };

            _cachedMacroSentiment = macro;
            _macroSentimentFetchedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "[macro-classifier] Macro sentiment: {Sentiment} (conf {Confidence}, impact {Days}d, themes: {Themes}) — {Reasoning}",
                macro.Sentiment, macro.Confidence, macro.ImpactDays,
                string.Join(", ", macro.Themes), macro.Reasoning);

            return macro;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[macro-classifier] Macro sentiment classification failed — proceeding without");
            return null;
        }
    }

    // ── Models ──────────────────────────────────────────────────────────

    public record MacroSentimentResult
    {
        public string Sentiment { get; init; } = "neutral";
        public int Confidence { get; init; }
        public int ImpactDays { get; init; } = 1;
        public List<string> Themes { get; init; } = [];
        public string? Reasoning { get; init; }
    }

    private record MacroSentimentResponse
    {
        public string? Sentiment { get; init; }
        public int Confidence { get; init; }
        public int ImpactDays { get; init; } = 1;
        public List<string>? Themes { get; init; }
        public string? Reasoning { get; init; }
    }

    private record ClassificationResponse
    {
        public List<ClassificationItem> Classifications { get; init; } = [];
    }

    private record ClassificationItem
    {
        public int Index { get; init; }
        public string Quality { get; init; } = "noise";
        public int Confidence { get; init; }
        public string? Reason { get; init; }
    }
}
