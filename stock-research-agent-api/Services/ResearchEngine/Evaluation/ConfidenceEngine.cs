using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class ConfidenceEngine : IConfidenceEngine
{
    public ConfidenceResult Evaluate(
        EvaluationContext context,
        AggregateScoreResult aggregate,
        RiskAssessment riskAssessment,
        string winningDirection)
    {
        var debugSignals = new List<string>();
        var outputs = aggregate.Outputs;
        var trend = outputs[EvaluatorKind.trend];
        var momentum = outputs[EvaluatorKind.momentum];
        var market = outputs[EvaluatorKind.market_context];

        var availableSignals = context.Indicators.IndicatorsComputed.Count;
        var totalPossibleSignals = availableSignals + context.Indicators.IndicatorsSkipped.Count;
        var dataQuality = totalPossibleSignals > 0 ? (double)availableSignals / totalPossibleSignals : 0.5;
        var dataQualityFactor = 0.75 + (0.25 * dataQuality);

        // ── Research Universe data quality boost ──────────────────────
        // Evidence count and research state contribute to data quality.
        // More evidence = higher confidence the signal is real.
        // Higher lifecycle states = more thoroughly vetted.
        // Configurable via "research_universe_weight" scoring weight (default 1.0).
        var ru = context.ResearchUniverse;
        if (ru.HasResearchAsset)
        {
            var ruWeight = context.LearningData.Weights.GetValueOrDefault("research_universe_weight", 1.0);

            // Evidence count boost: 0 to 0.05 range (capped at 20 evidence items)
            var evidenceBoost = Math.Min(ru.EvidenceCount, 20) / 20.0 * 0.05 * ruWeight;

            // Research state boost: higher lifecycle states = more vetting
            var stateBoost = ru.ResearchState switch
            {
                ResearchState.ReadyForEvaluation => 0.04,
                ResearchState.BuildingThesis => 0.03,
                ResearchState.Monitoring => 0.02,
                ResearchState.Discovered => 0.01,
                _ => 0.0,
            } * ruWeight;

            dataQualityFactor += evidenceBoost + stateBoost;
            dataQualityFactor = Math.Min(dataQualityFactor, 1.05); // soft cap
        }

        // ── Fundamentals-based confidence modifier ─────────────────
        // Strong fundamentals boost conviction; weak fundamentals add caution.
        // This uses the data fetched from TwelveData /profile and /statistics.
        double fundamentalsMod = 0.0;
        var fundamentals = context.Snapshot.Fundamentals;
        if (fundamentals is not null)
        {
            // Growth signals boost confidence
            if (fundamentals.RevenueGrowthYoy is double revGrowth && revGrowth > 0.10)
                fundamentalsMod += 0.03; // >10% YoY revenue growth
            if (fundamentals.EarningsGrowthYoy is double earningsGrowth && earningsGrowth > 0.15)
                fundamentalsMod += 0.02; // >15% YoY earnings growth

            // Profitability signals
            if (fundamentals.ReturnOnEquity is double roe2 && roe2 > 0.15)
                fundamentalsMod += 0.02; // >15% ROE

            // Valuation caution — extreme P/E with bearish direction is higher conviction bearish,
            // but it also means bullish calls should be more cautious
            if (fundamentals.PeRatio is double pe)
            {
                if (pe > 60 && winningDirection == "bullish")
                    fundamentalsMod -= 0.03; // very expensive stock, caution on bull calls
                else if (pe > 60 && winningDirection == "bearish")
                    fundamentalsMod += 0.02; // expensive stock confirms bearish thesis
                else if (pe > 0 && pe < 15)
                    fundamentalsMod += 0.01; // reasonably valued
            }

            // High short interest = caution on bullish, confirmation on bearish
            if (fundamentals.ShortPercentOfFloat is double shortPct && shortPct > 0.10)
            {
                if (winningDirection == "bullish")
                    fundamentalsMod -= 0.02; // heavy shorting against bull thesis
                else
                    fundamentalsMod += 0.02; // shorts confirm bear thesis
            }

            // High beta = less predictable outcomes
            if (fundamentals.Beta is double beta2 && beta2 > 2.0)
                fundamentalsMod -= 0.02;

            fundamentalsMod = Math.Clamp(fundamentalsMod, -0.08, 0.08);
            dataQualityFactor += fundamentalsMod;

            if (fundamentalsMod > 0.005)
                debugSignals.Add($"Confidence: fundamentals boost +{fundamentalsMod:F2} (growth/profitability/valuation)");
            else if (fundamentalsMod < -0.005)
                debugSignals.Add($"Confidence: fundamentals drag {fundamentalsMod:F2} (valuation/short interest/beta risk)");
        }

        // Weighted confirmation: predictive signals (trend, momentum, market_context)
        // count fully toward alignment; noise signals count minimally.
        // This prevents noise-signal agreement from inflating confidence.
        var predictiveKinds = new HashSet<EvaluatorKind>
        {
            EvaluatorKind.trend,
            EvaluatorKind.momentum,
            EvaluatorKind.market_context,
        };
        double weightedAligned = 0, weightedConflicting = 0;
        bool winIsBullish = winningDirection == "bullish";
        foreach (var (kind, output) in aggregate.Outputs)
        {
            if (!output.ParticipatesInConfirmation) continue;
            var net = output.BullishContribution - output.BearishContribution;
            if (Math.Abs(net) < 1) continue;
            double signalWeight = predictiveKinds.Contains(kind) ? 1.0 : 0.15;
            bool bucketVotesBullish = net > 0;
            if (bucketVotesBullish == winIsBullish)
                weightedAligned += signalWeight;
            else
                weightedConflicting += signalWeight;
        }

        double confirmMult = weightedAligned switch
        {
            >= 2.5 => 1.25,  // 2-3 predictive signals aligned
            >= 1.8 => 1.15,  // 2 predictive signals aligned
            >= 1.0 => 1.05,  // 1 predictive signal aligned
            _ => 1.00,       // only noise signals aligned
        };
        confirmMult -= weightedConflicting * 0.15;
        confirmMult = Math.Clamp(confirmMult, 0.75, 1.25);

        var weights = context.LearningData.Weights;

        // ── Learned synergy boost ──────────────────────────────────────
        // When two evaluators both contribute strongly to the same direction,
        // look up the historical pair synergy score from scoring_weight_overrides
        // (written by LearningEngine). Positive synergy = proven profitable pair.
        // Negative synergy = historically bad pair. Apply as confidence multiplier.
        double synergyMult = 1.0;
        {
            var activeKinds = new List<string>();
            foreach (var (kind, output) in aggregate.Outputs)
            {
                var net = output.BullishContribution - output.BearishContribution;
                bool kindVotesBullish = net > 0;
                // Signal must contribute at least 8 points and align with winning direction
                if (Math.Abs(net) >= 8 && kindVotesBullish == winIsBullish)
                    activeKinds.Add(kind.ToString());
            }

            if (activeKinds.Count >= 2)
            {
                double totalSynergyScore = 0;
                int pairsFound = 0;
                for (int i = 0; i < activeKinds.Count; i++)
                {
                    for (int j = i + 1; j < activeKinds.Count; j++)
                    {
                        // Look up both orderings since we don't know which the learning engine used
                        var key1 = $"synergy_pair_{activeKinds[i]}_{activeKinds[j]}";
                        var key2 = $"synergy_pair_{activeKinds[j]}_{activeKinds[i]}";
                        if (weights.TryGetValue(key1, out var score1))
                        {
                            totalSynergyScore += score1;
                            pairsFound++;
                        }
                        else if (weights.TryGetValue(key2, out var score2))
                        {
                            totalSynergyScore += score2;
                            pairsFound++;
                        }
                    }
                }

                if (pairsFound > 0)
                {
                    var avgSynergy = totalSynergyScore / pairsFound;
                    // Scale: +10% synergy → 1.05 multiplier. -10% synergy → 0.95 multiplier.
                    // Capped at ±8% confidence adjustment to avoid wild swings.
                    synergyMult = Math.Clamp(1.0 + avgSynergy * 0.005, 0.92, 1.08);
                    if (Math.Abs(synergyMult - 1.0) > 0.005)
                        debugSignals.Add($"Confidence: synergy {(synergyMult > 1 ? "boost" : "penalty")} {synergyMult:F3} — {pairsFound} active pair(s), avg synergy {avgSynergy:+0.0;-0.0}%");
                }
            }
        }

        var riskAdj = 1.0 - Math.Min(Math.Abs(riskAssessment.RiskPenalty), 30) / 100.0;
        var calFactor = context.LearningData.CalibrationFactor;

        var winningScore = Math.Max(aggregate.BullishScore, aggregate.BearishScore);
        var losingScore = Math.Min(aggregate.BullishScore, aggregate.BearishScore);
        var scoreSum = winningScore + losingScore;
        var decisionMargin = scoreSum > 0 ? (winningScore - losingScore) / scoreSum : 0.0;

        double oppositionPenalty = 1.0;
        if (decisionMargin < 0.48)
            oppositionPenalty = Math.Max(0.6, 0.6 + (decisionMargin / 0.48) * 0.4);

        // ── Regime-aware confidence penalty (learned from pattern detection) ──
        // Pattern detection identifies which market regimes produce the most failures
        // and writes penalty multipliers to scoring_weight_overrides. Detect current
        // regime from market_context scores and apply the corresponding penalty.
        double regimePenalty = 1.0;
        var mktBull = market.BullishContribution;
        var mktBear = market.BearishContribution;
        var mktDiff = Math.Abs(mktBull - mktBear);

        if (mktDiff < 5) // sideways: bull and bear scores within 5 points
        {
            regimePenalty = Math.Clamp(weights.GetValueOrDefault("regime_sideways_penalty", 1.0), 0.70, 1.0);
        }
        else if (mktBull > mktBear) // bull market
        {
            regimePenalty = Math.Clamp(weights.GetValueOrDefault("regime_bull_penalty", 1.0), 0.70, 1.0);
        }
        else // bear market
        {
            regimePenalty = Math.Clamp(weights.GetValueOrDefault("regime_bear_penalty", 1.0), 0.70, 1.0);
        }

        // Overconfidence penalty: learned cap when high-confidence predictions fail disproportionately
        var overconfidencePenalty = Math.Clamp(weights.GetValueOrDefault("regime_overconfidence_penalty", 1.0), 0.70, 1.0);

        var rawConfidence = winningScore * dataQualityFactor * confirmMult * riskAdj * calFactor * oppositionPenalty * regimePenalty * synergyMult;

        // ── Bearish mean-reversion trap penalty ──
        // Data: strong trend + strong momentum bearish = only 38% accuracy, +4.29% avg move
        // against. When both evaluators strongly agree on bearish, the drop has likely
        // already happened — we're chasing the end of the move, not the beginning.
        if (winningDirection == "bearish"
            && trend.BearishContribution >= 15
            && momentum.BearishContribution >= 12)
        {
            var trapPenalty = 0.80; // 20% confidence reduction
            rawConfidence *= trapPenalty;
            debugSignals.Add($"Confidence: bearish mean-reversion trap penalty {trapPenalty:F2} — trend bear={trend.BearishContribution:F0}, momentum bear={momentum.BearishContribution:F0}");
        }

        string? capReason = null;
        if (regimePenalty < 0.99)
        {
            var regimeLabel = mktDiff < 5 ? "sideways" : mktBull > mktBear ? "bull" : "bear";
            capReason = $"Regime penalty {regimePenalty:F2} ({regimeLabel} market, learned from failure patterns)";
        }
        if (context.Indicators.IndicatorsComputed.Count <= 3)
        {
            rawConfidence = Math.Min(rawConfidence, 45);
            capReason = "Only one signal bucket available";
        }
        if (trend.BullishContribution > 5 && trend.BearishContribution > 5
            && momentum.BullishContribution > 5 && momentum.BearishContribution > 5)
        {
            rawConfidence = Math.Min(rawConfidence, 60);
            capReason = "Trend and momentum conflict";
        }
        if (market.BearishContribution > 5 && winningDirection == "bullish")
        {
            rawConfidence = Math.Min(rawConfidence, 65);
            capReason = "Strong market context conflict";
        }
        if (market.BullishContribution > 5 && winningDirection == "bearish")
        {
            rawConfidence = Math.Min(rawConfidence, 65);
            capReason = "Strong market context conflict";
        }

        // ── Market Regime suppression ─────────────────────────────────
        // When MarketRegimeEngine detects a clear trend, counter-trend
        // predictions get hard-capped. This is the primary gate that
        // prevents the system from making bullish swing trades in a
        // bear market (which historically have near-zero win rates).
        var regime = context.MarketRegimeResult;
        if (regime is not null)
        {
            var primary = regime.PrimaryRegime;
            var regimeConf = regime.PrimaryConfidence;
            var regimeCapThreshold = weights.GetValueOrDefault("regime_suppress_confidence", 0.50);

            if (primary == MarketRegimeType.BearTrend && regimeConf >= regimeCapThreshold && winningDirection == "bullish")
            {
                // Bear market — cap bullish predictions aggressively
                var bearCap = (int)weights.GetValueOrDefault("regime_bear_bullish_cap", 35.0);
                rawConfidence = Math.Min(rawConfidence, bearCap);
                capReason = $"Regime suppression: BearTrend ({regimeConf:P0}) caps bullish at {bearCap}";
                debugSignals.Add($"REGIME_SUPPRESS: BearTrend ({regimeConf:P0}) → bullish capped at {bearCap}");
            }
            else if (primary == MarketRegimeType.BullTrend && regimeConf >= regimeCapThreshold && winningDirection == "bearish")
            {
                // Bull market — cap bearish predictions (less aggressive, bearish shorts can still work)
                var bullCap = (int)weights.GetValueOrDefault("regime_bull_bearish_cap", 45.0);
                rawConfidence = Math.Min(rawConfidence, bullCap);
                capReason = $"Regime suppression: BullTrend ({regimeConf:P0}) caps bearish at {bullCap}";
                debugSignals.Add($"REGIME_SUPPRESS: BullTrend ({regimeConf:P0}) → bearish capped at {bullCap}");
            }
            else if (primary == MarketRegimeType.HighVolatility && regimeConf >= regimeCapThreshold)
            {
                // High vol regime — cap all predictions to avoid noise trades
                var volCap = (int)weights.GetValueOrDefault("regime_high_vol_cap", 50.0);
                rawConfidence = Math.Min(rawConfidence, volCap);
                capReason ??= $"Regime suppression: HighVolatility ({regimeConf:P0}) caps at {volCap}";
                debugSignals.Add($"REGIME_SUPPRESS: HighVolatility ({regimeConf:P0}) → capped at {volCap}");
            }
        }

        // ── Liquidity / coverage penalty ─────────────────────────────
        // Stocks with thin data, no analyst coverage, low volume, or
        // micro-cap status should not receive high confidence. The FABP
        // problem: trend technicals on a $50M OTC bank with 500 shares/day
        // volume produced confidence 64. That's fake signal.
        double liquidityPenalty = 1.0;
        {
            var snapshot = context.Snapshot;
            int liquidityFlags = 0;

            // 1. No fundamentals data at all = no analyst coverage
            if (snapshot.Fundamentals is null)
                liquidityFlags += 2;

            // 2. Micro/nano-cap: market cap under $500M
            if (snapshot.Fundamentals?.MarketCap is long mktCap)
            {
                if (mktCap < 50_000_000)       // nano-cap < $50M
                    liquidityFlags += 3;
                else if (mktCap < 300_000_000)  // micro-cap < $300M
                    liquidityFlags += 2;
                else if (mktCap < 500_000_000)  // small-cap < $500M
                    liquidityFlags += 1;
            }

            // 3. Low average volume across recent bars
            if (snapshot.RecentBars.Count > 0)
            {
                var avgVol = snapshot.RecentBars.Average(b => b.Volume);
                if (avgVol < 50_000)        // extremely thin
                    liquidityFlags += 3;
                else if (avgVol < 200_000)  // low liquidity
                    liquidityFlags += 1;
            }
            else
            {
                liquidityFlags += 2; // no bars at all
            }

            // 4. No news coverage
            if (snapshot.NewsContext.Count == 0)
                liquidityFlags += 1;

            // 5. Market data unavailable on this run
            if (!snapshot.DataAvailability.MarketDataAvailable)
                liquidityFlags += 2;

            // Apply graduated penalty based on flag count
            if (liquidityFlags >= 6)
            {
                liquidityPenalty = 0.65; // severe: OTC micro-cap with no data
                rawConfidence *= liquidityPenalty;
                debugSignals.Add($"Confidence: liquidity penalty {liquidityPenalty:F2} — thin stock ({liquidityFlags} flags)");
            }
            else if (liquidityFlags >= 4)
            {
                liquidityPenalty = 0.78;
                rawConfidence *= liquidityPenalty;
                debugSignals.Add($"Confidence: liquidity penalty {liquidityPenalty:F2} — low coverage ({liquidityFlags} flags)");
            }
            else if (liquidityFlags >= 2)
            {
                liquidityPenalty = 0.90;
                rawConfidence *= liquidityPenalty;
                debugSignals.Add($"Confidence: liquidity drag {liquidityPenalty:F2} — limited data ({liquidityFlags} flags)");
            }

            // Hard cap for truly thin stocks regardless of other signals
            if (liquidityFlags >= 6 && rawConfidence > 40)
            {
                rawConfidence = 40;
                capReason = $"Liquidity cap: thin/illiquid stock ({liquidityFlags} coverage flags)";
            }
            else if (liquidityFlags >= 4 && rawConfidence > 50)
            {
                rawConfidence = 50;
                capReason = $"Liquidity cap: low coverage stock ({liquidityFlags} coverage flags)";
            }
        }

        var maxCap = weights.GetValueOrDefault("max_confidence_cap", 85.0);
        int confidence = (int)Math.Round(Math.Clamp(rawConfidence, 0, maxCap));

        // Apply overconfidence penalty only to predictions that exceed the cap
        // (avoids double-taxing predictions already clamped by max_confidence_cap)
        if (overconfidencePenalty < 1.0 && rawConfidence > maxCap)
        {
            confidence = (int)Math.Round(confidence * overconfidencePenalty);
            capReason ??= $"Overconfidence penalty {overconfidencePenalty:F2} (raw {rawConfidence:F0} exceeded cap {maxCap})";
        }

        bool clearDirection = decisionMargin > 0.54;
        var riskCapBoost = context.LearningData.RiskCapBoost;

        if (riskAssessment.RiskScore >= 75)
        {
            int cap = (clearDirection ? 50 : 35) + riskCapBoost;
            cap = Math.Min(cap, 70);
            confidence = Math.Min(confidence, cap);
            capReason ??= $"Risk {riskAssessment.RiskScore} ≥ 75 (dir {(clearDirection ? "clear" : "mixed")}, boost {riskCapBoost})";
        }
        else if (riskAssessment.RiskScore >= 60)
        {
            int cap = (clearDirection ? 60 : 50) + riskCapBoost;
            cap = Math.Min(cap, 75);
            confidence = Math.Min(confidence, cap);
            capReason ??= $"Risk {riskAssessment.RiskScore} ≥ 60 (dir {(clearDirection ? "clear" : "mixed")}, boost {riskCapBoost})";
        }
        else if (riskAssessment.RiskScore >= 50)
        {
            int cap = (clearDirection ? 70 : 65) + riskCapBoost;
            cap = Math.Min(cap, 80);
            confidence = Math.Min(confidence, cap);
            capReason ??= $"Risk {riskAssessment.RiskScore} ≥ 50 (dir {(clearDirection ? "clear" : "mixed")}, boost {riskCapBoost})";
        }

        if (riskAssessment.EarningsNear)
        {
            confidence = Math.Min(confidence, 45);
            capReason = "Earnings within 3 days — binary event";
        }

        // ── Historical volatility calibration (Phase 3) ──────────────
        // High historical volatility = wider expected ranges = cap confidence
        // on directional calls since outcomes are less predictable.
        // This does NOT replace live ATR — it provides historical context.
        if (ru.HistoricalVolatility is double histVol && histVol > 0)
        {
            if (histVol > 60 && confidence > 55)
            {
                // Extremely volatile stock — cap confidence
                confidence = Math.Min(confidence, 55);
                capReason ??= $"Historical volatility {histVol:F0}% — highly unpredictable";
            }
            else if (histVol > 40 && confidence > 65)
            {
                confidence = Math.Min(confidence, 65);
                capReason ??= $"Historical volatility {histVol:F0}% — elevated unpredictability";
            }
        }

        return new ConfidenceResult
        {
            Confidence = confidence,
            DataQualityFactor = dataQualityFactor,
            ConfirmationMultiplier = confirmMult,
            RiskAdjustment = riskAdj,
            CalibrationFactor = calFactor,
            OppositionPenalty = oppositionPenalty,
            RegimePenalty = regimePenalty * overconfidencePenalty,
            LiquidityPenalty = liquidityPenalty,
            DecisionMargin = decisionMargin,
            ClearDirection = clearDirection,
            ConfidenceCap = capReason,
            DebugSignals = debugSignals,
        };
    }
}
