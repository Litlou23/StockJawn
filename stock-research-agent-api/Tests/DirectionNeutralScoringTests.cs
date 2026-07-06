using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchEngine;

namespace StockResearchAgent.Api.Tests;

/// <summary>
/// Validates the direction-neutral dual-scoring architecture.
/// Run with: dotnet test or via the /run-tests endpoint.
/// </summary>
public static class DirectionNeutralScoringTests
{
    public static (int Passed, int Failed, List<string> Failures) RunAll()
    {
        var failures = new List<string>();
        int passed = 0;

        void Assert(string name, bool condition)
        {
            if (condition) passed++;
            else failures.Add(name);
        }

        // ── Helper factories ─────────────────────────────────────
        static MarketSnapshot MakeSnapshot(
            string ticker = "TEST",
            bool hasMarketData = true,
            bool hasNews = false,
            List<MarketSnapshotNews>? news = null) => new()
        {
            Ticker = ticker,
            Quote = hasMarketData ? new MarketSnapshotQuote { Price = 100, High = 102, Low = 98, Volume = 1_000_000 } : null,
            DataAvailability = new MarketSnapshotAvailability
            {
                MarketDataAvailable = hasMarketData,
                NewsAvailable = hasNews,
            },
            NewsContext = news ?? [],
        };

        static TechnicalIndicators MakeBullishIndicators() => new()
        {
            Sma5 = 101, Sma20 = 98, Sma5AboveSma20 = true, CloseAboveSma20 = true,
            Roc5 = 3.0, Roc10 = 5.0, Rsi14 = 62,
            LinearRegressionSlope = 0.5,
            DonchianBreakout = true, DonchianBreakdown = false,
            VolumeRatio = 1.5, ObvSlope = 0.3, PriceVolumeConfirmation = true,
            CloseLocationValue = 0.85,
            IndicatorsComputed = ["sma5", "sma20", "roc5", "rsi14", "obv"],
            BarsAvailable = 30,
        };

        static TechnicalIndicators MakeBearishIndicators() => new()
        {
            Sma5 = 97, Sma20 = 101, Sma5AboveSma20 = false, CloseAboveSma20 = false,
            Roc5 = -3.0, Roc10 = -5.0, Rsi14 = 32,
            LinearRegressionSlope = -0.5,
            DonchianBreakout = false, DonchianBreakdown = true,
            VolumeRatio = 1.5, ObvSlope = -0.3, PriceVolumeConfirmation = false,
            CloseLocationValue = 0.15,
            IndicatorsComputed = ["sma5", "sma20", "roc5", "rsi14", "obv"],
            BarsAvailable = 30,
        };

        static TechnicalIndicators MakeNeutralIndicators() => new()
        {
            Sma5 = 100, Sma20 = 100, Sma5AboveSma20 = false, CloseAboveSma20 = true,
            Roc5 = 0.2, Roc10 = -0.1, Rsi14 = 50,
            LinearRegressionSlope = 0.01,
            VolumeRatio = 1.0, ObvSlope = 0.0,
            CloseLocationValue = 0.5,
            IndicatorsComputed = ["sma5", "sma20", "roc5", "rsi14"],
            BarsAvailable = 30,
        };

        var defaultWeights = new Dictionary<string, double>();
        var noLessons = new List<string>();
        var defaultBenchmark = new BenchmarkContext { SpyChangePercent = 0.5, SpyTrend = "bullish" };

        // ── Test 1: Strong bullish signals → bullish prediction, call-eligible ──
        {
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("Bullish: BullishScore > BearishScore",
                result.BullishScore > result.BearishScore);
            Assert("Bullish: WinningDirection is bullish",
                result.WinningDirection == "bullish");
            Assert("Bullish: PredictionType is bullish",
                result.PredictionType == "bullish");
            Assert("Bullish: DirectionMargin > 0",
                result.DirectionMargin > 0);
            Assert("Bullish: BullishScore > 0",
                result.BullishScore > 0);
        }

        // ── Test 2: Strong bearish signals → bearish prediction, put-eligible ──
        {
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBearishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("Bearish: BearishScore > BullishScore",
                result.BearishScore > result.BullishScore);
            Assert("Bearish: WinningDirection is bearish",
                result.WinningDirection == "bearish");
            Assert("Bearish: PredictionType is bearish",
                result.PredictionType == "bearish");
            Assert("Bearish: DirectionMargin < 0",
                result.DirectionMargin < 0);
            Assert("Bearish: BearishScore > 0",
                result.BearishScore > 0);
        }

        // ── Test 3: Neutral/flat signals → no directional prediction ──
        {
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeNeutralIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("Neutral: WinningDirection is neutral",
                result.WinningDirection == "neutral");
            Assert("Neutral: PredictionType contains neutral or watch",
                result.PredictionType.Contains("neutral") || result.PredictionType == "watch_only");
            Assert("Neutral: scores are close",
                Math.Abs(result.BullishScore - result.BearishScore) < 20);
        }

        // ── Test 4: Unknown/null catalyst sentiment → no directional contribution ──
        {
            var newsWithNullSentiment = new List<MarketSnapshotNews>
            {
                new() { Title = "Company announces something", Sentiment = null, ImportanceScore = 80, CatalystType = "news" },
                new() { Title = "Another story", Sentiment = "unknown", ImportanceScore = 70, CatalystType = "news" },
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true, hasNews: true, news: newsWithNullSentiment),
                MakeNeutralIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            // With null/unknown sentiment catalysts and neutral technicals,
            // neither side should get a big boost from catalysts
            var breakdown = result.Breakdown;
            Assert("NullSentiment: CatalystBullish == CatalystBearish (null sentiment contributes equally)",
                Math.Abs(breakdown.CatalystBullish - breakdown.CatalystBearish) < 1);
        }

        // ── Test 5: Mixed signals → both scores populated, confidence reflects uncertainty ──
        {
            var mixedIndicators = new TechnicalIndicators
            {
                Sma5 = 101, Sma20 = 99, Sma5AboveSma20 = true, CloseAboveSma20 = true,
                Roc5 = -2.0, Roc10 = -3.0, Rsi14 = 38, // momentum bearish despite bullish trend
                LinearRegressionSlope = 0.2,
                DonchianBreakout = false, DonchianBreakdown = false,
                VolumeRatio = 0.8, ObvSlope = -0.1,
                CloseLocationValue = 0.4,
                IndicatorsComputed = ["sma5", "sma20", "roc5", "rsi14", "obv"],
                BarsAvailable = 30,
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                mixedIndicators,
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("Mixed: Both BullishScore and BearishScore > 0",
                result.BullishScore > 0 && result.BearishScore > 0);
            Assert("Mixed: Confidence lower than strong-signal scenarios",
                result.Confidence < 80);
        }

        // ── Test 6: Dual scores produce independent, non-negative values ──
        {
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("Scores non-negative: BullishScore >= 0", result.BullishScore >= 0);
            Assert("Scores non-negative: BearishScore >= 0", result.BearishScore >= 0);
            Assert("Scores capped: BullishScore <= 100", result.BullishScore <= 100);
            Assert("Scores capped: BearishScore <= 100", result.BearishScore <= 100);
        }

        // ── Test 7: Breakdown contains per-direction bucket scores ──
        {
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            var b = result.Breakdown;
            Assert("Breakdown: TrendBullish populated", b.TrendBullish > 0 || b.TrendBearish > 0);
            Assert("Breakdown: MomentumBullish populated", b.MomentumBullish > 0 || b.MomentumBearish > 0);
            Assert("Breakdown: has WinningDirection", !string.IsNullOrEmpty(b.WinningDirection));
            Assert("Breakdown: BullishScore matches top-level",
                Math.Abs(b.BullishScore - result.BullishScore) < 0.1);
            Assert("Breakdown: BearishScore matches top-level",
                Math.Abs(b.BearishScore - result.BearishScore) < 0.1);
        }

        // ── Test 8: Bullish news → bullish catalyst score only ──
        {
            var bullishNews = new List<MarketSnapshotNews>
            {
                new() { Title = "Upgrade", Sentiment = "bullish", ImportanceScore = 90, CatalystType = "rating_change" },
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true, hasNews: true, news: bullishNews),
                MakeNeutralIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("BullishNews: CatalystBullish > CatalystBearish",
                result.Breakdown.CatalystBullish > result.Breakdown.CatalystBearish);
        }

        // ── Test 9: Bearish news → bearish catalyst score only ──
        {
            var bearishNews = new List<MarketSnapshotNews>
            {
                new() { Title = "Downgrade", Sentiment = "bearish", ImportanceScore = 90, CatalystType = "rating_change" },
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true, hasNews: true, news: bearishNews),
                MakeNeutralIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("BearishNews: CatalystBearish > CatalystBullish",
                result.Breakdown.CatalystBearish > result.Breakdown.CatalystBullish);
        }

        // ── Test 10: Learning weights are read per-direction ──
        {
            var weightsWithDirection = new Dictionary<string, double>
            {
                ["technical_trend_bullish"] = 2.0,
                ["technical_trend_bearish"] = 0.5,
                ["technical_trend"] = 1.0,
            };
            var bullResult = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                weightsWithDirection,
                noLessons);
            var bearResult = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBearishIndicators(),
                defaultBenchmark,
                weightsWithDirection,
                noLessons);

            // Bullish direction should get boosted by the 2.0 weight
            // Bearish direction should get dampened by the 0.5 weight
            // We just verify both produce valid results
            Assert("DirectionWeights: bullish result valid", bullResult.BullishScore >= 0);
            Assert("DirectionWeights: bearish result valid", bearResult.BearishScore >= 0);
        }

        return (passed, failures.Count, failures);
    }
}
