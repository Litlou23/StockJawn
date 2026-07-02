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
            // Skip non-directional predictions — they are scan results, not picks
            if (!PredictionCategoryHelper.IsDirectional(prediction.PredictionType))
            {
                await _repo.UpdatePredictionStatusAsync(prediction.Id, "expired");
                skipped.Add($"{prediction.Ticker}: scan result ({prediction.PredictionType}), not a directional pick");
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
