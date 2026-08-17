using System.Text.Json;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketRegime;
using StockResearchAgent.Api.Services.MetaLabeling;
using StockResearchAgent.Api.Services.ResearchEngine;
using StockResearchAgent.Api.Services.ResearchEngine.Evaluation;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Backtesting;

/// <summary>
/// Core backtest engine. Steps through historical trading days, feeds
/// stored candle data into the existing scoring pipeline, generates
/// simulated predictions, evaluates them against future price action,
/// and records the results.
///
/// Uses the SAME ScoringEngine, ConfidenceEngine, and risk management
/// code as the live pipeline — just with historical data instead of live API calls.
/// No architecture changes. No new services. Just a new consumer.
/// </summary>
public class BacktestEngine
{
    private readonly HistoricalMarketSnapshotBuilder _snapshotBuilder;
    private readonly HistoricalDataLoader _dataLoader;
    private readonly IScoringEngine _scoringEngine;
    private readonly EnsembleScoringService _ensemble;
    private readonly TradeSetupEngine _setupEngine;
    private readonly VolatilityOpportunityEngine _voe;
    private readonly IMarketRegimeEngine _regimeEngine;
    private readonly ResearchRepository _repo;
    private readonly SupabaseClient _db;
    private readonly MetaLabelerService _metaLabeler;
    private readonly ILogger<BacktestEngine> _logger;

    /// <summary>
    /// Default cap on tickers scored per day. Callers can override via
    /// BacktestConfig.MaxTickersPerDay (null = unlimited). This exists only
    /// as a runaway-runtime safety valve — the Phase 8 target of
    /// "6 months × 4,500 tickers < 10 min" assumes this is set to null or a
    /// very large number by the caller.
    /// </summary>
    private const int DefaultMaxTickersPerDay = 500;

    /// <summary>Minimum confidence to count as a "trade" in backtest results.</summary>
    private const int MinTradeConfidence = 40;

    /// <summary>Ticker → sector map, loaded once per RunAsync from
    /// historical_research_profiles. Empty when the table isn't populated
    /// for the given tickers — SimulatedPortfolio then treats sector as
    /// unknown and its concentration limit becomes a no-op for those.</summary>
    private Dictionary<string, string> _sectorMap = new(StringComparer.OrdinalIgnoreCase);

    public BacktestEngine(
        HistoricalMarketSnapshotBuilder snapshotBuilder,
        HistoricalDataLoader dataLoader,
        IScoringEngine scoringEngine,
        EnsembleScoringService ensemble,
        TradeSetupEngine setupEngine,
        VolatilityOpportunityEngine voe,
        IMarketRegimeEngine regimeEngine,
        ResearchRepository repo,
        SupabaseClient db,
        MetaLabelerService metaLabeler,
        ILogger<BacktestEngine> logger)
    {
        _snapshotBuilder = snapshotBuilder;
        _dataLoader = dataLoader;
        _scoringEngine = scoringEngine;
        _ensemble = ensemble;
        _setupEngine = setupEngine;
        _voe = voe;
        _regimeEngine = regimeEngine;
        _repo = repo;
        _db = db;
        _metaLabeler = metaLabeler;
        _logger = logger;
    }

    // ── Public API ──────────────────────────────────────────────

    /// <summary>
    /// Run a full backtest over a date range with optional parameter overrides.
    /// Phase 4: uses SimulatedPortfolio to enforce position sizing, max open
    /// positions, daily loss limits, trailing stops, and all other risk rules.
    /// </summary>
    public async Task<BacktestRunResult> RunAsync(
        BacktestConfig config,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString();

        // Persist run record — includes sweep_id when this run is part of a
        // ParameterSweepEngine invocation. Standalone runs leave it null.
        await _db.InsertAsync("backtest_runs", new[] { new
        {
            id = runId,
            sweep_id = config.SweepId,
            start_date = config.StartDate.ToString("yyyy-MM-dd"),
            end_date = config.EndDate.ToString("yyyy-MM-dd"),
            parameters = JsonSerializer.Serialize(config.ParameterOverrides ?? new()),
            status = "running",
        }});

        _logger.LogInformation(
            "[backtest] Starting run {RunId}: {Start} → {End}, {TickerCount} tickers, ${Balance}",
            runId, config.StartDate, config.EndDate,
            config.Tickers?.Count ?? 0, config.StartingBalance);

        try
        {
            // Load base scoring weights (then apply overrides)
            var weights = await LoadWeightsWithOverrides(config.ParameterOverrides);

            // Get trading days in range from SPY candles
            var tradingDays = await GetTradingDaysAsync(config.StartDate, config.EndDate);
            if (tradingDays.Count == 0)
            {
                await MarkRunFailed(runId, "No trading days found — ensure SPY candles are loaded");
                return new BacktestRunResult { RunId = runId, Error = "No trading days" };
            }

            var tickers = config.Tickers ?? [];

            // Build sector map once so SimulatedPortfolio's sector-concentration
            // limit actually fires during entries.
            await LoadSectorMapAsync(tickers);

            // ── Phase 4: Portfolio simulator ──
            var portfolioConfig = SimPortfolioConfig.FromOverrides(config.ParameterOverrides);
            var portfolio = new SimulatedPortfolio(config.StartingBalance, portfolioConfig);
            var predictionsGenerated = 0;

            progress?.Report($"[backtest] {tradingDays.Count} trading days, {tickers.Count} tickers, ${config.StartingBalance}");

            for (int dayIdx = 0; dayIdx < tradingDays.Count; dayIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var day = tradingDays[dayIdx];

                // Step 1: Fetch today's candles for all tickers (exit checks + entry quotes)
                var todaysCandles = await FetchDayCandlesAsync(day, tickers, portfolio.OpenPositions);

                // Step 2: Run exit checks on open positions first (SL/TP/trailing/time stop)
                portfolio.ProcessDay(day, todaysCandles);

                // Step 3: Score tickers and try to open new positions
                var scored = await ProcessTradingDayWithPortfolioAsync(
                    day, tickers, weights, config, runId, portfolio, todaysCandles);
                predictionsGenerated += scored;

                if ((dayIdx + 1) % 10 == 0 || dayIdx == tradingDays.Count - 1)
                {
                    var pct = (int)((dayIdx + 1.0) / tradingDays.Count * 100);
                    var equity = portfolio.EquityCurve.Count > 0
                        ? portfolio.EquityCurve[^1].TotalEquity : config.StartingBalance;
                    var msg = $"[backtest] Day {dayIdx + 1}/{tradingDays.Count} ({pct}%) — " +
                              $"{predictionsGenerated} predictions, {portfolio.ClosedPositions.Count} trades, " +
                              $"equity ${equity:F2}, open {portfolio.OpenPositions.Count}";
                    _logger.LogInformation(msg);
                    progress?.Report(msg);
                }
            }

            // Force-close remaining positions at last day's prices
            if (tradingDays.Count > 0)
            {
                var lastDay = tradingDays[^1];
                var lastCandles = await FetchDayCandlesAsync(lastDay, tickers, portfolio.OpenPositions);
                portfolio.CloseAllOpen(lastDay, lastCandles);
            }

            var allTrades = portfolio.GetTrades();

            // Persist trades in chunks
            foreach (var chunk in allTrades.Chunk(50))
            {
                var rows = chunk.Select(t => new
                {
                    run_id = runId,
                    ticker = t.Ticker,
                    direction = t.Direction,
                    timeframe = t.Timeframe,
                    entry_date = t.EntryDate.ToString("yyyy-MM-dd"),
                    entry_price = t.EntryPrice,
                    exit_date = t.ExitDate?.ToString("yyyy-MM-dd"),
                    exit_price = t.ExitPrice,
                    exit_reason = t.ExitReason,
                    pnl_dollars = t.PnlDollars,
                    pnl_percent = t.PnlPercent,
                    max_favorable_percent = t.MaxFavorablePercent,
                    max_adverse_percent = t.MaxAdversePercent,
                    confidence = t.Confidence,
                    expected_value = t.ExpectedValue,
                    risk_reward_ratio = t.RiskRewardRatio,
                    score_debug = t.ScoreDebug,
                    meta_probability = t.MetaProbability,
                    meta_model_version = t.MetaModelVersion,
                }).ToArray();
                await _db.InsertAsync("backtest_trades", rows);
            }

            // Persist equity curve in chunks
            foreach (var chunk in portfolio.EquityCurve.Chunk(50))
            {
                var rows = chunk.Select(s => new
                {
                    run_id = runId,
                    snapshot_date = s.Date.ToString("yyyy-MM-dd"),
                    cash = s.Cash,
                    invested_value = s.InvestedValue,
                    total_equity = s.TotalEquity,
                    open_position_count = s.OpenPositionCount,
                }).ToArray();
                await _db.InsertAsync("backtest_equity_curve", rows);
            }

            // Compute summary metrics
            var metrics = ComputeMetrics(allTrades);
            var finalEquity = portfolio.EquityCurve.Count > 0
                ? portfolio.EquityCurve[^1].TotalEquity : config.StartingBalance;
            var portfolioPnl = Math.Round(
                (finalEquity - config.StartingBalance) / config.StartingBalance * 100, 2);

            // Update run record
            await _db.UpdateAsync("backtest_runs", $"id=eq.{runId}", new
            {
                status = "completed",
                tickers_tested = tickers.Count,
                trading_days = tradingDays.Count,
                predictions_generated = predictionsGenerated,
                trades_taken = allTrades.Count,
                total_pnl = portfolioPnl,
                win_rate = metrics.WinRate,
                max_drawdown = ComputeEquityDrawdown(portfolio.EquityCurve),
                sharpe_ratio = metrics.SharpeRatio,
                profit_factor = metrics.ProfitFactor,
                avg_win = metrics.AvgWin,
                avg_loss = metrics.AvgLoss,
                best_trade = metrics.BestTrade,
                worst_trade = metrics.WorstTrade,
                summary = $"${config.StartingBalance} → ${finalEquity:F2} ({portfolioPnl:+0.00;-0.00}%) | " +
                          metrics.Summary,
                completed_at = DateTimeOffset.UtcNow.ToString("o"),
            });

            _logger.LogInformation(
                "[backtest] Run {RunId} complete: {Trades} trades, ${Start} → ${End} ({Pnl:+0.00;-0.00}%)",
                runId, allTrades.Count, config.StartingBalance, finalEquity, portfolioPnl);

            return new BacktestRunResult
            {
                RunId = runId,
                TradingDays = tradingDays.Count,
                PredictionsGenerated = predictionsGenerated,
                TradeCount = allTrades.Count,
                Metrics = metrics,
                StartingBalance = config.StartingBalance,
                FinalEquity = Math.Round(finalEquity, 2),
                PortfolioPnlPercent = portfolioPnl,
                EquityCurve = portfolio.EquityCurve,
            };
        }
        catch (OperationCanceledException)
        {
            await MarkRunFailed(runId, "Cancelled by user");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[backtest] Run {RunId} failed", runId);
            await MarkRunFailed(runId, ex.Message);
            return new BacktestRunResult { RunId = runId, Error = ex.Message };
        }
    }

    // ── Day processing (Phase 4: portfolio-integrated) ─────────

    /// <summary>
    /// Score tickers for a trading day and try to open positions through the
    /// SimulatedPortfolio, which enforces cash, position limits, and all rules.
    /// Returns the number of predictions scored.
    /// </summary>
    private async Task<int> ProcessTradingDayWithPortfolioAsync(
        DateOnly day, IReadOnlyList<string> tickers,
        Dictionary<string, double> weights, BacktestConfig config, string runId,
        SimulatedPortfolio portfolio,
        Dictionary<string, HistoricalCandle> todaysCandles)
    {
        var scored = 0;

        // Build SPY regime for this day
        var regimeResult = await BuildHistoricalRegimeAsync(day);

        // Determine regime direction for entry gating
        string? regimeDirection = regimeResult?.PrimaryRegime switch
        {
            MarketRegimeType.BullTrend or MarketRegimeType.RiskOn or MarketRegimeType.Recovery
                or MarketRegimeType.Accumulation or MarketRegimeType.Expansion => "bullish",
            MarketRegimeType.BearTrend or MarketRegimeType.RiskOff or MarketRegimeType.Distribution
                or MarketRegimeType.Contraction => "bearish",
            _ => null,
        };

        // Score each ticker — respect the caller's per-day cap (null = engine default).
        var perDayCap = config.MaxTickersPerDay ?? DefaultMaxTickersPerDay;
        var tickersToProcess = tickers.Count > perDayCap
            ? tickers.Take(perDayCap).ToList()
            : tickers;

        // Collect scored candidates before opening (so we can sort by EV).
        // Meta-probability is captured now — the historical model version at the
        // moment the backtest ran, stamped onto every trade for later comparison.
        var candidates = new List<(ScoringEngine.ScoringResult scoring, double price, string ticker,
            double? metaProbability, int? metaModelVersion)>();

        foreach (var ticker in tickersToProcess)
        {
            var snapshot = await _snapshotBuilder.BuildAsync(ticker, day, runId);
            if (snapshot?.Quote is null) continue;

            var indicators = await _snapshotBuilder.ComputeIndicatorsAsync(ticker, day);
            if (indicators is null) continue;

            var benchmark = await _snapshotBuilder.ComputeBenchmarkAsync(snapshot.Quote, day);

            // VOE assessment
            var volAssessment = _voe.Assess(ticker, snapshot.RecentBars, indicators, snapshot.NewsContext);

            // Run the scoring pipeline — mirrors PredictionGenerator.cs:405-422.
            // ConfidenceEngine is already invoked inside ScoringEngine.Evaluate.
            ScoringEngine.ScoringResult scoring;
            if (config.UseEnsemble)
            {
                var ensembleResult = await _ensemble.ScoreWithEnsembleAsync(
                    snapshot, indicators, benchmark, weights, [],
                    researchSignals: null,
                    intelligence: null,
                    researchUniverse: null,
                    volatilityAssessment: volAssessment,
                    marketRegimeResult: regimeResult);
                scoring = ensembleResult.BlendedResult;
            }
            else
            {
                scoring = _scoringEngine.Evaluate(
                    snapshot, indicators, benchmark, weights, [],
                    researchSignals: null,
                    intelligence: null,
                    researchUniverse: null,
                    volatilityAssessment: volAssessment,
                    marketRegimeResult: regimeResult);
            }

            // Setup-history adjustment — mirrors PredictionGenerator.cs:591-599.
            // Backtest scoring stays comparable to live when this is on.
            if (config.UseSetupHistory)
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

            scored++;

            // Only consider directional predictions above confidence threshold
            var predType = scoring.PredictionType;
            if (predType is not ("bullish" or "bearish")) continue;
            if (scoring.Confidence < (config.MinConfidence ?? MinTradeConfidence)) continue;

            // Regime gate — don't trade against the market trend
            if (regimeDirection is not null)
            {
                var isBullishPred = predType == "bullish";
                if ((isBullishPred && regimeDirection == "bearish")
                    || (!isBullishPred && regimeDirection == "bullish"))
                    continue;
            }

            // Meta-labeler advisory scoring. Null when no model is loaded.
            var metaProb = _metaLabeler.IsReady
                ? (double?)_metaLabeler.Score(scoring.Breakdown)
                : null;
            var metaVersion = _metaLabeler.IsReady ? _metaLabeler.ActiveVersion : null;

            // Optional gate — reject candidates below the configured meta floor.
            // Null threshold = advisory only.
            if (config.MetaProbabilityThreshold.HasValue
                && metaProb.HasValue
                && metaProb.Value < config.MetaProbabilityThreshold.Value)
                continue;

            candidates.Add((scoring, snapshot.Quote.Price, ticker, metaProb, metaVersion));
        }

        // Sort by EV descending — open highest-edge trades first (mirrors live pipeline)
        var sorted = candidates
            .Select(c =>
            {
                var timeframe = DetermineTimeframe(c.scoring.Breakdown);
                var (slPct, tpPct) = GetSlTp(timeframe, config);
                var ev = slPct > 0
                    ? (c.scoring.Confidence / 100.0 * tpPct) - ((1 - c.scoring.Confidence / 100.0) * slPct)
                    : 0.0;
                var rr = slPct > 0 ? Math.Round(tpPct / slPct, 2) : 0.0;
                return (c.scoring, c.price, c.ticker, timeframe, ev, rr, c.metaProbability, c.metaModelVersion);
            })
            .OrderByDescending(c => c.ev)
            .ThenByDescending(c => c.scoring.Confidence);

        foreach (var (scoring, price, ticker, timeframe, ev, rr, metaProb, metaVersion) in sorted)
        {
            var scoreDebug = JsonSerializer.Serialize(new
            {
                scoring.BullishScore,
                scoring.BearishScore,
                scoring.DirectionMargin,
                scoring.Breakdown.ActionabilityTier,
                metaProbability = metaProb,
            });

            // Sector lookup — pulled from historical_research_profiles at run
            // start. Null when the profile hasn't been captured yet.
            _sectorMap.TryGetValue(ticker, out var sector);

            portfolio.TryOpenPosition(
                ticker: ticker,
                direction: scoring.WinningDirection,
                timeframe: timeframe,
                entryDate: day,
                entryPrice: price,
                confidence: scoring.Confidence,
                expectedValue: ev,
                riskRewardRatio: rr,
                sector: sector,
                scoreDebug: scoreDebug,
                metaProbability: metaProb,
                metaModelVersion: metaVersion);
        }

        return scored;
    }

    /// <summary>
    /// Fetch candles for every ticker needed on a given day (open positions +
    /// candidate tickers) in a single batched query. Replaces the earlier
    /// per-ticker DB round-trip that made a 4,500-ticker × 125-day backtest
    /// take hours.
    /// </summary>
    private async Task<Dictionary<string, HistoricalCandle>> FetchDayCandlesAsync(
        DateOnly day, IReadOnlyList<string> tickers, List<SimPosition> openPositions)
    {
        var allTickers = new HashSet<string>(tickers, StringComparer.OrdinalIgnoreCase);
        foreach (var pos in openPositions)
            allTickers.Add(pos.Ticker);

        return await _dataLoader.GetCandlesForDayAsync(allTickers, day);
    }

    /// <summary>
    /// Build the ticker → sector map from historical_research_profiles for
    /// the tickers this run will touch. Chunked queries to keep URL length
    /// bounded. Missing tickers stay absent from the map; SimulatedPortfolio
    /// treats a null sector as "unknown" and skips its concentration limit
    /// for that trade rather than mis-attributing.
    /// </summary>
    private async Task LoadSectorMapAsync(IEnumerable<string> tickers)
    {
        var uniqueTickers = tickers
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.ToUpperInvariant())
            .Distinct()
            .ToList();
        if (uniqueTickers.Count == 0) return;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in uniqueTickers.Chunk(200))
        {
            try
            {
                var inList = string.Join(',', chunk.Select(Uri.EscapeDataString));
                var rows = await _db.SelectAsync("historical_research_profiles",
                    $"ticker=in.({inList})",
                    select: "ticker,sector",
                    limit: chunk.Length);
                foreach (var r in rows)
                {
                    var t = r["ticker"]?.ToString();
                    var s = r["sector"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(t) && !string.IsNullOrWhiteSpace(s))
                        map[t] = s;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[backtest] Sector lookup failed for chunk — continuing without sectors");
            }
        }
        _sectorMap = map;
        _logger.LogInformation("[backtest] Loaded sectors for {Have}/{Total} tickers",
            map.Count, uniqueTickers.Count);
    }

    /// <summary>Get SL/TP percentages for a timeframe from config overrides.</summary>
    private static (double slPct, double tpPct) GetSlTp(string timeframe, BacktestConfig config)
    {
        if (timeframe is "3_day" or "1_week")
            return (config.GetOverride("risk_sl_swing", 0.03), config.GetOverride("risk_tp_swing", 0.05));
        return (config.GetOverride("risk_sl_day", 0.02), config.GetOverride("risk_tp_day", 0.03));
    }

    /// <summary>Compute max drawdown from equity curve (dollar-based).</summary>
    private static double ComputeEquityDrawdown(List<EquitySnapshot> curve)
    {
        if (curve.Count == 0) return 0;
        double peak = curve[0].TotalEquity;
        double maxDd = 0;
        foreach (var snap in curve)
        {
            peak = Math.Max(peak, snap.TotalEquity);
            var dd = (peak - snap.TotalEquity) / peak * 100;
            maxDd = Math.Max(maxDd, dd);
        }
        return Math.Round(maxDd, 2);
    }

    // ── Helpers ──────────────────────────────────────────────────

    /// <summary>Get trading days from SPY candles in the date range.</summary>
    private async Task<List<DateOnly>> GetTradingDaysAsync(DateOnly start, DateOnly end)
    {
        var spyCandles = await _dataLoader.GetCandlesAsync("SPY", start, end);
        return spyCandles.Select(c => c.Date).ToList();
    }

    /// <summary>Build market regime from historical SPY data.</summary>
    private async Task<MarketRegimeResult?> BuildHistoricalRegimeAsync(DateOnly day)
    {
        try
        {
            var spyQuote = await BuildHistoricalQuote("SPY", day);
            var qqqQuote = await BuildHistoricalQuote("QQQ", day);
            if (spyQuote is null) return null;

            // Compute SPY EMA26 and EMA50
            var lookback = day.AddDays(-80);
            var spyCandles = await _dataLoader.GetCandlesAsync("SPY", lookback, day);
            var spyCloses = spyCandles.Select(c => c.Close).ToList(); // already chronological

            var spyEma26 = HistoricalMarketSnapshotBuilder.ComputeEma(spyCloses, 26);
            var spyEma50 = HistoricalMarketSnapshotBuilder.ComputeEma(spyCloses, 50);

            double? qqqEma26 = null;
            var qqqCandles = await _dataLoader.GetCandlesAsync("QQQ", lookback, day);
            if (qqqCandles.Count >= 26)
            {
                var qqqCloses = qqqCandles.Select(c => c.Close).ToList();
                qqqEma26 = HistoricalMarketSnapshotBuilder.ComputeEma(qqqCloses, 26);
            }

            var ctx = new MarketRegimeContext
            {
                SpyTrendRatio = spyEma26 is not null && spyEma26 > 0
                    ? Math.Round(spyQuote.Price / spyEma26.Value, 4) : null,
                QqqTrendRatio = qqqQuote is not null && qqqEma26 is not null && qqqEma26 > 0
                    ? Math.Round(qqqQuote.Price / qqqEma26.Value, 4) : null,
                SpyLongTrendRatio = spyEma50 is not null && spyEma50 > 0
                    ? Math.Round(spyQuote.Price / spyEma50.Value, 4) : null,
                Vix = null, // no historical VIX data
            };

            return _regimeEngine.Classify(ctx);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[backtest] Regime classification failed for {Day}", day);
            return null;
        }
    }

    private async Task<MarketSnapshotQuote?> BuildHistoricalQuote(string ticker, DateOnly day)
    {
        var lookback = day.AddDays(-10);
        var candles = await _dataLoader.GetCandlesAsync(ticker, lookback, day);
        if (candles.Count < 2) return null;

        var latest = candles.Last(c => c.Date <= day);
        var prev = candles.LastOrDefault(c => c.Date < latest.Date);
        if (prev is null) return null;

        var change = latest.Close - prev.Close;
        return new MarketSnapshotQuote
        {
            Price = latest.Close,
            Open = latest.Open,
            High = latest.High,
            Low = latest.Low,
            PreviousClose = prev.Close,
            Volume = latest.Volume,
            Change = change,
            ChangePercent = prev.Close > 0 ? (change / prev.Close) * 100 : 0,
            Timestamp = latest.Date.ToString("yyyy-MM-dd"),
        };
    }

    /// <summary>Load base scoring weights then apply parameter overrides.</summary>
    private async Task<Dictionary<string, double>> LoadWeightsWithOverrides(
        Dictionary<string, double>? overrides)
    {
        var weights = (await _repo.GetScoringWeightsAsync())
            .ToDictionary(w => w.SignalName, w => w.Weight);

        var dbOverrides = await _repo.GetActiveWeightOverridesAsync();
        foreach (var o in dbOverrides)
            weights[o.SignalName] = o.EffectiveWeight;

        // Apply backtest-specific overrides on top
        if (overrides is not null)
        {
            foreach (var kv in overrides)
                weights[kv.Key] = kv.Value;
        }

        return weights;
    }

    /// <summary>Determine timeframe from scoring breakdown (mirrors PredictionGenerator logic).</summary>
    private static string DetermineTimeframe(ScoringBreakdown b)
    {
        double momentumSpeed = Math.Max(Math.Abs(b.MomentumScore), 0);
        double volumeSpeed = Math.Max(Math.Abs(b.VolumeScore), 0);
        double catalystSpeed = b.CatalystStrength;
        double trendPersistence = Math.Max(Math.Abs(b.TrendScore), 0);
        double researchPersistence = Math.Max(Math.Abs(b.ResearchSignalScore), 0);

        double velocity = (momentumSpeed * 1.2 + volumeSpeed * 1.0 + catalystSpeed * 1.5)
                        - (trendPersistence * 0.3 + researchPersistence * 0.5);
        velocity = Math.Clamp(velocity, 0, 100);

        return velocity switch
        {
            >= 50 => "1_day",
            >= 30 => "3_day",
            _ => "1_week",
        };
    }

    private async Task MarkRunFailed(string runId, string error)
    {
        await _db.UpdateAsync("backtest_runs", $"id=eq.{runId}", new
        {
            status = "failed",
            error_message = error,
            completed_at = DateTimeOffset.UtcNow.ToString("o"),
        });
    }

    // ── Metrics ─────────────────────────────────────────────────

    private static BacktestMetrics ComputeMetrics(List<BacktestTrade> trades)
    {
        if (trades.Count == 0)
            return new BacktestMetrics { Summary = "No trades generated" };

        var completedTrades = trades
            .Where(t => t.PnlPercent is not null)
            .ToList();

        if (completedTrades.Count == 0)
            return new BacktestMetrics { Summary = "No completed trades" };

        var wins = completedTrades.Where(t => t.PnlPercent > 0).ToList();
        var losses = completedTrades.Where(t => t.PnlPercent <= 0).ToList();

        var totalPnl = completedTrades.Sum(t => t.PnlPercent ?? 0);
        var winRate = (double)wins.Count / completedTrades.Count;
        var avgWin = wins.Count > 0 ? wins.Average(t => t.PnlPercent ?? 0) : 0;
        var avgLoss = losses.Count > 0 ? losses.Average(t => t.PnlPercent ?? 0) : 0;
        var profitFactor = Math.Abs(avgLoss) > 0 && wins.Count > 0
            ? (wins.Sum(t => t.PnlPercent ?? 0)) / Math.Abs(losses.Sum(t => t.PnlPercent ?? 0))
            : 0;

        // Max drawdown (cumulative P&L curve)
        double peak = 0, maxDd = 0, cumPnl = 0;
        foreach (var t in completedTrades.OrderBy(t => t.EntryDate))
        {
            cumPnl += t.PnlPercent ?? 0;
            peak = Math.Max(peak, cumPnl);
            maxDd = Math.Min(maxDd, cumPnl - peak);
        }

        // Sharpe ratio (daily returns → annualized)
        var returns = completedTrades
            .OrderBy(t => t.EntryDate)
            .Select(t => t.PnlPercent ?? 0)
            .ToList();
        var meanReturn = returns.Average();
        var stdDev = returns.Count > 1
            ? Math.Sqrt(returns.Sum(r => Math.Pow(r - meanReturn, 2)) / (returns.Count - 1))
            : 0;
        var sharpe = stdDev > 0 ? (meanReturn / stdDev) * Math.Sqrt(252) : 0;

        var best = completedTrades.Max(t => t.PnlPercent ?? 0);
        var worst = completedTrades.Min(t => t.PnlPercent ?? 0);

        return new BacktestMetrics
        {
            TotalPnl = Math.Round(totalPnl, 2),
            WinRate = Math.Round(winRate, 4),
            MaxDrawdown = Math.Round(maxDd, 2),
            SharpeRatio = Math.Round(sharpe, 2),
            ProfitFactor = Math.Round(profitFactor, 2),
            AvgWin = Math.Round(avgWin, 4),
            AvgLoss = Math.Round(avgLoss, 4),
            BestTrade = Math.Round(best, 4),
            WorstTrade = Math.Round(worst, 4),
            Summary = $"{completedTrades.Count} trades, {winRate:P0} win rate, " +
                      $"P&L={totalPnl:+0.00;-0.00}%, Sharpe={sharpe:F2}, " +
                      $"MaxDD={maxDd:F2}%, PF={profitFactor:F2}",
        };
    }
}

// ── DTOs ────────────────────────────────────────────────────────

public class BacktestConfig
{
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public List<string>? Tickers { get; init; }
    public Dictionary<string, double>? ParameterOverrides { get; init; }
    public int? MinConfidence { get; init; }
    public double StartingBalance { get; init; } = 1000;

    /// <summary>
    /// When true, run each ticker through EnsembleScoringService (three profiles
    /// blended by performance) instead of raw ScoringEngine.Evaluate — parity with
    /// PredictionGenerator.cs live path.
    /// </summary>
    public bool UseEnsemble { get; init; }

    /// <summary>
    /// When true, after scoring, look up the setup fingerprint's historical
    /// performance and let ScoringEngine.AdjustForSetupHistory nudge the result —
    /// parity with PredictionGenerator.cs live path.
    /// </summary>
    public bool UseSetupHistory { get; init; } = true;

    /// <summary>
    /// Optional parent sweep id — set by ParameterSweepEngine so the resulting
    /// backtest_runs row links back to backtest_sweeps.id. Standalone runs
    /// leave this null.
    /// </summary>
    public string? SweepId { get; init; }

    /// <summary>
    /// Cap on tickers scored per day. Null = unlimited (up to the engine's
    /// safety valve). Set explicitly to run against the full universe.
    /// </summary>
    public int? MaxTickersPerDay { get; init; }

    /// <summary>
    /// Meta-labeler probability floor (0.0–1.0). Predictions scoring below
    /// this get rejected before position sizing. Null = advisory only.
    /// Use sweeps over this to find the optimal enforcement threshold.
    /// </summary>
    public double? MetaProbabilityThreshold { get; init; }

    public double GetOverride(string key, double defaultValue)
        => ParameterOverrides is not null && ParameterOverrides.TryGetValue(key, out var v) ? v : defaultValue;
}

public class BacktestRunResult
{
    public string RunId { get; init; } = "";
    public int TradingDays { get; init; }
    public int PredictionsGenerated { get; init; }
    public int TradeCount { get; init; }
    public BacktestMetrics? Metrics { get; init; }
    public string? Error { get; init; }

    // Phase 4: portfolio simulation results
    public double StartingBalance { get; init; }
    public double FinalEquity { get; init; }
    public double PortfolioPnlPercent { get; init; }
    public List<EquitySnapshot>? EquityCurve { get; init; }
}

public class BacktestMetrics
{
    public double TotalPnl { get; init; }
    public double WinRate { get; init; }
    public double MaxDrawdown { get; init; }
    public double SharpeRatio { get; init; }
    public double ProfitFactor { get; init; }
    public double AvgWin { get; init; }
    public double AvgLoss { get; init; }
    public double BestTrade { get; init; }
    public double WorstTrade { get; init; }
    public string Summary { get; init; } = "";
}

public class BacktestTrade
{
    public string Ticker { get; init; } = "";
    public string Direction { get; init; } = "";
    public string Timeframe { get; init; } = "";
    public DateOnly EntryDate { get; init; }
    public double EntryPrice { get; init; }
    public DateOnly? ExitDate { get; init; }
    public double? ExitPrice { get; init; }
    public string? ExitReason { get; init; }
    public double? PnlDollars { get; init; }
    public double? PnlPercent { get; init; }
    public double? MaxFavorablePercent { get; init; }
    public double? MaxAdversePercent { get; init; }
    public int Confidence { get; init; }
    public double? ExpectedValue { get; init; }
    public double? RiskRewardRatio { get; init; }
    public string? ScoreDebug { get; init; }
    public double? MetaProbability { get; init; }
    public int? MetaModelVersion { get; init; }
}

internal record DayResult
{
    public int PredictionsScored { get; init; }
    public List<BacktestTrade> Trades { get; init; } = [];
}
