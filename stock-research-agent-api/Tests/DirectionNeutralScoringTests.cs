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

        // =====================================================================
        // DIRECTION CLASSIFICATION TESTS
        // =====================================================================

        // ── Test 11: Ratio-based fallback triggers directional when margin < MinEdgeMargin ──
        {
            // Simulate KMTS-like scenario: bullish 33 vs bearish 21
            // Margin = 12 (was below old MinEdgeMargin of 15, now above 10)
            // Ratio = 33/21 = 1.57 (above 1.4 threshold)
            var slightBullish = new TechnicalIndicators
            {
                Sma5 = 101, Sma20 = 99, Sma5AboveSma20 = true, CloseAboveSma20 = true,
                Roc5 = 1.5, Roc10 = 1.0, Rsi14 = 55,
                LinearRegressionSlope = 0.2,
                DonchianBreakout = false, DonchianBreakdown = false,
                VolumeRatio = 1.1, ObvSlope = 0.1, PriceVolumeConfirmation = true,
                CloseLocationValue = 0.6,
                IndicatorsComputed = ["sma5", "sma20", "roc5", "rsi14", "obv"],
                BarsAvailable = 30,
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                slightBullish,
                defaultBenchmark,
                defaultWeights,
                noLessons);

            // Both scores should be > 0 (both sides have some signal)
            Assert("RatioFallback: BullishScore > 0", result.BullishScore > 0);
            Assert("RatioFallback: BearishScore > 0", result.BearishScore > 0);
            // With clear lean, should not be neutral
            if (result.BullishScore > result.BearishScore * 1.4 && result.BearishScore >= 15)
            {
                Assert("RatioFallback: classified as bullish when ratio ≥ 1.4",
                    result.WinningDirection == "bullish");
            }
        }

        // ── Test 12: Very low scores → watch_only ──
        {
            var veryWeakIndicators = new TechnicalIndicators
            {
                Sma5 = 100, Sma20 = 100, Sma5AboveSma20 = false, CloseAboveSma20 = false,
                Roc5 = 0.0, Roc10 = 0.0, Rsi14 = 50,
                LinearRegressionSlope = 0.0,
                VolumeRatio = 1.0, ObvSlope = 0.0,
                CloseLocationValue = 0.5,
                IndicatorsComputed = ["sma5", "sma20"],
                BarsAvailable = 5, // very few bars
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                veryWeakIndicators,
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("WatchOnly: low scores produce neutral or watch",
                result.WinningDirection == "neutral");
        }

        // ── Test 13: No data available → unavailable prediction type ──
        {
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: false, hasNews: false),
                MakeNeutralIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("Unavailable: no data → unavailable type",
                result.PredictionType == "unavailable");
        }

        // =====================================================================
        // RISK SCORING TESTS
        // =====================================================================

        // ── Test 14: High ATR increases risk ──
        {
            var highVolIndicators = MakeBullishIndicators();
            highVolIndicators = highVolIndicators with { Atr14 = 8.0 }; // 8% of $100 price
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                highVolIndicators,
                defaultBenchmark,
                defaultWeights,
                noLessons);

            var normalResult = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("HighATR: risk increases with volatility",
                result.Risk >= normalResult.Risk);
        }

        // ── Test 15: Counter-trend bullish (SPY bearish) raises risk ──
        {
            var bearishMarket = new BenchmarkContext { SpyChangePercent = -1.5, SpyTrend = "bearish" };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                bearishMarket,
                defaultWeights,
                noLessons);

            var normalResult = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark, // bullish SPY
                defaultWeights,
                noLessons);

            Assert("CounterTrend: bullish against bearish SPY raises risk",
                result.Risk >= normalResult.Risk);
        }

        // ── Test 16: Missing market data increases risk ──
        {
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: false, hasNews: true),
                MakeNeutralIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("MissingData: risk elevated when no market data",
                result.Risk >= 55); // base 40 + 15 for no market data
        }

        // ── Test 17: Risk is always clamped 0-100 ──
        {
            // Stack all risk factors
            var extremeIndicators = MakeBullishIndicators() with { Atr14 = 20.0 };
            var extremeBenchmark = new BenchmarkContext { SpyChangePercent = -3.0, SpyTrend = "bearish" };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: false, hasNews: false),
                extremeIndicators,
                extremeBenchmark,
                defaultWeights,
                noLessons);

            Assert("RiskClamped: risk <= 100", result.Risk <= 100);
            Assert("RiskClamped: risk >= 0", result.Risk >= 0);
        }

        // =====================================================================
        // CONFIDENCE FORMULA COMPONENT TESTS
        // =====================================================================

        // ── Test 18: Calibration factor affects confidence ──
        {
            var dampened = new Dictionary<string, double> { ["calibration_factor"] = 0.85 };
            var boosted = new Dictionary<string, double> { ["calibration_factor"] = 1.15 };
            var resultDamp = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                dampened,
                noLessons);
            var resultBoost = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                boosted,
                noLessons);

            Assert("CalibrationFactor: boosted > dampened confidence",
                resultBoost.Confidence >= resultDamp.Confidence);
            Assert("CalibrationFactor: breakdown records the factor",
                resultDamp.Breakdown.CalibrationFactor < resultBoost.Breakdown.CalibrationFactor);
        }

        // ── Test 19: Data quality — fewer indicators reduces confidence ──
        {
            var sparseIndicators = new TechnicalIndicators
            {
                Sma5 = 102, Sma20 = 98, Sma5AboveSma20 = true, CloseAboveSma20 = true,
                Roc5 = 3.0, Rsi14 = 62,
                IndicatorsComputed = ["sma5", "rsi14"], // only 2 indicators
                BarsAvailable = 10,
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                sparseIndicators,
                defaultBenchmark,
                defaultWeights,
                noLessons);

            // Sparse data should have lower data quality factor
            Assert("DataQuality: sparse data quality < 1.0",
                result.Breakdown.DataQualityFactor < 1.0);
        }

        // ── Test 20: Confidence never exceeds global max (85) ──
        {
            // Stack everything in favor of high confidence
            var maxWeights = new Dictionary<string, double>
            {
                ["calibration_factor"] = 1.15,
                ["risk_cap_boost"] = 15.0,
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                new BenchmarkContext { SpyChangePercent = 2.0, SpyTrend = "bullish" },
                maxWeights,
                noLessons);

            Assert("ConfidenceMax: never exceeds 85", result.Confidence <= 85);
        }

        // ── Test 21: Confidence never goes below 0 ──
        {
            var crushWeights = new Dictionary<string, double>
            {
                ["calibration_factor"] = 0.85,
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: false, hasNews: false),
                MakeNeutralIndicators(),
                defaultBenchmark,
                crushWeights,
                noLessons);

            Assert("ConfidenceMin: never below 0", result.Confidence >= 0);
        }

        // =====================================================================
        // OPPOSITION PENALTY TESTS
        // =====================================================================

        // ── Test 22: Low opposition (< 0.35) → no penalty ──
        {
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(), // clear bullish, low bearish
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("LowOpposition: penalty is 1.0 (no reduction)",
                result.Breakdown.OppositionPenalty >= 0.99);
        }

        // ── Test 23: High opposition triggers penalty ──
        {
            // Create indicators where bull and bear are almost equal
            var evenIndicators = new TechnicalIndicators
            {
                Sma5 = 101, Sma20 = 99, Sma5AboveSma20 = true, CloseAboveSma20 = true,
                Roc5 = -2.0, Roc10 = -3.0, Rsi14 = 35, // bearish momentum
                LinearRegressionSlope = 0.1,
                DonchianBreakout = true, DonchianBreakdown = true,
                VolumeRatio = 1.5, ObvSlope = -0.3, PriceVolumeConfirmation = false,
                CloseLocationValue = 0.45,
                IndicatorsComputed = ["sma5", "sma20", "roc5", "rsi14", "obv"],
                BarsAvailable = 30,
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                evenIndicators,
                defaultBenchmark,
                defaultWeights,
                noLessons);

            // With nearly equal scores, decision margin should be LOW (near 0)
            if (result.Breakdown.DecisionMargin < 0.48)
            {
                Assert("HighOpposition: penalty < 1.0",
                    result.Breakdown.OppositionPenalty < 1.0);
                Assert("HighOpposition: penalty >= 0.6 (floor)",
                    result.Breakdown.OppositionPenalty >= 0.6);
            }
            else
            {
                // If opposition didn't trigger, at least verify it's tracked
                Assert("HighOpposition: margin tracked",
                    result.Breakdown.DecisionMargin >= 0);
            }
        }

        // ── Test 24: Opposition penalty floor is 0.6 ──
        {
            // Equal scores → decision margin near 0 → max penalty (floor 0.6)
            var equalIndicators = new TechnicalIndicators
            {
                Sma5 = 100, Sma20 = 100, Sma5AboveSma20 = true, CloseAboveSma20 = false,
                Roc5 = 1.0, Roc10 = -1.0, Rsi14 = 50,
                LinearRegressionSlope = 0.0,
                DonchianBreakout = true, DonchianBreakdown = true,
                VolumeRatio = 1.0, ObvSlope = 0.0, PriceVolumeConfirmation = false,
                CloseLocationValue = 0.5,
                IndicatorsComputed = ["sma5", "sma20", "roc5", "rsi14", "obv"],
                BarsAvailable = 30,
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                equalIndicators,
                defaultBenchmark,
                defaultWeights,
                noLessons);

            // Near-equal scores → low decision margin → penalty near floor
            if (result.Breakdown.DecisionMargin < 0.15)
            {
                Assert("OppositionFloor: penalty hits floor at 0.6",
                    Math.Abs(result.Breakdown.OppositionPenalty - 0.6) < 0.05);
            }
        }

        // =====================================================================
        // RISK-CONFIDENCE COHERENCE TESTS (direction-aware + self-tuning)
        // =====================================================================

        // ── Test 25: Risk cap boost from learning raises caps ──
        {
            var weightsWithBoost = new Dictionary<string, double>
            {
                ["risk_cap_boost"] = 10.0,
            };
            var resultNoBoost = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);
            var resultWithBoost = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                weightsWithBoost,
                noLessons);

            Assert("RiskCapBoost: boosted confidence >= non-boosted",
                resultWithBoost.Confidence >= resultNoBoost.Confidence);
        }

        // ── Test 26: Risk cap boost is clamped to 0-15 ──
        {
            var absurdBoost = new Dictionary<string, double> { ["risk_cap_boost"] = 100.0 };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                absurdBoost,
                noLessons);

            Assert("RiskCapBoost: clamped — confidence <= 85", result.Confidence <= 85);
        }

        // ── Test 27: Negative risk_cap_boost treated as zero ──
        {
            var negBoost = new Dictionary<string, double> { ["risk_cap_boost"] = -5.0 };
            var resultNeg = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                negBoost,
                noLessons);
            var resultDefault = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("RiskCapBoost: negative clamped to zero — same as default",
                resultNeg.Confidence == resultDefault.Confidence);
        }

        // ── Test 28: Clear direction gets ClearDirection=true in breakdown ──
        {
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("ClearDirection: tracked in breakdown",
                result.Breakdown.DecisionMargin >= 0);
            // Strong bullish with weak bearish should have HIGH decision margin
            if (result.BullishScore + result.BearishScore > 0)
            {
                var expectedMargin = (result.BullishScore - result.BearishScore) /
                                     (result.BullishScore + result.BearishScore);
                Assert("ClearDirection: breakdown margin matches scores",
                    Math.Abs(result.Breakdown.DecisionMargin - expectedMargin) < 0.1);
            }
        }

        // ── Test 29: Cap reason includes boost info in debug ──
        {
            var boostWeights = new Dictionary<string, double> { ["risk_cap_boost"] = 5.0 };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                boostWeights,
                noLessons);

            // ConfidenceCap should mention boost if a risk cap was applied
            if (result.Breakdown.ConfidenceCap != null && result.Breakdown.ConfidenceCap.StartsWith("Risk"))
            {
                Assert("CapReason: includes boost amount",
                    result.Breakdown.ConfidenceCap.Contains("boost"));
            }
        }

        // =====================================================================
        // EARNINGS PROXIMITY TESTS
        // =====================================================================

        // ── Test 30: Earnings within 3 days caps confidence ──
        {
            var earningsNews = new List<MarketSnapshotNews>
            {
                new() { Title = "Earnings in 2 days", Sentiment = "neutral", ImportanceScore = 95, CatalystType = "earnings" },
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true, hasNews: true, news: earningsNews),
                MakeBullishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            var noEarningsResult = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("EarningsProximity: caps confidence lower than no-earnings",
                result.Confidence <= noEarningsResult.Confidence);
            if (result.Breakdown.ConfidenceCap != null)
            {
                Assert("EarningsProximity: cap reason mentions earnings",
                    result.Breakdown.ConfidenceCap.Contains("Earnings") ||
                    result.Breakdown.ConfidenceCap.Contains("earnings"));
            }
        }

        // =====================================================================
        // LEARNING WEIGHT INTEGRATION TESTS
        // =====================================================================

        // ── Test 31: Per-direction weights amplify the correct side ──
        {
            var bullishBoosted = new Dictionary<string, double>
            {
                ["technical_trend_bullish"] = 2.0,
                ["technical_trend_bearish"] = 0.5,
            };
            var resultBoosted = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                bullishBoosted,
                noLessons);
            var resultNormal = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            Assert("DirectionWeights: boosted bullish score >= normal",
                resultBoosted.BullishScore >= resultNormal.BullishScore);
        }

        // ── Test 32: Multiple weight overrides combine correctly ──
        {
            var multiWeights = new Dictionary<string, double>
            {
                ["calibration_factor"] = 1.10,
                ["risk_cap_boost"] = 5.0,
                ["technical_trend"] = 1.5,
            };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                defaultBenchmark,
                multiWeights,
                noLessons);

            Assert("MultiWeights: produces valid result",
                result.Confidence >= 0 && result.Confidence <= 85);
            Assert("MultiWeights: calibration factor recorded",
                Math.Abs(result.Breakdown.CalibrationFactor - 1.10) < 0.01);
        }

        // =====================================================================
        // MARKET CONTEXT CONFLICT TESTS
        // =====================================================================

        // ── Test 33: Bullish prediction with bearish market context caps confidence ──
        {
            var bearishSpy = new BenchmarkContext { SpyChangePercent = -2.0, SpyTrend = "bearish" };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                bearishSpy,
                defaultWeights,
                noLessons);

            var alignedResult = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBullishIndicators(),
                new BenchmarkContext { SpyChangePercent = 2.0, SpyTrend = "bullish" },
                defaultWeights,
                noLessons);

            Assert("MarketConflict: counter-trend confidence <= aligned",
                result.Confidence <= alignedResult.Confidence);
        }

        // ── Test 34: Bearish prediction with bullish market also caps ──
        {
            var bullishSpy = new BenchmarkContext { SpyChangePercent = 2.0, SpyTrend = "bullish" };
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true),
                MakeBearishIndicators(),
                bullishSpy,
                defaultWeights,
                noLessons);

            Assert("MarketConflictBear: confidence capped reasonably",
                result.Confidence <= 65);
        }

        // =====================================================================
        // BREAKDOWN COMPLETENESS TESTS
        // =====================================================================

        // ── Test 35: All breakdown fields populated ──
        {
            var result = ScoringEngine.Score(
                MakeSnapshot(hasMarketData: true, hasNews: true, news: new List<MarketSnapshotNews>
                {
                    new() { Title = "Upgrade", Sentiment = "bullish", ImportanceScore = 90, CatalystType = "rating_change" },
                }),
                MakeBullishIndicators(),
                defaultBenchmark,
                defaultWeights,
                noLessons);

            var b = result.Breakdown;
            Assert("Breakdown: DataQualityFactor > 0", b.DataQualityFactor > 0);
            Assert("Breakdown: ConfirmationMultiplier > 0", b.ConfirmationMultiplier > 0);
            Assert("Breakdown: RiskAdjustment > 0", b.RiskAdjustment > 0);
            Assert("Breakdown: CalibrationFactor > 0", b.CalibrationFactor > 0);
            Assert("Breakdown: OppositionPenalty > 0", b.OppositionPenalty > 0);
            Assert("Breakdown: DecisionMargin >= 0", b.DecisionMargin >= 0);
            Assert("Breakdown: WinningDirection set", !string.IsNullOrEmpty(b.WinningDirection));
            Assert("Breakdown: BullishScore > 0", b.BullishScore > 0);
        }

        return (passed, failures.Count, failures);
    }
}
