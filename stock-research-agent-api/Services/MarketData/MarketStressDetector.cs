using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.MarketData;

/// <summary>
/// Detects market-wide stress conditions by checking VIX, SPY overnight move,
/// and oil price action. Returns a stress level that other systems use to:
///   1. Widen stop-loss thresholds (protect existing positions from volatility)
///   2. Raise confidence floor (only take high-conviction trades)
///   3. Enable bearish predictions (capitalize on downturns)
///
/// Stress levels:
///   Normal   — business as usual
///   Elevated — moderate stress, widen stops slightly, raise confidence floor
///   Extreme  — high stress, widen stops aggressively, favor bearish predictions
/// </summary>
public class MarketStressDetector
{
    private readonly MarketDataService _marketData;
    private readonly ResearchRepository _researchRepo;
    private readonly ILogger<MarketStressDetector> _logger;

    // Cached result — refreshes at most once per 15 minutes
    private MarketStressResult? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _computeLock = new(1, 1);

    public MarketStressDetector(
        MarketDataService marketData,
        ResearchRepository researchRepo,
        ILogger<MarketStressDetector> logger)
    {
        _marketData = marketData;
        _researchRepo = researchRepo;
        _logger = logger;
    }

    /// <summary>
    /// Evaluate current market stress. Results are cached for 15 minutes.
    /// </summary>
    public async Task<MarketStressResult> EvaluateAsync()
    {
        // Fast path — return cached result without locking
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            return _cached;

        // Serialize fresh computations so concurrent callers (e.g. 400 tickers
        // in a morning scan) don't all fire 3 quote fetches simultaneously
        await _computeLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock — another thread may have refreshed
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            var result = await ComputeStressAsync();
            _cached = result;
            _cachedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "[market-stress] Level={Level}, VIX={Vix}, SPY change={SpyChange:F2}%, Oil change={OilChange:F2}%, " +
                "Score={Score}, StopMultiplier={StopMult:F2}, ConfidenceFloor={ConfFloor}, BearishBias={BearBias:F2}",
                result.Level, result.Vix, result.SpyChangePercent, result.OilChangePercent,
                result.StressScore, result.StopLossMultiplier, result.ConfidenceFloor, result.BearishBias);

            return result;
        }
        finally
        {
            _computeLock.Release();
        }
    }

    /// <summary>Force a fresh evaluation, ignoring the cache.</summary>
    public async Task<MarketStressResult> EvaluateFreshAsync()
    {
        await _computeLock.WaitAsync();
        try
        {
            _cached = null;
            _cachedAt = DateTimeOffset.MinValue;
        }
        finally
        {
            _computeLock.Release();
        }
        return await EvaluateAsync();
    }

    private async Task<MarketStressResult> ComputeStressAsync()
    {
        double? vix = null;
        double? spyChange = null;
        double? oilChange = null;

        // Load configurable thresholds from scoring_weight_overrides
        var overrides = await _researchRepo.GetActiveWeightOverridesAsync();
        var weights = overrides.ToDictionary(o => o.SignalName, o => o.EffectiveWeight);

        var vixElevated = weights.GetValueOrDefault("stress_vix_elevated", 20.0);
        var vixExtreme = weights.GetValueOrDefault("stress_vix_extreme", 30.0);
        var spyDropElevated = weights.GetValueOrDefault("stress_spy_drop_elevated", -1.0);
        var spyDropExtreme = weights.GetValueOrDefault("stress_spy_drop_extreme", -2.0);
        var oilSpikeElevated = weights.GetValueOrDefault("stress_oil_spike_elevated", 3.0);
        var oilSpikeExtreme = weights.GetValueOrDefault("stress_oil_spike_extreme", 5.0);

        // ── Fetch VIX ──
        try
        {
            var vixQuote = await _marketData.GetQuoteAsync("VIX");
            vix = vixQuote?.Price;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[market-stress] Failed to fetch VIX");
        }

        // ── Fetch SPY change (percent_change from quote gives intraday/overnight move) ──
        try
        {
            var spyQuote = await _marketData.GetQuoteAsync("SPY");
            if (spyQuote is not null)
                spyChange = spyQuote.ChangePercent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[market-stress] Failed to fetch SPY");
        }

        // ── Fetch Oil (USO ETF as proxy for crude) ──
        try
        {
            var oilQuote = await _marketData.GetQuoteAsync("USO");
            if (oilQuote is not null)
                oilChange = oilQuote.ChangePercent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[market-stress] Failed to fetch USO");
        }

        // ── Score each indicator (0 = normal, 1 = elevated, 2 = extreme) ──
        var vixScore = vix switch
        {
            >= var v when v >= vixExtreme => 2,
            >= var v when v >= vixElevated => 1,
            _ => 0
        };

        var spyScore = spyChange switch
        {
            <= var s when s <= spyDropExtreme => 2,
            <= var s when s <= spyDropElevated => 1,
            _ => 0
        };

        var oilScore = oilChange switch
        {
            >= var o when o >= oilSpikeExtreme => 2,
            >= var o when o >= oilSpikeElevated => 1,
            _ => 0
        };

        // Combined stress score: max of individual scores, but bump to extreme if 2+ are elevated
        var scores = new[] { vixScore, spyScore, oilScore };
        var maxScore = scores.Max();
        var elevatedCount = scores.Count(s => s >= 1);
        var totalScore = elevatedCount >= 2 && maxScore < 2 ? 2 : maxScore;

        var level = totalScore switch
        {
            >= 2 => MarketStressLevel.Extreme,
            1 => MarketStressLevel.Elevated,
            _ => MarketStressLevel.Normal
        };

        // ── Compute adjustments based on stress level ──
        // Stop-loss multiplier: widens stops so volatility doesn't trigger premature exits
        // At extreme stress, stops widen by 1.5x (e.g. 9.5% → 14.25%)
        var stopMultiplier = level switch
        {
            MarketStressLevel.Extreme => weights.GetValueOrDefault("stress_stop_mult_extreme", 1.5),
            MarketStressLevel.Elevated => weights.GetValueOrDefault("stress_stop_mult_elevated", 1.25),
            _ => 1.0
        };

        // Confidence floor: minimum confidence to generate a prediction
        // Normal = use whatever the system has, Elevated = 55, Extreme = 65
        var confidenceFloor = (int)(level switch
        {
            MarketStressLevel.Extreme => weights.GetValueOrDefault("stress_conf_floor_extreme", 65),
            MarketStressLevel.Elevated => weights.GetValueOrDefault("stress_conf_floor_elevated", 55),
            _ => 0
        });

        // Bearish bias: additive boost to bearish scores during stress
        // This helps the scoring engine favor bearish predictions when the market is under pressure
        var bearishBias = level switch
        {
            MarketStressLevel.Extreme => weights.GetValueOrDefault("stress_bearish_bias_extreme", 8.0),
            MarketStressLevel.Elevated => weights.GetValueOrDefault("stress_bearish_bias_elevated", 4.0),
            _ => 0.0
        };

        var reasons = new List<string>();
        if (vixScore > 0) reasons.Add($"VIX at {vix:F1} ({(vixScore == 2 ? "extreme" : "elevated")})");
        if (spyScore > 0) reasons.Add($"SPY {spyChange:F2}% ({(spyScore == 2 ? "extreme" : "elevated")})");
        if (oilScore > 0) reasons.Add($"Oil +{oilChange:F2}% ({(oilScore == 2 ? "extreme" : "elevated")})");

        return new MarketStressResult
        {
            Level = level,
            StressScore = totalScore,
            Vix = vix,
            SpyChangePercent = spyChange,
            OilChangePercent = oilChange,
            VixScore = vixScore,
            SpyScore = spyScore,
            OilScore = oilScore,
            StopLossMultiplier = stopMultiplier,
            ConfidenceFloor = confidenceFloor,
            BearishBias = bearishBias,
            Reasons = reasons,
            EvaluatedAt = DateTimeOffset.UtcNow,
        };
    }
}

// ── Models ──────────────────────────────────────────────────────

public enum MarketStressLevel
{
    Normal,
    Elevated,
    Extreme
}

public record MarketStressResult
{
    public MarketStressLevel Level { get; init; } = MarketStressLevel.Normal;
    public int StressScore { get; init; }

    // Raw data
    public double? Vix { get; init; }
    public double? SpyChangePercent { get; init; }
    public double? OilChangePercent { get; init; }
    public int VixScore { get; init; }
    public int SpyScore { get; init; }
    public int OilScore { get; init; }

    // Adjustments for downstream systems
    /// <summary>Multiplier for stop-loss thresholds (1.0 = no change, 1.5 = 50% wider)</summary>
    public double StopLossMultiplier { get; init; } = 1.0;
    /// <summary>Minimum confidence score to generate a prediction (0 = no floor)</summary>
    public int ConfidenceFloor { get; init; }
    /// <summary>Additive bias to bearish scores in the scoring engine</summary>
    public double BearishBias { get; init; }

    public List<string> Reasons { get; init; } = [];
    public DateTimeOffset EvaluatedAt { get; init; }

    public bool IsStressed => Level != MarketStressLevel.Normal;
}
