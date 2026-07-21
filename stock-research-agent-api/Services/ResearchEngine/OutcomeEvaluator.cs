using System.Text.Json;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Evidence;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Evaluates open predictions against current market data.
/// Fetches real prices from Twelve Data. If data is unavailable,
/// predictions stay open -- never fakes outcomes.
/// </summary>
public class OutcomeEvaluator
{
    private readonly MarketDataService _marketData;
    private readonly ResearchRepository _repo;
    private readonly IEvidenceService _evidence;
    private readonly ILogger<OutcomeEvaluator> _logger;

    public OutcomeEvaluator(
        MarketDataService marketData,
        ResearchRepository repo,
        IEvidenceService evidence,
        ILogger<OutcomeEvaluator> logger)
    {
        _marketData = marketData;
        _repo = repo;
        _evidence = evidence;
        _logger = logger;
    }

    public record EvaluationResult(
        string PredictionId,
        string Ticker,
        PredictionOutcome Outcome,
        bool Saved);

    public async Task<EvaluationResult?> EvaluatePredictionAsync(PredictionCandidate prediction)
    {
        if (!PredictionCategoryHelper.IsDirectional(prediction.PredictionType))
            return null;

        if (prediction.EntryReferencePrice is null || prediction.EntryReferencePrice == 0)
        {
            _logger.LogWarning("[outcome-evaluator] {Ticker}: no entry reference price, cannot evaluate", prediction.Ticker);
            return null;
        }

        var quote = await _marketData.GetQuoteWithFallbackAsync(prediction.Ticker);
        if (quote is null)
        {
            _logger.LogWarning("[outcome-evaluator] {Ticker}: market data unavailable (quote + bar fallback both failed), skipping", prediction.Ticker);
            return null;
        }

        var startPrice = prediction.EntryReferencePrice.Value;
        var closePrice = quote.Price;
        var percentMove = ((closePrice - startPrice) / startPrice) * 100;

        // SPY relative performance for short-term picks
        double? relativePerformance = null;
        if (PredictionTimeWindows.ShortTerm.Contains(prediction.TimeWindow))
        {
            var spyQuote = await _marketData.GetQuoteWithFallbackAsync("SPY");
            if (spyQuote is not null && spyQuote.PreviousClose > 0)
            {
                var spyMove = ((spyQuote.Price - spyQuote.PreviousClose) / spyQuote.PreviousClose) * 100;
                relativePerformance = Math.Round(percentMove - spyMove, 2);
            }
        }

        bool? directionCorrect = prediction.PredictionType switch
        {
            PredictionType.bullish => percentMove > 0,
            PredictionType.bearish => percentMove < 0,
            _ => null,
        };

        var invalidationHit = (prediction.PredictionType == PredictionType.bullish && percentMove < -2)
            || (prediction.PredictionType == PredictionType.bearish && percentMove > 2);

        var maxFavorable = prediction.PredictionType == PredictionType.bullish
            ? ((quote.High - startPrice) / startPrice) * 100
            : ((startPrice - quote.Low) / startPrice) * 100;
        var maxAdverse = prediction.PredictionType == PredictionType.bullish
            ? ((startPrice - quote.Low) / startPrice) * 100
            : ((quote.High - startPrice) / startPrice) * 100;

        bool? targetHit = null;
        bool? stopHit = null;
        if (prediction.TargetPrice is double tp and > 0)
        {
            targetHit = prediction.PredictionType == PredictionType.bullish
                ? quote.High >= tp
                : quote.Low <= tp;
        }
        if (prediction.StopPrice is double sp and > 0)
        {
            stopHit = prediction.PredictionType == PredictionType.bullish
                ? quote.Low <= sp
                : quote.High >= sp;
        }

        // Price accuracy: how close was the predicted price to the actual close?
        double? priceAccuracyPercent = null;
        double? pricePredictionErrorPercent = null;
        if (prediction.PredictedPrice is double predPrice and > 0)
        {
            var priceError = Math.Abs(closePrice - predPrice);
            priceAccuracyPercent = Math.Round(Math.Max(0, 100 - (priceError / startPrice * 100)), 2);
            pricePredictionErrorPercent = Math.Round((priceError / startPrice) * 100, 2);
        }

        // Projected zone evaluation
        bool? wasInProjectedZone = null;
        bool? invalidationHitCheck = null;
        if (prediction.ProjectedPriceLow is double zoneLow && prediction.ProjectedPriceHigh is double zoneHigh)
        {
            wasInProjectedZone = closePrice >= zoneLow && closePrice <= zoneHigh;
        }
        if (prediction.InvalidationPrice is double invPrice and > 0)
        {
            invalidationHitCheck = prediction.PredictionType == PredictionType.bullish
                ? quote.Low <= invPrice
                : quote.High >= invPrice;
        }

        double outcomeScore = 50;
        if (directionCorrect == true)
            outcomeScore += Math.Min(Math.Abs(percentMove) * 10, 40);
        else if (directionCorrect == false)
            outcomeScore -= Math.Min(Math.Abs(percentMove) * 10, 40);
        if (priceAccuracyPercent is double pa)
            outcomeScore += (pa - 95) * 2;
        if (wasInProjectedZone == true) outcomeScore += 5;
        if (targetHit == true) outcomeScore += 5;
        if (stopHit == true) outcomeScore -= 10;
        if (invalidationHit || invalidationHitCheck == true) outcomeScore -= 10;
        outcomeScore = Math.Clamp(outcomeScore, 0, 100);

        var lesson = GenerateLesson(prediction, percentMove, directionCorrect, invalidationHit,
            targetHit, stopHit, Math.Round(maxFavorable, 2), Math.Round(maxAdverse, 2),
            prediction.RiskRewardRatio);

        var summaryParts = new List<string>
        {
            $"{prediction.Ticker}: {prediction.PredictionType} prediction.",
            $"Entry ${startPrice:F2}, current ${closePrice:F2} ({(percentMove > 0 ? "+" : "")}{percentMove:F2}%).",
            $"Direction {(directionCorrect == true ? "correct" : directionCorrect == false ? "wrong" : "N/A")}.",
        };
        if (prediction.ProjectedPriceLow is double zl && prediction.ProjectedPriceHigh is double zh)
            summaryParts.Add($"Projected zone ${zl:F2}–${zh:F2}, actual ${closePrice:F2} ({(wasInProjectedZone == true ? "IN zone" : "OUTSIDE zone")}).");
        if (prediction.PredictedPrice is double pp)
            summaryParts.Add($"Predicted ${pp:F2}, actual ${closePrice:F2} ({priceAccuracyPercent:F1}% accurate).");
        if (prediction.TargetPrice is double tgt)
            summaryParts.Add($"Target ${tgt:F2} {(targetHit == true ? "HIT" : "not reached")}.");
        if (prediction.StopPrice is double stp)
            summaryParts.Add($"Stop ${stp:F2} {(stopHit == true ? "TRIGGERED" : "held")}.");
        summaryParts.Add($"Max favorable: {maxFavorable:F2}%, max adverse: {maxAdverse:F2}%.");
        if (relativePerformance is not null)
            summaryParts.Add($"vs SPY: {(relativePerformance > 0 ? "+" : "")}{relativePerformance}%.");

        var outcomeData = new
        {
            prediction_id = prediction.Id,
            evaluation_time = DateTimeOffset.UtcNow.ToString("o"),
            start_price = startPrice,
            close_price = closePrice,
            high_after_prediction = quote.High,
            low_after_prediction = quote.Low,
            percent_move = Math.Round(percentMove, 2),
            direction_correct = directionCorrect,
            predicted_direction = prediction.WinningDirection,
            bullish_score_at_prediction = prediction.BullishScore,
            bearish_score_at_prediction = prediction.BearishScore,
            predicted_price = prediction.PredictedPrice,
            predicted_move_percent = prediction.PredictedMovePercent,
            projected_price_low = prediction.ProjectedPriceLow,
            projected_price_high = prediction.ProjectedPriceHigh,
            price_accuracy_percent = priceAccuracyPercent,
            price_prediction_error_percent = pricePredictionErrorPercent,
            was_in_projected_zone = wasInProjectedZone,
            target_hit = targetHit,
            stop_hit = stopHit,
            invalidation_hit = invalidationHit || invalidationHitCheck == true,
            max_favorable_percent = Math.Round(maxFavorable, 2),
            max_adverse_percent = Math.Round(maxAdverse, 2),
            outcome_score = outcomeScore,
            outcome_summary = string.Join(" ", summaryParts),
            lesson,
        };

        await _repo.SaveOutcomeAsync(outcomeData);
        await _repo.UpdatePredictionStatusAsync(prediction.Id, "evaluated");

        var outcome = new PredictionOutcome
        {
            PredictionId = prediction.Id,
            EvaluationTime = DateTimeOffset.UtcNow,
            StartPrice = startPrice,
            ClosePrice = closePrice,
            HighAfterPrediction = quote.High,
            LowAfterPrediction = quote.Low,
            PercentMove = Math.Round(percentMove, 2),
            DirectionCorrect = directionCorrect,
            PredictedPrice = prediction.PredictedPrice,
            PredictedMovePercent = prediction.PredictedMovePercent,
            ProjectedPriceLow = prediction.ProjectedPriceLow,
            ProjectedPriceHigh = prediction.ProjectedPriceHigh,
            PriceAccuracyPercent = priceAccuracyPercent,
            PricePredictionErrorPercent = pricePredictionErrorPercent,
            WasInProjectedZone = wasInProjectedZone,
            TargetHit = targetHit,
            StopHit = stopHit,
            InvalidationHit = invalidationHit || invalidationHitCheck == true,
            MaxFavorablePercent = Math.Round(maxFavorable, 2),
            MaxAdversePercent = Math.Round(maxAdverse, 2),
            OutcomeScore = outcomeScore,
            OutcomeSummary = outcomeData.outcome_summary,
            Lesson = lesson,
        };

        // Volatility learning — non-blocking, best-effort
        _ = CreateVolatilityLearningRecordAsync(
            prediction, outcome, startPrice,
            Math.Round(maxFavorable, 2), Math.Round(maxAdverse, 2));

        return new EvaluationResult(prediction.Id, prediction.Ticker, outcome, true);
    }

    public async Task<(List<EvaluationResult> Evaluated, List<string> Skipped, List<string> Errors)>
        EvaluateOpenPredictionsAsync()
    {
        var openPredictions = await _repo.GetOpenPredictionsAsync();
        var evaluated = new List<EvaluationResult>();
        var skipped = new List<string>();
        var errors = new List<string>();

        _logger.LogInformation("[outcome-evaluator] Found {Count} open predictions to evaluate", openPredictions.Count);

        var now = DateTimeOffset.UtcNow;
        foreach (var prediction in openPredictions)
        {
            // Non-directional predictions: leave neutral types (watch_only,
            // neutral_no_edge, neutral_high_volatility, neutral_range_bound)
            // for NeutralOutcomeEvaluator which does proper timeframe-gated
            // evaluation. Expire truly unevaluable types (rejected, unavailable).
            if (!PredictionCategoryHelper.IsDirectional(prediction.PredictionType))
            {
                if (prediction.PredictionType == PredictionType.watch_only
                    || prediction.PredictionType == PredictionType.neutral_no_edge
                    || prediction.PredictionType == PredictionType.neutral_high_volatility
                    || prediction.PredictionType == PredictionType.neutral_range_bound
                    || prediction.PredictionType == PredictionType.neutral)
                {
                    // Handled by NeutralOutcomeEvaluator in EOD Step 6 —
                    // skip here so they stay open until the proper time window elapses.
                    skipped.Add($"{prediction.Ticker}: neutral ({prediction.PredictionType}), deferred to neutral evaluator");
                    continue;
                }

                await _repo.UpdatePredictionStatusAsync(prediction.Id, "expired");
                skipped.Add($"{prediction.Ticker}: scan result ({prediction.PredictionType}), not evaluable");
                continue;
            }

            var ageHours = (now - prediction.CreatedAt).TotalHours;

            var minHours = prediction.TimeWindow switch
            {
                "intraday" => 4,
                "1_day" => 24,   // full trading day close-to-close
                "3_day" => 48,
                "1_week" => 120,
                "1_month" => 504,    // 21 days
                "3_month" => 1512,   // 63 days
                "6_month" => 3024,   // 126 days
                "1_year" => 6048,    // 252 days
                _ => 6,
            };

            var maxHours = PredictionTimeWindows.LongTerm.Contains(prediction.TimeWindow)
                ? minHours * 2
                : 240;

            if (ageHours < minHours)
            {
                skipped.Add($"{prediction.Ticker}: too early ({ageHours:F1}h < {minHours}h for {prediction.TimeWindow})");
                continue;
            }

            if (ageHours > maxHours)
            {
                await _repo.UpdatePredictionStatusAsync(prediction.Id, "expired");
                skipped.Add($"{prediction.Ticker}: expired ({ageHours:F0}h old)");
                continue;
            }

            try
            {
                var result = await EvaluatePredictionAsync(prediction);
                if (result is not null) evaluated.Add(result);
                else skipped.Add($"{prediction.Ticker}: could not evaluate (missing data)");
            }
            catch (Exception ex)
            {
                errors.Add($"{prediction.Ticker}: {ex.Message}");
            }
        }

        _logger.LogInformation("[outcome-evaluator] Evaluated {Eval}, skipped {Skip}, errors {Err}",
            evaluated.Count, skipped.Count, errors.Count);

        // Record evidence from evaluated outcomes (non-blocking).
        try
        {
            var evidenceRecords = new List<EvidenceRecord>();
            foreach (var result in evaluated)
            {
                var outcome = result.Outcome;
                var weight = outcome.DirectionCorrect == true ? 0.5
                           : outcome.DirectionCorrect == false ? -0.5 : 0.0;
                // Boost weight for large moves
                if (Math.Abs(outcome.PercentMove ?? 0) > 3) weight *= 1.5;

                evidenceRecords.Add(new EvidenceRecord
                {
                    Ticker = result.Ticker,
                    EvidenceType = EvidenceType.Research,
                    Source = "outcome-evaluation",
                    Weight = Math.Clamp(weight, -1.0, 1.0),
                    Importance = (int)Math.Clamp(outcome.OutcomeScore ?? 50, 1, 100),
                    Summary = (outcome.OutcomeSummary ?? "")[..Math.Min(300, (outcome.OutcomeSummary ?? "").Length)],
                    RelatedEventId = result.PredictionId,
                });
            }
            if (evidenceRecords.Count > 0)
            {
                var recorded = await _evidence.RecordManyAsync(evidenceRecords);
                _logger.LogInformation("[outcome-evaluator] Recorded {Count} evidence items from outcomes", recorded);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[outcome-evaluator] Evidence recording failed (non-blocking)");
        }

        return (evaluated, skipped, errors);
    }

    // -----------------------------------------------------------------------
    // Watch-only / abstention evaluation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluates whether the decision to abstain (watch_only / neutral_no_edge /
    /// neutral_high_volatility) was correct. Tracks missed alpha and whether
    /// each guardrail that caused the downgrade was justified.
    /// </summary>
    private async Task<EvaluationResult?> EvaluateAbstentionAsync(PredictionCandidate prediction)
    {
        if (prediction.EntryReferencePrice is null || prediction.EntryReferencePrice == 0)
        {
            _logger.LogWarning("[outcome-evaluator] {Ticker} (watch): no entry price, expiring", prediction.Ticker);
            await _repo.UpdatePredictionStatusAsync(prediction.Id, "expired");
            return null;
        }

        var quote = await _marketData.GetQuoteWithFallbackAsync(prediction.Ticker);
        if (quote is null)
        {
            _logger.LogWarning("[outcome-evaluator] {Ticker} (watch): market data unavailable (quote + bar fallback both failed)", prediction.Ticker);
            return null;
        }

        var startPrice = prediction.EntryReferencePrice.Value;
        var closePrice = quote.Price;
        var percentMove = ((closePrice - startPrice) / startPrice) * 100;
        var absMove = Math.Abs(percentMove);

        // Determine the original directional lean
        var originalDirection = prediction.WinningDirection;
        if (string.IsNullOrEmpty(originalDirection))
        {
            // Infer from bullish/bearish scores if winning_direction wasn't persisted
            if (prediction.BullishScore is double bs && prediction.BearishScore is double brs)
                originalDirection = bs >= brs ? "bullish" : "bearish";
            else
                originalDirection = percentMove > 0 ? "bullish" : "bearish"; // last resort: hindsight
        }

        // Was the original lean correct?
        bool? directionWouldHaveBeenCorrect = originalDirection switch
        {
            "bullish" => percentMove > 0,
            "bearish" => percentMove < 0,
            _ => null,
        };

        // Missed alpha: how much return did we leave on the table?
        var missedAlpha = directionWouldHaveBeenCorrect == true ? absMove : 0;

        // Max favorable move in the lean direction
        var maxFavorable = originalDirection == "bullish"
            ? ((quote.High - startPrice) / startPrice) * 100
            : ((startPrice - quote.Low) / startPrice) * 100;
        var maxAdverse = originalDirection == "bullish"
            ? ((startPrice - quote.Low) / startPrice) * 100
            : ((quote.High - startPrice) / startPrice) * 100;

        // Was abstaining the correct decision?
        // Correct if: the stock didn't move significantly OR moved against the lean
        // Wrong if: the stock moved >2% in the direction we would have called
        bool abstentionCorrect;
        if (directionWouldHaveBeenCorrect == true && absMove > 2.0)
            abstentionCorrect = false; // Missed a significant move
        else if (directionWouldHaveBeenCorrect == false && absMove > 2.0)
            abstentionCorrect = true; // Guardrails saved us from a bad call
        else
            abstentionCorrect = true; // Stock didn't move much — watching was fine

        // Guardrail justified: did the guardrails protect us or cost us?
        bool guardrailJustified = abstentionCorrect || maxAdverse > 3.0;

        // Score: 0-100 where higher = abstention was more justified
        double outcomeScore = 50;
        if (abstentionCorrect)
        {
            outcomeScore += 10;
            if (directionWouldHaveBeenCorrect == false) outcomeScore += 20; // guardrails were very right
            if (maxAdverse > 3.0) outcomeScore += 10; // would have been painful
        }
        else
        {
            outcomeScore -= Math.Min(missedAlpha * 5, 30); // penalize missed alpha
            if (maxFavorable > 5.0) outcomeScore -= 10; // really missed out
        }
        outcomeScore = Math.Clamp(outcomeScore, 0, 100);

        var lesson = GenerateAbstentionLesson(prediction, percentMove, originalDirection,
            directionWouldHaveBeenCorrect, missedAlpha, abstentionCorrect);

        var summaryParts = new List<string>
        {
            $"{prediction.Ticker}: {prediction.PredictionType} (original lean: {originalDirection}).",
            $"Entry ${startPrice:F2}, current ${closePrice:F2} ({(percentMove > 0 ? "+" : "")}{percentMove:F2}%).",
            $"Abstention was {(abstentionCorrect ? "CORRECT" : "WRONG")}.",
        };
        if (!abstentionCorrect)
            summaryParts.Add($"Missed alpha: {missedAlpha:F2}%.");
        if (prediction.DowngradeReasons.Count > 0)
            summaryParts.Add($"Downgrade reasons: {string.Join("; ", prediction.DowngradeReasons)}.");
        summaryParts.Add($"Max favorable: {maxFavorable:F2}%, max adverse: {maxAdverse:F2}%.");

        var outcomeData = new
        {
            prediction_id = prediction.Id,
            evaluation_time = DateTimeOffset.UtcNow.ToString("o"),
            start_price = startPrice,
            close_price = closePrice,
            high_after_prediction = quote.High,
            low_after_prediction = quote.Low,
            percent_move = Math.Round(percentMove, 2),
            direction_correct = directionWouldHaveBeenCorrect,
            predicted_direction = originalDirection,
            bullish_score_at_prediction = prediction.BullishScore,
            bearish_score_at_prediction = prediction.BearishScore,
            predicted_price = prediction.PredictedPrice,
            predicted_move_percent = prediction.PredictedMovePercent,
            projected_price_low = prediction.ProjectedPriceLow,
            projected_price_high = prediction.ProjectedPriceHigh,
            max_favorable_percent = Math.Round(maxFavorable, 2),
            max_adverse_percent = Math.Round(maxAdverse, 2),
            outcome_score = outcomeScore,
            outcome_summary = string.Join(" ", summaryParts),
            lesson,
            // Abstention-specific fields
            abstention_correct = abstentionCorrect,
            missed_alpha_percent = Math.Round(missedAlpha, 2),
            guardrail_justified = guardrailJustified,
            original_direction = originalDirection,
            downgrade_reasons_evaluated = prediction.DowngradeReasons.ToArray(),
        };

        await _repo.SaveOutcomeAsync(outcomeData);
        await _repo.UpdatePredictionStatusAsync(prediction.Id, "evaluated");

        var outcome = new PredictionOutcome
        {
            PredictionId = prediction.Id,
            EvaluationTime = DateTimeOffset.UtcNow,
            StartPrice = startPrice,
            ClosePrice = closePrice,
            HighAfterPrediction = quote.High,
            LowAfterPrediction = quote.Low,
            PercentMove = Math.Round(percentMove, 2),
            DirectionCorrect = directionWouldHaveBeenCorrect,
            MaxFavorablePercent = Math.Round(maxFavorable, 2),
            MaxAdversePercent = Math.Round(maxAdverse, 2),
            OutcomeScore = outcomeScore,
            OutcomeSummary = outcomeData.outcome_summary,
            Lesson = lesson,
            AbstentionCorrect = abstentionCorrect,
            MissedAlphaPercent = Math.Round(missedAlpha, 2),
            GuardrailJustified = guardrailJustified,
            OriginalDirection = originalDirection,
            DowngradeReasonsEvaluated = prediction.DowngradeReasons,
        };

        _logger.LogInformation(
            "[outcome-evaluator] {Ticker} (watch): abstention {Result}, missed_alpha={Alpha:F2}%, direction_lean={Dir} was {DirResult}",
            prediction.Ticker,
            abstentionCorrect ? "CORRECT" : "WRONG",
            missedAlpha,
            originalDirection,
            directionWouldHaveBeenCorrect == true ? "correct" : "wrong");

        return new EvaluationResult(prediction.Id, prediction.Ticker, outcome, true);
    }

    private static string GenerateAbstentionLesson(
        PredictionCandidate prediction, double percentMove, string originalDirection,
        bool? directionCorrect, double missedAlpha, bool abstentionCorrect)
    {
        var parts = new List<string>();
        var sign = percentMove > 0 ? "+" : "";

        if (abstentionCorrect)
        {
            parts.Add($"Correct to watch {prediction.Ticker} instead of taking {originalDirection} position ({sign}{percentMove:F2}%).");
            if (directionCorrect == false)
                parts.Add("Original directional lean was wrong — guardrails prevented a loss.");
            else
                parts.Add("Move was small enough that watching was appropriate.");
        }
        else
        {
            parts.Add($"Missed opportunity on {prediction.Ticker}: {originalDirection} lean was correct ({sign}{percentMove:F2}%), missed {missedAlpha:F2}% alpha.");
            if (prediction.DowngradeReasons.Count > 0)
                parts.Add($"Guardrails that blocked: {string.Join(", ", prediction.DowngradeReasons)}.");
            parts.Add("Consider loosening these guardrails for similar setups.");
        }

        parts.Add($"Confidence was {prediction.ConfidenceScore}, risk was {prediction.RiskScore}.");
        return string.Join(" ", parts);
    }

    // -----------------------------------------------------------------------
    // Lesson generation
    // -----------------------------------------------------------------------

    private static string GenerateLesson(
        PredictionCandidate prediction, double percentMove, bool? directionCorrect, bool invalidationHit,
        bool? targetHit, bool? stopHit, double maxFavorable, double maxAdverse,
        double? riskRewardRatio)
    {
        var parts = new List<string>();
        var sign = percentMove > 0 ? "+" : "";
        var absMove = Math.Abs(percentMove);

        // Header: outcome
        if (directionCorrect == true)
            parts.Add($"{prediction.PredictionType} on {prediction.Ticker} was correct ({sign}{percentMove:F2}%).");
        else if (directionCorrect == false)
            parts.Add($"{prediction.PredictionType} on {prediction.Ticker} was wrong ({sign}{percentMove:F2}%).");
        else
            parts.Add($"Neutral/watch on {prediction.Ticker}: {sign}{percentMove:F2}%.");

        if (invalidationHit)
            parts.Add("Invalidation triggered — thesis broke down.");

        // Decompose scoring buckets from ScoreDebugJson
        ScoringBreakdown? breakdown = null;
        if (!string.IsNullOrEmpty(prediction.ScoreDebugJson))
        {
            breakdown = ScoringBreakdownEnvelope.Parse(prediction.ScoreDebugJson);
        }

        if (breakdown is not null)
        {
            var dir = prediction.PredictionType == PredictionType.bullish ? "bullish" : "bearish";
            var buckets = new (string Name, double Bull, double Bear)[]
            {
                ("Trend",          breakdown.TrendBullish,          breakdown.TrendBearish),
                ("Momentum",       breakdown.MomentumBullish,       breakdown.MomentumBearish),
                ("Volume",         breakdown.VolumeBullish,         breakdown.VolumeBearish),
                ("Volatility",     breakdown.VolatilityBullish,     breakdown.VolatilityBearish),
                ("MarketContext",  breakdown.MarketContextBullish,  breakdown.MarketContextBearish),
                ("Catalyst",       breakdown.CatalystBullish,       breakdown.CatalystBearish),
                ("Learning",       breakdown.LearningBullish,       breakdown.LearningBearish),
                ("ResearchSignal", breakdown.ResearchSignalBullish, breakdown.ResearchSignalBearish),
            };

            // Which buckets supported vs opposed the predicted direction?
            var supporting = new List<(string Name, double Strength)>();
            var opposing = new List<(string Name, double Strength)>();
            foreach (var (name, bull, bear) in buckets)
            {
                var diff = bull - bear;
                if (Math.Abs(diff) < 1) continue; // negligible
                var supportsPrediction = dir == "bullish" ? diff > 0 : diff < 0;
                if (supportsPrediction)
                    supporting.Add((name, Math.Abs(diff)));
                else
                    opposing.Add((name, Math.Abs(diff)));
            }

            supporting.Sort((a, b) => b.Strength.CompareTo(a.Strength));
            opposing.Sort((a, b) => b.Strength.CompareTo(a.Strength));

            if (directionCorrect == false)
            {
                // Wrong prediction — identify what misled and what warned us
                if (supporting.Count > 0)
                    parts.Add($"Misleading signals: {string.Join(", ", supporting.Select(s => $"{s.Name}(+{s.Strength:F0})")).TrimEnd()}.");
                if (opposing.Count > 0)
                    parts.Add($"Ignored warnings: {string.Join(", ", opposing.Select(s => $"{s.Name}(-{s.Strength:F0})")).TrimEnd()}.");
                else
                    parts.Add("No opposing signals fired — all buckets agreed on the wrong direction.");

                // Overconfidence check
                if (prediction.ConfidenceScore >= 80 && absMove > 1)
                    parts.Add($"Overconfident: {prediction.ConfidenceScore} confidence on a {absMove:F1}% adverse move.");
                else if (prediction.ConfidenceScore >= 60 && absMove > 2)
                    parts.Add($"Confidence {prediction.ConfidenceScore} was too high for a {absMove:F1}% miss.");

                // Direction margin analysis
                if (breakdown.DirectionMargin < 10)
                    parts.Add($"Direction margin was only {breakdown.DirectionMargin:F0} — close call that went wrong.");

                // Conflicting buckets
                if (breakdown.ConflictingBuckets >= 3)
                    parts.Add($"{breakdown.ConflictingBuckets} buckets conflicted — mixed signals should lower confidence.");
            }
            else if (directionCorrect == true)
            {
                // Correct — highlight what worked
                if (supporting.Count > 0)
                    parts.Add($"Key drivers: {string.Join(", ", supporting.Take(3).Select(s => $"{s.Name}(+{s.Strength:F0})")).TrimEnd()}.");

                if (absMove > 3 && prediction.ConfidenceScore >= 70)
                    parts.Add("High-confidence call with strong follow-through — reinforce these signals.");
                else if (absMove < 0.5)
                    parts.Add("Direction right but move was negligible — not a meaningful signal.");

                // Underconfidence check
                if (prediction.ConfidenceScore < 50 && absMove > 3)
                    parts.Add($"Underconfident: only {prediction.ConfidenceScore} confidence on a {absMove:F1}% move — trust these signals more.");
            }

            // Aligned vs conflicting summary
            parts.Add($"Buckets: {breakdown.AlignedBuckets} aligned, {breakdown.ConflictingBuckets} conflicting. Confirmation: {breakdown.ConfirmationMultiplier:F2}x.");

            // Data quality factor
            if (breakdown.DataQualityFactor < 0.8)
                parts.Add($"Low data quality ({breakdown.DataQualityFactor:F2}) — fill missing data sources to improve accuracy.");

            // Confidence cap
            if (!string.IsNullOrEmpty(breakdown.ConfidenceCap))
                parts.Add($"Confidence was capped: {breakdown.ConfidenceCap}.");
        }
        else
        {
            // No breakdown available — fall back to basic info
            if (prediction.MissingDataWarnings.Count > 0)
                parts.Add($"Missing data: {string.Join(", ", prediction.MissingDataWarnings)}.");
            parts.Add($"Sources: {(prediction.DataSourcesUsed.Count > 0 ? string.Join(", ", prediction.DataSourcesUsed) : "none")}.");
            parts.Add($"Confidence {prediction.ConfidenceScore}, risk {prediction.RiskScore}.");
        }

        // --- Risk management analysis (always runs, independent of breakdown) ---

        // Stop/target discipline
        if (stopHit == true)
            parts.Add($"Stop was triggered — max adverse {maxAdverse:F1}% exceeded stop level.");
        if (targetHit == true)
            parts.Add($"Target was hit — max favorable reached {maxFavorable:F1}%.");
        else if (directionCorrect == true && maxFavorable > absMove * 2)
            parts.Add($"Intraday high was {maxFavorable:F1}% favorable but closed at only {absMove:F1}% — consider trailing stops.");

        // R:R outcome analysis
        if (riskRewardRatio is double rrActual)
        {
            if (rrActual < 1.0 && directionCorrect == false)
                parts.Add($"R:R was {rrActual:F2} — risked more than potential reward on a losing trade.");
            else if (rrActual >= 2.0 && directionCorrect == true)
                parts.Add($"R:R {rrActual:F2} with correct direction — good risk-managed win.");
        }

        // Max adverse vs max favorable — was this a whipsaw?
        if (maxAdverse > 2 && maxFavorable > 2)
            parts.Add($"Whipsaw: {maxFavorable:F1}% favorable and {maxAdverse:F1}% adverse — high intraday volatility.");

        // Risk score sanity check
        if (prediction.RiskScore <= 30 && absMove > 3 && directionCorrect == false)
            parts.Add($"Risk was scored only {prediction.RiskScore} but move was {absMove:F1}% adverse — risk model underestimated.");
        if (prediction.RiskScore >= 60 && prediction.ConfidenceScore >= 70)
            parts.Add($"Contradictory: confidence {prediction.ConfidenceScore} with risk {prediction.RiskScore} — these should be inversely related.");

        return string.Join(" ", parts);
    }

    // -----------------------------------------------------------------------
    // Volatility learning record creation
    // -----------------------------------------------------------------------

    private async Task CreateVolatilityLearningRecordAsync(
        PredictionCandidate prediction,
        PredictionOutcome outcome,
        double startPrice,
        double maxFavorable,
        double maxAdverse)
    {
        try
        {
            var assessment = await _repo.GetAssessmentAsync(prediction.Ticker, prediction.RunId);
            if (assessment is null)
            {
                _logger.LogDebug("[outcome-evaluator] {Ticker}: no VOE assessment for run {RunId}, skipping learning record",
                    prediction.Ticker, prediction.RunId);
                return;
            }

            // Fetch bars since prediction for time-to-move computation
            var bars = await _marketData.GetRecentBarsAsync(prediction.Ticker, 60);
            var predDate = prediction.CreatedAt.Date;
            var barsAfterEntry = bars
                .Where(b => DateTime.TryParse(b.Date, out var d) && d > predDate)
                .OrderBy(b => b.Date)
                .ToList();

            var isBullish = prediction.PredictionType == PredictionType.bullish;

            var timeTo3 = ComputeTimeToMove(barsAfterEntry, startPrice, 3.0, isBullish);
            var timeTo5 = ComputeTimeToMove(barsAfterEntry, startPrice, 5.0, isBullish);
            var timeToTarget = prediction.TargetPrice is double tp and > 0
                ? ComputeTimeToTarget(barsAfterEntry, tp, isBullish)
                : null;

            var holdingHours = (DateTimeOffset.UtcNow - prediction.CreatedAt).TotalHours;

            var recoverySpeed = ComputeRecoverySpeed(barsAfterEntry, startPrice, isBullish);
            var bounceQuality = ClassifyBounceQuality(maxFavorable, maxAdverse, outcome.DirectionCorrect);

            var (oppSuccess, oppReason) = EvaluateOpportunitySuccess(
                assessment, outcome, maxFavorable, barsAfterEntry, startPrice, isBullish);

            var record = new VolatilityLearningRecord
            {
                PredictionId = prediction.Id,
                RunId = prediction.RunId,
                Ticker = prediction.Ticker,
                OpportunityType = assessment.Opportunity.ToString(),
                OpportunityScore = assessment.OpportunityScore,
                StockVolatilityRegime = assessment.StockVolRegime.ToString(),
                AtrPercentile = assessment.AtrPercentile,
                AtrAcceleration = assessment.AtrAcceleration,
                BandwidthPercentile = assessment.BandwidthPercentile,
                GapType = assessment.GapClassification.ToString(),
                GapPercent = assessment.GapPercent,
                CatalystAgeHours = assessment.CatalystAgeHours,
                Confidence = prediction.ConfidenceScore,
                Risk = prediction.RiskScore,
                PredictionType = prediction.PredictionType.ToString(),
                TimeWindow = prediction.TimeWindow,
                DirectionCorrect = outcome.DirectionCorrect,
                OutcomeScore = outcome.OutcomeScore,
                HoldingPeriodHours = Math.Round(holdingHours, 1),
                MaxFavorableExcursion = maxFavorable,
                MaxAdverseExcursion = maxAdverse,
                TimeTo3Pct = timeTo3,
                TimeTo5Pct = timeTo5,
                TimeToTarget = timeToTarget,
                RecoverySpeed = recoverySpeed,
                BounceQualityRealized = bounceQuality.ToString(),
                OpportunitySuccess = oppSuccess,
                OpportunitySuccessReason = oppReason,
                ProfileId = prediction.ProfileId,
            };

            await _repo.SaveVolatilityLearningRecordAsync(record);

            _logger.LogInformation(
                "[outcome-evaluator] {Ticker}: VOE learning — opportunity={Opp}, success={Success}, bounce={Bounce}, timeTo3={T3}, timeTo5={T5}, direction={Dir}",
                prediction.Ticker,
                assessment.Opportunity,
                oppSuccess?.ToString() ?? "n/a",
                bounceQuality,
                timeTo3?.ToString() ?? "n/a",
                timeTo5?.ToString() ?? "n/a",
                outcome.DirectionCorrect == true ? "correct" : "wrong");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[outcome-evaluator] {Ticker}: volatility learning record failed (non-blocking)", prediction.Ticker);
        }
    }

    // bars ordered chronologically (oldest first)
    private static int? ComputeTimeToMove(
        List<MarketSnapshotBar> bars, double entryPrice, double targetPct, bool isBullish)
    {
        for (int i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            double movePct = isBullish
                ? ((bar.High - entryPrice) / entryPrice) * 100
                : ((entryPrice - bar.Low) / entryPrice) * 100;
            if (movePct >= targetPct) return i + 1;
        }
        return null;
    }

    private static int? ComputeTimeToTarget(
        List<MarketSnapshotBar> bars, double targetPrice, bool isBullish)
    {
        for (int i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            bool hit = isBullish ? bar.High >= targetPrice : bar.Low <= targetPrice;
            if (hit) return i + 1;
        }
        return null;
    }

    /// <summary>
    /// How quickly price recovered from max adverse excursion.
    /// 1.0 = recovered fully within 1 bar, lower = slower recovery.
    /// Null if no adverse excursion occurred.
    /// </summary>
    private static double? ComputeRecoverySpeed(
        List<MarketSnapshotBar> bars, double entryPrice, bool isBullish)
    {
        if (bars.Count < 2) return null;

        int worstBar = -1;
        double worstAdverse = 0;
        for (int i = 0; i < bars.Count; i++)
        {
            double adverse = isBullish
                ? ((entryPrice - bars[i].Low) / entryPrice) * 100
                : ((bars[i].High - entryPrice) / entryPrice) * 100;
            if (adverse > worstAdverse)
            {
                worstAdverse = adverse;
                worstBar = i;
            }
        }

        if (worstAdverse < 0.5 || worstBar < 0) return null;

        // Count bars after worst point to recover back to entry
        int barsToRecover = 0;
        for (int i = worstBar + 1; i < bars.Count; i++)
        {
            barsToRecover++;
            double price = isBullish ? bars[i].Close : bars[i].Close;
            bool recovered = isBullish ? price >= entryPrice : price <= entryPrice;
            if (recovered) return Math.Round(1.0 / barsToRecover, 3);
        }

        // Never recovered — speed based on how far back it got
        if (barsToRecover == 0) return 0;
        var lastClose = bars[^1].Close;
        double remainingAdverse = isBullish
            ? ((entryPrice - lastClose) / entryPrice) * 100
            : ((lastClose - entryPrice) / entryPrice) * 100;
        if (remainingAdverse <= 0) return Math.Round(1.0 / barsToRecover, 3);
        return Math.Round((1.0 - remainingAdverse / worstAdverse) / barsToRecover, 3);
    }

    private static BounceQuality ClassifyBounceQuality(
        double maxFavorable, double maxAdverse, bool? directionCorrect)
    {
        if (directionCorrect != true) return BounceQuality.None;

        // Ratio of favorable to adverse movement
        if (maxAdverse < 0.5) return maxFavorable > 2 ? BounceQuality.Excellent : BounceQuality.Good;
        var ratio = maxFavorable / Math.Max(maxAdverse, 0.1);
        return ratio switch
        {
            >= 5.0 => BounceQuality.Excellent,
            >= 2.5 => BounceQuality.Good,
            >= 1.0 => BounceQuality.Fair,
            _ => BounceQuality.Poor,
        };
    }

    private static (bool? Success, string? Reason) EvaluateOpportunitySuccess(
        VolatilityOpportunityAssessment assessment,
        PredictionOutcome outcome,
        double maxFavorable,
        List<MarketSnapshotBar> barsAfterEntry,
        double entryPrice,
        bool isBullish)
    {
        if (assessment.Opportunity == OpportunityType.None)
            return (null, null);

        return assessment.Opportunity switch
        {
            OpportunityType.DipAfterPanic => EvalDipAfterPanic(outcome, maxFavorable, isBullish),
            OpportunityType.MomentumContinuation => EvalMomentumContinuation(outcome, maxFavorable),
            OpportunityType.SqueezeBreakout => EvalSqueezeBreakout(outcome, maxFavorable, barsAfterEntry, entryPrice),
            OpportunityType.MeanReversion => EvalMeanReversion(outcome, maxFavorable),
            OpportunityType.VolatilityTrap => EvalVolatilityTrap(outcome, isBullish),
            OpportunityType.FailedBounce => EvalFailedBounce(outcome, isBullish),
            _ => (null, "Unknown opportunity type"),
        };
    }

    private static (bool?, string?) EvalDipAfterPanic(PredictionOutcome outcome, double maxFavorable, bool isBullish)
    {
        if (!isBullish) return (null, "DipAfterPanic expects bullish prediction");
        if (outcome.DirectionCorrect == true && maxFavorable >= 3.0)
            return (true, $"Price recovered {maxFavorable:F1}% from panic dip");
        if (outcome.DirectionCorrect == true)
            return (true, $"Price recovered {maxFavorable:F1}% — partial recovery");
        return (false, "Price did not recover from dip");
    }

    private static (bool?, string?) EvalMomentumContinuation(PredictionOutcome outcome, double maxFavorable)
    {
        if (outcome.DirectionCorrect == true && maxFavorable >= 2.0)
            return (true, $"Momentum continued — {maxFavorable:F1}% favorable move");
        if (outcome.DirectionCorrect == true)
            return (true, $"Momentum continued modestly — {maxFavorable:F1}%");
        return (false, "Momentum did not continue");
    }

    private static (bool?, string?) EvalSqueezeBreakout(
        PredictionOutcome outcome, double maxFavorable,
        List<MarketSnapshotBar> bars, double entryPrice)
    {
        // Did expansion actually occur? Check if range expanded after prediction
        if (bars.Count < 3) return (null, "Insufficient bars to evaluate squeeze breakout");

        var avgRange = bars.Take(Math.Min(5, bars.Count))
            .Average(b => (b.High - b.Low) / entryPrice * 100);
        bool expanded = avgRange > 1.5; // meaningful expansion

        if (expanded && outcome.DirectionCorrect == true)
            return (true, $"Squeeze broke out — avg range {avgRange:F2}%, favorable {maxFavorable:F1}%");
        if (!expanded)
            return (false, $"No expansion after squeeze — avg range only {avgRange:F2}%");
        return (false, "Expansion occurred but direction was wrong");
    }

    private static (bool?, string?) EvalMeanReversion(PredictionOutcome outcome, double maxFavorable)
    {
        if (outcome.DirectionCorrect == true && maxFavorable >= 1.5)
            return (true, $"Price reverted toward mean — {maxFavorable:F1}% favorable");
        if (outcome.DirectionCorrect == true)
            return (true, $"Partial mean reversion — {maxFavorable:F1}%");
        return (false, "Mean reversion did not materialize");
    }

    private static (bool?, string?) EvalVolatilityTrap(PredictionOutcome outcome, bool isBullish)
    {
        // VolatilityTrap expects bearish — price continues failing
        if (!isBullish && outcome.DirectionCorrect == true)
            return (true, "Correctly identified volatility trap — price continued lower");
        if (isBullish && outcome.DirectionCorrect == false)
            return (true, "Volatility trap — bullish signal was false");
        return (false, "Volatility trap classification was incorrect");
    }

    private static (bool?, string?) EvalFailedBounce(PredictionOutcome outcome, bool isBullish)
    {
        // FailedBounce expects bearish — recovery fails
        if (!isBullish && outcome.DirectionCorrect == true)
            return (true, "Bounce failed as predicted — price resumed decline");
        if (isBullish && outcome.DirectionCorrect == false)
            return (true, "Bounce failed — bullish call was wrong");
        return (false, "Bounce actually succeeded — FailedBounce was wrong");
    }
}
