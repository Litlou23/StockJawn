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

        bool? directionCorrect = prediction.PredictionType switch
        {
            PredictionType.bullish => percentMove > 0,
            PredictionType.bearish => percentMove < 0,
            _ => null,
        };

        var invalidationHit = (prediction.PredictionType == PredictionType.bullish && percentMove < -2)
            || (prediction.PredictionType == PredictionType.bearish && percentMove > 2);

        double outcomeScore = 50;
        if (directionCorrect == true)
            outcomeScore += Math.Min(Math.Abs(percentMove) * 10, 40);
        else if (directionCorrect == false)
            outcomeScore -= Math.Min(Math.Abs(percentMove) * 10, 40);
        if (invalidationHit) outcomeScore -= 10;
        outcomeScore = Math.Clamp(outcomeScore, 0, 100);

        var lesson = GenerateLesson(prediction, percentMove, directionCorrect, invalidationHit);

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
            invalidation_hit = invalidationHit,
            outcome_score = outcomeScore,
            outcome_summary = $"{prediction.Ticker}: {prediction.PredictionType} prediction. Entry ${startPrice:F2}, current ${closePrice:F2} ({(percentMove > 0 ? "+" : "")}{percentMove:F2}%). Direction {(directionCorrect == true ? "correct" : directionCorrect == false ? "wrong" : "N/A")}.",
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
            InvalidationHit = invalidationHit,
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
            var ageHours = (now - prediction.CreatedAt).TotalHours;

            var minHours = prediction.TimeWindow switch
            {
                "intraday" => 4,
                "1_day" => 6,
                "3_day" => 48,
                "1_week" => 120,
                _ => 6,
            };

            if (ageHours < minHours)
            {
                skipped.Add($"{prediction.Ticker}: too early ({ageHours:F1}h < {minHours}h for {prediction.TimeWindow})");
                continue;
            }

            if (ageHours > 240)
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
