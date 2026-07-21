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

        double confirmMult = aggregate.AlignedBuckets switch
        {
            >= 5 => 1.30,
            4 => 1.20,
            3 => 1.10,
            _ => 1.00,
        };
        confirmMult -= aggregate.ConflictingBuckets * 0.10;
        confirmMult = Math.Clamp(confirmMult, 0.75, 1.30);

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
        var weights = context.LearningData.Weights;

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

        var rawConfidence = winningScore * dataQualityFactor * confirmMult * riskAdj * calFactor * oppositionPenalty * regimePenalty;

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

        var maxCap = weights.GetValueOrDefault("max_confidence_cap", 85.0);
        int confidence = (int)Math.Round(Math.Clamp(rawConfidence, 0, maxCap));

        // Apply overconfidence penalty to high-confidence predictions only
        if (overconfidencePenalty < 1.0 && confidence >= 60)
        {
            confidence = (int)Math.Round(confidence * overconfidencePenalty);
            capReason ??= $"Overconfidence penalty {overconfidencePenalty:F2} (learned from high-conf failures)";
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
            DecisionMargin = decisionMargin,
            ClearDirection = clearDirection,
            ConfidenceCap = capReason,
            DebugSignals = debugSignals,
        };
    }
}
