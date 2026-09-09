using System.Text.Json;
using OpenAI.Chat;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.MarketIntelligence;
// StockFitProvider and FinnhubProvider now injected via MarketSnapshotBuilder
using StockResearchAgent.Api.Services.ResearchEngine.Evaluation;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.ResearchSignals;
using StockResearchAgent.Api.Services.Discovery;
using StockResearchAgent.Api.Services.MarketRegime;
using StockResearchAgent.Api.Services.ResearchUniverse;
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
    private readonly PaperStockCandidateRepository _stockRepo;
    private readonly ResearchSignalService _signalService;
    private readonly IMarketIntelligencePipeline _marketIntelligence;
    private readonly IScoringEngine _scoringEngine;
    private readonly EnsembleScoringService _ensemble;
    private readonly TradeSetupEngine _setupEngine;
    private readonly MarketSnapshotBuilder _snapshotBuilder;
    private readonly IHistoricalProfileBuilder _profileBuilder;
    private readonly VolatilityOpportunityEngine _voe;
    private readonly PredictionProfileRepository _profileRepo;
    private readonly MarketStressDetector _stressDetector;
    private readonly IMarketRegimeEngine _regimeEngine;
    private readonly FinnhubProvider _finnhub;
    private readonly ILogger<PredictionGenerator> _logger;
    private readonly ChatClient? _chatClient;
    private readonly bool _ensembleEnabled;
    private string? _cachedLearningContext;

    public PredictionGenerator(
        MarketDataService marketData,
        ResearchRepository repo,
        PaperStockCandidateRepository stockRepo,
        ResearchSignalService signalService,
        IMarketIntelligencePipeline marketIntelligence,
        IScoringEngine scoringEngine,
        EnsembleScoringService ensemble,
        TradeSetupEngine setupEngine,
        MarketSnapshotBuilder snapshotBuilder,
        IHistoricalProfileBuilder profileBuilder,
        VolatilityOpportunityEngine voe,
        PredictionProfileRepository profileRepo,
        MarketStressDetector stressDetector,
        IMarketRegimeEngine regimeEngine,
        FinnhubProvider finnhub,
        IConfiguration configuration,
        ILogger<PredictionGenerator> logger)
    {
        _marketData = marketData;
        _repo = repo;
        _stockRepo = stockRepo;
        _signalService = signalService;
        _marketIntelligence = marketIntelligence;
        _scoringEngine = scoringEngine;
        _ensemble = ensemble;
        _setupEngine = setupEngine;
        _snapshotBuilder = snapshotBuilder;
        _profileBuilder = profileBuilder;
        _voe = voe;
        _profileRepo = profileRepo;
        _stressDetector = stressDetector;
        _regimeEngine = regimeEngine;
        _finnhub = finnhub;
        _logger = logger;
        _ensembleEnabled = string.Equals(
            configuration["ENSEMBLE_SCORING_ENABLED"], "true",
            StringComparison.OrdinalIgnoreCase);

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
    // Market snapshot builder — delegates to MarketSnapshotBuilder
    // -----------------------------------------------------------------------

    public Task<MarketSnapshot> BuildMarketSnapshotAsync(string ticker, string runId)
        => _snapshotBuilder.BuildAsync(ticker, runId);

    // -----------------------------------------------------------------------
    // Prediction generation — signals first, AI explains
    // -----------------------------------------------------------------------

    /// <summary>
    /// Preloaded data that is the same for every ticker in a batch run.
    /// Load once via <see cref="PreloadSharedContextAsync"/> and pass to each ticker.
    /// </summary>
    public record SharedPredictionContext(
        Dictionary<string, double> Weights,
        List<string> Lessons,
        string? ProfileId = null,
        string? ProfileName = null,
        Dictionary<string, FinnhubProvider.EarningsEntry>? EarningsCalendar = null);

    /// <summary>
    /// Load scoring weights, overrides, and lessons once for the entire batch.
    /// </summary>
    public async Task<SharedPredictionContext> PreloadSharedContextAsync(string? profileId = null)
    {
        var weights = (await _repo.GetScoringWeightsAsync())
            .ToDictionary(w => w.SignalName, w => w.Weight);

        var overrides = await _repo.GetActiveWeightOverridesAsync();
        foreach (var o in overrides)
            weights[o.SignalName] = o.EffectiveWeight;

        var lessons = (await _repo.GetRecentLearningInsightsAsync(10))
            .Select(i => i.Summary).ToList();

        // Resolve profile: use provided ID, or fall back to champion
        string? resolvedProfileId = profileId;
        string? resolvedProfileName = null;

        if (string.IsNullOrEmpty(resolvedProfileId))
        {
            var champion = await _profileRepo.GetChampionProfileAsync();
            if (champion is not null)
            {
                resolvedProfileId = champion.Id;
                resolvedProfileName = champion.ProfileName;
            }
        }
        else
        {
            var profile = await _profileRepo.GetProfileByIdAsync(resolvedProfileId);
            resolvedProfileName = profile?.ProfileName;

            // Apply profile-specific weight overrides on top of base weights
            if (profile is not null)
            {
                var profileWeights = await _profileRepo.GetProfileWeightsAsync(resolvedProfileId);
                foreach (var kv in profileWeights)
                    weights[kv.Key] = kv.Value;
            }
        }

        // Fetch upcoming earnings calendar (single API call, shared across all tickers)
        Dictionary<string, FinnhubProvider.EarningsEntry>? earningsCalendar = null;
        try
        {
            var earningsList = await _finnhub.GetUpcomingEarningsAsync(7);
            if (earningsList.Count > 0)
            {
                earningsCalendar = new Dictionary<string, FinnhubProvider.EarningsEntry>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var e in earningsList)
                {
                    // Keep first entry per ticker (earliest date)
                    earningsCalendar.TryAdd(e.Ticker, e);
                }
                _logger.LogInformation("[prediction] Loaded {Count} upcoming earnings entries for catalyst scoring",
                    earningsCalendar.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[prediction] Earnings calendar fetch failed, continuing without");
        }

        return new SharedPredictionContext(weights, lessons, resolvedProfileId, resolvedProfileName, earningsCalendar);
    }

    public async Task<(PredictionCandidate? Prediction, List<PredictionInput> Inputs)>
        GeneratePredictionForTickerAsync(string ticker, string runId, MarketSnapshot snapshot,
            ResearchAsset? researchAsset = null, SharedPredictionContext? sharedContext = null)
    {
        // ── Step 1: Compute indicators, benchmark, and scores ────────
        Dictionary<string, double> weights;
        List<string> lessons;

        if (sharedContext is not null)
        {
            // Use preloaded data — avoids 3 DB round trips per ticker
            weights = new Dictionary<string, double>(sharedContext.Weights);
            lessons = sharedContext.Lessons;
        }
        else
        {
            // Fallback for single-ticker calls
            weights = (await _repo.GetScoringWeightsAsync())
                .ToDictionary(w => w.SignalName, w => w.Weight);

            var overrides = await _repo.GetActiveWeightOverridesAsync();
            foreach (var o in overrides)
                weights[o.SignalName] = o.EffectiveWeight;

            lessons = (await _repo.GetRecentLearningInsightsAsync(10))
                .Select(i => i.Summary).ToList();
        }

        var indicators = IndicatorEngine.Compute(snapshot.RecentBars);

        // Enrich indicators with TwelveData API values (MACD, EMA)
        // These are new signals not computable from 20 bars — MACD needs 26+ bars of EMA history,
        // EMA needs full price history for proper exponential smoothing.
        // Sequential calls — each goes through the rate-limited throttle in TwelveDataProvider.
        try
        {
            var apiMacd = await _marketData.GetMacdAsync(ticker);
            var apiEma = await _marketData.GetEmaAsync(ticker);

            indicators = IndicatorEngine.MergeApiIndicators(
                indicators,
                apiMacd: apiMacd,
                apiEma: apiEma);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[prediction] API indicator fetch failed for {Ticker}, using manual values", ticker);
        }

        // Fetch SPY/QQQ for market context (best-effort)
        MarketSnapshotQuote? spyQuote = null, qqqQuote = null;
        double? spyEma20 = null;
        double? spyEma50 = null;
        try
        {
            var spyTask = _marketData.GetQuoteAsync("SPY");
            var qqqTask = _marketData.GetQuoteAsync("QQQ");
            var spyEmaTask = _marketData.GetEmaAsync("SPY");
            await Task.WhenAll(spyTask, qqqTask, spyEmaTask);
            spyQuote = spyTask.Result;
            qqqQuote = qqqTask.Result;
            // Use EMA26 as ~20-day trend proxy (TwelveData returns 12/26/50)
            spyEma20 = spyEmaTask.Result.Ema26;
            spyEma50 = spyEmaTask.Result.Ema50;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[prediction] Failed to fetch SPY/QQQ benchmark quotes for {Ticker}", ticker);
        }

        // Fetch sector ETF EMA for sector momentum signal (best-effort).
        // Uses the stock's sector from fundamentals to look up the SPDR sector ETF,
        // then fetches its quote + EMA to determine sector-level trend.
        string? sectorEtf = null;
        double? sectorEtfPrice = null;
        double? sectorEtfEma = null;
        try
        {
            var sector = snapshot.Fundamentals?.Sector;
            sectorEtf = IndicatorEngine.GetSectorEtf(sector);
            if (sectorEtf is not null)
            {
                var sectorQuoteTask = _marketData.GetQuoteAsync(sectorEtf);
                var sectorEmaTask = _marketData.GetEmaAsync(sectorEtf);
                await Task.WhenAll(sectorQuoteTask, sectorEmaTask);
                sectorEtfPrice = sectorQuoteTask.Result?.Price;
                sectorEtfEma = sectorEmaTask.Result.Ema26; // ~20-day proxy
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[prediction] Failed to fetch sector ETF data for {Ticker}", ticker);
        }

        var benchmark = IndicatorEngine.ComputeBenchmarkContext(
            snapshot.Quote, spyQuote, qqqQuote, spyEma20,
            sectorEtf, sectorEtfPrice, sectorEtfEma);

        // ── Macro Sentiment (SPY news → AI classification) ───────────
        // Fetches + classifies once per scan run (cached 2h in NewsCatalystClassifier).
        // Overlays broad market sentiment onto BenchmarkContext so MarketContextEvaluator
        // can boost/penalize bullish/bearish scores system-wide based on geopolitical events,
        // Fed policy, macro shocks, etc.
        try
        {
            var macro = await _snapshotBuilder.GetMacroSentimentAsync();
            if (macro is not null)
            {
                benchmark = benchmark with
                {
                    MacroSentiment = macro.Sentiment,
                    MacroSentimentConfidence = macro.Confidence,
                    MacroImpactDays = macro.ImpactDays,
                    MacroThemes = macro.Themes,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[prediction] Macro sentiment overlay failed for {Ticker} — proceeding", ticker);
        }

        // ── Market Regime Classification ──────────────────────────────
        // Build context from available data and classify the current regime.
        // The regime result flows into EvaluationContext for use by evaluators
        // and ConfidenceEngine for counter-trend prediction suppression.
        MarketRegimeResult? regimeResult = null;
        try
        {
            var stressForRegime = await _stressDetector.EvaluateAsync();
            var qqqEma = await _marketData.GetEmaAsync("QQQ");

            // Trend-quality signals from the last ~60 SPY daily bars — feeds
            // the IsTradeableRegime gate. On days classified as chop/unstable,
            // downstream code short-circuits scoring instead of generating
            // signals that won't follow through.
            Services.MarketRegime.TrendQualityCalculator.TrendQuality? spyTrendQuality = null;
            try
            {
                var spyHistory = await _marketData.GetHistoricalBarsAsync(
                    "SPY",
                    DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-90),
                    DateOnly.FromDateTime(DateTime.UtcNow));
                if (spyHistory.Count >= 30)
                    spyTrendQuality = Services.MarketRegime.TrendQualityCalculator.Evaluate(spyHistory);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[prediction] SPY trend-quality fetch failed for {Ticker} — defaulting tradeable", ticker);
            }

            var regimeCtx = new MarketRegimeContext
            {
                // SPY price / EMA26 as short-term trend ratio (~50-day proxy)
                SpyTrendRatio = benchmark.SpyEmaRatio,
                // QQQ price / EMA26
                QqqTrendRatio = qqqQuote is not null && qqqEma.Ema26 is not null && qqqEma.Ema26 > 0
                    ? Math.Round(qqqQuote.Price / qqqEma.Ema26.Value, 4)
                    : null,
                // SPY price / EMA50 as long-term trend ratio
                SpyLongTrendRatio = spyQuote is not null && spyEma50 is not null && spyEma50 > 0
                    ? Math.Round(spyQuote.Price / spyEma50.Value, 4)
                    : null,
                // VIX from stress detector (already cached)
                Vix = stressForRegime.Vix,
                SpyAdx = spyTrendQuality?.Adx,
                RealizedVolRatio = spyTrendQuality?.RealizedVolRatio,
                HigherHighCount = spyTrendQuality?.HigherHighCount,
            };

            regimeResult = _regimeEngine.Classify(regimeCtx);
            if (spyTrendQuality is not null)
            {
                regimeResult = regimeResult with
                {
                    IsTradeableRegime = spyTrendQuality.IsTradeable,
                    TradeableRegimeReason = spyTrendQuality.Reason,
                    SpyAdx = spyTrendQuality.Adx,
                    RealizedVolRatio = spyTrendQuality.RealizedVolRatio,
                    HigherHighCount = spyTrendQuality.HigherHighCount,
                };
            }

            _logger.LogInformation(
                "[prediction] Market regime: {Primary} ({Confidence:P0}) — {Summary}",
                regimeResult.PrimaryRegime, regimeResult.PrimaryConfidence, regimeResult.Summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[prediction] Market regime classification failed for {Ticker}, continuing without", ticker);
        }

        // Fetch active research signals for this ticker
        var researchSignals = await _signalService.GetActiveSignalsForTickerAsync(ticker);
        var intelligence = await _marketIntelligence.BuildContextAsync(
            ticker, snapshot, indicators, benchmark, researchSignals);

        // Build Research Universe context from the threaded ResearchAsset (Phase 2)
        // and Historical Research Profile (Phase 3)
        HistoricalResearchProfile? historicalProfile = null;
        try
        {
            historicalProfile = await _profileBuilder.GetProfileAsync(ticker);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[prediction] Historical profile lookup skipped for {Ticker}", ticker);
        }

        var researchUniverse = researchAsset is not null
            ? new ResearchUniverseContext
            {
                InterestScore = researchAsset.InterestScore,
                EvidenceCount = researchAsset.EvidenceCount,
                ResearchState = researchAsset.CurrentState,
                DaysActive = researchAsset.DaysActive,
                HasResearchAsset = true,
                HistoricalVolatility = historicalProfile?.HistoricalVolatility,
                HistoricalAtrPercent = historicalProfile?.AtrPercent,
                PreviousPredictionAccuracy = historicalProfile?.PreviousPredictionAccuracy,
                PreviousPredictionCount = historicalProfile?.PreviousPredictionCount ?? 0,
            }
            : historicalProfile is not null
                ? new ResearchUniverseContext
                {
                    // Watchlist fallback — no ResearchAsset but profile exists
                    HasResearchAsset = false,
                    HistoricalVolatility = historicalProfile.HistoricalVolatility,
                    HistoricalAtrPercent = historicalProfile.AtrPercent,
                    PreviousPredictionAccuracy = historicalProfile.PreviousPredictionAccuracy,
                    PreviousPredictionCount = historicalProfile.PreviousPredictionCount,
                }
                : null;

        // ── VOE: compute volatility context before scoring ───────
        var volatilityAssessment = _voe.Assess(
            ticker, snapshot.RecentBars, indicators, snapshot.NewsContext);

        if (volatilityAssessment.Opportunity != Models.OpportunityType.None)
        {
            _logger.LogInformation(
                "[prediction] {Ticker}: VOE classified {Opportunity} (regime={Regime}, ATR pctile={AtrPctile})",
                ticker, volatilityAssessment.Opportunity, volatilityAssessment.StockVolRegime,
                volatilityAssessment.AtrPercentile?.ToString("F0") ?? "n/a");
        }

        // Persist assessment (fire-and-forget — non-blocking, best-effort)
        _ = _repo.SaveVolatilityAssessmentAsync(volatilityAssessment, runId);

        // ── Earnings calendar lookup ──────────────────────────────────
        int? daysUntilEarnings = null;
        double? estimatedEps = null;
        if (sharedContext?.EarningsCalendar is not null
            && sharedContext.EarningsCalendar.TryGetValue(ticker, out var earningsEntry))
        {
            if (DateTime.TryParse(earningsEntry.Date, out var earningsDate))
            {
                daysUntilEarnings = (int)(earningsDate - DateTime.UtcNow.Date).TotalDays;
                if (daysUntilEarnings < 0) daysUntilEarnings = 0; // reporting today
            }
            estimatedEps = earningsEntry.EstimateEps;
        }

        ScoringEngine.ScoringResult scoring;
        EnsembleScoringService.EnsembleResult? ensembleResult = null;

        if (_ensembleEnabled)
        {
            ensembleResult = await _ensemble.ScoreWithEnsembleAsync(
                snapshot, indicators, benchmark, weights, lessons, researchSignals,
                intelligence, researchUniverse, volatilityAssessment, regimeResult,
                daysUntilEarnings, estimatedEps);
            scoring = ensembleResult.BlendedResult;
            _logger.LogInformation(
                "[prediction] {Ticker}: ensemble scoring — agreement={Agreement:P0}, dominant={Dominant}",
                ticker, ensembleResult.Agreement, ensembleResult.DominantModel);
        }
        else
        {
            scoring = _scoringEngine.Evaluate(
                snapshot, indicators, benchmark, weights, lessons, researchSignals,
                intelligence, researchUniverse, volatilityAssessment, regimeResult,
                daysUntilEarnings, estimatedEps);
        }

        var predType = scoring.PredictionType;
        var confidence = scoring.Confidence;
        var risk = scoring.Risk;
        var totalScore = scoring.DirectionalScore;
        var bullishScore = scoring.BullishScore;
        var bearishScore = scoring.BearishScore;
        var winningDirection = scoring.WinningDirection;
        var directionMargin = scoring.DirectionMargin;
        var allSignals = scoring.Signals;

        // ── Market stress adjustments ──────────────────────────────────
        // When the market is stressed (high VIX, SPY dropping, oil spiking):
        //   1. Apply bearish bias and re-evaluate direction (offensive — capitalize on downturn)
        //   2. Enforce confidence floor on BULLISH predictions only (defensive — skip weak longs)
        MarketStressResult? stressResult = null;
        try
        {
            stressResult = await _stressDetector.EvaluateAsync();
            if (stressResult.IsStressed)
            {
                // Apply bearish bias and re-determine direction so it actually shifts predictions
                if (stressResult.BearishBias > 0)
                {
                    bearishScore += stressResult.BearishBias;
                    var (newDirection, newType) = ScoringEngine.DeterminePredictionType(
                        bullishScore, bearishScore, snapshot, indicators, weights);

                    if (newDirection != winningDirection || newType != predType)
                    {
                        _logger.LogInformation(
                            "[prediction] {Ticker}: stress bias shifted {OldType}→{NewType} (bearish +{Bias:F1})",
                            ticker, predType, newType, stressResult.BearishBias);
                        winningDirection = newDirection;
                        predType = newType;
                    }
                    else
                    {
                        _logger.LogInformation(
                            "[prediction] {Ticker}: stress bias +{Bias:F1} applied, direction unchanged ({Type})",
                            ticker, stressResult.BearishBias, predType);
                    }

                    directionMargin = bullishScore - bearishScore;
                    totalScore = Math.Max(bullishScore, bearishScore);
                }

                // Confidence floor — only block BULLISH predictions during stress.
                // Bearish predictions flow through (that's the offensive strategy).
                if (stressResult.ConfidenceFloor > 0 && confidence < stressResult.ConfidenceFloor
                    && predType == "bullish")
                {
                    _logger.LogInformation(
                        "[prediction] {Ticker}: bullish confidence {Conf} below stress floor {Floor} — downgrading to watch_only",
                        ticker, confidence, stressResult.ConfidenceFloor);
                    predType = "watch_only";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[prediction] Market stress check failed for {Ticker}", ticker);
        }

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

        if (ensembleResult is not null)
            dataSources.Add("ensemble-scoring");

        if (!snapshot.DataAvailability.OptionsChainAvailable)
            missingWarnings.Add("Options-chain data not connected — cannot confirm options setups");

        // ── Step 3: Ask OpenAI to explain the computed prediction ───
        var explanation = await GetAiExplanationAsync(
            ticker, snapshot, predType, totalScore, confidence, risk,
            allSignals, weights, lessons, benchmark);

        if (explanation is not null)
            dataSources.Add("openai-analysis");

        // ── Apply AI confidence adjustment ──
        // The AI is the decision-maker — it sees the full picture (trend, catalyst,
        // news, technicals, learning context) and adjusts confidence like a trader would.
        // Range: -30 to +25. This is intentionally wide — the AI should swing
        // confidence hard when the story is clear or clearly wrong.
        if (explanation?.ConfidenceAdjustment is int aiAdj and not 0)
        {
            var clampedAdj = Math.Clamp(aiAdj, -30, 25);
            var prevConf = confidence;
            confidence = Math.Clamp(confidence + clampedAdj, 5, 100);
            allSignals.Add($"AI confidence adjustment: {clampedAdj:+0;-0} ({explanation.AiAdjustmentReason ?? "no reason"})");
            _logger.LogInformation(
                "[prediction] {Ticker}: AI adjusted confidence {Prev}→{New} ({Adj:+0;-0}): {Reason}",
                ticker, prevConf, confidence, clampedAdj, explanation.AiAdjustmentReason ?? "n/a");
        }

        // ── Apply AI trade flag ──
        // "avoid" demotes to watch_only — the AI thinks this trade shouldn't be taken.
        // "caution" docks 15 confidence points — marginal setup, proceed with less size.
        // "strong_buy" boosts confidence by 15 on top of any adjustment — clear conviction.
        if (explanation?.AiFlag is "avoid" && predType is "bullish" or "bearish")
        {
            predType = "watch_only";
            confidence = Math.Min(confidence, 20);
            var avoidReason = $"AI flagged AVOID: {explanation.AiAdjustmentReason ?? "conflicting signals"}";
            missingWarnings.Add(avoidReason);
            _logger.LogInformation("[prediction] {Ticker}: AI vetoed trade → watch_only: {Reason}",
                ticker, explanation.AiAdjustmentReason ?? "n/a");
        }
        else if (explanation?.AiFlag is "caution")
        {
            confidence = Math.Max(5, confidence - 15);
            allSignals.Add($"AI caution flag: {explanation.AiAdjustmentReason ?? "borderline trade"}");
        }
        else if (explanation?.AiFlag is "strong_buy")
        {
            confidence = Math.Clamp(confidence + 15, 5, 100);
            allSignals.Add($"AI STRONG BUY: {explanation.AiAdjustmentReason ?? "high-conviction setup"}");
            _logger.LogInformation("[prediction] {Ticker}: AI flagged STRONG BUY (+15 confidence): {Reason}",
                ticker, explanation.AiAdjustmentReason ?? "n/a");
        }

        // --- AI direction override ---
        // AI can flip the scoring engine's direction when evidence clearly contradicts it
        if (explanation?.DirectionOverride is "bullish" or "bearish"
            && explanation.DirectionOverride != predType
            && predType is "bullish" or "bearish") // don't override watch_only
        {
            var oldDirection = predType;
            predType = explanation.DirectionOverride;
            allSignals.Add($"AI direction override: {oldDirection} → {predType} ({explanation.AiAdjustmentReason ?? "AI sees opposite setup"})");
            _logger.LogInformation("[prediction] {Ticker}: AI overrode direction {Old} → {New}: {Reason}",
                ticker, oldDirection, predType, explanation.AiAdjustmentReason ?? "n/a");
        }

        // Fall back to signal-derived explanation if AI unavailable
        var bullishCase = explanation?.BullishCase
            ?? string.Join("; ", allSignals.Where(s => !s.Contains("bearish") && !s.Contains("negative") && !s.Contains("below")));
        var bearishCase = explanation?.BearishCase
            ?? string.Join("; ", allSignals.Where(s => s.Contains("bearish") || s.Contains("negative") || s.Contains("below")));
        var thesis = explanation?.Thesis
            ?? scoring.Thesis?.Narrative
            ?? $"Score: {totalScore:F1}. Signals: {allSignals.Count}. {predType} stance based on {(dataSources.Count > 0 ? string.Join(" + ", dataSources) : "limited data")}.";
        var invalidation = explanation?.InvalidationRule
            ?? (predType == "bullish"
                ? "Invalidate if price drops >2% from entry or bearish catalyst emerges"
                : predType == "bearish"
                    ? "Invalidate if price rises >2% from entry or bullish catalyst emerges"
                    : "Invalidate if major catalyst changes thesis direction");

        // ── Step 4: Dynamic time window + ATR-based price prediction engine ──
        var timeWindow = DetermineTimeWindow(scoring.Breakdown);

        // ── 1-day bearish block ──
        // Data shows 1-day bearish predictions have only 14.3% accuracy (14 predictions).
        // Stocks bounce back too quickly for short-term bearish calls to work.
        // Downgrade to watch_only and cap confidence.
        if (timeWindow == PredictionTimeWindows.OneDay && predType == "bearish")
        {
            predType = "watch_only";
            confidence = Math.Min(confidence, 25);
            _logger.LogInformation(
                "[prediction] {Ticker}: 1-day bearish downgraded to watch_only — historical accuracy 14.3%",
                ticker);
        }

        var entryPrice = snapshot.Quote?.Price;
        var priceCalc = ComputeAtrPriceForecast(
            entryPrice, predType, timeWindow, snapshot, confidence, risk, scoring.Breakdown, researchUniverse, weights);

        // ── Let AI override price targets when it provides them ──
        // The AI sees trend, catalyst, news, market context — it may have a better
        // sense of where this stock is headed than pure ATR math.
        if (explanation?.PredictedPrice is > 0 && entryPrice is > 0 && priceCalc.PredictedPrice is > 0)
        {
            var aiTarget = explanation.PredictedPrice.Value;
            // Sanity check: AI target must be in the right direction and within 20% of entry
            var aiMoveFromEntry = (aiTarget - entryPrice.Value) / entryPrice.Value * 100;
            var isDirectionCorrect = (predType == "bullish" && aiMoveFromEntry > 0) ||
                                     (predType == "bearish" && aiMoveFromEntry < 0);
            if (isDirectionCorrect && Math.Abs(aiMoveFromEntry) <= 20)
            {
                // Blend: 60% mechanical, 40% AI target for predicted price
                var blendedTarget = priceCalc.PredictedPrice.Value * 0.6 + aiTarget * 0.4;
                priceCalc.PredictedPrice = Math.Round(blendedTarget, 2);
                allSignals.Add($"AI target price: ${aiTarget:F2} (blended into prediction)");
            }
        }

        // ── Use AI's key levels for support/resistance when provided ──
        if (explanation?.KeyLevels is not null)
        {
            if (explanation.KeyLevels.Support is > 0)
                priceCalc.SupportLevel = explanation.KeyLevels.Support.Value;
            if (explanation.KeyLevels.Resistance is > 0)
                priceCalc.ResistanceLevel = explanation.KeyLevels.Resistance.Value;

            // ── AI-informed stop price ──
            // For bullish: if AI's support is below entry but within 5%, use it as a
            // smarter stop than pure ATR math. A support level based on chart structure
            // (prior lows, moving average, demand zone) is where a real trader would put their stop.
            // For bearish: if AI's resistance is above entry but within 5%, same logic.
            if (entryPrice is > 0 && priceCalc.StopPrice is > 0)
            {
                if (predType == "bullish" && explanation.KeyLevels.Support is > 0)
                {
                    var aiStop = explanation.KeyLevels.Support.Value;
                    var distFromEntry = (entryPrice.Value - aiStop) / entryPrice.Value * 100;
                    // AI stop must be below entry (valid bullish stop) and within 5% (reasonable)
                    if (aiStop < entryPrice.Value && distFromEntry > 0.5 && distFromEntry <= 5.0)
                    {
                        // Blend: 50% mechanical, 50% AI stop
                        priceCalc.StopPrice = Math.Round(priceCalc.StopPrice.Value * 0.5 + aiStop * 0.5, 2);
                        allSignals.Add($"AI stop level: ${aiStop:F2} (support-based, blended into stop)");
                    }
                }
                else if (predType == "bearish" && explanation.KeyLevels.Resistance is > 0)
                {
                    var aiStop = explanation.KeyLevels.Resistance.Value;
                    var distFromEntry = (aiStop - entryPrice.Value) / entryPrice.Value * 100;
                    if (aiStop > entryPrice.Value && distFromEntry > 0.5 && distFromEntry <= 5.0)
                    {
                        priceCalc.StopPrice = Math.Round(priceCalc.StopPrice.Value * 0.5 + aiStop * 0.5, 2);
                        allSignals.Add($"AI stop level: ${aiStop:F2} (resistance-based, blended into stop)");
                    }
                }
            }
        }

        // Second-pass finalization: apply R/R-aware caps + actionability tier
        // now that we know the risk/reward ratio.
        scoring = ScoringEngine.FinalizeWithRiskReward(scoring, priceCalc.RiskRewardRatio);

        // Setup history adjustment: if this fingerprint has historical performance
        // data, boost or penalize confidence accordingly.
        try
        {
            var setupEvidence = TradeSetupEngine.BuildSignalEvidenceFromBreakdown(scoring.Breakdown);
            var setupFp = TradeSetupEngine.GenerateFingerprint(setupEvidence, scoring.WinningDirection);
            if (!string.IsNullOrEmpty(setupFp.Fingerprint))
            {
                var setupPerf = await _setupEngine.LookupSetupPerformanceAsync(setupFp.Fingerprint);
                var isFavorable = TradeSetupEngine.IsHistoricallyFavorable(setupPerf, null);
                scoring = ScoringEngine.AdjustForSetupHistory(scoring, setupPerf, isFavorable);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[prediction] Setup history adjustment skipped for {Ticker}", ticker);
        }

        confidence = scoring.Confidence;

        // ── Per-ticker confidence reliability factor (Bayesian-smoothed) ──
        // Delegated to ScoringEngine.AdjustForTickerReliability (single source of truth).
        try
        {
            var tickerOutcomes = await _repo.GetTickerAccuracyFromOutcomesAsync(ticker);

            if (tickerOutcomes is not null && tickerOutcomes.Value.Total >= 5)
            {
                int n = tickerOutcomes.Value.Total;
                double tickerAccuracy = (double)tickerOutcomes.Value.Correct / n;

                var globalStats = await _repo.GetPredictionStatsAsync(profileId: sharedContext?.ProfileId);
                double globalAccuracy = globalStats.EvaluatedPredictions > 0
                    ? (double)globalStats.CorrectPredictions / globalStats.EvaluatedPredictions
                    : 0.50;

                var prevConfidence = confidence;
                var (adjusted, shouldDowngrade) = ScoringEngine.AdjustForTickerReliability(
                    scoring, tickerAccuracy, n, globalAccuracy);
                scoring = adjusted;
                confidence = scoring.Confidence;

                if (shouldDowngrade)
                {
                    predType = "watch_only";
                    _logger.LogInformation(
                        "[prediction] {Ticker}: downgrading to watch_only — ticker reliability triggered",
                        ticker);
                }
                else if (confidence != prevConfidence)
                {
                    _logger.LogInformation(
                        "[prediction] {Ticker}: ticker reliability adjusted confidence {Prev}→{New} (accuracy={Acc:F0}%, n={N})",
                        ticker, prevConfidence, confidence, tickerAccuracy * 100, n);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[prediction] Ticker reliability lookup skipped for {Ticker}", ticker);
        }

        // ── Time window confidence adjustment ─────────────────────────
        // Data: 1-day predictions have 33.3% accuracy vs 52.7% for 3-day.
        // 1-week has 43.3%. Apply DB-configurable penalties to suppress
        // time windows that historically underperform.
        {
            var twPenalty1Day = Math.Clamp(weights.GetValueOrDefault("tw_penalty_1day", 0.85), 0.5, 1.0);
            var twPenalty1Week = Math.Clamp(weights.GetValueOrDefault("tw_penalty_1week", 0.93), 0.5, 1.0);

            if (timeWindow == PredictionTimeWindows.OneDay)
            {
                var prevConf = confidence;
                confidence = (int)Math.Round(confidence * twPenalty1Day);
                _logger.LogInformation(
                    "[prediction] {Ticker}: 1-day time window penalty {Penalty:F2} → conf {Prev}→{New} (33% historical accuracy)",
                    ticker, twPenalty1Day, prevConf, confidence);
            }
            else if (timeWindow == PredictionTimeWindows.OneWeek)
            {
                var prevConf = confidence;
                confidence = (int)Math.Round(confidence * twPenalty1Week);
                if (confidence != prevConf)
                    _logger.LogInformation(
                        "[prediction] {Ticker}: 1-week time window penalty {Penalty:F2} → conf {Prev}→{New}",
                        ticker, twPenalty1Week, prevConf, confidence);
            }
        }

        // ── Expected move sweet-spot adjustment ──────────────────────
        // Data: 4-7% expected move = 54.2% accuracy (best).
        //       2-4% = 41.3% (worst). <2% = 47.4%.
        // Stocks that need to move a meaningful but not extreme amount
        // are the most predictable. Penalize small expected moves where
        // the stock doesn't have enough room to hit targets.
        if (priceCalc.ExpectedMovePercent is double expMove && expMove > 0)
        {
            var smallMovePenalty = Math.Clamp(weights.GetValueOrDefault("exp_move_small_penalty", 0.90), 0.5, 1.0);
            var smallMoveThreshold = weights.GetValueOrDefault("exp_move_small_threshold", 3.0);
            var sweetSpotBonus = Math.Clamp(weights.GetValueOrDefault("exp_move_sweet_bonus", 1.05), 1.0, 1.15);
            var sweetSpotLow = weights.GetValueOrDefault("exp_move_sweet_low", 4.0);
            var sweetSpotHigh = weights.GetValueOrDefault("exp_move_sweet_high", 7.0);

            if (expMove < smallMoveThreshold)
            {
                var prevConf = confidence;
                confidence = (int)Math.Round(confidence * smallMovePenalty);
                if (confidence != prevConf)
                    _logger.LogInformation(
                        "[prediction] {Ticker}: small expected move {Move:F1}% < {Thresh}% → conf {Prev}→{New}",
                        ticker, expMove, smallMoveThreshold, prevConf, confidence);
            }
            else if (expMove >= sweetSpotLow && expMove <= sweetSpotHigh)
            {
                var prevConf = confidence;
                confidence = (int)Math.Round(confidence * sweetSpotBonus);
                if (confidence != prevConf)
                    _logger.LogInformation(
                        "[prediction] {Ticker}: sweet-spot expected move {Move:F1}% → conf {Prev}→{New}",
                        ticker, expMove, prevConf, confidence);
            }
        }

        // ── Direction bias adjustment ────────────────────────────────
        // Data: bearish predictions at high confidence have 25.9% accuracy
        // vs bullish at 49.3%. Bearish signals tend to be overconfident.
        // Apply DB-configurable multipliers per direction.
        {
            var bearishMult = Math.Clamp(weights.GetValueOrDefault("direction_bearish_mult", 0.90), 0.5, 1.2);
            var bullishMult = Math.Clamp(weights.GetValueOrDefault("direction_bullish_mult", 1.0), 0.5, 1.2);

            if (winningDirection == "bearish" && Math.Abs(bearishMult - 1.0) > 0.005)
            {
                var prevConf = confidence;
                confidence = (int)Math.Round(confidence * bearishMult);
                _logger.LogInformation(
                    "[prediction] {Ticker}: bearish direction bias {Mult:F2} → conf {Prev}→{New}",
                    ticker, bearishMult, prevConf, confidence);
            }
            else if (winningDirection == "bullish" && Math.Abs(bullishMult - 1.0) > 0.005)
            {
                var prevConf = confidence;
                confidence = (int)Math.Round(confidence * bullishMult);
                _logger.LogInformation(
                    "[prediction] {Ticker}: bullish direction bias {Mult:F2} → conf {Prev}→{New}",
                    ticker, bullishMult, prevConf, confidence);
            }
        }

        // Track downgrade reasons for watch_only calibration learning.
        // Start with ScoringEngine's actionability reasons, then add R:R downgrade if applicable.
        var downgradeReasons = new List<string>();
        foreach (var reason in scoring.Breakdown.ActionabilityReasons)
        {
            if (reason.Contains("Downgraded", StringComparison.OrdinalIgnoreCase))
                downgradeReasons.Add(reason);
        }

        // If R:R ratio is poor, downgrade to watch_only.
        // Threshold is 0.8 — below this the trade doesn't make sense from a
        // risk management perspective regardless of directional conviction.
        if (priceCalc.RiskRewardRatio is double rr and < 0.8
            && (predType == "bullish" || predType == "bearish"))
        {
            predType = "watch_only";
            var rrReason = $"Downgraded to watch_only: R:R ratio {rr:F2} < 0.8 — risk exceeds potential reward";
            priceCalc.Warnings.Add(rrReason);
            downgradeReasons.Add(rrReason);
        }

        // ── Step 5: Assemble prediction (scores from engine, text from AI) ──
        var prediction = new PredictionCandidate
        {
            RunId = runId,
            Ticker = ticker,
            PredictionType = Enum.TryParse<PredictionType>(predType, out var pt) ? pt : PredictionType.neutral_no_edge,
            AssetType = PredictionAssetType.stock,
            TimeWindow = timeWindow,
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
            ScoreDebugJson = JsonSerializer.Serialize(
                ensembleResult is not null
                    ? new {
                        scoring.Breakdown,
                        Ensemble = new { ensembleResult.Agreement, ensembleResult.DominantModel, Models = ensembleResult.ModelScores.Select(m => new { m.ModelName, m.HistoricalAccuracy, m.ModelWeight }) },
                        Volatility = new {
                            volatilityAssessment.AtrPercentile,
                            StockVolRegime = volatilityAssessment.StockVolRegime.ToString(),
                            GapType = volatilityAssessment.GapClassification.ToString(),
                            volatilityAssessment.GapPercent,
                            OpportunityType = volatilityAssessment.Opportunity.ToString(),
                            volatilityAssessment.OpportunityScore,
                            volatilityAssessment.RiskModifier,
                        },
                    }
                    : (object)new {
                        scoring.Breakdown,
                        Volatility = new {
                            volatilityAssessment.AtrPercentile,
                            StockVolRegime = volatilityAssessment.StockVolRegime.ToString(),
                            GapType = volatilityAssessment.GapClassification.ToString(),
                            volatilityAssessment.GapPercent,
                            OpportunityType = volatilityAssessment.Opportunity.ToString(),
                            volatilityAssessment.OpportunityScore,
                            volatilityAssessment.RiskModifier,
                        },
                    },
                new JsonSerializerOptions { WriteIndented = false }),
            IndicatorsJson = JsonSerializer.Serialize(indicators, new JsonSerializerOptions { WriteIndented = false }),
            WeightsSnapshotJson = JsonSerializer.Serialize(weights, new JsonSerializerOptions { WriteIndented = false }),
            ActionabilityScore = scoring.Breakdown.ActionabilityScore,
            ActionabilityTier = scoring.Breakdown.ActionabilityTier,
            DowngradeReasons = downgradeReasons,
            ExpectedValuePercent = ComputeExpectedValue(confidence, priceCalc.TargetPrice, priceCalc.StopPrice, entryPrice),
            Status = PredictionCategoryHelper.IsPassThrough(pt) ? "passed" : "open",
            ProfileId = sharedContext?.ProfileId,
        };

        var inputs = BuildInputs(ticker, snapshot, lessons, intelligence);
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

    /// <summary>
    /// A neutral prediction that should be superseded after the replacement is persisted.
    /// Keyed by ticker+timeWindow so the correct replacement ID can be resolved post-sort.
    /// </summary>
    public record PendingSupersession(string NeutralPredictionId, string ReplacementTicker, string ReplacementTimeWindow, string Reason);

    public async Task<(List<PredictionCandidate> Predictions, List<PredictionInput> AllInputs, List<PendingSupersession> Supersessions)>
        GeneratePredictionsForWatchlistAsync(string[] watchlist, string runId, List<MarketSnapshot> snapshots, Dictionary<string, ResearchAsset>? assetLookup = null, string? profileId = null)
    {
        var predictions = new List<PredictionCandidate>();
        var allInputs = new List<PredictionInput>();
        var pendingSupersessions = new List<PendingSupersession>();

        // ── Dedup: fetch recent predictions (any status) created today and build
        // ticker→time_windows lookup. This prevents duplicates within the same day
        // even if prior predictions were already evaluated/closed.
        // Preload shared data once instead of per-ticker (saves 3 DB queries x N tickers)
        var sharedContext = await PreloadSharedContextAsync(profileId);
        _logger.LogInformation("[prediction] Preloaded shared context: {WeightCount} weights, {LessonCount} lessons",
            sharedContext.Weights.Count, sharedContext.Lessons.Count);

        // Scope dedup to the current profile so challenger predictions don't block champion slots.
        // Multi-day ticker cooldown: don't re-predict the same ticker+timeWindow combo
        // if one was generated within the cooldown period. Default 2 days.
        // This kills the "CAT picked 21 times in 14 days" ticker spam problem.
        var tickerCooldownDays = (int)sharedContext.Weights.GetValueOrDefault("ticker_prediction_cooldown_days", 2.0);
        var cooldownStart = DateTimeOffset.UtcNow.Date.AddDays(-(tickerCooldownDays - 1));
        var recentPredictions = await _repo.GetPredictionsByDateRangeAsync(
            cooldownStart, DateTimeOffset.UtcNow, profileId: sharedContext.ProfileId);
        // Also include open predictions from earlier days
        var openPredictions = await _repo.GetOpenPredictionsAsync(profileId: sharedContext.ProfileId);
        var allExisting = recentPredictions
            .Concat(openPredictions)
            .DistinctBy(p => p.Id)
            .ToList();

        // Build ticker → (time_window → prediction) lookup for supersession checks
        var existingByTickerAndWindow = allExisting
            .GroupBy(p => p.Ticker.ToUpperInvariant())
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(p => p.TimeWindow, StringComparer.OrdinalIgnoreCase)
                      .ToDictionary(wg => wg.Key, wg => wg.First(), StringComparer.OrdinalIgnoreCase));

        // Track within-batch additions to prevent intra-batch duplicates
        var batchTracker = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // ── Min stock price gate: skip cheap stocks at prediction level ──
        var minStockPrice = sharedContext.Weights.GetValueOrDefault("min_stock_price", 10.0);

        // ── Per-ticker accuracy gate: skip tickers with proven poor prediction accuracy ──
        var tickerAccuracyMinPct = sharedContext.Weights.GetValueOrDefault("ticker_accuracy_min_pct", 30.0);
        var tickerAccuracyMinPredictions = (int)sharedContext.Weights.GetValueOrDefault("ticker_accuracy_min_predictions", 4.0);
        Dictionary<string, (int Total, int Correct, double AccuracyPct)> tickerAccuracies;
        try
        {
            tickerAccuracies = await _repo.GetAllTickerAccuraciesAsync();
            var blocked = tickerAccuracies.Count(kv => kv.Value.Total >= tickerAccuracyMinPredictions && kv.Value.AccuracyPct < tickerAccuracyMinPct);
            if (blocked > 0)
                _logger.LogInformation("[prediction] Ticker accuracy gate: {Blocked} tickers below {Min}% accuracy will be skipped", blocked, tickerAccuracyMinPct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[prediction] Failed to load ticker accuracies — gate disabled for this run");
            tickerAccuracies = new();
        }

        // ── Momentum pre-screen: rank tickers by recent momentum + catalyst presence ──
        // Think like a trader: scan for movers first, then look at the chart.
        // Stocks with strong 3-month trends AND catalyst news get processed first.
        var rankedSnapshots = snapshots
            .Select(s => new
            {
                Snapshot = s,
                MomentumScore = ComputeMomentumRank(s),
            })
            .OrderByDescending(x => x.MomentumScore)
            .Select(x => x.Snapshot)
            .ToList();

        _logger.LogInformation("[prediction] Momentum pre-screen ranked {Count} tickers. Top 5: {Top5}",
            rankedSnapshots.Count,
            string.Join(", ", rankedSnapshots.Take(5).Select(s =>
                $"{s.Ticker}({ComputeMomentumRank(s):F0})")));

        foreach (var snapshot in rankedSnapshots)
        {
            // Filter out stocks below min_stock_price before wasting an API call.
            // BUG FIX: when Quote.Price is null/0 (market data fetch failed for low-volume junk),
            // the old check `quotePrice > 0 && quotePrice < min` silently passed them through.
            // Now: if we have no price data at all, skip the ticker — no price = untradeable.
            var quotePrice = snapshot.Quote?.Price ?? 0;
            if (quotePrice <= 0)
            {
                _logger.LogInformation("[prediction] Skipping {Ticker}: no quote price available — untradeable",
                    snapshot.Ticker);
                continue;
            }
            if (quotePrice < minStockPrice)
            {
                _logger.LogDebug("[prediction] Skipping {Ticker}: price ${Price:F2} < min ${Min:F2}",
                    snapshot.Ticker, quotePrice, minStockPrice);
                continue;
            }

            // ── Catalyst + trend gate: think like a trader ──
            // Skip stocks that have no story AND no sustained trend.
            // A trader wouldn't waste time on a stock with no news and sideways price action.
            var catalystGateMinImportance = sharedContext.Weights.GetValueOrDefault("catalyst_gate_min_importance", 50.0);
            var hasCatalyst = snapshot.NewsContext.Any(n => n.ImportanceScore >= catalystGateMinImportance);
            var trendStr = snapshot.TechnicalContext?.ThreeMonthTrendStructure;
            var hasStrongTrend = trendStr is "strong_uptrend" or "uptrend" or "strong_downtrend" or "downtrend";
            var oneMonthMove = Math.Abs(snapshot.TechnicalContext?.OneMonthChangePct ?? 0);

            if (!hasCatalyst && !hasStrongTrend && oneMonthMove < 5)
            {
                _logger.LogInformation(
                    "[prediction] Skipping {Ticker}: no catalyst (top importance: {TopImp:F0}), " +
                    "trend={Trend}, 1M move={Move:F1}% — no story, no momentum",
                    snapshot.Ticker,
                    snapshot.NewsContext.Count > 0 ? snapshot.NewsContext.Max(n => n.ImportanceScore) : 0,
                    trendStr ?? "unknown",
                    snapshot.TechnicalContext?.OneMonthChangePct ?? 0);
                continue;
            }

            // ── Per-ticker accuracy gate ──
            // Skip tickers with proven poor prediction accuracy (e.g. VRTX 0/4, MWH 0/4).
            // Learning engine identifies these but only as advisory text — this is the mechanical block.
            var tickerKey0 = snapshot.Ticker.ToUpperInvariant();
            if (tickerAccuracies.TryGetValue(tickerKey0, out var tickerAcc)
                && tickerAcc.Total >= tickerAccuracyMinPredictions
                && tickerAcc.AccuracyPct < tickerAccuracyMinPct)
            {
                _logger.LogInformation(
                    "[prediction] Skipping {Ticker}: historical accuracy {Acc:F0}% ({Correct}/{Total}) below min {Min}%",
                    snapshot.Ticker, tickerAcc.AccuracyPct, tickerAcc.Correct, tickerAcc.Total, tickerAccuracyMinPct);
                continue;
            }

            ResearchAsset? asset = null;
            assetLookup?.TryGetValue(snapshot.Ticker, out asset);
            var (pred, inputs) = await GeneratePredictionForTickerAsync(
                snapshot.Ticker, runId, snapshot, asset, sharedContext);
            if (pred is not null)
            {
                // ── ATR floor gate: skip low-volatility stocks ──
                // Data: <1.5% ATR → 0% target hit, 78.6% stop hit (n=14).
                // Stops are too tight relative to the stock's natural movement.
                var minAtrPercent = sharedContext.Weights.GetValueOrDefault("min_atr_percent", 1.5);
                if (pred.AtrPercent is > 0 and double atrPct && atrPct < minAtrPercent)
                {
                    _logger.LogInformation(
                        "[prediction] Skipping {Ticker}: ATR {Atr:F2}% below min {Min:F1}% — low-vol stocks hit stops 78% of the time",
                        pred.Ticker, atrPct, minAtrPercent);
                    continue;
                }
                var tickerKey = pred.Ticker.ToUpperInvariant();
                var isNewDirectional = PredictionCategoryHelper.IsDirectional(pred.PredictionType);

                // Check against existing DB predictions
                if (existingByTickerAndWindow.TryGetValue(tickerKey, out var windowMap)
                    && windowMap.TryGetValue(pred.TimeWindow, out var existingPred))
                {
                    var isExistingNeutral = !PredictionCategoryHelper.IsDirectional(existingPred.PredictionType);

                    // Neutral → directional supersession: the new directional prediction
                    // replaces the neutral one that was holding the dedup slot.
                    // Supersession is deferred until after persistence so we have the
                    // replacement's DB-assigned ID.
                    if (isExistingNeutral && isNewDirectional && existingPred.Status == "open")
                    {
                        var reason = $"Neutral {existingPred.PredictionType} superseded by directional {pred.PredictionType} prediction";
                        pendingSupersessions.Add(new PendingSupersession(
                            existingPred.Id, pred.Ticker, pred.TimeWindow, reason));

                        _logger.LogInformation(
                            "[prediction] {Ticker}: will supersede neutral prediction {OldId} ({OldType}) with directional {NewType} for time_window={TimeWindow}",
                            pred.Ticker, existingPred.Id, existingPred.PredictionType, pred.PredictionType, pred.TimeWindow);

                        // Update the lookup so subsequent batch items see the new prediction
                        windowMap[pred.TimeWindow] = pred;
                    }
                    else
                    {
                        _logger.LogInformation(
                            "[prediction] {Ticker}: skipping — prediction already exists today with time_window={TimeWindow}",
                            pred.Ticker, pred.TimeWindow);
                        continue;
                    }
                }

                // Check against earlier items in this batch
                if (batchTracker.TryGetValue(tickerKey, out var batchWindows)
                    && batchWindows.Contains(pred.TimeWindow))
                {
                    _logger.LogInformation(
                        "[prediction] {Ticker}: skipping — duplicate within this batch for time_window={TimeWindow}",
                        pred.Ticker, pred.TimeWindow);
                    continue;
                }

                // Track this prediction
                if (!batchTracker.ContainsKey(tickerKey))
                    batchTracker[tickerKey] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                batchTracker[tickerKey].Add(pred.TimeWindow);

                predictions.Add(pred);
                allInputs.AddRange(inputs);
            }
        }

        predictions.Sort((a, b) => b.ConfidenceScore.CompareTo(a.ConfidenceScore));
        return (predictions, allInputs, pendingSupersessions);
    }

    // -----------------------------------------------------------------------
    // OpenAI call — AI is the decision-maker for confidence and trade flags
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
        List<string> lessons,
        BenchmarkContext? benchmark = null)
    {
        if (_chatClient is null) return null;

        try
        {
            var learningContext = await GetLearningContextAsync();
            var systemPrompt = BuildExplanationSystemPrompt();
            var userPrompt = BuildExplanationUserPrompt(
                ticker, snapshot, direction, totalScore, confidence, risk, signals, weights, lessons, learningContext, benchmark);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt),
            };

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 800,
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
            };

            var completion = await _chatClient.CompleteChatAsync(messages, options);
            var text = completion.Value.Content.Count > 0 ? completion.Value.Content[0].Text : null;

            if (string.IsNullOrWhiteSpace(text)) return null;

            var result = JsonSerializer.Deserialize<AiExplanationResponse>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
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
            You are a swing trader making the final call on every prediction.
            The scoring engine computed a starting direction, confidence, and risk from
            market signals — but YOU are the decision-maker. You see the full picture:
            trend, catalyst, news, technicals, market context, and learning history.
            Your confidence_adjustment and ai_flag are what ultimately decide if a trade
            gets taken, boosted, or killed. Think like a trader with money on the line.

            TREND + CATALYST THINKING:
            - A stock in a strong 3-month uptrend with accelerating momentum AND a
              catalyst (earnings beat, sector rotation, institutional buying) is the
              highest-quality setup. Boost confidence for these.
            - A stock with no trend (sideways chop) and no catalyst is noise. Dock
              confidence and consider "caution" or "avoid".
            - A stock trending down but the scoring engine says bullish? That's a
              reversal bet — risky. Dock confidence unless the catalyst is overwhelming.
            - The 3-month trend matters MORE than the 5-day technicals for swing trades.

            You MUST respond with valid JSON matching this schema:
            {
              "thesis": "<1-3 sentence explanation of why the computed signals support this direction>",
              "bullish_case": "<specific bullish factors from the provided signals and data>",
              "bearish_case": "<specific bearish factors from the provided signals and data>",
              "invalidation_rule": "<specific price level or condition that would invalidate this prediction>",
              "key_levels": { "support": <price or null>, "resistance": <price or null> },
              "predicted_price": <number or null — your best estimate of where this stock will close at the end of the time window>,
              "predicted_move_percent": <number or null — expected % move from current price, positive for up, negative for down>,
              "confidence_adjustment": <integer from -30 to +25 — YOUR adjustment as the decision-maker>,
              "ai_flag": "<'strong_buy' | 'proceed' | 'caution' | 'avoid'> — your trade conviction>,
              "direction_override": "<null | 'bullish' | 'bearish'> — set ONLY if the scoring engine got the direction WRONG>,
              "ai_adjustment_reason": "<one-line reason for your confidence_adjustment and ai_flag>"
            }

            YOUR OUTPUTS DIRECTLY CONTROL THE TRADE:
            - predicted_price: blended into the actual price target. Be realistic.
            - key_levels.support / key_levels.resistance: used as the actual stop price.
              For bullish: set support at the nearest chart support (prior low, EMA, demand zone)
              where you'd actually place your stop. This is NOT decoration — it sets the stop.
              For bearish: set resistance at the nearest overhead level.
            - confidence_adjustment + ai_flag: control whether the trade gets taken at all.
            - direction_override: if the scoring engine picked the WRONG direction, you can
              flip it. Use this RARELY — only when the evidence clearly contradicts the engine.
              Example: engine says bullish but SPY is crashing, macro is risk_off, stock has no
              catalyst, and the 3-month trend is down. That's clearly bearish — flip it.
              Set to null (default) when the direction is reasonable.

            Rules:
            - Reference ONLY the signals, scores, and data provided. Do NOT invent signals.
            - Be specific about price levels from the bars provided (support/resistance).
            - Keep thesis to 1-3 sentences. Be concise and insightful.
            - Invalidation rule should reference specific price levels when possible.
            - predicted_price must be a realistic price based on the current price, signals, and key levels.
            - predicted_move_percent should match the direction (positive for bullish, negative for bearish).

            confidence_adjustment and ai_flag rules:
            - YOU ARE THE DECISION-MAKER. The scoring engine provides a starting point,
              but you have the final say on confidence. Think like a swing trader looking
              at a chart with the news in front of you. Would YOU take this trade?
            - confidence_adjustment: integer from -30 to +25. Use 0 ONLY if you truly
              agree with the score. Be decisive — don't be afraid to swing hard.
              Dock confidence (-10 to -30) when:
                * No catalyst — the stock has no reason to move. Why are we trading this?
                * 3-month trend is sideways or down but direction is bullish (fighting the trend)
                * Learning context shows this ticker or pattern has been failing
                * Bearish call in a strong bull regime (or vice versa)
                * Signals are thin or contradictory despite high computed score
                * The stock has no volume, no institutional interest, no story
                * SPY/QQQ bearish or macro risk_off but you're going bullish — fighting the market
                * Sector ETF trending bearish while stock is bullish — swimming upstream
              Boost confidence (+5 to +25) when:
                * Strong 3-month uptrend + accelerating momentum + fresh catalyst = dream setup
                * Big institutional stock (>$100B market cap) pulling back to support with catalyst
                * Earnings beat + technical breakout + sector in favor
                * Learning shows this pattern has been consistently winning
                * Everything aligns — trend, catalyst, technicals, AND market context
                * SPY/QQQ trending bullish + macro sentiment risk_on + sector ETF bullish + stock bullish = green light
                * Market is WITH your direction — wind at your back
            - ai_flag: "strong_buy" (high conviction — you'd put your own money here),
              "proceed" (normal — decent setup), "caution" (marginal — docks 15 confidence),
              "avoid" (this trade should NOT be taken — demotes to watch_only).
              Use "strong_buy" when trend + catalyst + technicals ALL align on a quality stock.
              Use "avoid" when the prediction contradicts obvious context (e.g.,
              bearish on a stock that just had a massive earnings beat in a bull market,
              or bullish on a stock with no catalyst and sideways/down 3-month trend).
            - ai_adjustment_reason: one line explaining your adjustment. Be specific.
              Example: "NVDA strong uptrend + AI spending catalyst + pullback to 20-day = dream setup (+20)"
              Example: "No catalyst, sideways 3 months, scoring engine fooled by 2-day bounce (-25)"

            Risk management principles — apply these when writing explanations:
            - A high-confidence call with a poor risk/reward ratio is NOT a good trade.
            - Earnings within 3 days dominate all other signals — acknowledge binary event risk.
            - If most signals agree but one major bucket (trend or market context) opposes,
              call out the conflict explicitly in the bearish/bullish case.
            - Reference the stop level and invalidation price in context of ATR — a stop
              that's less than 1 ATR away will likely get triggered by normal volatility.
            - If data quality is low (few indicators computed), say so in the thesis.
              High confidence on sparse data is reckless.
            - Never present a prediction as a certainty. Use language that reflects the
              probability: "signals favor", "setup suggests", "weight of evidence leans".
            - If fundamentals data is provided, reference it in your explanation.
              Mention how valuation (P/E), growth, profitability, or short interest
              supports or conflicts with the technical direction. If a "Confidence:
              fundamentals boost/drag" signal is present, explain what drove it.
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
        List<string> lessons,
        string? learningContext = null,
        BenchmarkContext? benchmark = null)
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

            // ── 3-month trend context — the bigger picture ──
            if (t.ThreeMonthTrendStructure is not null)
            {
                sb.AppendLine($"### 3-Month Trend Context:");
                sb.AppendLine($"  Structure: {t.ThreeMonthTrendStructure} | 3M Change: {t.ThreeMonthChangePct:+0.0;-0.0}% | 1M Change: {t.OneMonthChangePct:+0.0;-0.0}%");
                sb.AppendLine($"  Momentum: {t.MomentumTrend} | SMA50: ${t.Sma50:F2}");
                sb.AppendLine($"  Higher Highs: {t.HigherHighCount} | Higher Lows: {t.HigherLowCount}");
                if (t.ThreeMonthSummary is not null)
                    sb.AppendLine($"  Summary: {t.ThreeMonthSummary}");
                sb.AppendLine();
                sb.AppendLine("  >> IMPORTANT: Think like a swing trader. Ask yourself: Is this stock in a sustained");
                sb.AppendLine("  >> trend with a reason to keep going? A stock up 15% over 3 months with accelerating");
                sb.AppendLine("  >> momentum AND a catalyst is a much better setup than one with sideways chop and no news.");
                sb.AppendLine("  >> Weight the 3-month trend heavily in your confidence_adjustment.");
            }
        }

        // ── Market context — what is the overall market doing? ──
        if (benchmark is not null)
        {
            sb.AppendLine("### Market Context (CRITICAL — check this before deciding):");
            if (benchmark.SpyTrend is not null)
                sb.AppendLine($"  SPY: {benchmark.SpyChangePercent:+0.00;-0.00}% today | Trend: {benchmark.SpyTrend} | Multi-day: {benchmark.SpyMultiDayTrend ?? "n/a"}");
            if (benchmark.QqqTrend is not null)
                sb.AppendLine($"  QQQ: {benchmark.QqqChangePercent:+0.00;-0.00}% today | Trend: {benchmark.QqqTrend}");
            if (benchmark.SectorEtf is not null)
                sb.AppendLine($"  Sector ETF ({benchmark.SectorEtf}): trend={benchmark.SectorEtfTrend ?? "n/a"} | EMA ratio={benchmark.SectorEtfEmaRatio:F3}");
            if (benchmark.MacroSentiment is not null)
                sb.AppendLine($"  Macro sentiment: {benchmark.MacroSentiment} (confidence: {benchmark.MacroSentimentConfidence}/100, impact: {benchmark.MacroImpactDays} days)");
            if (benchmark.MacroThemes?.Count > 0)
                sb.AppendLine($"  Macro themes: {string.Join(", ", benchmark.MacroThemes)}");
            if (benchmark.RelativeStrengthVsSpy is not null)
                sb.AppendLine($"  Relative strength vs SPY: {benchmark.RelativeStrengthVsSpy:+0.00;-0.00}%");
            sb.AppendLine();
            sb.AppendLine("  >> A bullish pick in a risk_off / bearish SPY environment needs an EXTREMELY strong");
            sb.AppendLine("  >> stock-specific catalyst to overcome the headwind. Dock confidence if the market is");
            sb.AppendLine("  >> against the direction. Boost if the market is WITH the direction.");
        }

        if (snapshot.NewsContext.Count > 0)
        {
            sb.AppendLine("### News:");
            foreach (var n in snapshot.NewsContext.Take(5))
                sb.AppendLine($"  - [{n.CatalystType ?? "news"}] {n.Title} (sentiment: {n.Sentiment ?? "unknown"})");
        }

        if (snapshot.Fundamentals is not null)
        {
            var f = snapshot.Fundamentals;
            sb.AppendLine("### Fundamentals:");
            if (f.Sector is not null) sb.AppendLine($"  Sector: {f.Sector} | Industry: {f.Industry}");
            if (f.MarketCap is not null) sb.AppendLine($"  Market Cap: ${f.MarketCap:N0}");
            if (f.PeRatio is not null) sb.AppendLine($"  P/E: {f.PeRatio:F1} | Forward P/E: {(f.ForwardPe?.ToString("F1") ?? "n/a")}");
            if (f.PbRatio is not null) sb.AppendLine($"  P/B: {f.PbRatio:F2} | P/S: {(f.PsRatio?.ToString("F2") ?? "n/a")}");
            if (f.DividendYield is not null) sb.AppendLine($"  Dividend Yield: {f.DividendYield:P2}");
            if (f.ProfitMargin is not null) sb.AppendLine($"  Profit Margin: {f.ProfitMargin:P1} | Operating Margin: {(f.OperatingMargin?.ToString("P1") ?? "n/a")}");
            if (f.ReturnOnEquity is not null) sb.AppendLine($"  ROE: {f.ReturnOnEquity:P1} | Debt/Equity: {(f.DebtToEquity?.ToString("F2") ?? "n/a")}");
            if (f.RevenueGrowthYoy is not null) sb.AppendLine($"  Revenue Growth YoY: {f.RevenueGrowthYoy:P1} | Earnings Growth YoY: {(f.EarningsGrowthYoy?.ToString("P1") ?? "n/a")}");
            if (f.QuarterlyRevenueGrowth is not null) sb.AppendLine($"  Quarterly Rev Growth: {f.QuarterlyRevenueGrowth:P1} | Quarterly Earnings Growth: {(f.QuarterlyEarningsGrowth?.ToString("P1") ?? "n/a")}");
            if (f.Beta is not null) sb.AppendLine($"  Beta: {f.Beta:F2}");
            if (f.ShortPercentOfFloat is not null) sb.AppendLine($"  Short % of Float: {f.ShortPercentOfFloat:P1}");
            if (f.FiftyTwoWeekHigh is not null) sb.AppendLine($"  52-Week Range: ${f.FiftyTwoWeekLow:F2} - ${f.FiftyTwoWeekHigh:F2}");
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

        if (!string.IsNullOrEmpty(learningContext))
        {
            sb.AppendLine();
            sb.AppendLine("### System learning context (from yesterday's analysis):");
            sb.AppendLine(learningContext);
            sb.AppendLine();
            sb.AppendLine("Use this context to inform your confidence_adjustment and ai_flag. If this ticker or pattern has been failing, dock confidence. If the market regime conflicts with the direction, flag it.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Fetches the latest learning report and builds a compact context string
    /// for the AI prediction prompt. Cached per prediction run to avoid repeated DB calls.
    /// </summary>
    private async Task<string?> GetLearningContextAsync()
    {
        if (_cachedLearningContext is not null) return _cachedLearningContext;

        try
        {
            var report = await _repo.GetLatestLearningReportAsync();
            if (report is null) return null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"- Overall accuracy: {report.OverallAccuracy:P1} | Bullish: {report.BullAccuracy:P1} | Bearish: {report.BearAccuracy:P1}");
            if (report.MarketRegime is not null)
                sb.AppendLine($"- Market regime: {report.MarketRegime}");

            if (report.TopSignals.Count > 0)
            {
                sb.Append("- Strong signals: ");
                sb.AppendLine(string.Join(", ", report.TopSignals.Take(3).Select(s => $"{s.SignalName}({s.Accuracy:P0})")));
            }
            if (report.WeakSignals.Count > 0)
            {
                sb.Append("- Weak signals: ");
                sb.AppendLine(string.Join(", ", report.WeakSignals.Take(3).Select(s => $"{s.SignalName}({s.Accuracy:P0})")));
            }

            // Include key lines from AI summary — the most actionable part
            if (!string.IsNullOrEmpty(report.AiSummary))
            {
                // Extract first 500 chars of AI summary (the most relevant findings)
                var summarySnippet = report.AiSummary.Length > 500
                    ? report.AiSummary[..500] + "..."
                    : report.AiSummary;
                sb.AppendLine($"- AI analysis: {summarySnippet}");
            }

            if (report.WeightChanges.Count > 0)
            {
                sb.Append("- Recent weight changes: ");
                sb.AppendLine(string.Join(", ", report.WeightChanges.Select(
                    w => $"{w.SignalName} {w.ChangePercent:+0.0;-0.0}%")));
            }

            _cachedLearningContext = sb.ToString();
            return _cachedLearningContext;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[prediction] Failed to load learning context for AI prompt");
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static List<PredictionInput> BuildInputs(
        string ticker,
        MarketSnapshot snapshot,
        List<string> lessons,
        MarketIntelligenceContext intelligence)
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

        if (snapshot.Fundamentals is not null)
        {
            var f = snapshot.Fundamentals;
            var fundamentalParts = new List<string>();
            if (f.Sector is not null) fundamentalParts.Add($"Sector: {f.Sector}");
            if (f.PeRatio is not null) fundamentalParts.Add($"P/E: {f.PeRatio:F1}");
            if (f.MarketCap is not null) fundamentalParts.Add($"MktCap: ${f.MarketCap:N0}");
            if (f.RevenueGrowthYoy is not null) fundamentalParts.Add($"RevGrowth: {f.RevenueGrowthYoy:P1}");
            if (f.Beta is not null) fundamentalParts.Add($"Beta: {f.Beta:F2}");

            if (fundamentalParts.Count > 0)
            {
                inputs.Add(new PredictionInput
                {
                    PredictionId = "",
                    InputType = "fundamentals",
                    SourceName = "twelve-data-fundamentals",
                    Summary = string.Join(" | ", fundamentalParts),
                });
            }
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

        if (!string.IsNullOrWhiteSpace(intelligence.Thesis.Narrative))
        {
            inputs.Add(new PredictionInput
            {
                PredictionId = "",
                InputType = "market_thesis",
                SourceName = "market-intelligence",
                Summary = intelligence.Thesis.Narrative,
            });
        }

        foreach (var evidence in intelligence.Evidence.Take(3))
        {
            inputs.Add(new PredictionInput
            {
                PredictionId = "",
                InputType = "market_evidence",
                SourceName = "market-intelligence",
                Summary = $"{evidence.Title}: {evidence.Description}",
            });
        }

        return inputs;
    }

    // Old ScoreTechnicalSignals, ScoreCatalystSignals, DeterminePredictionType,
    // CalculateConfidence, CalculateRisk removed — replaced by ScoringEngine.Score()

    // -----------------------------------------------------------------------
    // Dynamic time window assignment based on signal velocity
    // -----------------------------------------------------------------------

    /// <summary>
    /// Determines the evaluation time window based on the signal profile.
    /// Uses a "move velocity" score: high momentum + volume + catalyst strength = fast move (short window),
    /// high trend + research = slow move (longer window).
    ///
    /// CatalystStrength is direction-independent (0-25) — it measures repricing
    /// pressure ("how likely is this stock to move quickly?") not direction.
    /// This avoids double-counting with momentum/trend which already handle direction.
    /// </summary>
    private static string DetermineTimeWindow(ScoringBreakdown b)
    {
        // Velocity components: signals that suggest price moves quickly
        double momentumSpeed = Math.Max(Math.Abs(b.MomentumScore), 0);
        double volumeSpeed = Math.Max(Math.Abs(b.VolumeScore), 0);
        // CatalystStrength is already non-negative and direction-independent
        double catalystSpeed = b.CatalystStrength;

        // Persistence components: signals that suggest price moves slowly
        double trendPersistence = Math.Max(Math.Abs(b.TrendScore), 0);
        double researchPersistence = Math.Max(Math.Abs(b.ResearchSignalScore), 0);

        // Velocity score: 0-100 range
        // High velocity = fast-moving setup, low velocity = slow-moving setup
        double velocity = (momentumSpeed * 1.2 + volumeSpeed * 1.0 + catalystSpeed * 1.5)
                        - (trendPersistence * 0.3 + researchPersistence * 0.5);
        velocity = Math.Clamp(velocity, 0, 100);

        return velocity switch
        {
            >= 50 => PredictionTimeWindows.OneDay,      // High: catalyst + momentum spike → scalp
            >= 30 => PredictionTimeWindows.ThreeDay,     // Fast: strong momentum/catalyst
            _     => PredictionTimeWindows.OneWeek,      // Everything else: trend-driven
            // 1_month eliminated: 32% accuracy, 8.8% avg adverse — not worth generating
        };
    }

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

    /// <summary>
    /// EV = (winProb × gain%) - (lossProb × loss%).
    /// Uses confidence as the win probability and target/stop distances as gain/loss.
    /// Returns null if we don't have the prices needed to compute it.
    /// </summary>
    private static double? ComputeExpectedValue(int confidenceScore, double? targetPrice, double? stopPrice, double? entryPrice)
    {
        if (entryPrice is not double entry || entry <= 0) return null;
        if (targetPrice is not double target || target <= 0) return null;
        if (stopPrice is not double stop || stop <= 0) return null;

        var winProb = confidenceScore / 100.0;
        var lossProb = 1.0 - winProb;
        var gainPercent = Math.Abs((target - entry) / entry) * 100.0;
        var lossPercent = Math.Abs((entry - stop) / entry) * 100.0;

        var ev = (winProb * gainPercent) - (lossProb * lossPercent);
        return Math.Round(ev, 4);
    }

    private static AtrPriceForecast ComputeAtrPriceForecast(
        double? entryPrice, string predType, string timeWindow,
        MarketSnapshot snapshot, int confidence, int risk,
        ScoringBreakdown? breakdown = null,
        ResearchUniverseContext? researchUniverse = null,
        Dictionary<string, double>? configWeights = null)
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

        // Historical ATR sanity check (Phase 3): if the live ATR diverges
        // significantly from the historical ATR, flag it. This catches unusual
        // volatility regimes (e.g., earnings week spike, post-crash compression).
        if (researchUniverse?.HistoricalAtrPercent is double histAtr && histAtr > 0 && result.AtrPercent is double liveAtr)
        {
            var atrRatio = liveAtr / histAtr;
            if (atrRatio > 2.0)
                result.Warnings.Add($"Live ATR ({liveAtr:F2}%) is {atrRatio:F1}x historical ATR ({histAtr:F2}%) — unusual volatility expansion");
            else if (atrRatio < 0.5)
                result.Warnings.Add($"Live ATR ({liveAtr:F2}%) is {atrRatio:F1}x historical ATR ({histAtr:F2}%) — unusual volatility compression");
        }

        // --- Timeframe multiplier ---
        var tfMultiplier = TimeframeMultipliers.GetValueOrDefault(timeWindow, 1.0);
        result.TimeframeMultiplier = tfMultiplier;

        // --- Signal modifier from ScoringEngine breakdown (single source of truth) ---
        // Derive catalyst/volume/trend factors from ScoringEngine's bucket scores
        // instead of independently recalculating from raw snapshot data.
        double catalystFactor, volumeFactor, trendFactor;
        if (breakdown is not null)
        {
            // Normalize ScoringEngine net scores (-30..+30 range) to -1..+1 factor
            catalystFactor = Math.Clamp((breakdown.CatalystBullish - breakdown.CatalystBearish) / 30.0, -1, 1);
            volumeFactor = Math.Clamp((breakdown.VolumeBullish - breakdown.VolumeBearish) / 30.0, -1, 1);
            trendFactor = Math.Clamp((breakdown.TrendBullish - breakdown.TrendBearish) / 30.0, -1, 1);
        }
        else
        {
            // Fallback for missing breakdown (should not happen in normal flow)
            catalystFactor = ScoreCatalystFactor(snapshot);
            volumeFactor = ScoreVolumeFactor(snapshot);
            trendFactor = ScoreTrendFactor(snapshot);
        }
        var riskScore = risk / 100.0;

        var modifier = 1.0
            + (catalystFactor * 0.25)
            + (volumeFactor * 0.15)
            + (trendFactor * 0.15)
            - (riskScore * 0.25);
        modifier = Math.Clamp(modifier, 0.75, 1.75);
        result.SignalModifier = Math.Round(modifier, 3);

        // --- Expected move ---
        var expectedMove = atr14 * tfMultiplier * modifier;

        // ── Scalp target dampener: reduce target AND stop distance for short timeframes ──
        // Scalping wants tighter, more reachable targets. Apply dampener (0.0-1.0)
        // to shrink the expected move on short-term predictions so targets get hit
        // faster and profits get booked. Also tightens stops proportionally so R:R
        // stays reasonable and EV calculations don't reject valid scalp candidates.
        // Default 1.0 = no change.
        double effectiveScalpDampener = 1.0;
        if (configWeights is not null)
        {
            var scalpDampener = configWeights.GetValueOrDefault("scalp_target_dampener", 1.0);
            if (scalpDampener < 1.0 && scalpDampener > 0
                && (timeWindow is "intraday" or "1_day" or "3_day" or "1_week" or "swing"))
            {
                effectiveScalpDampener = scalpDampener;
                expectedMove *= effectiveScalpDampener;
            }
        }

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

            // Scale stop distance by time window so multi-day predictions aren't
            // stopped out by normal intraday noise. DB-configurable per time window:
            //   prediction_stop_atr_mult_intraday (default 0.8)
            //   prediction_stop_atr_mult_1day     (default 1.0)
            //   prediction_stop_atr_mult_3day     (default 1.5)
            //   prediction_stop_atr_mult_1week    (default 2.0)
            var stopTimeWindowMult = timeWindow switch
            {
                "intraday" => configWeights?.GetValueOrDefault("prediction_stop_atr_mult_intraday", 0.8) ?? 0.8,
                "1_day"    => configWeights?.GetValueOrDefault("prediction_stop_atr_mult_1day", 1.0) ?? 1.0,
                "3_day"    => configWeights?.GetValueOrDefault("prediction_stop_atr_mult_3day", 1.5) ?? 1.5,
                "1_week"   => configWeights?.GetValueOrDefault("prediction_stop_atr_mult_1week", 2.0) ?? 2.0,
                "1_month"  => configWeights?.GetValueOrDefault("prediction_stop_atr_mult_1month", 3.0) ?? 3.0,
                _          => 1.0,
            };
            var stopAtr = atr14 * effectiveScalpDampener * stopTimeWindowMult;
            var atrStop = ep - stopAtr;
            var supportStop = support - 0.25 * stopAtr;
            result.StopPrice = Math.Round(Math.Max(atrStop, supportStop), 2);

            result.InvalidationPrice = Math.Round(ep - 1.5 * atr14 * stopTimeWindowMult, 2);
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

            var stopTimeWindowMult = timeWindow switch
            {
                "intraday" => configWeights?.GetValueOrDefault("prediction_stop_atr_mult_intraday", 0.8) ?? 0.8,
                "1_day"    => configWeights?.GetValueOrDefault("prediction_stop_atr_mult_1day", 1.0) ?? 1.0,
                "3_day"    => configWeights?.GetValueOrDefault("prediction_stop_atr_mult_3day", 1.5) ?? 1.5,
                "1_week"   => configWeights?.GetValueOrDefault("prediction_stop_atr_mult_1week", 2.0) ?? 2.0,
                "1_month"  => configWeights?.GetValueOrDefault("prediction_stop_atr_mult_1month", 3.0) ?? 3.0,
                _          => 1.0,
            };
            var stopAtr = atr14 * effectiveScalpDampener * stopTimeWindowMult;
            var atrStop = ep + stopAtr;
            var resistanceStop = resistance + 0.25 * stopAtr;
            result.StopPrice = Math.Round(Math.Min(atrStop, resistanceStop), 2);

            result.InvalidationPrice = Math.Round(ep + 1.5 * atr14 * stopTimeWindowMult, 2);
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

    /// <summary>
    /// Momentum pre-screen rank: higher score = better candidate.
    /// Combines 3-month trend structure, recent momentum, and catalyst presence.
    /// Think like a trader: movers with a story get looked at first.
    /// </summary>
    private static double ComputeMomentumRank(MarketSnapshot snapshot)
    {
        double score = 50; // baseline

        var tech = snapshot.TechnicalContext;
        if (tech is not null)
        {
            // 3-month trend structure (up to ±30)
            score += tech.ThreeMonthTrendStructure switch
            {
                "strong_uptrend" => 30,
                "uptrend" => 15,
                "sideways" => 0,
                "downtrend" => -15,
                "strong_downtrend" => -30,
                _ => 0,
            };

            // Momentum acceleration (up to ±15)
            score += tech.MomentumTrend switch
            {
                "accelerating" => 15,
                "decelerating" => -5,
                "reversing" => -15,
                _ => 0,
            };

            // 1-month change magnitude — bigger recent movers rank higher
            if (tech.OneMonthChangePct is double oneM)
                score += Math.Clamp(oneM, -10, 10);
        }

        // Catalyst presence — news-driven stocks get a boost
        if (snapshot.NewsContext is { Count: > 0 } news)
        {
            // High-importance catalyst (earnings, insider, macro) = big boost
            var topImportance = news.Max(n => n.ImportanceScore);
            if (topImportance >= 70) score += 20;
            else if (topImportance >= 50) score += 10;
            else if (news.Count >= 3) score += 5;
        }

        // Stock size/price tier — bigger stocks get love.
        // These are the names a trader focuses on: liquid, well-covered,
        // institutional flow, tighter spreads, more predictable behavior.
        var price = snapshot.Quote?.Price ?? 0;
        if (price >= 200) score += 15;       // mega-cap territory (AAPL, NVDA, MSFT)
        else if (price >= 100) score += 10;  // large-cap (CRM, AMZN, META)
        else if (price >= 50) score += 5;    // mid-large (AMD, UBER, COIN)
        // Below $50 gets no bonus — small stocks have to earn their spot via trend + catalyst

        // Market cap boost if fundamentals available
        var mktCap = snapshot.Fundamentals?.MarketCap;
        if (mktCap >= 100_000_000_000) score += 10;       // $100B+ mega-cap
        else if (mktCap >= 10_000_000_000) score += 5;    // $10B+ large-cap

        return score;
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

    /// <summary>AI confidence adjustment: -30 to +25. Applied on top of scoring engine confidence.</summary>
    public int? ConfidenceAdjustment { get; set; }

    /// <summary>AI trade flag: "strong_buy", "proceed", "caution", or "avoid". Avoid demotes to watch_only.</summary>
    public string? AiFlag { get; set; }

    /// <summary>AI direction override: null (agree), "bullish", or "bearish". Flips the prediction direction when set.</summary>
    public string? DirectionOverride { get; set; }

    /// <summary>One-line reason for the confidence adjustment or flag.</summary>
    public string? AiAdjustmentReason { get; set; }
}

internal class AiKeyLevels
{
    public double? Support { get; set; }
    public double? Resistance { get; set; }
}
