using System.Text.Json;
using OpenAI.Chat;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Providers.StockFit;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.ResearchSignals;
using StockResearchAgent.Api.Services.UniverseDiscovery;

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
    private readonly ResearchSignalService _signalService;
    private readonly StockFitProvider _stockFit;
    private readonly FinnhubProvider _finnhub;
    private readonly ILogger<PredictionGenerator> _logger;
    private readonly ChatClient? _chatClient;

    public PredictionGenerator(
        MarketDataService marketData,
        ResearchRepository repo,
        ResearchSignalService signalService,
        StockFitProvider stockFit,
        FinnhubProvider finnhub,
        IConfiguration configuration,
        ILogger<PredictionGenerator> logger)
    {
        _marketData = marketData;
        _repo = repo;
        _signalService = signalService;
        _stockFit = stockFit;
        _finnhub = finnhub;
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

        // StockFit — company news + SEC filings + earnings context. Never a
        // source of price/technical data. If not configured or the endpoint
        // errors, we log a warning and continue with an empty catalyst set —
        // never fake filings/articles.
        if (_stockFit.IsConfigured)
        {
            try
            {
                var news = await _stockFit.GetNewsAsync(ticker, limit: 15);
                foreach (var w in news.Warnings) warnings.Add($"stockfit_news:{w}");
                foreach (var a in news.Data ?? [])
                {
                    newsContext.Add(new MarketSnapshotNews
                    {
                        Title = a.Title,
                        SourceName = a.Publisher ?? "stockfit",
                        Url = a.ArticleUrl ?? "",
                        PublishedAt = (a.PublishedAt ?? DateTimeOffset.UtcNow).ToString("o"),
                        CatalystType = "news",
                        Sentiment = a.Sentiment,
                        ImportanceScore = ScoreNewsImportance(a),
                    });
                }

                var filings = await _stockFit.GetFilingsAsync(ticker, limit: 10);
                foreach (var w in filings.Warnings) warnings.Add($"stockfit_filings:{w}");
                foreach (var f in filings.Data ?? [])
                {
                    newsContext.Add(new MarketSnapshotNews
                    {
                        Title = f.Headline,
                        SourceName = "SEC via stockfit",
                        Url = f.FilingUrl ?? "",
                        PublishedAt = (f.FilingDate ?? DateTimeOffset.UtcNow).ToString("o"),
                        CatalystType = MapFilingToCatalystType(f.FilingType, f.EventType),
                        Sentiment = null, // filings are structural — no sentiment invented
                        ImportanceScore = f.CatalystStrengthScore,
                    });
                }

                var earnings = await _stockFit.GetEarningsCalendarAsync(ticker);
                foreach (var w in earnings.Warnings) warnings.Add($"stockfit_earnings:{w}");
                foreach (var e in (earnings.Data ?? []).Where(x => x.DaysUntilReport is >= 0 and <= 14))
                {
                    newsContext.Add(new MarketSnapshotNews
                    {
                        Title = $"Earnings in {e.DaysUntilReport}d ({e.FiscalPeriod ?? "?"}{(e.Time is null ? "" : " " + e.Time)})",
                        SourceName = "earnings via stockfit",
                        Url = "",
                        PublishedAt = DateTimeOffset.UtcNow.ToString("o"),
                        CatalystType = "earnings",
                        Sentiment = null,
                        ImportanceScore = e.DaysUntilReport switch
                        {
                            <= 1 => 95,
                            <= 3 => 85,
                            <= 7 => 70,
                            _ => 55,
                        },
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[prediction] StockFit fetch failed for {Ticker}", ticker);
                warnings.Add($"stockfit_exception:{ex.Message}");
            }
        }
        else
        {
            warnings.Add("stockfit_not_configured");
        }

        // ── Finnhub — company-specific news (last 3 days) ──
        // Complements StockFit with real-time company news from Finnhub.
        // Returns empty list if FINNHUB_API_KEY is not configured.
        try
        {
            var finnhubNews = await _finnhub.GetCompanyNewsAsync(ticker, daysBack: 3);
            if (finnhubNews.Count > 0)
            {
                _logger.LogInformation("[prediction] Finnhub returned {Count} news items for {Ticker}", finnhubNews.Count, ticker);
                foreach (var article in finnhubNews.Take(10))
                {
                    // Skip if we already have a news item with the same title (dedup vs StockFit)
                    if (newsContext.Any(n => string.Equals(n.Title, article.Headline, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var hoursAgo = (DateTimeOffset.UtcNow - article.Datetime).TotalHours;
                    var importance = hoursAgo switch
                    {
                        <= 6 => 65.0,
                        <= 24 => 50.0,
                        <= 48 => 35.0,
                        _ => 25.0,
                    };

                    newsContext.Add(new MarketSnapshotNews
                    {
                        Title = article.Headline,
                        SourceName = article.Source ?? "finnhub",
                        Url = article.Url ?? "",
                        PublishedAt = article.Datetime.ToString("o"),
                        CatalystType = "news",
                        Sentiment = null, // Finnhub doesn't provide sentiment — let scoring handle it
                        ImportanceScore = importance,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[prediction] Finnhub news fetch failed for {Ticker}", ticker);
            warnings.Add($"finnhub_news_exception:{ex.Message}");
        }

        // RSS feeds are used for universe discovery only (ticker selection),
        // not for prediction scoring. News for predictions comes from
        // Finnhub (company news) and StockFit (SEC filings/earnings).

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
        // ── Step 1: Compute indicators, benchmark, and scores ────────
        var weights = (await _repo.GetScoringWeightsAsync())
            .ToDictionary(w => w.SignalName, w => w.Weight);
        var lessons = (await _repo.GetRecentLearningInsightsAsync(10))
            .Select(i => i.Summary).ToList();

        var indicators = IndicatorEngine.Compute(snapshot.RecentBars);

        // Fetch SPY/QQQ for market context (best-effort)
        MarketSnapshotQuote? spyQuote = null, qqqQuote = null;
        try
        {
            var spyTask = _marketData.GetQuoteAsync("SPY");
            var qqqTask = _marketData.GetQuoteAsync("QQQ");
            await Task.WhenAll(spyTask, qqqTask);
            spyQuote = spyTask.Result;
            qqqQuote = qqqTask.Result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[prediction] Failed to fetch SPY/QQQ benchmark quotes for {Ticker}", ticker);
        }

        var benchmark = IndicatorEngine.ComputeBenchmarkContext(snapshot.Quote, spyQuote, qqqQuote);

        // Fetch active research signals for this ticker
        var researchSignals = await _signalService.GetActiveSignalsForTickerAsync(ticker);

        var scoring = ScoringEngine.Score(snapshot, indicators, benchmark, weights, lessons, researchSignals);
        var predType = scoring.PredictionType;
        var confidence = scoring.Confidence;
        var risk = scoring.Risk;
        var totalScore = scoring.DirectionalScore;
        var bullishScore = scoring.BullishScore;
        var bearishScore = scoring.BearishScore;
        var winningDirection = scoring.WinningDirection;
        var directionMargin = scoring.DirectionMargin;
        var allSignals = scoring.Signals;

        if (confidence < 5 && predType == "watch_only") return (null, []);

        // ── Step 2: Build data-source metadata ──────────────────────
        var dataSources = new List<string>();
        var missingWarnings = new List<string>();

        if (snapshot.DataAvailability.MarketDataAvailable) dataSources.Add("twelve-data");
        else missingWarnings.Add("Market data unavailable — prediction based on news/catalysts only");

        if (snapshot.DataAvailability.NewsAvailable)
        {
            var sources = snapshot.NewsContext.Select(n => n.SourceName).Distinct().ToList();
            if (sources.Any(s => s.Contains("finnhub", StringComparison.OrdinalIgnoreCase))) dataSources.Add("finnhub-news");
            if (sources.Any(s => s.Contains("stockfit", StringComparison.OrdinalIgnoreCase) || s.Contains("SEC", StringComparison.OrdinalIgnoreCase))) dataSources.Add("stockfit-news");
        }
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

        // ── Step 4: ATR-based price prediction engine ──
        var entryPrice = snapshot.Quote?.Price;
        var priceCalc = ComputeAtrPriceForecast(
            entryPrice, predType, "1_day", snapshot, confidence, risk);

        // Second-pass finalization: apply R/R-aware caps + actionability tier
        // now that we know the risk/reward ratio.
        scoring = ScoringEngine.FinalizeWithRiskReward(scoring, priceCalc.RiskRewardRatio);
        confidence = scoring.Confidence;

        // If R:R ratio is extremely poor, downgrade to watch_only.
        // Threshold is 0.5 (not 1.5) — in learning mode we want to observe
        // predictions with marginal R:R so the learning engine can calibrate.
        if (priceCalc.RiskRewardRatio is double rr and < 0.5
            && (predType == "bullish" || predType == "bearish"))
        {
            predType = "watch_only";
            priceCalc.Warnings.Add($"Downgraded to watch_only: R:R ratio {rr:F2} < 0.5 minimum");
        }

        // ── Step 5: Assemble prediction (scores from engine, text from AI) ──
        var prediction = new PredictionCandidate
        {
            RunId = runId,
            Ticker = ticker,
            PredictionType = Enum.TryParse<PredictionType>(predType, out var pt) ? pt : PredictionType.neutral_no_edge,
            AssetType = PredictionAssetType.stock,
            TimeWindow = "1_day",
            ConfidenceScore = confidence,
            ImportanceScore = Math.Min(Math.Abs((int)totalScore), 100),
            RiskScore = risk,
            BullishScore = bullishScore,
            BearishScore = bearishScore,
            WinningDirection = winningDirection,
            DirectionConfidence = directionMargin,
            EntryReferencePrice = entryPrice,
            Atr14 = priceCalc.Atr14,
            AtrPercent = priceCalc.AtrPercent,
            TimeframeMultiplier = priceCalc.TimeframeMultiplier,
            SignalModifier = priceCalc.SignalModifier,
            ExpectedMoveDollar = priceCalc.ExpectedMoveDollar,
            ExpectedMovePercent = priceCalc.ExpectedMovePercent,
            PredictedPrice = priceCalc.PredictedPrice,
            PredictedMovePercent = priceCalc.PredictedMovePercent,
            ProjectedPriceLow = priceCalc.ProjectedPriceLow,
            ProjectedPriceHigh = priceCalc.ProjectedPriceHigh,
            TargetPrice = priceCalc.TargetPrice,
            StopPrice = priceCalc.StopPrice,
            InvalidationPrice = priceCalc.InvalidationPrice,
            SupportLevel = priceCalc.SupportLevel,
            ResistanceLevel = priceCalc.ResistanceLevel,
            RiskRewardRatio = priceCalc.RiskRewardRatio,
            PricePredictionMethod = priceCalc.Method,
            PricePredictionWarnings = priceCalc.Warnings,
            BullishCase = string.IsNullOrEmpty(bullishCase) ? "No strong bullish signals" : bullishCase,
            BearishCase = string.IsNullOrEmpty(bearishCase) ? "No strong bearish signals identified" : bearishCase,
            PredictionReason = thesis,
            InvalidationRule = invalidation,
            DataSourcesUsed = dataSources,
            MissingDataWarnings = missingWarnings,
            ScoreDebugJson = JsonSerializer.Serialize(scoring.Breakdown, new JsonSerializerOptions { WriteIndented = false }),
            ActionabilityScore = scoring.Breakdown.ActionabilityScore,
            ActionabilityTier = scoring.Breakdown.ActionabilityTier,
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
            "[prediction] {Ticker}: {Direction} (conf={Conf}, risk={Risk}, bull={Bull:F1}, bear={Bear:F1}, margin={Margin:F1}) — AI explanation: {HasAI}",
            ticker, predType, confidence, risk, bullishScore, bearishScore, directionMargin, explanation is not null);

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
              "key_levels": { "support": <price or null>, "resistance": <price or null> },
              "predicted_price": <number or null — your best estimate of where this stock will close at the end of the time window>,
              "predicted_move_percent": <number or null — expected % move from current price, positive for up, negative for down>
            }

            Rules:
            - Reference ONLY the signals, scores, and data provided. Do NOT invent signals.
            - Be specific about price levels from the bars provided (support/resistance).
            - Explain the reasoning behind the computed direction — don't override it.
            - Keep thesis to 1-3 sentences. Be concise and insightful.
            - Invalidation rule should reference specific price levels when possible.
            - predicted_price must be a realistic price based on the current price, signals, and key levels.
            - predicted_move_percent should match the direction (positive for bullish, negative for bearish).
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
        sb.AppendLine($"- Bullish score: {Math.Max(0, totalScore):F1} (independent bullish evidence)");
        sb.AppendLine($"- Bearish score: {Math.Max(0, -totalScore):F1} (independent bearish evidence)");
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

    // Old ScoreTechnicalSignals, ScoreCatalystSignals, DeterminePredictionType,
    // CalculateConfidence, CalculateRisk removed — replaced by ScoringEngine.Score()

    // -----------------------------------------------------------------------
    // ATR-based price prediction engine
    // -----------------------------------------------------------------------

    private static readonly Dictionary<string, double> TimeframeMultipliers = new()
    {
        ["intraday"] = 0.5,
        ["1_day"] = 1.0,
        ["2_day"] = 1.4,
        ["3_day"] = 1.7,
        ["1_week"] = 2.2,
        ["1_month"] = 4.5,
        ["3_month"] = 8.0,
        ["6_month"] = 12.0,
        ["1_year"] = 17.0,
    };

    internal class AtrPriceForecast
    {
        public double? Atr14 { get; set; }
        public double? AtrPercent { get; set; }
        public double? TimeframeMultiplier { get; set; }
        public double? SignalModifier { get; set; }
        public double? ExpectedMoveDollar { get; set; }
        public double? ExpectedMovePercent { get; set; }
        public double? PredictedPrice { get; set; }
        public double? PredictedMovePercent { get; set; }
        public double? ProjectedPriceLow { get; set; }
        public double? ProjectedPriceHigh { get; set; }
        public double? TargetPrice { get; set; }
        public double? StopPrice { get; set; }
        public double? InvalidationPrice { get; set; }
        public double? SupportLevel { get; set; }
        public double? ResistanceLevel { get; set; }
        public double? RiskRewardRatio { get; set; }
        public string Method { get; set; } = "unavailable";
        public List<string> Warnings { get; set; } = [];
    }

    private static AtrPriceForecast ComputeAtrPriceForecast(
        double? entryPrice, string predType, string timeWindow,
        MarketSnapshot snapshot, int confidence, int risk)
    {
        var result = new AtrPriceForecast();
        if (entryPrice is not double ep || ep == 0) return result;
        if (predType != "bullish" && predType != "bearish") return result;

        var bars = snapshot.RecentBars;
        if (bars.Count < 2)
        {
            result.Warnings.Add("Not enough bars for ATR calculation");
            return result;
        }

        // --- ATR14 from TrueRange ---
        var trueRanges = new List<double>();
        for (int i = 1; i < bars.Count; i++)
        {
            var high = bars[i].High;
            var low = bars[i].Low;
            var prevClose = bars[i - 1].Close;
            var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            trueRanges.Add(tr);
        }

        int atrPeriod = Math.Min(14, trueRanges.Count);
        if (atrPeriod < 5)
        {
            result.Warnings.Add($"Only {atrPeriod} bars for ATR (need 14 for best accuracy)");
        }
        var atr14 = trueRanges.Take(atrPeriod).Average();
        result.Atr14 = Math.Round(atr14, 4);
        result.AtrPercent = Math.Round((atr14 / ep) * 100, 2);

        // Sanity checks on ATR
        if (result.AtrPercent > 10)
            result.Warnings.Add($"ATR is unusually high ({result.AtrPercent}% of price) — wide projected range");
        if (result.AtrPercent < 0.3)
            result.Warnings.Add($"ATR is unusually low ({result.AtrPercent}% of price) — stock may be range-bound");

        // --- Timeframe multiplier ---
        var tfMultiplier = TimeframeMultipliers.GetValueOrDefault(timeWindow, 1.0);
        result.TimeframeMultiplier = tfMultiplier;

        // --- Signal modifier: 1.0 + (catalyst*0.25) + (volume*0.15) + (trend*0.15) - (risk*0.25) ---
        var catalystScore = ScoreCatalystFactor(snapshot);
        var volumeScore = ScoreVolumeFactor(snapshot);
        var trendScore = ScoreTrendFactor(snapshot);
        var riskScore = risk / 100.0;

        var modifier = 1.0
            + (catalystScore * 0.25)
            + (volumeScore * 0.15)
            + (trendScore * 0.15)
            - (riskScore * 0.25);
        modifier = Math.Clamp(modifier, 0.75, 1.75);
        result.SignalModifier = Math.Round(modifier, 3);

        // --- Expected move ---
        var expectedMove = atr14 * tfMultiplier * modifier;
        result.ExpectedMoveDollar = Math.Round(expectedMove, 2);
        result.ExpectedMovePercent = Math.Round((expectedMove / ep) * 100, 2);

        // --- Support / resistance from bars ---
        var lookbackBars = bars.Take(Math.Min(10, bars.Count)).ToList();
        var support = lookbackBars.Min(b => b.Low);
        var resistance = lookbackBars.Max(b => b.High);
        result.SupportLevel = Math.Round(support, 2);
        result.ResistanceLevel = Math.Round(resistance, 2);

        // --- Projected price zone ---
        if (predType == "bullish")
        {
            result.ProjectedPriceLow = Math.Round(ep, 2);
            result.ProjectedPriceHigh = Math.Round(ep + expectedMove, 2);
            result.PredictedPrice = Math.Round(ep + expectedMove * 0.6, 2);
            result.PredictedMovePercent = Math.Round((expectedMove * 0.6 / ep) * 100, 2);

            // Use ATR-based target; only cap at resistance if raw target is far above it.
            // Resistance from a 10-bar lookback is not a hard ceiling — stocks routinely
            // break through short-term highs.
            var rawTarget = ep + expectedMove;
            result.TargetPrice = Math.Round(rawTarget, 2);

            var atrStop = ep - atr14;
            var supportStop = support - 0.25 * atr14;
            result.StopPrice = Math.Round(Math.Max(atrStop, supportStop), 2);

            result.InvalidationPrice = Math.Round(ep - 1.5 * atr14, 2);
        }
        else
        {
            result.ProjectedPriceLow = Math.Round(ep - expectedMove, 2);
            result.ProjectedPriceHigh = Math.Round(ep, 2);
            result.PredictedPrice = Math.Round(ep - expectedMove * 0.6, 2);
            result.PredictedMovePercent = Math.Round((-expectedMove * 0.6 / ep) * 100, 2);

            // Use ATR-based target; only cap at support if raw target is far below it.
            var rawTarget = ep - expectedMove;
            result.TargetPrice = Math.Round(rawTarget, 2);

            var atrStop = ep + atr14;
            var resistanceStop = resistance + 0.25 * atr14;
            result.StopPrice = Math.Round(Math.Min(atrStop, resistanceStop), 2);

            result.InvalidationPrice = Math.Round(ep + 1.5 * atr14, 2);
        }

        // --- Risk/reward ratio ---
        var reward = Math.Abs(result.TargetPrice!.Value - ep);
        var riskDollar = Math.Abs(ep - result.StopPrice!.Value);
        result.RiskRewardRatio = riskDollar > 0 ? Math.Round(reward / riskDollar, 2) : 0;

        if (result.RiskRewardRatio < 1.0)
            result.Warnings.Add($"Poor risk/reward ratio: {result.RiskRewardRatio:F2} (below 1.0)");

        if (predType == "bullish" && result.TargetPrice > resistance)
            result.Warnings.Add($"Target ${result.TargetPrice:F2} is above recent resistance ${resistance:F2} — breakout needed");
        else if (predType == "bearish" && result.TargetPrice < support)
            result.Warnings.Add($"Target ${result.TargetPrice:F2} is below recent support ${support:F2} — breakdown needed");

        result.Method = atrPeriod >= 14 ? "atr14_full" : $"atr{atrPeriod}_partial";
        return result;
    }

    private static double ScoreCatalystFactor(MarketSnapshot snapshot)
    {
        if (snapshot.NewsContext.Count == 0) return 0;
        var avgImportance = snapshot.NewsContext.Average(n => n.ImportanceScore);
        return Math.Clamp(avgImportance / 5.0, 0, 1);
    }

    // -----------------------------------------------------------------------
    // StockFit → MarketSnapshotNews helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Deterministic importance score for a StockFit news article. Blends
    /// provider-supplied relevance with a small sentiment lift; never
    /// invents. Missing signals default to 30 (neutral placeholder in the
    /// 0..100 range that catalyst scoring reads).
    /// </summary>
    private static double ScoreNewsImportance(NormalizedNewsArticle a)
    {
        double baseScore = 40;
        if (a.RelevanceScore is double rel) baseScore = Math.Clamp(rel * 100.0, 10, 90);
        if (a.SentimentScore is double s && Math.Abs(s) > 0.3) baseScore += 10;
        // Recency lift — news from the last 24h counts more than a week-old article.
        if (a.PublishedAt is DateTimeOffset p)
        {
            var hours = (DateTimeOffset.UtcNow - p).TotalHours;
            if (hours <= 6) baseScore += 15;
            else if (hours <= 24) baseScore += 8;
            else if (hours > 168) baseScore *= 0.5;
        }
        return Math.Round(Math.Clamp(baseScore, 0, 100), 1);
    }

    private static string MapFilingToCatalystType(string filingType, string? eventType)
    {
        // For 8-K, prefer the inferred event (earnings_release, acquisition,
        // etc.) since those score differently in the learning engine. Fall
        // back to filing-type shorthand for non-8-K filings.
        var ft = filingType.ToUpperInvariant();
        if (ft == "8-K" && !string.IsNullOrWhiteSpace(eventType))
            return $"8k_{eventType}";

        return ft switch
        {
            "8-K" => "8k_filing",
            "10-Q" => "quarterly_report",
            "10-K" => "annual_report",
            "S-1" or "S-3" => "shelf_or_ipo",
            "4" => "insider_transaction",
            "13D" => "beneficial_ownership_change",
            "13G" or "13F" or "13F-HR" => "institutional_holding",
            _ => eventType ?? "filing",
        };
    }

    private static double ScoreVolumeFactor(MarketSnapshot snapshot)
    {
        if (snapshot.TechnicalContext is null) return 0;
        if (snapshot.TechnicalContext.VolumeSummary.Contains("elevated", StringComparison.OrdinalIgnoreCase))
            return 0.8;
        if (snapshot.TechnicalContext.VolumeSummary.Contains("below", StringComparison.OrdinalIgnoreCase))
            return -0.3;
        return 0;
    }

    private static double ScoreTrendFactor(MarketSnapshot snapshot)
    {
        if (snapshot.TechnicalContext is null) return 0;
        return snapshot.TechnicalContext.TrendDirection switch
        {
            "bullish" => 0.7,
            "bearish" => -0.5,
            _ => 0,
        };
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
    public double? PredictedPrice { get; set; }
    public double? PredictedMovePercent { get; set; }
}

internal class AiKeyLevels
{
    public double? Support { get; set; }
    public double? Resistance { get; set; }
}
