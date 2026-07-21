using System.Globalization;
using System.Text.RegularExpressions;
using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.MarketIntelligence;

public class MarketFactService : IMarketFactService
{
    private static readonly Regex EarningsDaysRegex = new(@"Earnings in (?<days>\d+)d", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public List<MarketFact> BuildFacts(
        string ticker,
        MarketSnapshot snapshot,
        TechnicalIndicators indicators,
        BenchmarkContext benchmark,
        List<ResearchSignal> researchSignals)
    {
        var observedAt = snapshot.CreatedAt == default ? DateTimeOffset.UtcNow : snapshot.CreatedAt;
        var facts = new List<MarketFact>();

        if (snapshot.Quote is not null)
        {
            var quote = snapshot.Quote;
            facts.Add(NumberFact(ticker, "current_price", "Current Price", FactCategory.price, FactSource.market_snapshot, quote.Price, "usd", observedAt, "MarketSnapshot.Quote.Price"));
            facts.Add(NumberFact(ticker, "daily_change_percent", "Daily Change %", FactCategory.price, FactSource.market_snapshot, quote.ChangePercent, "percent", observedAt, "MarketSnapshot.Quote.ChangePercent"));
            facts.Add(NumberFact(ticker, "daily_volume", "Daily Volume", FactCategory.volume, FactSource.market_snapshot, quote.Volume, "shares", observedAt, "MarketSnapshot.Quote.Volume"));
            facts.Add(NumberFact(ticker, "intraday_range_percent", "Intraday Range %", FactCategory.volatility, FactSource.market_snapshot,
                quote.Price > 0 ? ((quote.High - quote.Low) / quote.Price) * 100.0 : 0.0, "percent", observedAt, "MarketSnapshot.Quote.High", "MarketSnapshot.Quote.Low"));

            if (quote.PreviousClose > 0)
            {
                facts.Add(NumberFact(ticker, "gap_percent", "Gap %", FactCategory.price, FactSource.market_snapshot,
                    ((quote.Open - quote.PreviousClose) / quote.PreviousClose) * 100.0, "percent", observedAt, "MarketSnapshot.Quote.Open", "MarketSnapshot.Quote.PreviousClose"));
            }
        }

        facts.Add(NumberFact(ticker, "bars_available", "Bars Available", FactCategory.data_quality, FactSource.technical_indicator, indicators.BarsAvailable, "count", observedAt, "IndicatorEngine.Compute"));
        facts.Add(NumberFact(ticker, "indicator_count", "Indicators Computed", FactCategory.data_quality, FactSource.technical_indicator, indicators.IndicatorsComputed.Count, "count", observedAt, "IndicatorEngine.Compute"));
        facts.Add(NumberFact(ticker, "indicator_skip_count", "Indicators Skipped", FactCategory.data_quality, FactSource.technical_indicator, indicators.IndicatorsSkipped.Count, "count", observedAt, "IndicatorEngine.Compute"));

        AddOptionalNumberFact(facts, ticker, "atr14", "ATR 14", FactCategory.volatility, FactSource.technical_indicator, indicators.Atr14, "usd", observedAt, "TechnicalIndicators.Atr14");
        AddOptionalNumberFact(facts, ticker, "rsi14", "RSI 14", FactCategory.momentum, FactSource.technical_indicator, indicators.Rsi14, "index", observedAt, "TechnicalIndicators.Rsi14");
        AddOptionalNumberFact(facts, ticker, "roc5", "ROC 5", FactCategory.momentum, FactSource.technical_indicator, indicators.Roc5, "percent", observedAt, "TechnicalIndicators.Roc5");
        AddOptionalNumberFact(facts, ticker, "roc10", "ROC 10", FactCategory.momentum, FactSource.technical_indicator, indicators.Roc10, "percent", observedAt, "TechnicalIndicators.Roc10");
        AddOptionalNumberFact(facts, ticker, "volume_ratio", "Volume Ratio", FactCategory.volume, FactSource.technical_indicator, indicators.VolumeRatio, "ratio", observedAt, "TechnicalIndicators.VolumeRatio");
        AddOptionalNumberFact(facts, ticker, "bollinger_bandwidth", "Bollinger Bandwidth", FactCategory.volatility, FactSource.technical_indicator, indicators.BollingerBandwidth, "percent", observedAt, "TechnicalIndicators.BollingerBandwidth");
        AddOptionalNumberFact(facts, ticker, "close_location_value", "Close Location Value", FactCategory.market_structure, FactSource.technical_indicator, indicators.CloseLocationValue, "percent", observedAt, "TechnicalIndicators.CloseLocationValue");
        AddOptionalNumberFact(facts, ticker, "linear_regression_slope", "Linear Regression Slope", FactCategory.trend, FactSource.technical_indicator, indicators.LinearRegressionSlope, "slope", observedAt, "TechnicalIndicators.LinearRegressionSlope");

        facts.Add(BooleanFact(ticker, "sma5_above_sma20", "SMA 5 Above SMA 20", FactCategory.trend, FactSource.technical_indicator, indicators.Sma5AboveSma20, observedAt, "TechnicalIndicators.Sma5AboveSma20"));
        facts.Add(BooleanFact(ticker, "close_above_sma20", "Close Above SMA 20", FactCategory.trend, FactSource.technical_indicator, indicators.CloseAboveSma20, observedAt, "TechnicalIndicators.CloseAboveSma20"));

        AddOptionalBooleanFact(facts, ticker, "donchian_breakout", "Donchian Breakout", FactCategory.market_structure, indicators.DonchianBreakout, observedAt, "TechnicalIndicators.DonchianBreakout");
        AddOptionalBooleanFact(facts, ticker, "donchian_breakdown", "Donchian Breakdown", FactCategory.market_structure, indicators.DonchianBreakdown, observedAt, "TechnicalIndicators.DonchianBreakdown");
        AddOptionalBooleanFact(facts, ticker, "bollinger_breakout", "Bollinger Breakout", FactCategory.market_structure, indicators.BollingerBreakout, observedAt, "TechnicalIndicators.BollingerBreakout");
        AddOptionalBooleanFact(facts, ticker, "price_volume_confirmation", "Price Volume Confirmation", FactCategory.volume, indicators.PriceVolumeConfirmation, observedAt, "TechnicalIndicators.PriceVolumeConfirmation");

        // MACD (API-sourced)
        AddOptionalNumberFact(facts, ticker, "macd_line", "MACD Line", FactCategory.momentum, FactSource.technical_indicator, indicators.MacdLine, "value", observedAt, "TechnicalIndicators.MacdLine");
        AddOptionalNumberFact(facts, ticker, "macd_signal", "MACD Signal", FactCategory.momentum, FactSource.technical_indicator, indicators.MacdSignal, "value", observedAt, "TechnicalIndicators.MacdSignal");
        AddOptionalNumberFact(facts, ticker, "macd_histogram", "MACD Histogram", FactCategory.momentum, FactSource.technical_indicator, indicators.MacdHistogram, "value", observedAt, "TechnicalIndicators.MacdHistogram");
        AddOptionalBooleanFact(facts, ticker, "macd_bullish_crossover", "MACD Bullish Crossover", FactCategory.momentum, indicators.MacdBullishCrossover, observedAt, "TechnicalIndicators.MacdBullishCrossover");

        // EMA (API-sourced)
        AddOptionalNumberFact(facts, ticker, "ema12", "EMA 12", FactCategory.trend, FactSource.technical_indicator, indicators.Ema12, "usd", observedAt, "TechnicalIndicators.Ema12");
        AddOptionalNumberFact(facts, ticker, "ema26", "EMA 26", FactCategory.trend, FactSource.technical_indicator, indicators.Ema26, "usd", observedAt, "TechnicalIndicators.Ema26");
        AddOptionalNumberFact(facts, ticker, "ema50", "EMA 50", FactCategory.trend, FactSource.technical_indicator, indicators.Ema50, "usd", observedAt, "TechnicalIndicators.Ema50");

        AddOptionalNumberFact(facts, ticker, "relative_strength_vs_spy", "Relative Strength vs SPY", FactCategory.benchmark, FactSource.benchmark_context, benchmark.RelativeStrengthVsSpy, "percent", observedAt, "BenchmarkContext.RelativeStrengthVsSpy");
        AddOptionalNumberFact(facts, ticker, "relative_strength_vs_qqq", "Relative Strength vs QQQ", FactCategory.benchmark, FactSource.benchmark_context, benchmark.RelativeStrengthVsQqq, "percent", observedAt, "BenchmarkContext.RelativeStrengthVsQqq");
        AddOptionalNumberFact(facts, ticker, "spy_change_percent", "SPY Change %", FactCategory.benchmark, FactSource.benchmark_context, benchmark.SpyChangePercent, "percent", observedAt, "BenchmarkContext.SpyChangePercent");
        AddOptionalNumberFact(facts, ticker, "qqq_change_percent", "QQQ Change %", FactCategory.benchmark, FactSource.benchmark_context, benchmark.QqqChangePercent, "percent", observedAt, "BenchmarkContext.QqqChangePercent");

        facts.Add(NumberFact(ticker, "news_item_count", "News Item Count", FactCategory.catalyst, FactSource.news_context, snapshot.NewsContext.Count, "count", observedAt, "MarketSnapshot.NewsContext"));
        if (snapshot.NewsContext.Count > 0)
        {
            facts.Add(NumberFact(ticker, "average_news_importance", "Average News Importance", FactCategory.catalyst, FactSource.news_context,
                snapshot.NewsContext.Average(n => n.ImportanceScore), "score", observedAt, "MarketSnapshot.NewsContext.ImportanceScore"));
        }

        var earningsDays = TryExtractNearestEarningsDays(snapshot.NewsContext);
        if (earningsDays is not null)
        {
            facts.Add(NumberFact(ticker, "earnings_days_until", "Earnings Days Until", FactCategory.event_risk, FactSource.news_context, earningsDays.Value, "days", observedAt, "MarketSnapshot.NewsContext.Title"));
        }

        facts.Add(NumberFact(ticker, "active_research_signal_count", "Active Research Signal Count", FactCategory.research_signal, FactSource.research_signal,
            researchSignals.Count(s => s.Active), "count", observedAt, "ResearchSignalService"));
        if (researchSignals.Count > 0)
        {
            facts.Add(NumberFact(ticker, "net_research_signal_strength", "Net Research Signal Strength", FactCategory.research_signal, FactSource.research_signal,
                researchSignals.Sum(s => s.Strength), "score", observedAt, "ResearchSignal.Strength"));
            facts.Add(NumberFact(ticker, "average_research_signal_confidence", "Average Research Signal Confidence", FactCategory.research_signal, FactSource.research_signal,
                researchSignals.Average(s => s.Confidence), "ratio", observedAt, "ResearchSignal.Confidence"));
            facts.Add(NumberFact(ticker, "institutional_signal_count", "Institutional Signal Count", FactCategory.ownership, FactSource.research_signal,
                researchSignals.Count(IsInstitutionalSignal), "count", observedAt, "ResearchSignal.SignalCategory", "ResearchSignal.Provider"));
            facts.Add(NumberFact(ticker, "insider_signal_count", "Insider Signal Count", FactCategory.ownership, FactSource.research_signal,
                researchSignals.Count(IsInsiderSignal), "count", observedAt, "ResearchSignal.SignalType", "ResearchSignal.Provider"));
        }

        return facts;
    }

    private static MarketFact NumberFact(
        string ticker,
        string name,
        string displayName,
        FactCategory category,
        FactSource source,
        double value,
        string unit,
        DateTimeOffset observedAt,
        params string[] sourceComponents) =>
        new()
        {
            FactId = name,
            Ticker = ticker,
            Name = name,
            DisplayName = displayName,
            Category = category,
            Source = source,
            Value = FactValue.Number(Math.Round(value, 4), unit),
            ObservedAt = observedAt,
            SourceComponents = sourceComponents.ToList(),
        };

    private static MarketFact BooleanFact(
        string ticker,
        string name,
        string displayName,
        FactCategory category,
        FactSource source,
        bool value,
        DateTimeOffset observedAt,
        params string[] sourceComponents) =>
        new()
        {
            FactId = name,
            Ticker = ticker,
            Name = name,
            DisplayName = displayName,
            Category = category,
            Source = source,
            Value = FactValue.Flag(value),
            ObservedAt = observedAt,
            SourceComponents = sourceComponents.ToList(),
        };

    private static void AddOptionalNumberFact(
        List<MarketFact> facts,
        string ticker,
        string name,
        string displayName,
        FactCategory category,
        FactSource source,
        double? value,
        string unit,
        DateTimeOffset observedAt,
        params string[] sourceComponents)
    {
        if (value is null) return;
        facts.Add(NumberFact(ticker, name, displayName, category, source, value.Value, unit, observedAt, sourceComponents));
    }

    private static void AddOptionalBooleanFact(
        List<MarketFact> facts,
        string ticker,
        string name,
        string displayName,
        FactCategory category,
        bool? value,
        DateTimeOffset observedAt,
        params string[] sourceComponents)
    {
        if (value is null) return;
        facts.Add(BooleanFact(ticker, name, displayName, category, FactSource.technical_indicator, value.Value, observedAt, sourceComponents));
    }

    private static double? TryExtractNearestEarningsDays(List<MarketSnapshotNews> newsItems)
    {
        int? nearest = null;
        foreach (var news in newsItems.Where(n => string.Equals(n.CatalystType, "earnings", StringComparison.OrdinalIgnoreCase)))
        {
            var match = EarningsDaysRegex.Match(news.Title);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups["days"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
                continue;

            if (nearest is null || days < nearest.Value)
                nearest = days;
        }

        return nearest;
    }

    private static bool IsInstitutionalSignal(ResearchSignal signal) =>
        signal.SignalCategory.Contains("institution", StringComparison.OrdinalIgnoreCase)
        || signal.Provider.Contains("institution", StringComparison.OrdinalIgnoreCase)
        || signal.SignalType.Contains("institution", StringComparison.OrdinalIgnoreCase);

    private static bool IsInsiderSignal(ResearchSignal signal) =>
        signal.SignalCategory.Contains("insider", StringComparison.OrdinalIgnoreCase)
        || signal.Provider.Contains("insider", StringComparison.OrdinalIgnoreCase)
        || signal.SignalType.Contains("insider", StringComparison.OrdinalIgnoreCase);
}
