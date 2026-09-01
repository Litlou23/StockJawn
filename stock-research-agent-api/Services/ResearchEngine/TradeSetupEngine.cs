using System.Text.Json;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Trade Setup Detection Engine — the core of STOCKJAWN's prediction philosophy.
///
/// Instead of asking "will this stock go up tomorrow?", this engine asks:
/// "Given today's information, is this a historically favorable setup
///  that justifies taking a position?"
///
/// Responsibilities:
///   1. Classify a scored prediction into a named trade setup (fingerprinting)
///   2. Look up historical performance for that setup type
///   3. Determine whether the setup is historically favorable
///   4. Compute expected value and risk metrics
///   5. Set appropriate holding period and exit parameters
///
/// This engine does NOT determine direction or confidence — that's ScoringEngine's job.
/// This engine takes ScoringEngine's output and wraps it in the setup framework.
/// </summary>
public class TradeSetupEngine
{
    // Minimum net score in a bucket for it to count as "active evidence"
    private const double ActiveBucketThreshold = 3.0;

    // Minimum sample size before we trust a setup's historical performance
    private const int MinSampleForTrust = 8;

    // Minimum sample size before we consider a setup "historically favorable"
    private const int MinSampleForFavorable = 12;

    // Minimum expected value to qualify as favorable
    private const double MinExpectedValueForFavorable = 0.5; // 0.5% EV

    // Recent performance window for degradation detection
    private const int RecentWindowDays = 30;

    // If recent win rate drops this far below all-time, mark as untrusted
    private const double DegradationThreshold = 0.15;

    private readonly ResearchRepository _repo;
    private readonly MarketDataService _marketData;
    private readonly IOpenAiCompletionService _ai;
    private readonly ILogger<TradeSetupEngine> _logger;

    public TradeSetupEngine(ResearchRepository repo, MarketDataService marketData,
        IOpenAiCompletionService ai, ILogger<TradeSetupEngine> logger)
    {
        _repo = repo;
        _marketData = marketData;
        _ai = ai;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // 1. Classify a prediction into a trade setup
    // -----------------------------------------------------------------------

    /// <summary>
    /// Takes scoring output and classifies it into a trade setup with a fingerprint,
    /// historical performance lookup, and appropriate trade parameters.
    /// </summary>
    public async Task<TradeSetup> ClassifySetupAsync(
        PredictionCandidate prediction,
        ScoringEngine.ScoringResult scoring,
        string runId,
        string? paperStockCandidateId = null)
    {
        // Step 1: Build signal evidence from each bucket
        var evidence = BuildSignalEvidence(scoring);

        // Step 2: Generate the setup fingerprint
        var fingerprint = GenerateFingerprint(evidence, scoring.WinningDirection);

        // Step 3: Determine trade parameters
        var (target, stop, invalidation, riskReward) = ComputeTradeParameters(
            prediction, scoring);

        // Step 4: Determine expected holding period
        var holdingPeriod = DetermineHoldingPeriod(prediction, scoring, evidence);
        var maxDays = HoldingPeriodToDays(holdingPeriod);

        // Step 5: Look up historical performance for this fingerprint
        var historicalPerf = await LookupSetupPerformanceAsync(fingerprint.Fingerprint);

        // Step 6: Detect market regime
        var regime = DetectMarketRegime(scoring.Breakdown);

        // Step 7: Compute setup score
        var setupScore = ComputeSetupScore(
            scoring, fingerprint, historicalPerf, regime);

        // Step 8: Determine if historically favorable
        var isFavorable = IsHistoricallyFavorable(historicalPerf, regime);

        var setup = new TradeSetup
        {
            PredictionId = prediction.Id,
            PaperStockCandidateId = paperStockCandidateId,
            RunId = runId,
            Ticker = prediction.Ticker,
            Fingerprint = fingerprint,
            SignalEvidence = evidence,
            Direction = scoring.WinningDirection,
            EntryPrice = prediction.EntryReferencePrice,
            TargetPrice = target,
            StopPrice = stop,
            InvalidationPrice = invalidation,
            RiskRewardRatio = riskReward,
            ExpectedHoldingPeriod = holdingPeriod,
            MaxHoldingDays = maxDays,
            HistoricalPerformance = historicalPerf,
            MarketRegime = regime,
            SetupScore = setupScore,
            IsHistoricallyFavorable = isFavorable,
            Status = SetupStatus.active,
        };

        _logger.LogInformation(
            "[setup-engine] {Ticker}: fingerprint={Fingerprint} ({Components} components), " +
            "score={Score:F1}, favorable={Favorable}, " +
            "historical={History}",
            prediction.Ticker,
            fingerprint.Fingerprint,
            fingerprint.ConfirmationCount,
            setupScore,
            isFavorable,
            historicalPerf is not null
                ? $"WR={historicalPerf.WinRate * 100:F0}% EV={historicalPerf.ExpectedValuePercent:F2}% n={historicalPerf.SampleSize}"
                : "no history");

        return setup;
    }

    /// <summary>
    /// Convenience overload: reconstructs a ScoringResult from the prediction's
    /// ScoreDebugJson so callers that don't have the live scoring output can
    /// still classify setups (e.g., the morning pipeline after predictions are saved).
    /// Returns null if the prediction lacks scoring data.
    /// </summary>
    public async Task<TradeSetup?> ClassifySetupAsync(
        PredictionCandidate prediction,
        string? paperStockCandidateId = null)
    {
        if (string.IsNullOrEmpty(prediction.ScoreDebugJson))
            return null;

        var breakdown = ScoringBreakdownEnvelope.Parse(prediction.ScoreDebugJson);
        if (breakdown is null) return null;

        var reconstructed = new ScoringEngine.ScoringResult
        {
            BullishScore = prediction.BullishScore ?? 0,
            BearishScore = prediction.BearishScore ?? 0,
            WinningDirection = prediction.WinningDirection ?? "neutral",
            Confidence = prediction.ConfidenceScore,
            Risk = prediction.RiskScore,
            Breakdown = breakdown,
            Signals = [], // signals aren't persisted, but fingerprinting uses breakdown scores
        };

        return await ClassifySetupAsync(
            prediction, reconstructed, prediction.RunId, paperStockCandidateId);
    }

    // -----------------------------------------------------------------------
    // 2. Signal Evidence Extraction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts evidence from each scoring bucket, capturing what signals
    /// contributed and in what direction.
    /// </summary>
    private static Dictionary<string, BucketEvidence> BuildSignalEvidence(
        ScoringEngine.ScoringResult scoring)
    {
        var breakdown = scoring.Breakdown;
        var evidence = new Dictionary<string, BucketEvidence>();

        void AddBucket(string name, double bull, double bear, List<string> signals)
        {
            var net = bull - bear;
            var direction = Math.Abs(net) < ActiveBucketThreshold
                ? "neutral"
                : net > 0 ? "bullish" : "bearish";

            evidence[name] = new BucketEvidence
            {
                BucketName = name,
                BullishScore = bull,
                BearishScore = bear,
                NetScore = net,
                DominantDirection = direction,
                IsActive = Math.Abs(net) >= ActiveBucketThreshold,
                Signals = signals.Where(s => s.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith(CapitalizeBucket(name), StringComparison.OrdinalIgnoreCase)).ToList(),
            };
        }

        AddBucket("trend", breakdown.TrendBullish, breakdown.TrendBearish, scoring.Signals);
        AddBucket("momentum", breakdown.MomentumBullish, breakdown.MomentumBearish, scoring.Signals);
        AddBucket("volume", breakdown.VolumeBullish, breakdown.VolumeBearish, scoring.Signals);
        AddBucket("volatility", breakdown.VolatilityBullish, breakdown.VolatilityBearish, scoring.Signals);
        AddBucket("market_context", breakdown.MarketContextBullish, breakdown.MarketContextBearish, scoring.Signals);
        AddBucket("catalyst", breakdown.CatalystBullish, breakdown.CatalystBearish, scoring.Signals);
        AddBucket("research_signal", breakdown.ResearchSignalBullish, breakdown.ResearchSignalBearish, scoring.Signals);

        return evidence;
    }

    /// <summary>
    /// Public overload for callers that only have a ScoringBreakdown (e.g., PredictionGenerator
    /// doing a quick fingerprint lookup without a full ScoringResult).
    /// </summary>
    public static Dictionary<string, BucketEvidence> BuildSignalEvidenceFromBreakdown(ScoringBreakdown breakdown)
    {
        var evidence = new Dictionary<string, BucketEvidence>();

        void AddBucket(string name, double bull, double bear)
        {
            var net = bull - bear;
            var direction = Math.Abs(net) < ActiveBucketThreshold
                ? "neutral"
                : net > 0 ? "bullish" : "bearish";

            evidence[name] = new BucketEvidence
            {
                BucketName = name,
                BullishScore = bull,
                BearishScore = bear,
                NetScore = net,
                DominantDirection = direction,
                IsActive = Math.Abs(net) >= ActiveBucketThreshold,
            };
        }

        AddBucket("trend", breakdown.TrendBullish, breakdown.TrendBearish);
        AddBucket("momentum", breakdown.MomentumBullish, breakdown.MomentumBearish);
        AddBucket("volume", breakdown.VolumeBullish, breakdown.VolumeBearish);
        AddBucket("volatility", breakdown.VolatilityBullish, breakdown.VolatilityBearish);
        AddBucket("market_context", breakdown.MarketContextBullish, breakdown.MarketContextBearish);
        AddBucket("catalyst", breakdown.CatalystBullish, breakdown.CatalystBearish);
        AddBucket("research_signal", breakdown.ResearchSignalBullish, breakdown.ResearchSignalBearish);

        return evidence;
    }

    private static string CapitalizeBucket(string bucket) => bucket switch
    {
        "trend" => "Trend",
        "momentum" => "Momentum",
        "volume" => "Volume",
        "volatility" => "Volatility",
        "market_context" => "Market",
        "catalyst" => "Catalyst",
        "research_signal" => "Research",
        _ => bucket,
    };

    // -----------------------------------------------------------------------
    // 3. Fingerprint Generation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Generates a canonical fingerprint from the active signal evidence.
    /// The fingerprint captures WHICH signals are active and their direction,
    /// creating a hashable identity for the setup type.
    ///
    /// Example outputs:
    ///   "bearish_momentum|bearish_trend|bear_market"
    ///   "bullish_catalyst|bullish_momentum|bullish_trend|bullish_volume|bull_market"
    ///   "bullish_catalyst|bullish_trend"
    /// </summary>
    public static SetupFingerprint GenerateFingerprint(
        Dictionary<string, BucketEvidence> evidence, string winningDirection)
    {
        var components = new List<string>();
        var descriptions = new List<string>();

        foreach (var (bucket, ev) in evidence.OrderBy(e => e.Key))
        {
            if (!ev.IsActive) continue;

            var component = bucket switch
            {
                "market_context" => ev.DominantDirection == "bullish" ? "bull_market" : "bear_market",
                _ => $"{ev.DominantDirection}_{bucket}",
            };

            components.Add(component);
            descriptions.Add(BucketToDescription(bucket, ev.DominantDirection));
        }

        var confirmationCount = components.Count(c =>
        {
            // Count how many components agree with the winning direction
            return (winningDirection == "bullish" && c.Contains("bullish") || c.Contains("bull_market"))
                || (winningDirection == "bearish" && c.Contains("bearish") || c.Contains("bear_market"));
        });

        return new SetupFingerprint
        {
            Fingerprint = string.Join("|", components),
            Components = components,
            ConfirmationCount = confirmationCount,
            Description = components.Count > 0
                ? string.Join(" + ", descriptions)
                : "No clear setup",
            Direction = winningDirection,
        };
    }

    private static string BucketToDescription(string bucket, string direction) => (bucket, direction) switch
    {
        ("trend", "bullish") => "bullish trend",
        ("trend", "bearish") => "bearish trend",
        ("momentum", "bullish") => "positive momentum",
        ("momentum", "bearish") => "negative momentum",
        ("volume", "bullish") => "strong volume",
        ("volume", "bearish") => "weak volume",
        ("volatility", "bullish") => "volatility expansion",
        ("volatility", "bearish") => "volatility contraction",
        ("market_context", "bullish") => "favorable market regime",
        ("market_context", "bearish") => "unfavorable market regime",
        ("catalyst", "bullish") => "positive catalyst",
        ("catalyst", "bearish") => "negative catalyst",
        ("research_signal", "bullish") => "bullish research signal",
        ("research_signal", "bearish") => "bearish research signal",
        _ => $"{direction} {bucket}",
    };

    // -----------------------------------------------------------------------
    // 4. Trade Parameters
    // -----------------------------------------------------------------------

    /// <summary>
    /// Computes target, stop, invalidation, and risk/reward from the prediction's
    /// ATR-based price levels. If the prediction already has these, use them.
    /// Otherwise, compute from entry price and ATR.
    /// </summary>
    private static (double? Target, double? Stop, double? Invalidation, double? RiskReward)
        ComputeTradeParameters(PredictionCandidate pred, ScoringEngine.ScoringResult scoring)
    {
        var entry = pred.EntryReferencePrice;
        if (entry is null || entry == 0) return (null, null, null, null);

        // Use prediction's own levels if available
        var target = pred.TargetPrice;
        var stop = pred.StopPrice;
        var invalidation = pred.InvalidationPrice;

        // If not set, derive from ATR
        if (target is null && pred.Atr14 is double atr && atr > 0)
        {
            var atrMult = scoring.Confidence > 70 ? 2.0 : 1.5;
            target = scoring.WinningDirection == "bullish"
                ? Math.Round(entry.Value + atr * atrMult, 2)
                : Math.Round(entry.Value - atr * atrMult, 2);
        }

        if (stop is null && pred.Atr14 is double atrStop && atrStop > 0)
        {
            stop = scoring.WinningDirection == "bullish"
                ? Math.Round(entry.Value - atrStop * 1.0, 2)
                : Math.Round(entry.Value + atrStop * 1.0, 2);
        }

        if (invalidation is null && stop is not null)
        {
            // Invalidation is a wider level than stop — the point at which
            // the entire thesis is wrong, not just the trade.
            var buffer = Math.Abs(entry.Value - stop.Value) * 0.5;
            invalidation = scoring.WinningDirection == "bullish"
                ? Math.Round(stop.Value - buffer, 2)
                : Math.Round(stop.Value + buffer, 2);
        }

        // Risk/Reward
        double? riskReward = null;
        if (target is not null && stop is not null && entry.Value > 0)
        {
            var reward = Math.Abs(target.Value - entry.Value);
            var risk = Math.Abs(entry.Value - stop.Value);
            if (risk > 0) riskReward = Math.Round(reward / risk, 2);
        }

        return (target, stop, invalidation, riskReward);
    }

    // -----------------------------------------------------------------------
    // 5. Holding Period Determination
    // -----------------------------------------------------------------------

    /// <summary>
    /// Determines expected holding period based on setup characteristics.
    /// Catalyst-driven setups resolve faster. Range-bound setups need more time.
    /// </summary>
    private static SetupHoldingPeriod DetermineHoldingPeriod(
        PredictionCandidate pred,
        ScoringEngine.ScoringResult scoring,
        Dictionary<string, BucketEvidence> evidence)
    {
        // If prediction already specifies a time window, respect it
        if (pred.TimeWindow != "1_day")
        {
            return pred.TimeWindow switch
            {
                "intraday" => SetupHoldingPeriod.intraday,
                "3_day" => SetupHoldingPeriod.one_to_three_days,
                "1_week" => SetupHoldingPeriod.one_week,
                "1_month" => SetupHoldingPeriod.one_month,
                "3_month" or "6_month" or "1_year" => SetupHoldingPeriod.multi_month,
                _ => SetupHoldingPeriod.one_to_three_days,
            };
        }

        // Strong catalyst with high confidence → fast resolution expected
        var catalystEvidence = evidence.GetValueOrDefault("catalyst");
        if (catalystEvidence is { IsActive: true } && scoring.Confidence >= 65)
            return SetupHoldingPeriod.one_to_three_days;

        // High confidence with aligned signals → one week
        if (scoring.Confidence >= 55 && scoring.Breakdown.AlignedBuckets >= 4)
            return SetupHoldingPeriod.one_week;

        // Moderate confidence → two weeks to play out
        if (scoring.Confidence >= 40)
            return SetupHoldingPeriod.two_weeks;

        // Low confidence or few signals → needs more time
        return SetupHoldingPeriod.one_month;
    }

    private static int HoldingPeriodToDays(SetupHoldingPeriod period) => period switch
    {
        SetupHoldingPeriod.intraday => 1,
        SetupHoldingPeriod.one_to_three_days => 3,
        SetupHoldingPeriod.one_week => 5,
        SetupHoldingPeriod.two_weeks => 10,
        SetupHoldingPeriod.one_month => 22,
        SetupHoldingPeriod.multi_month => 66,
        _ => 5,
    };

    // -----------------------------------------------------------------------
    // 6. Historical Performance Lookup
    // -----------------------------------------------------------------------

    /// <summary>
    /// Looks up historical performance for a given setup fingerprint.
    /// Returns null if no history exists (the setup is novel).
    /// </summary>
    public async Task<SetupPerformance?> LookupSetupPerformanceAsync(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return null;

        try
        {
            var stat = await _repo.GetSetupLearningStatAsync(fingerprint);
            if (stat is null) return null;

            Dictionary<string, RegimePerformance>? byRegime = null;
            if (!string.IsNullOrEmpty(stat.MarketRegimeBreakdownJson))
            {
                try
                {
                    byRegime = JsonSerializer.Deserialize<Dictionary<string, RegimePerformance>>(
                        stat.MarketRegimeBreakdownJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch { /* ignore parse errors */ }
            }

            return new SetupPerformance
            {
                SetupFingerprint = stat.SetupFingerprint,
                Description = stat.Description,
                Direction = stat.Direction,
                SampleSize = stat.TotalOccurrences,
                WinRate = stat.WinRate,
                AverageWinPercent = stat.AverageWinPercent,
                AverageLossPercent = stat.AverageLossPercent,
                ExpectedValuePercent = stat.ExpectedValuePercent,
                AverageHoldingDays = stat.AverageHoldingDays,
                AverageConfirmationCount = stat.AverageConfirmationCount,
                Confidence = stat.Confidence,
                RiskRating = stat.RiskRating,
                IsTrusted = stat.IsTrusted,
                ByRegime = byRegime ?? new(),
                LastUpdatedAt = stat.LastUpdatedAt,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[setup-engine] Failed to look up performance for fingerprint {Fingerprint}", fingerprint);
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // 7. Setup Scoring
    // -----------------------------------------------------------------------

    /// <summary>
    /// Computes a composite setup quality score (0-100).
    /// This is distinct from the prediction's confidence score — it measures
    /// how favorable this TYPE of setup has historically been.
    /// </summary>
    private static double ComputeSetupScore(
        ScoringEngine.ScoringResult scoring,
        SetupFingerprint fingerprint,
        SetupPerformance? history,
        string? regime)
    {
        double score = 0;

        // Base: confirmation count (0-30 pts)
        score += Math.Min(fingerprint.ConfirmationCount * 7.5, 30);

        // Prediction confidence contribution (0-20 pts)
        score += scoring.Confidence * 0.2;

        // Data quality (0-10 pts)
        score += scoring.Breakdown.DataQualityFactor * 10;

        // Historical performance (0-40 pts) — the biggest component
        if (history is not null && history.SampleSize >= MinSampleForTrust)
        {
            // Win rate contribution (0-15 pts)
            score += Math.Max(0, (history.WinRate - 0.4) * 25); // 0 at 40% WR, 15 at 100%

            // Expected value contribution (0-15 pts)
            score += Math.Clamp(history.ExpectedValuePercent * 5, -5, 15);

            // Regime-specific bonus/penalty (0-10 pts)
            if (regime is not null && history.ByRegime.TryGetValue(regime, out var regimePerf))
            {
                score += Math.Clamp(regimePerf.ExpectedValuePercent * 3, -5, 10);
            }

            // Trust penalty
            if (!history.IsTrusted) score -= 15;
        }
        else
        {
            // Unknown setup — neutral, slight penalty for uncertainty
            score -= 5;
        }

        return Math.Clamp(Math.Round(score, 1), 0, 100);
    }

    // -----------------------------------------------------------------------
    // 8. Historical Favorability Determination
    // -----------------------------------------------------------------------

    /// <summary>
    /// Determines if a setup clears the bar for "historically favorable."
    /// A setup is favorable when:
    ///   - Sufficient sample size
    ///   - Positive expected value
    ///   - Win rate above coin-flip
    ///   - Setup is still trusted (not degraded)
    ///   - In the current market regime, it still works
    /// </summary>
    public static bool IsHistoricallyFavorable(SetupPerformance? history, string? regime)
    {
        if (history is null) return false;
        if (history.SampleSize < MinSampleForFavorable) return false;
        if (!history.IsTrusted) return false;
        if (history.ExpectedValuePercent < MinExpectedValueForFavorable) return false;
        if (history.WinRate < 0.45) return false;

        // Check regime-specific performance if available
        if (regime is not null && history.ByRegime.TryGetValue(regime, out var regimePerf))
        {
            // Even if overall EV is positive, if it's negative in this regime, not favorable
            if (regimePerf.SampleSize >= 5 && regimePerf.ExpectedValuePercent < 0)
                return false;
        }

        return true;
    }

    // -----------------------------------------------------------------------
    // 9. Market Regime Detection (reused from LearningEngine, centralized here)
    // -----------------------------------------------------------------------

    public static string? DetectMarketRegime(ScoringBreakdown b)
    {
        var marketNet = b.MarketContextBullish - b.MarketContextBearish;
        var volNet = b.VolatilityBullish - b.VolatilityBearish;

        if (volNet > 5) return "high_volatility";
        if (marketNet > 5) return "bull_trend";
        if (marketNet < -5) return "bear_trend";
        if (Math.Abs(marketNet) <= 3) return "sideways";
        return null;
    }

    // -----------------------------------------------------------------------
    // 10. Setup Persistence
    // -----------------------------------------------------------------------

    /// <summary>
    /// Persists a trade setup to the database for tracking and learning.
    /// </summary>
    public async Task<TradeSetup?> SaveSetupAsync(TradeSetup setup)
    {
        try
        {
            var data = new
            {
                prediction_id = setup.PredictionId,
                paper_stock_candidate_id = setup.PaperStockCandidateId,
                run_id = setup.RunId,
                ticker = setup.Ticker,
                fingerprint = setup.Fingerprint.Fingerprint,
                fingerprint_components = JsonSerializer.Serialize(setup.Fingerprint.Components),
                fingerprint_description = setup.Fingerprint.Description,
                confirmation_count = setup.Fingerprint.ConfirmationCount,
                direction = setup.Direction,
                signal_evidence_json = JsonSerializer.Serialize(setup.SignalEvidence),
                entry_price = setup.EntryPrice,
                target_price = setup.TargetPrice,
                stop_price = setup.StopPrice,
                invalidation_price = setup.InvalidationPrice,
                risk_reward_ratio = setup.RiskRewardRatio,
                expected_holding_period = setup.ExpectedHoldingPeriod.ToString(),
                max_holding_days = setup.MaxHoldingDays,
                market_regime = setup.MarketRegime,
                setup_score = setup.SetupScore,
                is_historically_favorable = setup.IsHistoricallyFavorable,
                historical_win_rate = setup.HistoricalPerformance?.WinRate,
                historical_ev = setup.HistoricalPerformance?.ExpectedValuePercent,
                historical_sample_size = setup.HistoricalPerformance?.SampleSize,
                status = setup.Status.ToString(),
                created_at = setup.CreatedAt.ToString("o"),
            };

            return await _repo.SaveTradeSetupAsync(data) ? setup : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[setup-engine] Failed to save setup for {Ticker}", setup.Ticker);
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // 11. Setup Evaluation — did the thesis succeed?
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluates an active setup against current prices.
    /// Instead of a one-day direction check, determines if:
    ///   - Target was reached (success)
    ///   - Stop was hit (failure)
    ///   - Invalidation was triggered (failure)
    ///   - Max holding period elapsed (expired)
    ///
    /// Returns null if the setup should remain active (not yet resolved).
    /// </summary>
    public static SetupOutcome? EvaluateSetup(
        TradeSetup setup,
        double currentPrice,
        double highSinceEntry,
        double lowSinceEntry,
        int daysSinceEntry)
    {
        if (setup.EntryPrice is not double entry || entry <= 0)
            return null;

        var isBullish = setup.Direction == "bullish";
        var returnPct = ((currentPrice - entry) / entry) * 100;

        // Compute max favorable/adverse excursion
        var maxFavorablePrice = isBullish ? highSinceEntry : lowSinceEntry;
        var maxAdversePrice = isBullish ? lowSinceEntry : highSinceEntry;
        var maxFavorablePct = isBullish
            ? ((highSinceEntry - entry) / entry) * 100
            : ((entry - lowSinceEntry) / entry) * 100;
        var maxAdversePct = isBullish
            ? ((entry - lowSinceEntry) / entry) * 100
            : ((highSinceEntry - entry) / entry) * 100;

        // Check target hit
        var targetHit = setup.TargetPrice is double target && (
            (isBullish && highSinceEntry >= target) ||
            (!isBullish && lowSinceEntry <= target));

        // Check stop hit
        var stopHit = setup.StopPrice is double stop && (
            (isBullish && lowSinceEntry <= stop) ||
            (!isBullish && highSinceEntry >= stop));

        // Check invalidation
        var invalidationHit = setup.InvalidationPrice is double inv && (
            (isBullish && lowSinceEntry <= inv) ||
            (!isBullish && highSinceEntry >= inv));

        // Direction correct
        var directionCorrect = isBullish ? returnPct > 0 : returnPct < 0;

        // Determine resolution
        // Priority: if target was hit before stop (even if stop was also hit later), it's a win.
        // In reality we'd need intraday data to know which happened first.
        // For now: target hit + direction correct = success.
        SetupStatus resolution;
        bool succeeded;

        if (targetHit && directionCorrect)
        {
            resolution = SetupStatus.target_hit;
            succeeded = true;
        }
        else if (invalidationHit)
        {
            resolution = SetupStatus.invalidated;
            succeeded = false;
        }
        else if (stopHit)
        {
            resolution = SetupStatus.stop_hit;
            succeeded = false;
        }
        else if (daysSinceEntry >= setup.MaxHoldingDays)
        {
            resolution = SetupStatus.expired;
            // Expired with direction correct is a partial win
            succeeded = directionCorrect;
        }
        else
        {
            // Not resolved yet — setup remains active
            return null;
        }

        var exitPrice = currentPrice;
        if (targetHit && setup.TargetPrice.HasValue)
            exitPrice = setup.TargetPrice.Value; // Assume exit at target
        if (stopHit && !targetHit && setup.StopPrice.HasValue)
            exitPrice = setup.StopPrice.Value; // Assume exit at stop

        var exitReturnPct = ((exitPrice - entry) / entry) * 100;

        var summary = resolution switch
        {
            SetupStatus.target_hit => $"Target hit. {setup.Ticker} reached ${setup.TargetPrice:F2} " +
                $"(+{Math.Abs(exitReturnPct):F1}%) in {daysSinceEntry} days.",
            SetupStatus.stop_hit => $"Stop hit. {setup.Ticker} hit ${setup.StopPrice:F2} " +
                $"({exitReturnPct:F1}%) in {daysSinceEntry} days.",
            SetupStatus.invalidated => $"Setup invalidated. {setup.Ticker} reached invalidation level. " +
                $"Thesis was wrong.",
            SetupStatus.expired => $"Setup expired after {daysSinceEntry} days. " +
                $"{setup.Ticker} at ${currentPrice:F2} ({returnPct:F1}%). " +
                $"Direction was {(directionCorrect ? "correct" : "wrong")}.",
            _ => $"{setup.Ticker} resolved as {resolution} ({returnPct:F1}%).",
        };

        var lesson = BuildSetupLesson(setup, resolution, exitReturnPct, daysSinceEntry);

        return new SetupOutcome
        {
            SetupSucceeded = succeeded,
            Resolution = resolution,
            ResolvedAt = DateTimeOffset.UtcNow,
            DaysHeld = daysSinceEntry,
            ExitPrice = exitPrice,
            MaxFavorablePrice = maxFavorablePrice,
            MaxAdversePrice = maxAdversePrice,
            MaxFavorablePercent = Math.Round(maxFavorablePct, 2),
            MaxAdversePercent = Math.Round(maxAdversePct, 2),
            ReturnPercent = Math.Round(exitReturnPct, 2),
            DirectionCorrect = directionCorrect,
            TargetHit = targetHit,
            StopHit = stopHit,
            InvalidationHit = invalidationHit,
            OutcomeSummary = summary,
            Lesson = lesson,
        };
    }

    private static string BuildSetupLesson(
        TradeSetup setup, SetupStatus resolution, double returnPct, int daysHeld)
    {
        var fpDesc = setup.Fingerprint.Description;

        return resolution switch
        {
            SetupStatus.target_hit =>
                $"Setup [{fpDesc}] on {setup.Ticker}: TARGET HIT (+{Math.Abs(returnPct):F1}%, {daysHeld}d). " +
                $"This setup type should be weighted higher. R/R was {setup.RiskRewardRatio:F1}.",
            SetupStatus.stop_hit =>
                $"Setup [{fpDesc}] on {setup.Ticker}: STOP HIT ({returnPct:F1}%, {daysHeld}d). " +
                $"Review if stop was too tight or if this setup type is unreliable.",
            SetupStatus.invalidated =>
                $"Setup [{fpDesc}] on {setup.Ticker}: INVALIDATED ({returnPct:F1}%, {daysHeld}d). " +
                $"Thesis was wrong — this setup type may not work in current regime ({setup.MarketRegime}).",
            SetupStatus.expired =>
                $"Setup [{fpDesc}] on {setup.Ticker}: EXPIRED ({returnPct:F1}%, {daysHeld}d). " +
                $"Consider extending holding period for this setup type.",
            _ => $"Setup [{fpDesc}] on {setup.Ticker}: resolved as {resolution}.",
        };
    }

    // -----------------------------------------------------------------------
    // 12. Batch: Get best-performing setups for learning display
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the top N setup fingerprints ranked by historical expected value.
    /// These are the setups the system should prioritize.
    /// </summary>
    public async Task<List<SetupPerformance>> GetTopSetupsAsync(int count = 10)
    {
        var allStats = await _repo.GetAllSetupLearningStatsAsync();
        return allStats
            .Where(s => s.TotalOccurrences >= MinSampleForTrust && s.IsTrusted)
            .OrderByDescending(s => s.ExpectedValuePercent)
            .Take(count)
            .Select(s => new SetupPerformance
            {
                SetupFingerprint = s.SetupFingerprint,
                Description = s.Description,
                Direction = s.Direction,
                SampleSize = s.TotalOccurrences,
                WinRate = s.WinRate,
                AverageWinPercent = s.AverageWinPercent,
                AverageLossPercent = s.AverageLossPercent,
                ExpectedValuePercent = s.ExpectedValuePercent,
                AverageHoldingDays = s.AverageHoldingDays,
                AverageConfirmationCount = s.AverageConfirmationCount,
                Confidence = s.Confidence,
                RiskRating = s.RiskRating,
                IsTrusted = s.IsTrusted,
                LastUpdatedAt = s.LastUpdatedAt,
            })
            .ToList();
    }

    /// <summary>
    /// Returns setups that should no longer be trusted — historically good
    /// but recently degraded.
    /// </summary>
    public async Task<List<SetupPerformance>> GetDegradedSetupsAsync()
    {
        var allStats = await _repo.GetAllSetupLearningStatsAsync();
        return allStats
            .Where(s => s.TotalOccurrences >= MinSampleForTrust && !s.IsTrusted)
            .OrderBy(s => s.ExpectedValuePercent)
            .Select(s => new SetupPerformance
            {
                SetupFingerprint = s.SetupFingerprint,
                Description = s.Description,
                Direction = s.Direction,
                SampleSize = s.TotalOccurrences,
                WinRate = s.WinRate,
                ExpectedValuePercent = s.ExpectedValuePercent,
                IsTrusted = false,
                LastUpdatedAt = s.LastUpdatedAt,
            })
            .ToList();
    }

    // -----------------------------------------------------------------------
    // EMA Pullback Setup Scanner
    // -----------------------------------------------------------------------

    /// <summary>
    /// Scans a ticker for a valid EMA Pullback setup using mechanical rules:
    ///   1. Uptrend: price > EMA21, EMA21 > EMA50
    ///   2. Pullback: recent bar(s) touched or dipped near EMA21
    ///   3. Bounce confirmation: most recent bar closed above EMA21 with a green candle
    ///   4. R:R >= 2.0 (stop below pullback low, target at swing high)
    ///
    /// Returns a PredictionCandidate if a valid setup is found, null otherwise.
    /// The confidence score reflects setup quality (trend strength, pullback depth, volume).
    /// </summary>
    public async Task<PredictionCandidate?> ScanForEmaPullbackAsync(
        string ticker, MarketSnapshot snapshot, string runId)
    {
        // Need at least a quote and recent bars
        if (snapshot.Quote is null || snapshot.RecentBars.Count < 10)
            return null;

        var price = snapshot.Quote.Price;
        if (price <= 0) return null;

        // Fetch EMA 21 and EMA 50
        var ema21 = await _marketData.GetEma21Async(ticker);
        var emaData = await _marketData.GetEmaAsync(ticker);
        var ema50 = emaData.Ema50;

        if (ema21 is null || ema50 is null || ema21.Value <= 0 || ema50.Value <= 0)
            return null;

        // ── CHECK 1: Uptrend — EMA21 > EMA50 ──
        if (ema21.Value <= ema50.Value)
        {
            _logger.LogDebug("[ema-pullback] {Ticker}: SKIP — not in uptrend (EMA21 {Ema21:F2} <= EMA50 {Ema50:F2})",
                ticker, ema21.Value, ema50.Value);
            return null;
        }

        // ── CHECK 2: Price near or above EMA21 ──
        // After bounce, price should be at or slightly above EMA21
        var priceToEma21Pct = (price - ema21.Value) / ema21.Value * 100;
        if (priceToEma21Pct > 3.0) // more than 3% above = already extended, not a pullback
        {
            _logger.LogDebug("[ema-pullback] {Ticker}: SKIP — price too far above EMA21 ({Pct:F2}%)",
                ticker, priceToEma21Pct);
            return null;
        }
        if (priceToEma21Pct < -2.0) // more than 2% below = broken trend, not a pullback
        {
            _logger.LogDebug("[ema-pullback] {Ticker}: SKIP — price too far below EMA21 ({Pct:F2}%)",
                ticker, priceToEma21Pct);
            return null;
        }

        // ── CHECK 3: Pullback evidence — at least one recent bar dipped near EMA21 ──
        // Look at last 5 bars for a bar whose low was within 1% of EMA21 or below it
        var recentBars = snapshot.RecentBars
            .OrderByDescending(b => b.Date)
            .Take(5)
            .ToList();

        var pullbackBar = recentBars.FirstOrDefault(b =>
        {
            var lowToEma = (b.Low - ema21.Value) / ema21.Value * 100;
            return lowToEma <= 0.5; // low was within 0.5% above or below EMA21
        });

        if (pullbackBar is null)
        {
            _logger.LogDebug("[ema-pullback] {Ticker}: SKIP — no recent pullback to EMA21", ticker);
            return null;
        }

        // ── CHECK 4: Bounce confirmation — most recent bar is green and closed above EMA21 ──
        var latestBar = recentBars[0];
        if (latestBar.Close <= latestBar.Open) // red candle = no confirmation
        {
            _logger.LogDebug("[ema-pullback] {Ticker}: SKIP — latest bar is red (no bounce confirmation)", ticker);
            return null;
        }
        if (latestBar.Close < ema21.Value) // closed below EMA21
        {
            _logger.LogDebug("[ema-pullback] {Ticker}: SKIP — latest close {Close:F2} below EMA21 {Ema21:F2}",
                ticker, latestBar.Close, ema21.Value);
            return null;
        }

        // ── COMPUTE TRADE PARAMETERS ──
        // Stop: below pullback low (lowest low of bars that touched EMA21)
        var pullbackLow = recentBars
            .Where(b => (b.Low - ema21.Value) / ema21.Value * 100 <= 1.0)
            .Min(b => b.Low);
        var stopPrice = Math.Round(pullbackLow * 0.995, 2); // 0.5% cushion below pullback low

        // Target: highest high in recent bars (swing high)
        var allBars = snapshot.RecentBars.OrderByDescending(b => b.Date).Take(20).ToList();
        var swingHigh = allBars.Max(b => b.High);
        var targetPrice = Math.Round(swingHigh, 2);

        // R:R check
        var risk = price - stopPrice;
        var reward = targetPrice - price;
        if (risk <= 0 || reward <= 0)
            return null;

        var rrRatio = Math.Round(reward / risk, 2);
        if (rrRatio < 2.0)
        {
            _logger.LogDebug("[ema-pullback] {Ticker}: SKIP — R:R {RR:F2} < 2.0 (target {Target:F2}, stop {Stop:F2})",
                ticker, rrRatio, targetPrice, stopPrice);
            return null;
        }

        // ── GRADE SETUP QUALITY (0–100) ──
        var qualityScore = 50; // base

        // Trend strength: wider EMA21-EMA50 gap = stronger trend
        var emaSeparationPct = (ema21.Value - ema50.Value) / ema50.Value * 100;
        if (emaSeparationPct > 5) qualityScore += 15;
        else if (emaSeparationPct > 2) qualityScore += 8;

        // Pullback depth: shallow pullback (barely touched EMA21) = better
        var pullbackDepthPct = Math.Abs((pullbackBar.Low - ema21.Value) / ema21.Value * 100);
        if (pullbackDepthPct < 0.5) qualityScore += 10; // barely touched
        else if (pullbackDepthPct < 1.5) qualityScore += 5;
        else qualityScore -= 5; // deep pullback = weaker

        // R:R bonus
        if (rrRatio >= 3.0) qualityScore += 10;
        else if (rrRatio >= 2.5) qualityScore += 5;

        // Volume: declining on pullback, rising on bounce = ideal
        if (recentBars.Count >= 3)
        {
            var bounceVolume = recentBars[0].Volume;
            var pullbackVolume = recentBars.Skip(1).Take(2).Average(b => b.Volume);
            if (bounceVolume > pullbackVolume * 1.2)
                qualityScore += 10; // volume confirmation
        }

        // Cap quality score
        qualityScore = Math.Clamp(qualityScore, 20, 90);

        // ── BUILD SETUP DETAILS ──
        var setupDetails = new
        {
            ema21 = ema21.Value,
            ema50 = ema50.Value,
            ema_separation_pct = Math.Round(emaSeparationPct, 2),
            pullback_low = pullbackLow,
            pullback_depth_pct = Math.Round(pullbackDepthPct, 2),
            swing_high = swingHigh,
            rr_ratio = rrRatio,
            price_to_ema21_pct = Math.Round(priceToEma21Pct, 2),
            bounce_candle_date = latestBar.Date,
            bounce_volume = latestBar.Volume,
        };

        // ── AI GRADING — refine mechanical score with AI judgment ──
        var aiQuality = await GradeEmaPullbackWithAiAsync(
            ticker, price, ema21.Value, ema50.Value, emaSeparationPct,
            pullbackDepthPct, rrRatio, stopPrice, targetPrice,
            latestBar, recentBars, qualityScore);

        if (aiQuality is not null)
            qualityScore = aiQuality.Value;

        _logger.LogInformation(
            "[ema-pullback] {Ticker}: SETUP DETECTED — quality={Quality}, R:R={RR:F1}, entry={Price:F2}, stop={Stop:F2}, target={Target:F2}, EMA21={Ema21:F2}, EMA50={Ema50:F2}",
            ticker, qualityScore, rrRatio, price, stopPrice, targetPrice, ema21.Value, ema50.Value);

        return new PredictionCandidate
        {
            Id = Guid.NewGuid().ToString(),
            RunId = runId,
            Ticker = ticker,
            PredictionType = PredictionType.bullish,
            AssetType = PredictionAssetType.stock,
            TimeWindow = "swing", // no time prediction — hold until target or stop
            ConfidenceScore = qualityScore,
            ImportanceScore = qualityScore,
            RiskScore = Math.Clamp(100 - qualityScore, 10, 80),
            EntryReferencePrice = price,
            TargetPrice = targetPrice,
            StopPrice = stopPrice,
            RiskRewardRatio = rrRatio,
            PricePredictionMethod = "ema_pullback",
            SetupType = "ema_pullback",
            SetupDetailsJson = JsonSerializer.Serialize(setupDetails),
            BullishCase = $"EMA Pullback setup: price pulled back to 21 EMA ({ema21.Value:F2}) in uptrend and bounced. Trend strength: EMA21 {emaSeparationPct:F1}% above EMA50.",
            BearishCase = $"Invalidation if price closes below pullback low ({pullbackLow:F2}) or EMA21 breaks below EMA50.",
            PredictionReason = $"Mechanical EMA Pullback: uptrend confirmed (EMA21 > EMA50), price pulled back to 21 EMA, green bounce candle. R:R {rrRatio:F1}:1.",
            InvalidationRule = $"Close below {stopPrice:F2} (pullback low) invalidates the setup.",
            WinningDirection = "bullish",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    // =======================================================================
    //  VWAP RECLAIM — mean-reversion to volume-weighted average
    // =======================================================================

    /// <summary>
    /// Detect stocks that dropped below their volume-weighted average price
    /// (computed from recent daily bars) and are now reclaiming with volume.
    /// Entry on reclaim, stop below the low of the dip, target previous swing high.
    /// </summary>
    public async Task<PredictionCandidate?> ScanForVwapReclaimAsync(
        string ticker, MarketSnapshot snapshot, string runId)
    {
        if (snapshot.Quote is null || snapshot.RecentBars.Count < 15)
            return null;

        var price = snapshot.Quote.Price;
        if (price <= 0) return null;

        var bars = snapshot.RecentBars.OrderByDescending(b => b.Date).Take(20).ToList();
        if (bars.Count < 10) return null;

        // ── Compute VWAP from last 10 trading days ──
        var vwapBars = bars.Take(10).ToList();
        var totalDollarVolume = vwapBars.Sum(b => ((b.High + b.Low + b.Close) / 3.0) * b.Volume);
        var totalVolume = vwapBars.Sum(b => b.Volume);
        if (totalVolume <= 0) return null;
        var vwap = totalDollarVolume / totalVolume;

        // ── CHECK 1: Price is now at or above VWAP ──
        var priceToVwapPct = (price - vwap) / vwap * 100;
        if (priceToVwapPct > 2.0) // already too far above — chasing
        {
            _logger.LogDebug("[vwap-reclaim] {Ticker}: SKIP — price {Pct:F2}% above VWAP", ticker, priceToVwapPct);
            return null;
        }
        if (priceToVwapPct < -1.5) // still below — not reclaimed
        {
            _logger.LogDebug("[vwap-reclaim] {Ticker}: SKIP — price {Pct:F2}% below VWAP (not reclaimed)", ticker, priceToVwapPct);
            return null;
        }

        // ── CHECK 2: Recent dip below VWAP — at least one of last 5 bars closed below ──
        var recentBars = bars.Take(5).ToList();
        var dipBar = recentBars.Skip(1).FirstOrDefault(b => b.Close < vwap);
        if (dipBar is null)
        {
            _logger.LogDebug("[vwap-reclaim] {Ticker}: SKIP — no recent dip below VWAP", ticker);
            return null;
        }

        // ── CHECK 3: Reclaim confirmation — latest bar is green and closed above VWAP ──
        var latestBar = recentBars[0];
        if (latestBar.Close <= latestBar.Open)
        {
            _logger.LogDebug("[vwap-reclaim] {Ticker}: SKIP — latest bar is red (no reclaim confirmation)", ticker);
            return null;
        }
        if (latestBar.Close < vwap)
        {
            _logger.LogDebug("[vwap-reclaim] {Ticker}: SKIP — latest close {Close:F2} below VWAP {Vwap:F2}",
                ticker, latestBar.Close, vwap);
            return null;
        }

        // ── CHECK 4: Volume confirmation — bounce bar has above-average volume ──
        var avgVolume = vwapBars.Average(b => b.Volume);
        var volumeRatio = latestBar.Volume / Math.Max(avgVolume, 1);
        if (volumeRatio < 0.8) // below-average volume on reclaim = weak
        {
            _logger.LogDebug("[vwap-reclaim] {Ticker}: SKIP — weak volume on reclaim ({Ratio:F2}x avg)", ticker, volumeRatio);
            return null;
        }

        // ── COMPUTE TRADE PARAMETERS ──
        // Stop: below the low of the dip
        var dipLow = recentBars.Where(b => b.Close < vwap || b.Low < vwap).Min(b => b.Low);
        var stopPrice = Math.Round(dipLow * 0.995, 2); // 0.5% cushion

        // Target: previous swing high (highest high in last 20 bars)
        var swingHigh = bars.Max(b => b.High);
        var targetPrice = Math.Round(swingHigh, 2);

        // R:R check
        var risk = price - stopPrice;
        var reward = targetPrice - price;
        if (risk <= 0 || reward <= 0) return null;

        var rrRatio = Math.Round(reward / risk, 2);
        if (rrRatio < 2.0)
        {
            _logger.LogDebug("[vwap-reclaim] {Ticker}: SKIP — R:R {RR:F2} < 2.0", ticker, rrRatio);
            return null;
        }

        // ── GRADE SETUP QUALITY ──
        var qualityScore = 50;

        // Volume on reclaim
        if (volumeRatio >= 1.5) qualityScore += 15;
        else if (volumeRatio >= 1.2) qualityScore += 8;

        // How clean was the dip? Shallow dip below VWAP = better
        var dipDepthPct = Math.Abs((dipBar.Close - vwap) / vwap * 100);
        if (dipDepthPct < 1.0) qualityScore += 10;
        else if (dipDepthPct < 2.0) qualityScore += 5;
        else qualityScore -= 5;

        // R:R bonus
        if (rrRatio >= 3.0) qualityScore += 10;
        else if (rrRatio >= 2.5) qualityScore += 5;

        // Price above both short EMAs = strong context
        var ema21 = await _marketData.GetEma21Async(ticker);
        if (ema21 is not null && price > ema21.Value)
            qualityScore += 5;

        qualityScore = Math.Clamp(qualityScore, 20, 90);

        // ── SETUP DETAILS ──
        var setupDetails = new
        {
            vwap = Math.Round(vwap, 2),
            price_to_vwap_pct = Math.Round(priceToVwapPct, 2),
            dip_bar_date = dipBar.Date,
            dip_depth_pct = Math.Round(dipDepthPct, 2),
            volume_ratio = Math.Round(volumeRatio, 2),
            rr_ratio = rrRatio,
        };

        // ── AI GRADING ──
        var aiQuality = await GradeSetupWithAiAsync(
            "vwap_reclaim", ticker, price, rrRatio, stopPrice, targetPrice,
            latestBar, recentBars, qualityScore,
            $"VWAP (10-day): ${vwap:F2}\nPrice-to-VWAP: {priceToVwapPct:F1}%\nDip depth: {dipDepthPct:F1}%\nVolume ratio on reclaim: {volumeRatio:F2}x avg",
            "VWAP RECLAIM: Stock dipped below volume-weighted average and is reclaiming with volume. " +
            "Grade: trend context, volume conviction on reclaim, depth of dip (shallow=better), bounce candle quality.");

        if (aiQuality is not null)
            qualityScore = aiQuality.Value;

        _logger.LogInformation(
            "[vwap-reclaim] {Ticker}: SETUP DETECTED — quality={Quality}, R:R={RR:F1}, entry=${Price:F2}, stop=${Stop:F2}, target=${Target:F2}, VWAP=${Vwap:F2}",
            ticker, qualityScore, rrRatio, price, stopPrice, targetPrice, vwap);

        return new PredictionCandidate
        {
            Id = Guid.NewGuid().ToString(),
            RunId = runId,
            Ticker = ticker,
            PredictionType = PredictionType.bullish,
            AssetType = PredictionAssetType.stock,
            TimeWindow = "swing",
            ConfidenceScore = qualityScore,
            ImportanceScore = qualityScore,
            RiskScore = Math.Clamp(100 - qualityScore, 10, 80),
            EntryReferencePrice = price,
            TargetPrice = targetPrice,
            StopPrice = stopPrice,
            RiskRewardRatio = rrRatio,
            PricePredictionMethod = "vwap_reclaim",
            SetupType = "vwap_reclaim",
            SetupDetailsJson = JsonSerializer.Serialize(setupDetails),
            BullishCase = $"VWAP Reclaim: price dipped below 10-day VWAP (${vwap:F2}) and reclaimed with {volumeRatio:F1}x avg volume.",
            BearishCase = $"Invalidation if price closes below dip low (${dipLow:F2}) or fails to hold above VWAP.",
            PredictionReason = $"VWAP Reclaim: dipped {dipDepthPct:F1}% below VWAP, reclaimed with volume confirmation. R:R {rrRatio:F1}:1.",
            InvalidationRule = $"Close below ${stopPrice:F2} (dip low) invalidates the setup.",
            WinningDirection = "bullish",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    // =======================================================================
    //  BULL FLAG — consolidation after strong move, breakout entry
    // =======================================================================

    /// <summary>
    /// Detect bull flag: strong upward "pole" move (5-10 bars), followed by
    /// 3-5 bars of tight consolidation (the "flag"), and a breakout candle
    /// closing above the flag's high with volume.
    /// </summary>
    public async Task<PredictionCandidate?> ScanForBullFlagAsync(
        string ticker, MarketSnapshot snapshot, string runId)
    {
        if (snapshot.Quote is null || snapshot.RecentBars.Count < 15)
            return null;

        var price = snapshot.Quote.Price;
        if (price <= 0) return null;

        var bars = snapshot.RecentBars.OrderByDescending(b => b.Date).Take(20).ToList();
        if (bars.Count < 12) return null;

        // ── FIND THE FLAG (consolidation) — last 3-5 bars ──
        // Flag = tight range. Check 3, 4, and 5-bar windows, pick the best.
        int bestFlagLen = 0;
        double bestFlagTightness = double.MaxValue;

        for (int flagLen = 3; flagLen <= 5 && flagLen < bars.Count - 5; flagLen++)
        {
            var flagBars = bars.Skip(1).Take(flagLen).ToList(); // skip latest (breakout candle)
            var flagHigh = flagBars.Max(b => b.High);
            var flagLow = flagBars.Min(b => b.Low);
            var flagRange = (flagHigh - flagLow) / flagLow * 100;

            if (flagRange < bestFlagTightness)
            {
                bestFlagTightness = flagRange;
                bestFlagLen = flagLen;
            }
        }

        if (bestFlagLen == 0 || bestFlagTightness > 5.0) // flag range > 5% = not tight enough
        {
            _logger.LogDebug("[bull-flag] {Ticker}: SKIP — no tight consolidation found (best range={Range:F1}%)",
                ticker, bestFlagTightness);
            return null;
        }

        var flag = bars.Skip(1).Take(bestFlagLen).ToList();
        var flagHighPrice = flag.Max(b => b.High);
        var flagLowPrice = flag.Min(b => b.Low);

        // ── FIND THE POLE (strong move before flag) ──
        var poleBars = bars.Skip(1 + bestFlagLen).Take(7).ToList();
        if (poleBars.Count < 3) return null;

        var poleStart = poleBars.Last().Open;
        var poleEnd = poleBars.First().Close;
        var poleMovePercent = (poleEnd - poleStart) / poleStart * 100;

        if (poleMovePercent < 5.0) // need at least 5% move for a valid pole
        {
            _logger.LogDebug("[bull-flag] {Ticker}: SKIP — pole move too small ({Move:F1}%)", ticker, poleMovePercent);
            return null;
        }

        // ── CHECK: Flag should be retracing downward (lower highs/lows) or flat ──
        // Flag closing range should be within the pole's upper portion
        var flagAvgClose = flag.Average(b => b.Close);
        var flagRetracePct = (poleEnd - flagAvgClose) / (poleEnd - poleStart) * 100;
        if (flagRetracePct > 60) // flag retraced more than 60% of pole = not a flag
        {
            _logger.LogDebug("[bull-flag] {Ticker}: SKIP — flag retrace too deep ({Retrace:F0}%)", ticker, flagRetracePct);
            return null;
        }

        // ── CHECK: Breakout — latest bar closes above flag high ──
        var latestBar = bars[0];
        if (latestBar.Close <= flagHighPrice)
        {
            _logger.LogDebug("[bull-flag] {Ticker}: SKIP — no breakout (close {Close:F2} <= flag high {FH:F2})",
                ticker, latestBar.Close, flagHighPrice);
            return null;
        }

        // ── COMPUTE TRADE PARAMETERS ──
        // Stop: below flag low
        var stopPrice = Math.Round(flagLowPrice * 0.995, 2);

        // Target: measured move (pole height added to breakout point)
        var poleHeight = poleEnd - poleStart;
        var targetPrice = Math.Round(flagHighPrice + poleHeight, 2);

        var risk = price - stopPrice;
        var reward = targetPrice - price;
        if (risk <= 0 || reward <= 0) return null;

        var rrRatio = Math.Round(reward / risk, 2);
        if (rrRatio < 2.0)
        {
            _logger.LogDebug("[bull-flag] {Ticker}: SKIP — R:R {RR:F2} < 2.0", ticker, rrRatio);
            return null;
        }

        // ── GRADE SETUP QUALITY ──
        var qualityScore = 50;

        // Pole strength
        if (poleMovePercent >= 10) qualityScore += 15;
        else if (poleMovePercent >= 7) qualityScore += 8;

        // Flag tightness (range %)
        if (bestFlagTightness < 2.0) qualityScore += 15;
        else if (bestFlagTightness < 3.5) qualityScore += 8;

        // Flag retrace — shallow is better
        if (flagRetracePct < 25) qualityScore += 10;
        else if (flagRetracePct < 40) qualityScore += 5;

        // Volume: declining during flag, expanding on breakout
        var flagAvgVol = flag.Average(b => b.Volume);
        var poleAvgVol = poleBars.Average(b => b.Volume);
        if (flagAvgVol < poleAvgVol * 0.7) qualityScore += 5; // volume dried up in flag
        if (latestBar.Volume > flagAvgVol * 1.3) qualityScore += 5; // volume on breakout

        // R:R bonus
        if (rrRatio >= 3.0) qualityScore += 5;

        qualityScore = Math.Clamp(qualityScore, 20, 90);

        var setupDetails = new
        {
            pole_move_pct = Math.Round(poleMovePercent, 2),
            flag_range_pct = Math.Round(bestFlagTightness, 2),
            flag_retrace_pct = Math.Round(flagRetracePct, 1),
            flag_length = bestFlagLen,
            flag_high = flagHighPrice,
            flag_low = flagLowPrice,
            breakout_close = latestBar.Close,
            rr_ratio = rrRatio,
        };

        // ── AI GRADING ──
        var recentBars = bars.Take(5).ToList();
        var aiQuality = await GradeSetupWithAiAsync(
            "bull_flag", ticker, price, rrRatio, stopPrice, targetPrice,
            latestBar, recentBars, qualityScore,
            $"Pole move: {poleMovePercent:F1}% over {poleBars.Count} bars\nFlag: {bestFlagLen} bars, range {bestFlagTightness:F1}%, retrace {flagRetracePct:F0}%\nBreakout close: ${latestBar.Close:F2} above flag high ${flagHighPrice:F2}",
            "BULL FLAG: Strong up-move (pole), tight consolidation (flag), breakout above flag high. " +
            "Grade: pole strength, flag tightness, breakout volume, retrace depth (shallow=better).");

        if (aiQuality is not null)
            qualityScore = aiQuality.Value;

        _logger.LogInformation(
            "[bull-flag] {Ticker}: SETUP DETECTED — quality={Quality}, R:R={RR:F1}, entry=${Price:F2}, stop=${Stop:F2}, target=${Target:F2}, pole={Pole:F1}%, flag={Flag:F1}%",
            ticker, qualityScore, rrRatio, price, stopPrice, targetPrice, poleMovePercent, bestFlagTightness);

        return new PredictionCandidate
        {
            Id = Guid.NewGuid().ToString(),
            RunId = runId,
            Ticker = ticker,
            PredictionType = PredictionType.bullish,
            AssetType = PredictionAssetType.stock,
            TimeWindow = "swing",
            ConfidenceScore = qualityScore,
            ImportanceScore = qualityScore,
            RiskScore = Math.Clamp(100 - qualityScore, 10, 80),
            EntryReferencePrice = price,
            TargetPrice = targetPrice,
            StopPrice = stopPrice,
            RiskRewardRatio = rrRatio,
            PricePredictionMethod = "bull_flag",
            SetupType = "bull_flag",
            SetupDetailsJson = JsonSerializer.Serialize(setupDetails),
            BullishCase = $"Bull Flag: {poleMovePercent:F1}% pole move, {bestFlagLen}-bar flag ({bestFlagTightness:F1}% range), breakout above ${flagHighPrice:F2}. Measured move target ${targetPrice:F2}.",
            BearishCase = $"Invalidation if price closes below flag low (${flagLowPrice:F2}). Flag retrace was {flagRetracePct:F0}%.",
            PredictionReason = $"Bull Flag breakout: {poleMovePercent:F1}% pole, {bestFlagTightness:F1}% tight flag, clean breakout. R:R {rrRatio:F1}:1.",
            InvalidationRule = $"Close below ${stopPrice:F2} (flag low) invalidates the setup.",
            WinningDirection = "bullish",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    // =======================================================================
    //  RANGE CONTRACTION BREAKOUT — NR7-style narrow range then expansion
    // =======================================================================

    /// <summary>
    /// Detect stocks with contracting daily ranges (narrowest range in N days)
    /// that are breaking out with volume expansion. Adapted from Opening Range
    /// Breakout for daily timeframe.
    /// </summary>
    public async Task<PredictionCandidate?> ScanForRangeBreakoutAsync(
        string ticker, MarketSnapshot snapshot, string runId)
    {
        if (snapshot.Quote is null || snapshot.RecentBars.Count < 10)
            return null;

        var price = snapshot.Quote.Price;
        if (price <= 0) return null;

        var bars = snapshot.RecentBars.OrderByDescending(b => b.Date).Take(15).ToList();
        if (bars.Count < 8) return null;

        var latestBar = bars[0];

        // ── CHECK 1: Range contraction — is the prior bar's range among the narrowest? ──
        // NR7: yesterday's range is the narrowest of the last 7 days
        var priorBars = bars.Skip(1).Take(7).ToList();
        if (priorBars.Count < 7) return null;

        var ranges = priorBars.Select(b => b.High - b.Low).ToList();
        var priorRange = ranges[0]; // the bar just before the breakout
        var isNr7 = priorRange <= ranges.Min() * 1.05; // within 5% of the minimum = NR7-ish

        // Also check NR4 (narrowest of 4)
        var isNr4 = priorRange <= ranges.Take(4).Min() * 1.05;

        if (!isNr7 && !isNr4)
        {
            _logger.LogDebug("[range-breakout] {Ticker}: SKIP — no range contraction", ticker);
            return null;
        }

        // ── CHECK 2: Breakout — today's bar breaks above the contraction range ──
        var contractionHigh = priorBars[0].High;
        var contractionLow = priorBars[0].Low;

        // Determine direction: bullish breakout (close above contraction high)
        var isBullish = latestBar.Close > contractionHigh;
        var isBearish = latestBar.Close < contractionLow;

        if (!isBullish && !isBearish)
        {
            _logger.LogDebug("[range-breakout] {Ticker}: SKIP — no breakout from contraction range", ticker);
            return null;
        }

        // Only trade bullish breakouts for now (aligns with existing system bias)
        if (!isBullish)
        {
            _logger.LogDebug("[range-breakout] {Ticker}: SKIP — bearish breakout (only trading bullish)", ticker);
            return null;
        }

        // ── CHECK 3: Volume expansion on breakout ──
        var avgVolume = priorBars.Average(b => b.Volume);
        var volumeRatio = latestBar.Volume / Math.Max(avgVolume, 1);
        if (volumeRatio < 1.0) // need at least average volume
        {
            _logger.LogDebug("[range-breakout] {Ticker}: SKIP — weak volume on breakout ({Ratio:F2}x avg)", ticker, volumeRatio);
            return null;
        }

        // ── CHECK 4: Uptrend context — breakout should be with the trend ──
        var ema21 = await _marketData.GetEma21Async(ticker);
        var emaData = await _marketData.GetEmaAsync(ticker);
        if (ema21 is null || emaData.Ema50 is null) return null;

        if (price < ema21.Value) // breakout below EMA21 = against trend
        {
            _logger.LogDebug("[range-breakout] {Ticker}: SKIP — breakout below EMA21 (against trend)", ticker);
            return null;
        }

        // ── COMPUTE TRADE PARAMETERS ──
        // Stop: below contraction low
        var stopPrice = Math.Round(contractionLow * 0.995, 2);

        // Target: measured move (contraction range × expansion factor, added to breakout)
        // Use the average range of the 3 widest bars before contraction as the expansion target
        var wideRanges = ranges.OrderByDescending(r => r).Take(3).Average();
        var targetPrice = Math.Round(contractionHigh + wideRanges * 2, 2);

        var risk = price - stopPrice;
        var reward = targetPrice - price;
        if (risk <= 0 || reward <= 0) return null;

        var rrRatio = Math.Round(reward / risk, 2);
        if (rrRatio < 2.0)
        {
            _logger.LogDebug("[range-breakout] {Ticker}: SKIP — R:R {RR:F2} < 2.0", ticker, rrRatio);
            return null;
        }

        // ── GRADE SETUP QUALITY ──
        var qualityScore = 50;

        // NR7 is stronger than NR4
        if (isNr7) qualityScore += 15;
        else if (isNr4) qualityScore += 8;

        // Volume expansion
        if (volumeRatio >= 2.0) qualityScore += 15;
        else if (volumeRatio >= 1.5) qualityScore += 10;
        else if (volumeRatio >= 1.2) qualityScore += 5;

        // Trend alignment (price above EMAs)
        if (price > ema21.Value && ema21.Value > emaData.Ema50.Value)
            qualityScore += 10; // strong uptrend context

        // Breakout candle quality — big body, closing near high
        var bodyRatio = Math.Abs(latestBar.Close - latestBar.Open) / Math.Max(latestBar.High - latestBar.Low, 0.01);
        if (bodyRatio > 0.7) qualityScore += 5; // strong body

        // R:R bonus
        if (rrRatio >= 3.0) qualityScore += 5;

        qualityScore = Math.Clamp(qualityScore, 20, 90);

        var nrType = isNr7 ? "NR7" : "NR4";
        var setupDetails = new
        {
            nr_type = nrType,
            contraction_range = Math.Round(priorRange, 2),
            contraction_high = contractionHigh,
            contraction_low = contractionLow,
            volume_ratio = Math.Round(volumeRatio, 2),
            breakout_close = latestBar.Close,
            ema21 = ema21.Value,
            ema50 = emaData.Ema50.Value,
            rr_ratio = rrRatio,
        };

        // ── AI GRADING ──
        var recentBars = bars.Take(5).ToList();
        var aiQuality = await GradeSetupWithAiAsync(
            "range_breakout", ticker, price, rrRatio, stopPrice, targetPrice,
            latestBar, recentBars, qualityScore,
            $"Range contraction: {nrType} (range ${priorRange:F2})\nBreakout close: ${latestBar.Close:F2} above ${contractionHigh:F2}\nVolume: {volumeRatio:F2}x avg\nEMA21: ${ema21.Value:F2}, EMA50: ${emaData.Ema50.Value:F2}",
            "RANGE CONTRACTION BREAKOUT: Narrowest range in 4-7 days followed by expansion breakout. " +
            "Grade: contraction quality (NR7>NR4), volume expansion, trend alignment, breakout candle strength.");

        if (aiQuality is not null)
            qualityScore = aiQuality.Value;

        _logger.LogInformation(
            "[range-breakout] {Ticker}: SETUP DETECTED — quality={Quality}, {NR}, R:R={RR:F1}, entry=${Price:F2}, stop=${Stop:F2}, target=${Target:F2}",
            ticker, qualityScore, nrType, rrRatio, price, stopPrice, targetPrice);

        return new PredictionCandidate
        {
            Id = Guid.NewGuid().ToString(),
            RunId = runId,
            Ticker = ticker,
            PredictionType = PredictionType.bullish,
            AssetType = PredictionAssetType.stock,
            TimeWindow = "swing",
            ConfidenceScore = qualityScore,
            ImportanceScore = qualityScore,
            RiskScore = Math.Clamp(100 - qualityScore, 10, 80),
            EntryReferencePrice = price,
            TargetPrice = targetPrice,
            StopPrice = stopPrice,
            RiskRewardRatio = rrRatio,
            PricePredictionMethod = "range_breakout",
            SetupType = "range_breakout",
            SetupDetailsJson = JsonSerializer.Serialize(setupDetails),
            BullishCase = $"Range Contraction Breakout ({nrType}): narrowest range then breakout above ${contractionHigh:F2} with {volumeRatio:F1}x volume. Trend-aligned (above EMA21).",
            BearishCase = $"Invalidation if price closes below contraction low (${contractionLow:F2}).",
            PredictionReason = $"Range Breakout ({nrType}): contraction then {volumeRatio:F1}x volume breakout in uptrend. R:R {rrRatio:F1}:1.",
            InvalidationRule = $"Close below ${stopPrice:F2} (contraction low) invalidates the setup.",
            WinningDirection = "bullish",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    // =======================================================================
    //  VOLUME SHELF BOUNCE — pullback to high-volume support zone
    // =======================================================================

    /// <summary>
    /// Build a simple volume profile from recent bars and detect stocks
    /// pulling back to a high-volume price shelf (support) and bouncing.
    /// </summary>
    public async Task<PredictionCandidate?> ScanForVolumeShelfBounceAsync(
        string ticker, MarketSnapshot snapshot, string runId)
    {
        if (snapshot.Quote is null || snapshot.RecentBars.Count < 15)
            return null;

        var price = snapshot.Quote.Price;
        if (price <= 0) return null;

        var bars = snapshot.RecentBars.OrderByDescending(b => b.Date).Take(20).ToList();
        if (bars.Count < 12) return null;

        // ── BUILD VOLUME PROFILE ──
        // Divide price range into bins, accumulate volume per bin
        var allHigh = bars.Max(b => b.High);
        var allLow = bars.Min(b => b.Low);
        var priceRange = allHigh - allLow;
        if (priceRange <= 0) return null;

        const int numBins = 20;
        var binSize = priceRange / numBins;
        var volumeProfile = new double[numBins];

        foreach (var bar in bars)
        {
            // Distribute bar's volume across bins it spans
            var barLowBin = Math.Max(0, (int)((bar.Low - allLow) / binSize));
            var barHighBin = Math.Min(numBins - 1, (int)((bar.High - allLow) / binSize));
            var binsSpanned = barHighBin - barLowBin + 1;
            var volPerBin = bar.Volume / Math.Max(binsSpanned, 1);

            for (int i = barLowBin; i <= barHighBin; i++)
                volumeProfile[i] += volPerBin;
        }

        // ── FIND HIGH-VOLUME SHELF (support zone) ──
        // The shelf is the bin (below current price) with the most volume
        var currentBin = Math.Min(numBins - 1, Math.Max(0, (int)((price - allLow) / binSize)));
        var bestShelfBin = -1;
        var bestShelfVol = 0.0;
        var avgBinVol = volumeProfile.Average();

        for (int i = 0; i < currentBin; i++) // only bins below current price
        {
            if (volumeProfile[i] > bestShelfVol)
            {
                bestShelfVol = volumeProfile[i];
                bestShelfBin = i;
            }
        }

        if (bestShelfBin < 0 || bestShelfVol < avgBinVol * 1.5) // shelf must be at least 1.5x avg
        {
            _logger.LogDebug("[volume-shelf] {Ticker}: SKIP — no significant volume shelf below price", ticker);
            return null;
        }

        var shelfPriceLow = allLow + bestShelfBin * binSize;
        var shelfPriceHigh = shelfPriceLow + binSize;
        var shelfMid = (shelfPriceLow + shelfPriceHigh) / 2;

        // ── CHECK 1: Price is near the shelf (within 2 bins above) ──
        var priceToShelfPct = (price - shelfPriceHigh) / shelfPriceHigh * 100;
        if (priceToShelfPct > 3.0) // too far above shelf
        {
            _logger.LogDebug("[volume-shelf] {Ticker}: SKIP — price {Pct:F1}% above shelf (too far)", ticker, priceToShelfPct);
            return null;
        }
        if (priceToShelfPct < -2.0) // broken below shelf
        {
            _logger.LogDebug("[volume-shelf] {Ticker}: SKIP — price broke below shelf", ticker);
            return null;
        }

        // ── CHECK 2: Recent pullback to shelf — at least one bar touched the shelf zone ──
        var recentBars = bars.Take(5).ToList();
        var touchBar = recentBars.FirstOrDefault(b => b.Low <= shelfPriceHigh && b.Low >= shelfPriceLow * 0.99);
        if (touchBar is null)
        {
            _logger.LogDebug("[volume-shelf] {Ticker}: SKIP — no recent bar touched volume shelf", ticker);
            return null;
        }

        // ── CHECK 3: Bounce — latest bar is green and closed above the shelf ──
        var latestBar = recentBars[0];
        if (latestBar.Close <= latestBar.Open)
        {
            _logger.LogDebug("[volume-shelf] {Ticker}: SKIP — latest bar is red (no bounce)", ticker);
            return null;
        }
        if (latestBar.Close < shelfPriceHigh)
        {
            _logger.LogDebug("[volume-shelf] {Ticker}: SKIP — close below shelf top", ticker);
            return null;
        }

        // ── COMPUTE TRADE PARAMETERS ──
        var stopPrice = Math.Round(shelfPriceLow * 0.995, 2); // below shelf
        var swingHigh = bars.Max(b => b.High);
        var targetPrice = Math.Round(swingHigh, 2);

        var risk = price - stopPrice;
        var reward = targetPrice - price;
        if (risk <= 0 || reward <= 0) return null;

        var rrRatio = Math.Round(reward / risk, 2);
        if (rrRatio < 2.0)
        {
            _logger.LogDebug("[volume-shelf] {Ticker}: SKIP — R:R {RR:F2} < 2.0", ticker, rrRatio);
            return null;
        }

        // ── GRADE SETUP QUALITY ──
        var qualityScore = 50;

        // Shelf strength: how much more volume than average?
        var shelfStrengthRatio = bestShelfVol / Math.Max(avgBinVol, 1);
        if (shelfStrengthRatio >= 3.0) qualityScore += 15;
        else if (shelfStrengthRatio >= 2.0) qualityScore += 10;
        else qualityScore += 5;

        // Bounce quality
        var bounceBodyPct = (latestBar.Close - latestBar.Open) / Math.Max(latestBar.Open, 1) * 100;
        if (bounceBodyPct > 1.5) qualityScore += 10;
        else if (bounceBodyPct > 0.5) qualityScore += 5;

        // Trend context
        var ema21 = await _marketData.GetEma21Async(ticker);
        var emaData = await _marketData.GetEmaAsync(ticker);
        if (ema21 is not null && emaData.Ema50 is not null && ema21.Value > emaData.Ema50.Value)
            qualityScore += 10; // uptrend context

        // R:R bonus
        if (rrRatio >= 3.0) qualityScore += 10;
        else if (rrRatio >= 2.5) qualityScore += 5;

        qualityScore = Math.Clamp(qualityScore, 20, 90);

        var setupDetails = new
        {
            shelf_price_low = Math.Round(shelfPriceLow, 2),
            shelf_price_high = Math.Round(shelfPriceHigh, 2),
            shelf_strength_ratio = Math.Round(shelfStrengthRatio, 2),
            touch_bar_date = touchBar.Date,
            bounce_body_pct = Math.Round(bounceBodyPct, 2),
            rr_ratio = rrRatio,
        };

        // ── AI GRADING ──
        var aiQuality = await GradeSetupWithAiAsync(
            "volume_shelf_bounce", ticker, price, rrRatio, stopPrice, targetPrice,
            latestBar, recentBars, qualityScore,
            $"Volume shelf: ${shelfPriceLow:F2}-${shelfPriceHigh:F2} ({shelfStrengthRatio:F1}x avg volume)\nBounce body: {bounceBodyPct:F1}%\nTouch bar: {touchBar.Date}",
            "VOLUME SHELF BOUNCE: Price pulled back to a high-volume support zone and bounced. " +
            "Grade: shelf strength (volume concentration), bounce candle quality, trend context, how cleanly price held the shelf.");

        if (aiQuality is not null)
            qualityScore = aiQuality.Value;

        _logger.LogInformation(
            "[volume-shelf] {Ticker}: SETUP DETECTED — quality={Quality}, R:R={RR:F1}, entry=${Price:F2}, stop=${Stop:F2}, target=${Target:F2}, shelf=${ShelfLow:F2}-${ShelfHigh:F2}",
            ticker, qualityScore, rrRatio, price, stopPrice, targetPrice, shelfPriceLow, shelfPriceHigh);

        return new PredictionCandidate
        {
            Id = Guid.NewGuid().ToString(),
            RunId = runId,
            Ticker = ticker,
            PredictionType = PredictionType.bullish,
            AssetType = PredictionAssetType.stock,
            TimeWindow = "swing",
            ConfidenceScore = qualityScore,
            ImportanceScore = qualityScore,
            RiskScore = Math.Clamp(100 - qualityScore, 10, 80),
            EntryReferencePrice = price,
            TargetPrice = targetPrice,
            StopPrice = stopPrice,
            RiskRewardRatio = rrRatio,
            PricePredictionMethod = "volume_shelf_bounce",
            SetupType = "volume_shelf_bounce",
            SetupDetailsJson = JsonSerializer.Serialize(setupDetails),
            BullishCase = $"Volume Shelf Bounce: price held ${shelfPriceLow:F2}-${shelfPriceHigh:F2} support ({shelfStrengthRatio:F1}x avg volume) and bounced {bounceBodyPct:F1}%.",
            BearishCase = $"Invalidation if price closes below volume shelf (${shelfPriceLow:F2}).",
            PredictionReason = $"Volume Shelf Bounce: high-volume support at ${shelfMid:F2}, clean bounce. R:R {rrRatio:F1}:1.",
            InvalidationRule = $"Close below ${stopPrice:F2} (below shelf) invalidates the setup.",
            WinningDirection = "bullish",
            Status = "open",
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    // =======================================================================
    //  GENERIC AI GRADING — shared by all pattern scanners
    // =======================================================================

    /// <summary>
    /// AI-grade any setup pattern. Each caller passes pattern-specific context
    /// and grading criteria. Returns blended score (40% mechanical, 60% AI)
    /// or null on failure (caller falls back to mechanical).
    /// </summary>
    private async Task<int?> GradeSetupWithAiAsync(
        string setupType, string ticker, double price, double rrRatio,
        double stopPrice, double targetPrice,
        MarketSnapshotBar latestBar, List<MarketSnapshotBar> recentBars,
        int mechanicalScore,
        string setupSpecificContext, string gradingCriteria)
    {
        if (!_ai.IsConfigured)
            return null;

        try
        {
            var barsBlock = string.Join("\n", recentBars.Select(b =>
                $"  {b.Date}: O={b.Open:F2} H={b.High:F2} L={b.Low:F2} C={b.Close:F2} V={b.Volume:N0}"));

            var prompt = $$"""
                You are a technical analyst grading a {{setupType}} setup for a real trading system.
                Grade the QUALITY of this setup on a 20-90 scale. This score becomes the confidence
                that determines position sizing and whether the trade gets taken.

                TICKER: {{ticker}}
                CURRENT PRICE: ${{price:F2}}
                R:R RATIO: {{rrRatio:F1}}:1
                ENTRY: ${{price:F2}} | STOP: ${{stopPrice:F2}} | TARGET: ${{targetPrice:F2}}

                SETUP-SPECIFIC DATA:
                {{setupSpecificContext}}

                LATEST BAR: {{latestBar.Date}} O={{latestBar.Open:F2}} H={{latestBar.High:F2}} L={{latestBar.Low:F2}} C={{latestBar.Close:F2}} V={{latestBar.Volume:N0}}

                RECENT BARS (newest first):
                {{barsBlock}}

                MECHANICAL SCORE: {{mechanicalScore}} (your score can differ)

                {{gradingCriteria}}

                Respond in JSON only: {"score": <int 20-90>, "reason": "<one sentence>"}
                """;

            var result = await _ai.CompleteAsync(new AiCompletionRequest
            {
                Messages = [new AiChatMessageDto { Role = "user", Content = prompt }],
                ResponseFormatJson = true,
                MaxOutputTokens = 150,
                ModelOverride = 0, // gpt-4.1-mini
            }, CancellationToken.None);

            if (string.IsNullOrWhiteSpace(result.Text))
                return null;

            using var doc = JsonDocument.Parse(result.Text);
            var aiScore = doc.RootElement.GetProperty("score").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var reasonEl)
                ? reasonEl.GetString() ?? "" : "";

            aiScore = Math.Clamp(aiScore, 20, 90);

            var blended = (int)Math.Round(mechanicalScore * 0.4 + aiScore * 0.6);
            blended = Math.Clamp(blended, 20, 90);

            _logger.LogInformation(
                "[{Setup}-ai] {Ticker}: mechanical={Mechanical}, ai={Ai}, blended={Blended} — {Reason}",
                setupType, ticker, mechanicalScore, aiScore, blended, reason);

            return blended;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Setup}-ai] AI grading failed for {Ticker}, using mechanical score {Score}",
                setupType, ticker, mechanicalScore);
            return null;
        }
    }

    // -----------------------------------------------------------------------
    // AI grading for EMA pullback setups (legacy — kept for backward compat)
    // -----------------------------------------------------------------------

    private async Task<int?> GradeEmaPullbackWithAiAsync(
        string ticker, double price, double ema21, double ema50,
        double emaSeparationPct, double pullbackDepthPct, double rrRatio,
        double stopPrice, double targetPrice,
        MarketSnapshotBar bounceCandle, List<MarketSnapshotBar> recentBars,
        int mechanicalScore)
    {
        if (!_ai.IsConfigured)
            return null;

        try
        {
            // Build a concise price action summary from recent bars
            var barsBlock = string.Join("\n", recentBars.Select(b =>
                $"  {b.Date}: O={b.Open:F2} H={b.High:F2} L={b.Low:F2} C={b.Close:F2} V={b.Volume:N0}"));

            var prompt = $$"""
                You are a technical analyst grading an EMA pullback setup for a real trading system.
                Grade the QUALITY of this setup on a 20-90 scale. This score becomes the confidence
                that determines position sizing and whether the trade gets taken.

                TICKER: {{ticker}}
                CURRENT PRICE: ${{price:F2}}
                EMA 21: ${{ema21:F2}} | EMA 50: ${{ema50:F2}}
                EMA SEPARATION: {{emaSeparationPct:F1}}% (wider = stronger trend)
                PULLBACK DEPTH: {{pullbackDepthPct:F1}}% from EMA21 (shallow = better)
                R:R RATIO: {{rrRatio:F1}}:1
                ENTRY: ${{price:F2}} | STOP: ${{stopPrice:F2}} | TARGET: ${{targetPrice:F2}}
                BOUNCE CANDLE: {{bounceCandle.Date}} O={{bounceCandle.Open:F2}} H={{bounceCandle.High:F2}} L={{bounceCandle.Low:F2}} C={{bounceCandle.Close:F2}} V={{bounceCandle.Volume:N0}}

                RECENT BARS (newest first):
                {{barsBlock}}

                MECHANICAL SCORE: {{mechanicalScore}} (your score can differ)

                GRADING CRITERIA:
                - TREND HEALTH (20pts): Is EMA21 > EMA50 with clean separation? Widening gap = strong. Narrowing = weakening.
                - PULLBACK QUALITY (20pts): Did price touch/barely pierce EMA21 then bounce cleanly?
                  V-shaped recovery = strong. Lingering near EMA = weak. Multiple EMA tests = weakening.
                - BOUNCE CONFIRMATION (20pts): Is the bounce candle strong? Big green body closing well above EMA21 = strong.
                  Small body/long upper wick = weak. Volume on bounce vs pullback?
                - RISK/REWARD (15pts): R:R >= 3 = great, 2.5 = good, 2.0 = acceptable.
                - CONTEXT (15pts): Is the stock making higher highs/lows? Clean trend or choppy?
                  Gap ups/downs that distort the setup?

                PENALTIES:
                - Price already extended above EMA21 (>1.5%) = chasing, deduct 10-15
                - Declining volume on bounce = no conviction, deduct 10
                - EMA separation narrowing = trend may be ending, deduct 10
                - Multiple failed bounces off EMA21 recently = support weakening, deduct 15

                Respond in JSON only: {"score": <int 20-90>, "reason": "<one sentence>"}
                """;

            var result = await _ai.CompleteAsync(new AiCompletionRequest
            {
                Messages = [new AiChatMessageDto { Role = "user", Content = prompt }],
                ResponseFormatJson = true,
                MaxOutputTokens = 150,
                ModelOverride = 0, // gpt-4.1-mini — fast and cheap for grading
            }, CancellationToken.None);

            if (string.IsNullOrWhiteSpace(result.Text))
                return null;

            using var doc = JsonDocument.Parse(result.Text);
            var aiScore = doc.RootElement.GetProperty("score").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var reasonEl)
                ? reasonEl.GetString() ?? "" : "";

            // Clamp to valid range
            aiScore = Math.Clamp(aiScore, 20, 90);

            // Blend: 40% mechanical, 60% AI — AI has final say but mechanical anchors
            var blended = (int)Math.Round(mechanicalScore * 0.4 + aiScore * 0.6);
            blended = Math.Clamp(blended, 20, 90);

            _logger.LogInformation(
                "[ema-pullback-ai] {Ticker}: mechanical={Mechanical}, ai={Ai}, blended={Blended} — {Reason}",
                ticker, mechanicalScore, aiScore, blended, reason);

            return blended;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ema-pullback-ai] AI grading failed for {Ticker}, using mechanical score {Score}",
                ticker, mechanicalScore);
            return null; // fall back to mechanical
        }
    }
}
