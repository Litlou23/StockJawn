using System.Text.Json.Serialization;

namespace StockResearchAgent.Api.Models;

/// <summary>
/// One active market regime with its confidence level.
/// Multiple regimes can be active simultaneously.
/// </summary>
public record MarketRegime
{
    public MarketRegimeType Type { get; init; }
    /// <summary>Confidence 0.0–1.0 that this regime is currently active.</summary>
    public double Confidence { get; init; }
    /// <summary>Human-readable reason this regime was detected.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Full output of <see cref="Services.MarketRegime.IMarketRegimeEngine"/>.
/// Contains all simultaneously active regimes and summary analytics.
/// </summary>
public record MarketRegimeResult
{
    /// <summary>All detected regimes, ordered by confidence descending.</summary>
    public List<MarketRegime> ActiveRegimes { get; init; } = [];
    /// <summary>The single highest-confidence regime (convenience accessor).</summary>
    public MarketRegimeType PrimaryRegime { get; init; } = MarketRegimeType.Unknown;
    public double PrimaryConfidence { get; init; }
    /// <summary>Deterministic summary (e.g. "Bull Trend (82%), Risk On (74%)").</summary>
    public string Summary { get; init; } = "";
    public DateTimeOffset ClassifiedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Market-wide context consumed by <see cref="Services.MarketRegime.IMarketRegimeEngine"/>.
/// Designed for future expansion — add fields without changing the interface.
/// All fields are nullable so partial data still produces a classification.
/// </summary>
public record MarketRegimeContext
{
    // ── Broad market trends ──────────────────────────────────────
    /// <summary>SPY price relative to its 50-day SMA (e.g. 1.03 = 3% above).</summary>
    public double? SpyTrendRatio { get; init; }
    /// <summary>SPY price relative to its 200-day SMA.</summary>
    public double? SpyLongTrendRatio { get; init; }
    /// <summary>QQQ price relative to its 50-day SMA.</summary>
    public double? QqqTrendRatio { get; init; }

    // ── Volatility ───────────────────────────────────────────────
    /// <summary>Current VIX level.</summary>
    public double? Vix { get; init; }
    /// <summary>VIX 20-day percentile rank (0–100).</summary>
    public double? VixPercentileRank { get; init; }
    /// <summary>Market-wide average ATR as % of price.</summary>
    public double? MarketVolatility { get; init; }

    // ── Breadth ──────────────────────────────────────────────────
    /// <summary>Percentage of S&P 500 stocks above their 50-day SMA (0–100).</summary>
    public double? BreadthAbove50Sma { get; init; }
    /// <summary>Percentage above 200-day SMA.</summary>
    public double? BreadthAbove200Sma { get; init; }
    /// <summary>NYSE advance/decline ratio (> 1.0 = more advancers).</summary>
    public double? AdvanceDeclineRatio { get; init; }

    // ── Volume ───────────────────────────────────────────────────
    /// <summary>Market-wide relative volume vs 20-day average.</summary>
    public double? RelativeVolume { get; init; }
    /// <summary>Up-volume / down-volume ratio.</summary>
    public double? UpDownVolumeRatio { get; init; }

    // ── Sector leadership ────────────────────────────────────────
    /// <summary>GICS sector with strongest relative strength (e.g. "Technology").</summary>
    public string? LeadingSector { get; init; }
    /// <summary>GICS sector with weakest relative strength.</summary>
    public string? LaggingSector { get; init; }
    /// <summary>Number of sectors in uptrend (> 50-day SMA).</summary>
    public int? SectorsInUptrend { get; init; }

    // ── Macro (placeholders for future data sources) ─────────────
    /// <summary>10-year Treasury yield.</summary>
    public double? TenYearYield { get; init; }
    /// <summary>Yield curve slope (10Y - 2Y).</summary>
    public double? YieldCurveSlope { get; init; }
    /// <summary>Dollar index (DXY).</summary>
    public double? DollarIndex { get; init; }

    // ── Momentum ─────────────────────────────────────────────────
    /// <summary>SPY 14-day RSI.</summary>
    public double? SpyRsi { get; init; }
    /// <summary>SPY 20-day rate of change (percent).</summary>
    public double? SpyRateOfChange { get; init; }
}
