using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Computes technical indicators from real OHLCV bars.
/// Never fakes data — if there aren't enough bars for an indicator, it's skipped.
/// </summary>
public static class IndicatorEngine
{
    // -----------------------------------------------------------------------
    // Sector → ETF mapping (SPDR sector ETFs)
    // Keys match TwelveData /profile "sector" values.
    // -----------------------------------------------------------------------
    public static readonly IReadOnlyDictionary<string, string> SectorEtfMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Technology"]              = "XLK",
            ["Healthcare"]              = "XLV",
            ["Financials"]              = "XLF",
            ["Consumer Discretionary"]  = "XLY",
            ["Consumer Staples"]        = "XLP",
            ["Energy"]                  = "XLE",
            ["Industrials"]             = "XLI",
            ["Materials"]               = "XLB",
            ["Real Estate"]             = "XLRE",
            ["Utilities"]               = "XLU",
            ["Communication Services"]  = "XLC",
        };

    /// <summary>Look up the sector ETF ticker for a TwelveData sector name. Returns null if unmapped.</summary>
    public static string? GetSectorEtf(string? sector)
        => sector is not null && SectorEtfMap.TryGetValue(sector, out var etf) ? etf : null;

    public static TechnicalIndicators Compute(List<MarketSnapshotBar> bars)
    {
        var computed = new List<string>();
        var skipped = new List<string>();
        int count = bars.Count;
        if (count < 2) return new TechnicalIndicators { BarsAvailable = count, IndicatorsSkipped = ["All — fewer than 2 bars"] };

        var closes = bars.Select(b => b.Close).ToList();
        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();
        var latestClose = closes[0];

        // SMA5
        double? sma5 = null;
        if (count >= 5) { sma5 = closes.Take(5).Average(); computed.Add("SMA5"); }
        else skipped.Add("SMA5 (need 5 bars)");

        // SMA20
        double? sma20 = null;
        if (count >= 20) { sma20 = closes.Take(20).Average(); computed.Add("SMA20"); }
        else skipped.Add("SMA20 (need 20 bars)");

        // ROC5
        double? roc5 = null;
        if (count >= 6 && closes[5] > 0) { roc5 = ((latestClose - closes[5]) / closes[5]) * 100; computed.Add("ROC5"); }
        else skipped.Add("ROC5 (need 6 bars)");

        // ROC10
        double? roc10 = null;
        if (count >= 11 && closes[10] > 0) { roc10 = ((latestClose - closes[10]) / closes[10]) * 100; computed.Add("ROC10"); }
        else skipped.Add("ROC10 (need 11 bars)");

        // RSI14
        double? rsi14 = null;
        if (count >= 15)
        {
            var changes = new List<double>();
            for (int i = 0; i < 14; i++)
                changes.Add(closes[i] - closes[i + 1]);

            var gains = changes.Where(c => c > 0).DefaultIfEmpty(0).Average();
            var losses = changes.Where(c => c < 0).Select(c => Math.Abs(c)).DefaultIfEmpty(0).Average();

            rsi14 = losses == 0 ? 100 : Math.Round(100 - (100 / (1 + gains / losses)), 2);
            computed.Add("RSI14");
        }
        else skipped.Add("RSI14 (need 15 bars)");

        // Stochastic close location (%K-like)
        double? stochLoc = null;
        if (count >= 14)
        {
            var period = Math.Min(14, count);
            var periodHighs = highs.Take(period);
            var periodLows = lows.Take(period);
            var hh = periodHighs.Max();
            var ll = periodLows.Min();
            stochLoc = (hh - ll) > 0 ? Math.Round(((latestClose - ll) / (hh - ll)) * 100, 2) : 50;
            computed.Add("Stochastic");
        }
        else skipped.Add("Stochastic (need 14 bars)");

        // Linear regression slope (5 bars)
        double? lrSlope = null;
        if (count >= 5)
        {
            lrSlope = ComputeLinearRegressionSlope(closes.Take(5).Reverse().ToList());
            computed.Add("LinRegSlope5");
        }
        else skipped.Add("LinRegSlope5 (need 5 bars)");

        // Donchian 20
        double? donchianHigh = null, donchianLow = null;
        bool? donchianBreakout = null, donchianBreakdown = null;
        if (count >= 20)
        {
            donchianHigh = highs.Take(20).Max();
            donchianLow = lows.Take(20).Min();
            donchianBreakout = latestClose >= donchianHigh;
            donchianBreakdown = latestClose <= donchianLow;
            computed.Add("Donchian20");
        }
        else skipped.Add("Donchian20 (need 20 bars)");

        // ATR14
        double? atr14 = null;
        if (count >= 15)
        {
            var trs = new List<double>();
            for (int i = 0; i < Math.Min(14, count - 1); i++)
            {
                var tr = Math.Max(highs[i] - lows[i],
                    Math.Max(Math.Abs(highs[i] - closes[i + 1]), Math.Abs(lows[i] - closes[i + 1])));
                trs.Add(tr);
            }
            atr14 = trs.Average();
            computed.Add("ATR14");
        }
        else if (count >= 2)
        {
            var trs = new List<double>();
            for (int i = 0; i < count - 1; i++)
            {
                var tr = Math.Max(highs[i] - lows[i],
                    Math.Max(Math.Abs(highs[i] - closes[i + 1]), Math.Abs(lows[i] - closes[i + 1])));
                trs.Add(tr);
            }
            atr14 = trs.Average();
            computed.Add($"ATR{trs.Count}");
            skipped.Add("ATR14 (partial, only {0} bars)".Replace("{0}", trs.Count.ToString()));
        }

        // Bollinger Bands 20
        double? bbUpper = null, bbMiddle = null, bbLower = null, bbBandwidth = null;
        bool? bbBreakout = null;
        if (count >= 20)
        {
            bbMiddle = sma20;
            var stdDev = Math.Sqrt(closes.Take(20).Select(c => Math.Pow(c - sma20!.Value, 2)).Average());
            bbUpper = Math.Round(sma20!.Value + 2 * stdDev, 4);
            bbLower = Math.Round(sma20!.Value - 2 * stdDev, 4);
            bbBandwidth = sma20 > 0 ? Math.Round((bbUpper.Value - bbLower.Value) / sma20.Value * 100, 2) : null;
            bbBreakout = latestClose > bbUpper || latestClose < bbLower;
            computed.Add("BollingerBands20");
        }
        else skipped.Add("BollingerBands20 (need 20 bars)");

        // Volume ratio
        double? volRatio = null;
        if (count >= 2)
        {
            var avgVol = volumes.Average();
            volRatio = avgVol > 0 ? Math.Round(volumes[0] / avgVol, 2) : null;
            computed.Add("VolumeRatio");
        }

        // OBV slope (5-bar)
        double? obvSlope = null;
        if (count >= 5)
        {
            var obvValues = new List<double>();
            double obv = 0;
            for (int i = Math.Min(5, count - 1); i >= 0; i--)
            {
                if (i < count - 1)
                {
                    if (closes[i] > closes[i + 1]) obv += volumes[i];
                    else if (closes[i] < closes[i + 1]) obv -= volumes[i];
                }
                obvValues.Add(obv);
            }
            obvSlope = ComputeLinearRegressionSlope(obvValues);
            computed.Add("OBV");
        }
        else skipped.Add("OBV (need 5 bars)");

        // Price-volume confirmation
        bool? pvConfirm = null;
        if (count >= 2 && volRatio is not null)
        {
            var priceUp = closes[0] > closes[1];
            var volUp = volRatio > 1.0;
            pvConfirm = (priceUp && volUp) || (!priceUp && !volUp);
            computed.Add("PriceVolConfirm");
        }

        // Close location in 20-day range
        double? clv = null;
        if (count >= 20)
        {
            var rangeHigh = highs.Take(20).Max();
            var rangeLow = lows.Take(20).Min();
            clv = (rangeHigh - rangeLow) > 0 ? Math.Round(((latestClose - rangeLow) / (rangeHigh - rangeLow)) * 100, 2) : 50;
            computed.Add("CloseLocationValue");
        }
        else skipped.Add("CloseLocationValue (need 20 bars)");

        return new TechnicalIndicators
        {
            Sma5 = sma5,
            Sma20 = sma20,
            Sma5AboveSma20 = sma5 is not null && sma20 is not null && sma5 > sma20,
            CloseAboveSma20 = sma20 is not null && latestClose > sma20,
            Roc5 = roc5 is not null ? Math.Round(roc5.Value, 2) : null,
            Roc10 = roc10 is not null ? Math.Round(roc10.Value, 2) : null,
            Rsi14 = rsi14,
            StochasticCloseLocation = stochLoc,
            LinearRegressionSlope = lrSlope is not null ? Math.Round(lrSlope.Value, 4) : null,
            DonchianHigh20 = donchianHigh,
            DonchianLow20 = donchianLow,
            DonchianBreakout = donchianBreakout,
            DonchianBreakdown = donchianBreakdown,
            Atr14 = atr14 is not null ? Math.Round(atr14.Value, 4) : null,
            BollingerUpper = bbUpper,
            BollingerMiddle = bbMiddle,
            BollingerLower = bbLower,
            BollingerBandwidth = bbBandwidth,
            BollingerBreakout = bbBreakout,
            VolumeRatio = volRatio,
            ObvSlope = obvSlope,
            PriceVolumeConfirmation = pvConfirm,
            CloseLocationValue = clv,
            IndicatorsComputed = computed,
            IndicatorsSkipped = skipped,
            BarsAvailable = count,
        };
    }

    public static BenchmarkContext ComputeBenchmarkContext(
        MarketSnapshotQuote? tickerQuote,
        MarketSnapshotQuote? spyQuote,
        MarketSnapshotQuote? qqqQuote,
        double? spyEma20 = null,
        string? sectorEtf = null,
        double? sectorEtfPrice = null,
        double? sectorEtfEma = null)
    {
        double? spyChange = null, qqqChange = null;
        double? relSpy = null, relQqq = null;
        string? spyTrend = null, qqqTrend = null;

        if (spyQuote is not null && spyQuote.PreviousClose > 0)
        {
            spyChange = Math.Round(spyQuote.ChangePercent, 2);
            spyTrend = spyQuote.ChangePercent > 0.3 ? "bullish" : spyQuote.ChangePercent < -0.3 ? "bearish" : "neutral";
        }

        if (qqqQuote is not null && qqqQuote.PreviousClose > 0)
        {
            qqqChange = Math.Round(qqqQuote.ChangePercent, 2);
            qqqTrend = qqqQuote.ChangePercent > 0.3 ? "bullish" : qqqQuote.ChangePercent < -0.3 ? "bearish" : "neutral";
        }

        if (tickerQuote is not null)
        {
            if (spyChange is not null)
                relSpy = Math.Round(tickerQuote.ChangePercent - spyChange.Value, 2);
            if (qqqChange is not null)
                relQqq = Math.Round(tickerQuote.ChangePercent - qqqChange.Value, 2);
        }

        // Multi-day SPY trend from EMA(20): price/EMA ratio tells us if the market
        // has been trending up or down over ~4 weeks, not just today's move.
        double? spyEmaRatio = null;
        string? spyMultiDayTrend = null;
        if (spyQuote is not null && spyEma20 is not null && spyEma20 > 0)
        {
            spyEmaRatio = Math.Round(spyQuote.Price / spyEma20.Value, 4);
            // >0.3% above EMA = bullish trend, >0.3% below = bearish, else neutral
            var deviation = (spyEmaRatio.Value - 1.0) * 100;
            spyMultiDayTrend = deviation > 0.3 ? "bullish" : deviation < -0.3 ? "bearish" : "neutral";
        }

        // Sector ETF trend from EMA — same logic as SPY multi-day trend.
        double? sectorEmaRatio = null;
        string? sectorEtfTrend = null;
        if (sectorEtf is not null && sectorEtfPrice is not null && sectorEtfEma is not null && sectorEtfEma > 0)
        {
            sectorEmaRatio = Math.Round(sectorEtfPrice.Value / sectorEtfEma.Value, 4);
            var sectorDeviation = (sectorEmaRatio.Value - 1.0) * 100;
            sectorEtfTrend = sectorDeviation > 0.3 ? "bullish" : sectorDeviation < -0.3 ? "bearish" : "neutral";
        }

        return new BenchmarkContext
        {
            SpyChangePercent = spyChange,
            QqqChangePercent = qqqChange,
            SpyTrend = spyTrend,
            QqqTrend = qqqTrend,
            RelativeStrengthVsSpy = relSpy,
            RelativeStrengthVsQqq = relQqq,
            SpyEmaRatio = spyEmaRatio,
            SpyMultiDayTrend = spyMultiDayTrend,
            SectorEtf = sectorEtf,
            SectorEtfEmaRatio = sectorEmaRatio,
            SectorEtfTrend = sectorEtfTrend,
        };
    }

    /// <summary>
    /// Merges API-sourced indicator values (MACD, EMA) into an existing TechnicalIndicators record.
    /// These indicators require full price history and cannot be accurately computed from 20 bars.
    /// RSI and Bollinger Bands are already computed from bars by <see cref="Compute"/>.
    /// </summary>
    public static TechnicalIndicators MergeApiIndicators(
        TechnicalIndicators manual,
        (double MacdLine, double Signal, double Histogram)? apiMacd = null,
        (double? Ema12, double? Ema26, double? Ema50)? apiEma = null)
    {
        var computed = new List<string>(manual.IndicatorsComputed);

        // MACD: only available from API (needs 26+ bars of EMA history)
        double? macdLine = null, macdSignal = null, macdHist = null;
        bool? macdBullish = null;
        if (apiMacd is not null)
        {
            macdLine = apiMacd.Value.MacdLine;
            macdSignal = apiMacd.Value.Signal;
            macdHist = apiMacd.Value.Histogram;
            macdBullish = apiMacd.Value.Histogram > 0 &&
                          apiMacd.Value.MacdLine > apiMacd.Value.Signal;
            computed.Add("MACD_API");
        }

        // EMA: only available from API (needs full history for proper exponential smoothing)
        double? ema12 = null, ema26 = null, ema50 = null;
        if (apiEma is not null)
        {
            ema12 = apiEma.Value.Ema12;
            ema26 = apiEma.Value.Ema26;
            ema50 = apiEma.Value.Ema50;
            if (ema12 is not null) computed.Add("EMA12_API");
            if (ema26 is not null) computed.Add("EMA26_API");
            if (ema50 is not null) computed.Add("EMA50_API");
        }

        return manual with
        {
            MacdLine = macdLine,
            MacdSignal = macdSignal,
            MacdHistogram = macdHist,
            MacdBullishCrossover = macdBullish,
            Ema12 = ema12,
            Ema26 = ema26,
            Ema50 = ema50,
            IndicatorsComputed = computed,
        };
    }

    private static double ComputeLinearRegressionSlope(List<double> values)
    {
        int n = values.Count;
        if (n < 2) return 0;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += values[i];
            sumXY += i * values[i];
            sumX2 += i * i;
        }
        var denom = n * sumX2 - sumX * sumX;
        return denom == 0 ? 0 : (n * sumXY - sumX * sumY) / denom;
    }
}
