using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.MarketIntelligence;

public class MarketFeatureService : IMarketFeatureService
{
    public List<MarketFeature> DeriveFeatures(string ticker, List<MarketFact> facts)
    {
        var now = DateTimeOffset.UtcNow;
        var features = new List<MarketFeature>();
        var factMap = facts.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        bool smaAligned = GetBool(factMap, "sma5_above_sma20");
        bool closeAboveSma = GetBool(factMap, "close_above_sma20");
        double slope = GetNumber(factMap, "linear_regression_slope");
        double roc5 = GetNumber(factMap, "roc5");
        double roc10 = GetNumber(factMap, "roc10");
        double rsi = GetNumber(factMap, "rsi14");
        double volumeRatio = GetNumber(factMap, "volume_ratio");
        double closeLocation = GetNumber(factMap, "close_location_value");
        double relSpy = GetNumber(factMap, "relative_strength_vs_spy");
        double relQqq = GetNumber(factMap, "relative_strength_vs_qqq");
        double atr14 = GetNumber(factMap, "atr14");
        double currentPrice = GetNumber(factMap, "current_price");
        double atrPercent = currentPrice > 0 ? (atr14 / currentPrice) * 100.0 : 0.0;
        double earningsDays = GetNumber(factMap, "earnings_days_until", fallback: -1);
        double netSignalStrength = GetNumber(factMap, "net_research_signal_strength");
        double avgSignalConfidence = GetNumber(factMap, "average_research_signal_confidence");
        double institutionalSignals = GetNumber(factMap, "institutional_signal_count");
        double insiderSignals = GetNumber(factMap, "insider_signal_count");
        double indicatorCount = GetNumber(factMap, "indicator_count");
        double indicatorSkipCount = GetNumber(factMap, "indicator_skip_count");

        if (smaAligned && closeAboveSma && slope > 0.02)
        {
            features.Add(Feature(ticker, "strong_uptrend", "Strong Uptrend",
                "Short-term trend structure is aligned with price holding above the medium trend baseline.",
                MarketFeaturePolarity.bullish, StrengthFromConfidence(0.82), 0.82, now,
                "sma5_above_sma20", "close_above_sma20", "linear_regression_slope"));
        }
        else if (!smaAligned && !closeAboveSma && slope < -0.02)
        {
            features.Add(Feature(ticker, "strong_downtrend", "Strong Downtrend",
                "Trend structure is negatively aligned with price trading below the medium trend baseline.",
                MarketFeaturePolarity.bearish, StrengthFromConfidence(0.82), 0.82, now,
                "sma5_above_sma20", "close_above_sma20", "linear_regression_slope"));
        }
        else if (Math.Abs(slope) < 0.02)
        {
            features.Add(Feature(ticker, "weak_trend", "Weak Trend",
                "Slope is flat enough that trend conviction is limited.",
                MarketFeaturePolarity.neutral, StrengthFromConfidence(0.64), 0.64, now,
                "linear_regression_slope"));
        }

        if (roc5 > 0 && roc10 > 0 && roc5 >= roc10 && rsi >= 55)
        {
            features.Add(Feature(ticker, "momentum_accelerating_bullish", "Momentum Accelerating",
                "Near-term momentum is positive and at least as strong as the longer short-term lookback.",
                MarketFeaturePolarity.bullish, StrengthFromConfidence(0.78), 0.78, now,
                "roc5", "roc10", "rsi14"));
        }
        else if (roc5 < 0 && roc10 < 0 && roc5 <= roc10 && rsi <= 45)
        {
            features.Add(Feature(ticker, "momentum_accelerating_bearish", "Momentum Deteriorating",
                "Near-term momentum is weakening faster than the longer short-term lookback.",
                MarketFeaturePolarity.bearish, StrengthFromConfidence(0.78), 0.78, now,
                "roc5", "roc10", "rsi14"));
        }

        if (volumeRatio >= 1.5)
        {
            features.Add(Feature(ticker, "high_relative_volume", "High Relative Volume",
                "Current volume is significantly above its recent baseline.",
                MarketFeaturePolarity.informational, StrengthFromConfidence(0.76), 0.76, now,
                "volume_ratio"));
        }

        if (GetBool(factMap, "donchian_breakout") || GetBool(factMap, "bollinger_breakout"))
        {
            features.Add(Feature(ticker, "resistance_break", "Resistance Break",
                "Price is pressing through a recent structural ceiling.",
                MarketFeaturePolarity.bullish, StrengthFromConfidence(0.8), 0.8, now,
                "donchian_breakout", "bollinger_breakout"));
        }

        if (GetBool(factMap, "donchian_breakdown"))
        {
            features.Add(Feature(ticker, "support_break", "Support Break",
                "Price is breaking below a recent structural floor.",
                MarketFeaturePolarity.bearish, StrengthFromConfidence(0.8), 0.8, now,
                "donchian_breakdown"));
        }

        if (closeLocation >= 65 && closeAboveSma)
        {
            features.Add(Feature(ticker, "support_holding", "Support Holding",
                "Price is closing in the upper end of its recent range while remaining above trend support.",
                MarketFeaturePolarity.bullish, StrengthFromConfidence(0.7), 0.7, now,
                "close_location_value", "close_above_sma20"));
        }

        if (relSpy >= 1.0 || relQqq >= 1.0)
        {
            features.Add(Feature(ticker, "sector_leadership", "Sector Leadership",
                "The ticker is outperforming broad benchmarks on a relative basis.",
                MarketFeaturePolarity.bullish, StrengthFromConfidence(0.72), 0.72, now,
                "relative_strength_vs_spy", "relative_strength_vs_qqq"));
        }
        else if (relSpy <= -1.0 || relQqq <= -1.0)
        {
            features.Add(Feature(ticker, "sector_lagging", "Sector Lagging",
                "The ticker is underperforming broad benchmarks on a relative basis.",
                MarketFeaturePolarity.bearish, StrengthFromConfidence(0.72), 0.72, now,
                "relative_strength_vs_spy", "relative_strength_vs_qqq"));
        }

        if (atrPercent >= 4.0 || GetNumber(factMap, "bollinger_bandwidth") >= 10.0)
        {
            features.Add(Feature(ticker, "high_volatility", "High Volatility",
                "The setup is operating in a wider-than-normal movement regime.",
                MarketFeaturePolarity.risk, StrengthFromConfidence(0.74), 0.74, now,
                "atr14", "current_price", "bollinger_bandwidth"));
        }

        if (earningsDays >= 0 && earningsDays <= 7)
        {
            features.Add(Feature(ticker, "event_risk", "Event Risk",
                "An earnings event is close enough to dominate the technical setup.",
                MarketFeaturePolarity.risk, StrengthFromConfidence(0.88), 0.88, now,
                "earnings_days_until"));
        }

        if (institutionalSignals > 0 || (netSignalStrength > 0 && avgSignalConfidence >= 0.6 && insiderSignals > 0))
        {
            features.Add(Feature(ticker, "institutional_buying", "Institutional Buying",
                "External signals suggest accumulation from informed or size-sensitive participants.",
                MarketFeaturePolarity.bullish, StrengthFromConfidence(0.75), 0.75, now,
                "institutional_signal_count", "insider_signal_count", "net_research_signal_strength", "average_research_signal_confidence"));
        }
        else if (netSignalStrength < 0 && avgSignalConfidence >= 0.6 && (institutionalSignals > 0 || insiderSignals > 0))
        {
            features.Add(Feature(ticker, "institutional_selling", "Institutional Selling",
                "External signals suggest distribution or bearish informed flow.",
                MarketFeaturePolarity.bearish, StrengthFromConfidence(0.75), 0.75, now,
                "institutional_signal_count", "insider_signal_count", "net_research_signal_strength", "average_research_signal_confidence"));
        }

        if (indicatorCount > 0 && indicatorSkipCount <= indicatorCount)
        {
            features.Add(Feature(ticker, "adequate_signal_coverage", "Adequate Signal Coverage",
                "The current snapshot has enough computed indicators to support downstream interpretation.",
                MarketFeaturePolarity.informational, StrengthFromConfidence(0.68), 0.68, now,
                "indicator_count", "indicator_skip_count"));
        }

        return features;
    }

    private static MarketFeature Feature(
        string ticker,
        string id,
        string name,
        string description,
        MarketFeaturePolarity polarity,
        FeatureStrength strength,
        double confidence,
        DateTimeOffset derivedAt,
        params string[] factIds) =>
        new()
        {
            FeatureId = id,
            Ticker = ticker,
            Name = name,
            Description = description,
            Polarity = polarity,
            Strength = strength,
            Confidence = confidence,
            FactIds = factIds.ToList(),
            SourceComponents = ["MarketFeatureService"],
            DerivedAt = derivedAt,
        };

    private static FeatureStrength StrengthFromConfidence(double confidence) =>
        confidence switch
        {
            >= 0.85 => FeatureStrength.strong,
            >= 0.65 => FeatureStrength.moderate,
            _ => FeatureStrength.weak,
        };

    private static double GetNumber(
        Dictionary<string, MarketFact> factMap,
        string name,
        double fallback = 0.0) =>
        factMap.TryGetValue(name, out var fact) && fact.Value.NumericValue is double value ? value : fallback;

    private static bool GetBool(Dictionary<string, MarketFact> factMap, string name) =>
        factMap.TryGetValue(name, out var fact) && fact.Value.BooleanValue == true;
}
