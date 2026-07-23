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
    private readonly ILogger<PredictionGenerator> _logger;
    private readonly ChatClient? _chatClient;
    private readonly bool _ensembleEnabled;

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
        string? ProfileName = null);

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

        return new SharedPredictionContext(weights, lessons, resolvedProfileId, resolvedProfileName);
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

        ScoringEngine.ScoringResult scoring;
        EnsembleScoringService.EnsembleResult? ensembleResult = null;

        if (_ensembleEnabled)
        {
            ensembleResult = await _ensemble.ScoreWithEnsembleAsync(
                snapshot, indicators, benchmark, weights, lessons, researchSignals,
                intelligence, researchUniverse, volatilityAssessment);
            scoring = ensembleResult.BlendedResult;
            _logger.LogInformation(
                "[prediction] {Ticker}: ensemble scoring — agreement={Agreement:P0}, dominant={Dominant}",
                ticker, ensembleResult.Agreement, ensembleResult.DominantModel);
        }
        else
        {
            scoring = _scoringEngine.Evaluate(
                snapshot, indicators, benchmark, weights, lessons, researchSignals,
                intelligence, researchUniverse, volatilityAssessment);
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
            allSignals, weights, lessons);

        if (explanation is not null)
            dataSources.Add("openai-analysis");

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
        var entryPrice = snapshot.Quote?.Price;
        var priceCalc = ComputeAtrPriceForecast(
            entryPrice, predType, timeWindow, snapshot, confidence, risk, scoring.Breakdown, researchUniverse);

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

        // Scope dedup to the current profile so challenger predictions don't block champion slots
        var todayStart = DateTimeOffset.UtcNow.Date;
        var recentPredictions = await _repo.GetPredictionsByDateRangeAsync(
            todayStart, DateTimeOffset.UtcNow, profileId: sharedContext.ProfileId);
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

        foreach (var snapshot in snapshots)
        {
            ResearchAsset? asset = null;
            assetLookup?.TryGetValue(snapshot.Ticker, out asset);
            var (pred, inputs) = await GeneratePredictionForTickerAsync(
                snapshot.Ticker, runId, snapshot, asset, sharedContext);
            if (pred is not null)
            {
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
            You are a stock market analyst writing prediction explanations with
            strong risk management discipline.

            IMPORTANT: You do NOT decide the prediction direction, confidence, or risk.
            Those have already been computed by the scoring engine from real market signals.
            Your job is to EXPLAIN WHY those signals led to this prediction AND to
            frame the trade in terms of risk management.

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

        return sb.ToString();
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
            >= 65 => PredictionTimeWindows.OneDay,      // High: major catalyst + momentum spike
            >= 40 => PredictionTimeWindows.ThreeDay,     // Fast: strong momentum/catalyst
            >= 20 => PredictionTimeWindows.OneWeek,      // Normal: trend-driven
            >= 10 => PredictionTimeWindows.OneMonth,     // Slow: research/fundamental
            _     => PredictionTimeWindows.OneWeek,      // Default
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
        ResearchUniverseContext? researchUniverse = null)
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
