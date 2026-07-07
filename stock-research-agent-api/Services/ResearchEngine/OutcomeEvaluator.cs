using StockResearchAgent.Api.Models;
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
    private readonly ILogger<OutcomeEvaluator> _logger;

    public OutcomeEvaluator(
        MarketDataService marketData,
        ResearchRepository repo,
        ILogger<OutcomeEvaluator> logger)
    {
        _marketData = marketData;
        _repo = repo;
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

        if (prediction.EntryReferencePrice is null or 0)
        {
            _logger.LogWarning("[outcome-evaluator] {Ticker}: no entry reference price, cannot evaluate", prediction.Ticker);
            return null;
        }

        var quote = await _marketData.GetQuoteAsync(prediction.Ticker);
        if (quote is null)
        {
            _logger.LogWarning("[outcome-evaluator] {Ticker}: market data unavailable, skipping", prediction.Ticker);
            return null;
        }

        var startPrice = prediction.EntryReferencePrice.Value;
        var closePrice = quote.Price;
        var percentMove = ((closePrice - startPrice) / startPrice) * 100;

        // SPY relative performance for short-term picks
        double? relativePerformance = null;
        if (PredictionTimeWindows.ShortTerm.Contains(prediction.TimeWindow))
        {
            var spyQuote = await _marketData.GetQuoteAsync("SPY");
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

        var lesson = GenerateLesson(prediction, percentMove, directionCorrect, invalidationHit);

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
            // Non-directional predictions: evaluate watch_only as abstention decisions,
            // expire everything else (scan results with no directional lean).
            if (!PredictionCategoryHelper.IsDirectional(prediction.PredictionType))
            {
                if (prediction.PredictionType == PredictionType.watch_only
                    || prediction.PredictionType == PredictionType.neutral_no_edge
                    || prediction.PredictionType == PredictionType.neutral_high_volatility)
                {
                    // These had a directional lean but were downgraded — evaluate them
                    // as abstention decisions so we can calibrate our guardrails.
                    try
                    {
                        var watchResult = await EvaluateAbstentionAsync(prediction);
                        if (watchResult is not null)
                        {
                            evaluated.Add(watchResult);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{prediction.Ticker} (watch): {ex.Message}");
                        continue;
                    }
                }

                await _repo.UpdatePredictionStatusAsync(prediction.Id, "expired");
                skipped.Add($"{prediction.Ticker}: scan result ({prediction.PredictionType}), not evaluable");
                continue;
            }

            var ageHours = (now - prediction.CreatedAt).TotalHours;

            var minHours = prediction.TimeWindow switch
            {
                "intraday" => 4,
                "1_day" => 6,
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
        if (prediction.EntryReferencePrice is null or 0)
        {
            _logger.LogWarning("[outcome-evaluator] {Ticker} (watch): no entry price, expiring", prediction.Ticker);
            await _repo.UpdatePredictionStatusAsync(prediction.Id, "expired");
            return null;
        }

        var quote = await _marketData.GetQuoteAsync(prediction.Ticker);
        if (quote is null)
        {
            _logger.LogWarning("[outcome-evaluator] {Ticker} (watch): market data unavailable", prediction.Ticker);
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
        PredictionCandidate prediction, double percentMove, bool? directionCorrect, bool invalidationHit)
    {
        var parts = new List<string>();
        var sign = percentMove > 0 ? "+" : "";

        if (directionCorrect == true)
        {
            parts.Add($"{prediction.PredictionType} prediction on {prediction.Ticker} was correct ({sign}{percentMove:F2}%).");
            if (Math.Abs(percentMove) > 3)
                parts.Add("Strong move -- signals used were reliable for this setup.");
        }
        else if (directionCorrect == false)
        {
            parts.Add($"{prediction.PredictionType} prediction on {prediction.Ticker} was wrong ({sign}{percentMove:F2}%).");
            if (invalidationHit) parts.Add("Invalidation rule was triggered -- the thesis broke down.");
            if (prediction.MissingDataWarnings.Count > 0)
                parts.Add($"Missing data may have contributed: {string.Join(", ", prediction.MissingDataWarnings)}.");
        }
        else
        {
            parts.Add($"Neutral/watch prediction on {prediction.Ticker}: {sign}{percentMove:F2}% move.");
        }

        parts.Add($"Data sources: {(prediction.DataSourcesUsed.Count > 0 ? string.Join(", ", prediction.DataSourcesUsed) : "none")}.");
        parts.Add($"Confidence was {prediction.ConfidenceScore}, risk was {prediction.RiskScore}.");

        return string.Join(" ", parts);
    }
}
