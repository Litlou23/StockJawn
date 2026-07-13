using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.MarketRegime;

/// <summary>
/// Deterministic, rule-based market regime classifier.
///
/// Each regime detector is an independent method returning a
/// confidence (0.0–1.0).  Regimes above <see cref="MinConfidence"/>
/// are included in the result — multiple regimes can be active
/// simultaneously (e.g. Bull Trend + High Volatility + Risk On).
///
/// Thresholds are exposed as internal constants for easy tuning.
/// Future phases can replace this with ML-backed classification
/// by implementing the same <see cref="IMarketRegimeEngine"/> interface.
///
/// Stateless, no I/O, safe to register as singleton.
/// </summary>
public class MarketRegimeEngine : IMarketRegimeEngine
{
    private const double MinConfidence = 0.40;

    public MarketRegimeResult Classify(MarketRegimeContext ctx)
    {
        var detectors = new (MarketRegimeType type, Func<MarketRegimeContext, (double confidence, string reason)> detect)[]
        {
            (MarketRegimeType.BullTrend, DetectBullTrend),
            (MarketRegimeType.BearTrend, DetectBearTrend),
            (MarketRegimeType.Sideways, DetectSideways),
            (MarketRegimeType.HighVolatility, DetectHighVolatility),
            (MarketRegimeType.LowVolatility, DetectLowVolatility),
            (MarketRegimeType.RiskOn, DetectRiskOn),
            (MarketRegimeType.RiskOff, DetectRiskOff),
            (MarketRegimeType.MomentumMarket, DetectMomentum),
            (MarketRegimeType.MeanReversionMarket, DetectMeanReversion),
            (MarketRegimeType.Recovery, DetectRecovery),
            (MarketRegimeType.Distribution, DetectDistribution),
            (MarketRegimeType.Accumulation, DetectAccumulation),
            (MarketRegimeType.Expansion, DetectExpansion),
            (MarketRegimeType.Contraction, DetectContraction),
        };

        var active = new List<Models.MarketRegime>();

        foreach (var (type, detect) in detectors)
        {
            var (confidence, reason) = detect(ctx);
            if (confidence >= MinConfidence)
            {
                active.Add(new Models.MarketRegime
                {
                    Type = type,
                    Confidence = Math.Round(confidence, 4),
                    Reason = reason,
                });
            }
        }

        active = active.OrderByDescending(r => r.Confidence).ToList();

        var primary = active.FirstOrDefault();

        var summaryParts = active.Select(r => $"{r.Type} ({r.Confidence:P0})");
        var summary = active.Count > 0
            ? string.Join(", ", summaryParts)
            : "No regime detected with sufficient confidence.";

        return new MarketRegimeResult
        {
            ActiveRegimes = active,
            PrimaryRegime = primary?.Type ?? MarketRegimeType.Unknown,
            PrimaryConfidence = primary?.Confidence ?? 0.0,
            Summary = summary,
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Individual regime detectors
    // Each returns (confidence 0–1, reason string).
    // Partial data → proportionally lower confidence, never NaN.
    // ═══════════════════════════════════════════════════════════════

    private static (double, string) DetectBullTrend(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        if (ctx.SpyTrendRatio is not null) { signals++; if (ctx.SpyTrendRatio > 1.0) hits++; }
        if (ctx.SpyLongTrendRatio is not null) { signals++; if (ctx.SpyLongTrendRatio > 1.0) hits++; }
        if (ctx.QqqTrendRatio is not null) { signals++; if (ctx.QqqTrendRatio > 1.0) hits++; }
        if (ctx.BreadthAbove50Sma is not null) { signals++; if (ctx.BreadthAbove50Sma > 60) hits++; }
        if (ctx.AdvanceDeclineRatio is not null) { signals++; if (ctx.AdvanceDeclineRatio > 1.2) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"{hits}/{signals} trend signals bullish.");
    }

    private static (double, string) DetectBearTrend(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        if (ctx.SpyTrendRatio is not null) { signals++; if (ctx.SpyTrendRatio < 0.98) hits++; }
        if (ctx.SpyLongTrendRatio is not null) { signals++; if (ctx.SpyLongTrendRatio < 0.98) hits++; }
        if (ctx.QqqTrendRatio is not null) { signals++; if (ctx.QqqTrendRatio < 0.98) hits++; }
        if (ctx.BreadthAbove50Sma is not null) { signals++; if (ctx.BreadthAbove50Sma < 40) hits++; }
        if (ctx.AdvanceDeclineRatio is not null) { signals++; if (ctx.AdvanceDeclineRatio < 0.8) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"{hits}/{signals} trend signals bearish.");
    }

    private static (double, string) DetectSideways(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        if (ctx.SpyTrendRatio is not null)
        { signals++; if (ctx.SpyTrendRatio >= 0.98 && ctx.SpyTrendRatio <= 1.02) hits++; }
        if (ctx.SpyRateOfChange is not null)
        { signals++; if (Math.Abs(ctx.SpyRateOfChange.Value) < 3.0) hits++; }
        if (ctx.BreadthAbove50Sma is not null)
        { signals++; if (ctx.BreadthAbove50Sma >= 40 && ctx.BreadthAbove50Sma <= 60) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"Price range-bound — {hits}/{signals} signals flat.");
    }

    private static (double, string) DetectHighVolatility(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        if (ctx.Vix is not null) { signals++; if (ctx.Vix > 25) hits++; }
        if (ctx.VixPercentileRank is not null) { signals++; if (ctx.VixPercentileRank > 70) hits++; }
        if (ctx.MarketVolatility is not null) { signals++; if (ctx.MarketVolatility > 2.5) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"VIX {ctx.Vix?.ToString("F1") ?? "?"} — {hits}/{signals} volatility signals elevated.");
    }

    private static (double, string) DetectLowVolatility(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        if (ctx.Vix is not null) { signals++; if (ctx.Vix < 15) hits++; }
        if (ctx.VixPercentileRank is not null) { signals++; if (ctx.VixPercentileRank < 30) hits++; }
        if (ctx.MarketVolatility is not null) { signals++; if (ctx.MarketVolatility < 1.2) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"VIX {ctx.Vix?.ToString("F1") ?? "?"} — {hits}/{signals} volatility signals subdued.");
    }

    private static (double, string) DetectRiskOn(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        if (ctx.QqqTrendRatio is not null && ctx.SpyTrendRatio is not null)
        { signals++; if (ctx.QqqTrendRatio > ctx.SpyTrendRatio) hits++; } // Growth > Value
        if (ctx.Vix is not null) { signals++; if (ctx.Vix < 20) hits++; }
        if (ctx.UpDownVolumeRatio is not null) { signals++; if (ctx.UpDownVolumeRatio > 1.3) hits++; }
        if (ctx.SectorsInUptrend is not null) { signals++; if (ctx.SectorsInUptrend >= 8) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"Risk-on — {hits}/{signals} signals favor risk assets.");
    }

    private static (double, string) DetectRiskOff(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        if (ctx.Vix is not null) { signals++; if (ctx.Vix > 25) hits++; }
        if (ctx.UpDownVolumeRatio is not null) { signals++; if (ctx.UpDownVolumeRatio < 0.7) hits++; }
        if (ctx.SectorsInUptrend is not null) { signals++; if (ctx.SectorsInUptrend <= 3) hits++; }
        if (ctx.TenYearYield is not null) { signals++; if (ctx.TenYearYield < 3.0) hits++; } // flight to safety

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"Risk-off — {hits}/{signals} signals favor safety.");
    }

    private static (double, string) DetectMomentum(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        if (ctx.SpyRateOfChange is not null) { signals++; if (ctx.SpyRateOfChange > 5) hits++; }
        if (ctx.RelativeVolume is not null) { signals++; if (ctx.RelativeVolume > 1.2) hits++; }
        if (ctx.BreadthAbove50Sma is not null) { signals++; if (ctx.BreadthAbove50Sma > 70) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"Strong directional momentum — {hits}/{signals} signals.");
    }

    private static (double, string) DetectMeanReversion(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        if (ctx.SpyRsi is not null)
        { signals++; if (ctx.SpyRsi < 30 || ctx.SpyRsi > 70) hits++; }
        if (ctx.SpyRateOfChange is not null)
        { signals++; if (Math.Abs(ctx.SpyRateOfChange.Value) > 8) hits++; }
        if (ctx.VixPercentileRank is not null)
        { signals++; if (ctx.VixPercentileRank > 80) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"Extremes suggest reversion — {hits}/{signals} signals.");
    }

    private static (double, string) DetectRecovery(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        // Below 200 SMA but above 50 SMA = recovering from selloff
        if (ctx.SpyLongTrendRatio is not null && ctx.SpyTrendRatio is not null)
        { signals++; if (ctx.SpyLongTrendRatio < 1.0 && ctx.SpyTrendRatio > 1.0) hits++; }
        if (ctx.BreadthAbove50Sma is not null)
        { signals++; if (ctx.BreadthAbove50Sma > 50 && ctx.BreadthAbove50Sma < 70) hits++; }
        if (ctx.SpyRateOfChange is not null)
        { signals++; if (ctx.SpyRateOfChange > 3) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"Recovery pattern — {hits}/{signals} signals.");
    }

    private static (double, string) DetectDistribution(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        // Price near highs but breadth weakening
        if (ctx.SpyTrendRatio is not null) { signals++; if (ctx.SpyTrendRatio > 1.02) hits++; }
        if (ctx.BreadthAbove50Sma is not null)
        { signals++; if (ctx.BreadthAbove50Sma < 55) hits++; } // divergence
        if (ctx.UpDownVolumeRatio is not null) { signals++; if (ctx.UpDownVolumeRatio < 0.9) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"Distribution — price strong but internals weakening ({hits}/{signals}).");
    }

    private static (double, string) DetectAccumulation(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        // Price weak but breadth improving
        if (ctx.SpyTrendRatio is not null) { signals++; if (ctx.SpyTrendRatio < 1.0) hits++; }
        if (ctx.BreadthAbove50Sma is not null)
        { signals++; if (ctx.BreadthAbove50Sma > 45) hits++; }
        if (ctx.UpDownVolumeRatio is not null) { signals++; if (ctx.UpDownVolumeRatio > 1.1) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"Accumulation — price soft but internals improving ({hits}/{signals}).");
    }

    private static (double, string) DetectExpansion(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        if (ctx.BreadthAbove50Sma is not null) { signals++; if (ctx.BreadthAbove50Sma > 75) hits++; }
        if (ctx.SectorsInUptrend is not null) { signals++; if (ctx.SectorsInUptrend >= 9) hits++; }
        if (ctx.RelativeVolume is not null) { signals++; if (ctx.RelativeVolume > 1.1) hits++; }
        if (ctx.SpyRateOfChange is not null) { signals++; if (ctx.SpyRateOfChange > 4) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"Broad expansion — {hits}/{signals} signals.");
    }

    private static (double, string) DetectContraction(MarketRegimeContext ctx)
    {
        var signals = 0; var hits = 0;

        if (ctx.BreadthAbove50Sma is not null) { signals++; if (ctx.BreadthAbove50Sma < 30) hits++; }
        if (ctx.SectorsInUptrend is not null) { signals++; if (ctx.SectorsInUptrend <= 2) hits++; }
        if (ctx.AdvanceDeclineRatio is not null) { signals++; if (ctx.AdvanceDeclineRatio < 0.6) hits++; }

        if (signals == 0) return (0, "");
        var c = (double)hits / signals;
        return (c, $"Market contraction — {hits}/{signals} signals.");
    }
}
