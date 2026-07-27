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
