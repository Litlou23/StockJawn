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
            EvaluatorKind.catalyst,
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

        // DB-configurable confirmation multiplier max — default 1.12 (was hardcoded 1.25).
        // Data shows 70+ confidence band (driven by max confirmMult 1.25) had 46.7% accuracy
        // while 50-59 band had 60% accuracy. The compound inflation from confirmMult × calFactor
        // × dataQuality was pushing "everything agrees" stocks (that already moved) too high.
        var maxConfirmMult = Math.Clamp(
            context.LearningData.Weights.GetValueOrDefault("confirmation_multiplier_max", 1.12),
            0.80, 1.25);
        var boostRange = maxConfirmMult - 1.0;

        double confirmMult = weightedAligned switch
        {
            >= 2.5 => maxConfirmMult,                    // 2-3 predictive signals aligned → full boost
            >= 1.8 => 1.0 + boostRange * 0.60,           // 2 predictive signals aligned → 60% of boost
            >= 1.0 => 1.0 + boostRange * 0.20,           // 1 predictive signal aligned → 20% of boost
            _ => 1.00,                                    // only noise signals aligned
        };
        confirmMult -= weightedConflicting * 0.15;
        confirmMult = Math.Clamp(confirmMult, 0.75, maxConfirmMult);

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

        var rawWinning = Math.Max(aggregate.BullishScore, aggregate.BearishScore);
        var losingScore = Math.Min(aggregate.BullishScore, aggregate.BearishScore);
        var scoreSum = rawWinning + losingScore;
        var decisionMargin = scoreSum > 0 ? (rawWinning - losingScore) / scoreSum : 0.0;

        // ── Diminishing returns on directional score ──────────────────
        // Data proves: scores above ~45 DON'T correlate with better accuracy.
        // Bull 62 / Bear 17 → 34.7% accuracy. Bull 39 / Bear 26 → 51.6%.
        // High one-sided scores mean "found lots of agreeing evidence" not
        // "this prediction is more likely correct." Cap the contribution.
        var sweetSpot = weights.GetValueOrDefault("confidence_sweet_spot", 45.0);
        var diminishRate = Math.Clamp(weights.GetValueOrDefault("confidence_diminish_rate", 0.3), 0.1, 0.9);
        double winningScore;
        if (rawWinning <= sweetSpot)
            winningScore = rawWinning;
        else
            winningScore = sweetSpot + (rawWinning - sweetSpot) * diminishRate;

        // ── Signal contestation adjustment ────────────────────────────
        // INVERTED from old logic. Data shows contested signals (both sides
        // have meaningful scores) predict BETTER than one-sided signals.
        // When the model finds counter-evidence but the winning direction
        // still prevails, that's a MORE reliable signal, not less.
        // Only penalize true indecision where direction is genuinely unclear.
        double oppositionPenalty = 1.0;
        var contestationBonus = Math.Clamp(weights.GetValueOrDefault("contestation_bonus", 1.12), 1.0, 1.25);
        var contestationMinScore = weights.GetValueOrDefault("contestation_min_score", 15.0);
        var indecisionThreshold = weights.GetValueOrDefault("indecision_threshold", 0.15);

        if (losingScore >= contestationMinScore && decisionMargin >= indecisionThreshold)
        {
            // Healthy contestation: losing side has meaningful score but winner is clear
            // The model found evidence AGAINST the pick and still chose it → stronger signal
            var contestStrength = Math.Min(losingScore / rawWinning, 0.45);
            oppositionPenalty = 1.0 + (contestationBonus - 1.0) * (contestStrength / 0.45);
            debugSignals.Add($"Confidence: contestation bonus {oppositionPenalty:F3} — losing score {losingScore:F0} validates direction despite opposition");
        }
        else if (decisionMargin < indecisionThreshold)
        {
            // True indecision: scores too close, direction unreliable
            oppositionPenalty = Math.Max(0.6, 0.6 + (decisionMargin / indecisionThreshold) * 0.4);
            debugSignals.Add($"Confidence: indecision penalty {oppositionPenalty:F3} — margin {decisionMargin:F2} too narrow for reliable direction");
        }

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

        // ── Momentum exhaustion vs fresh momentum detection ──
        // Strong trend + strong momentum can mean two things:
        //   (a) EXHAUSTION — stock already ran, RSI overbought/oversold, near extreme → penalize
        //   (b) FRESH RUN  — breakout with room, RSI mid-range, volume confirming → reward
        // We use RSI + distance from Donchian extremes + volume to distinguish.
        // DB-configurable thresholds and penalties.
        {
            var rsi = context.Indicators.Rsi14;
            var volAssess = context.VolatilityAssessment;
            var distFromResistance = volAssess?.DistanceFromResistance; // negative = below resistance
            var distFromSupport = volAssess?.DistanceFromSupport;       // positive = above support
            var volume = outputs.ContainsKey(EvaluatorKind.volume) ? outputs[EvaluatorKind.volume] : null;

            var exhaustionPenalty = Math.Clamp(weights.GetValueOrDefault("momentum_exhaustion_penalty", 0.80), 0.5, 1.0);
            var freshBonus = Math.Clamp(weights.GetValueOrDefault("momentum_fresh_bonus", 1.08), 1.0, 1.15);
            var trendThreshold = weights.GetValueOrDefault("momentum_exhaustion_trend_threshold", 18.0);
            var momThreshold = weights.GetValueOrDefault("momentum_exhaustion_momentum_threshold", 10.0);

            // ── BULLISH direction ──
            if (winningDirection == "bullish"
                && trend.BullishContribution >= trendThreshold
                && momentum.BullishContribution >= momThreshold)
            {
                // Exhaustion signals: RSI overbought (>70), near resistance (<1% away), weak volume
                bool rsiOverbought = rsi is not null && rsi > 70;
                bool nearResistance = distFromResistance is not null && distFromResistance > -1.0; // within 1% of high
                bool weakVolume = volume is not null && volume.BullishContribution - volume.BearishContribution < 2;

                int exhaustionFlags = (rsiOverbought ? 1 : 0) + (nearResistance ? 1 : 0) + (weakVolume ? 1 : 0);

                if (exhaustionFlags >= 2)
                {
                    // Exhausted — stock is overbought, near resistance, or volume fading
                    rawConfidence *= exhaustionPenalty;
                    debugSignals.Add($"Confidence: bullish EXHAUSTION penalty {exhaustionPenalty:F2} — trend bull={trend.BullishContribution:F0}, mom bull={momentum.BullishContribution:F0}, RSI={rsi:F0}, flags={exhaustionFlags}. Chasing the top.");
                }
                else if (exhaustionFlags == 0 && rsi is not null && rsi >= 45 && rsi <= 65
                         && volume is not null && volume.BullishContribution > volume.BearishContribution)
                {
                    // Fresh momentum — RSI mid-range with volume confirmation = breakout with room
                    rawConfidence *= freshBonus;
                    debugSignals.Add($"Confidence: bullish FRESH momentum bonus {freshBonus:F2} — trend bull={trend.BullishContribution:F0}, mom bull={momentum.BullishContribution:F0}, RSI={rsi:F0}. Healthy run, not exhausted.");
                }
                else
                {
                    // Ambiguous — mild penalty (half the exhaustion penalty distance from 1.0)
                    var mildPenalty = 1.0 - (1.0 - exhaustionPenalty) * 0.5;
                    rawConfidence *= mildPenalty;
                    debugSignals.Add($"Confidence: bullish momentum caution {mildPenalty:F2} — strong signals but ambiguous exhaustion (RSI={rsi:F0}, flags={exhaustionFlags}).");
                }
            }

            // ── BEARISH direction ──
            // Mirror: strong bearish trend + momentum can be oversold bounce trap OR fresh breakdown
            if (winningDirection == "bearish"
                && trend.BearishContribution >= 15
                && momentum.BearishContribution >= 12)
            {
                bool rsiOversold = rsi is not null && rsi < 30;
                bool nearSupport = distFromSupport is not null && distFromSupport < 1.0; // within 1% of low
                bool weakVolume2 = volume is not null && volume.BearishContribution - volume.BullishContribution < 2;

                int exhaustionFlags = (rsiOversold ? 1 : 0) + (nearSupport ? 1 : 0) + (weakVolume2 ? 1 : 0);

                if (exhaustionFlags >= 2)
                {
                    // Exhausted bearish — oversold, near support, mean-reversion likely
                    rawConfidence *= exhaustionPenalty;
                    debugSignals.Add($"Confidence: bearish EXHAUSTION (mean-reversion trap) {exhaustionPenalty:F2} — trend bear={trend.BearishContribution:F0}, mom bear={momentum.BearishContribution:F0}, RSI={rsi:F0}, flags={exhaustionFlags}. Chasing the bottom.");
                }
                else if (exhaustionFlags == 0 && rsi is not null && rsi >= 35 && rsi <= 55
                         && volume is not null && volume.BearishContribution > volume.BullishContribution)
                {
                    // Fresh breakdown — RSI mid-range, volume confirming downside
                    rawConfidence *= freshBonus;
                    debugSignals.Add($"Confidence: bearish FRESH breakdown bonus {freshBonus:F2} — trend bear={trend.BearishContribution:F0}, mom bear={momentum.BearishContribution:F0}, RSI={rsi:F0}. Healthy selloff, not oversold.");
                }
                else
                {
                    // Ambiguous — original penalty
                    var trapPenalty = 0.80;
                    rawConfidence *= trapPenalty;
                    debugSignals.Add($"Confidence: bearish mean-reversion trap {trapPenalty:F2} — trend bear={trend.BearishContribution:F0}, mom bear={momentum.BearishContribution:F0}. Likely chasing.");
                }
            }
        }

        // ── Gap-chasing penalty ──
        // Data: AMAT gapped 4.47% and lost -7% to -8% across 5 trades. Entering after a
        // large gap means chasing a move that already happened. Significant/Large/Extreme
        // gaps in the winning direction get penalized.
        // DB-configurable via gap_chase_penalty (default 0.85) and gap_chase_threshold (default 3.0%).
        {
            var volAssessment = context.VolatilityAssessment;
            if (volAssessment?.GapPercent is double gapPct && gapPct > 0)
            {
                var gapThreshold = weights.GetValueOrDefault("gap_chase_threshold", 3.0);
                var gapPenalty = Math.Clamp(weights.GetValueOrDefault("gap_chase_penalty", 0.85), 0.5, 1.0);

                // Gap up + bullish = chasing upward move. Gap down + bearish = chasing downward move.
                // GapPercent is signed: positive = gap up, negative = gap down.
                var gapDirectionMatchesPrediction =
                    (volAssessment.GapClassification >= GapType.Significant)
                    && ((winningDirection == "bullish" && gapPct >= gapThreshold)
                     || (winningDirection == "bearish" && gapPct <= -gapThreshold));

                if (gapDirectionMatchesPrediction)
                {
                    rawConfidence *= gapPenalty;
                    debugSignals.Add($"Confidence: gap-chasing penalty {gapPenalty:F2} — gap {gapPct:F1}% in predicted direction. Entry is chasing the move.");
                }
            }
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

        // ── Piecewise calibration (simplified isotonic regression) ──────
        // Research shows isotonic regression outperforms single-factor
        // calibration by ~22% ECE improvement. This is a simplified version:
        // different confidence bands get different correction multipliers
        // learned from historical accuracy data. DB-configurable so the
        // LearningEngine can update these as accuracy data accumulates.
        // Default 1.0 = no adjustment until we have enough data to tune.
        {
            double bandCal;
            if (rawConfidence < 30)
                bandCal = Math.Clamp(weights.GetValueOrDefault("cal_band_under_30", 1.0), 0.7, 1.3);
            else if (rawConfidence < 45)
                bandCal = Math.Clamp(weights.GetValueOrDefault("cal_band_30_45", 1.0), 0.7, 1.3);
            else if (rawConfidence < 55)
                bandCal = Math.Clamp(weights.GetValueOrDefault("cal_band_45_55", 1.0), 0.7, 1.3);
            else
                bandCal = Math.Clamp(weights.GetValueOrDefault("cal_band_55_plus", 0.92), 0.7, 1.3);

            if (Math.Abs(bandCal - 1.0) > 0.005)
            {
                rawConfidence *= bandCal;
                debugSignals.Add($"Confidence: piecewise calibration {bandCal:F2} for band (raw was {rawConfidence / bandCal:F0})");
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
            // Earnings are binary events but also the biggest movers.
            // Cap at 65 instead of 45 — still cautious, but doesn't kill
            // post-earnings momentum plays where the catalyst already fired.
            confidence = Math.Min(confidence, 65);
            capReason = "Earnings within 3 days — binary event (cap 65)";
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
