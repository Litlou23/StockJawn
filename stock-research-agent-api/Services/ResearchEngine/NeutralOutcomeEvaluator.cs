using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Evaluates neutral predictions (high_volatility, no_edge, range_bound) against
/// what actually happened. Does NOT produce a simple correct/incorrect — instead
/// measures whether the neutral classification was justified and computes a
/// counterfactual for learning.
///
/// Runs in parallel with the existing directional evaluator; does not modify it.
/// </summary>
public class NeutralOutcomeEvaluator
{
    /// <summary>
    /// Minimum absolute % move to consider a significant opportunity was missed.
    /// Below this, the neutral call was arguably correct regardless of type.
    /// </summary>
    private const double SignificantMoveThreshold = 3.0;

    /// <summary>
    /// Minimum absolute % move to flag as a breakout for no_edge evaluation.
    /// </summary>
    private const double BreakoutThreshold = 5.0;

    /// <summary>
    /// High volatility threshold — realized vol above this confirms high_volatility was correct.
    /// Annualized daily std dev; ~2% daily = ~32% annualized.
    /// </summary>
    private const double HighVolThreshold = 2.0;

    private readonly ResearchRepository _researchRepo;
    private readonly PaperStockCandidateRepository _stockRepo;
    private readonly NeutralOutcomeRepository _neutralRepo;
    private readonly MarketDataService _marketData;
    private readonly ILogger<NeutralOutcomeEvaluator> _logger;

    public NeutralOutcomeEvaluator(
        ResearchRepository researchRepo,
        PaperStockCandidateRepository stockRepo,
        NeutralOutcomeRepository neutralRepo,
        MarketDataService marketData,
        ILogger<NeutralOutcomeEvaluator> logger)
    {
        _researchRepo = researchRepo;
        _stockRepo = stockRepo;
        _neutralRepo = neutralRepo;
        _marketData = marketData;
        _logger = logger;
    }

    // Same timeframe gating as StockCandidateService — neutral predictions
    // should be evaluated after the same window as directional ones.
    private static readonly Dictionary<string, int> MinEvalHours = new()
    {
        ["intraday"] = 4,
        ["1_day"] = 24,
        ["3_day"] = 48,
        ["1_week"] = 120,
        ["1_month"] = 504,
        ["3_month"] = 1512,
        ["6_month"] = 3024,
        ["1_year"] = 6048,
    };

    private static readonly Dictionary<string, int> MaxEvalHours = new()
    {
        ["intraday"] = 24,
        ["1_day"] = 48,
        ["3_day"] = 96,
        ["1_week"] = 240,
        ["1_month"] = 1008,
        ["3_month"] = 3024,
        ["6_month"] = 6048,
        ["1_year"] = 12096,
    };

    private static readonly HashSet<PredictionType> NeutralTypes =
    [
        PredictionType.neutral_high_volatility,
        PredictionType.neutral_no_edge,
        PredictionType.neutral_range_bound,
        PredictionType.neutral,
    ];

    // -----------------------------------------------------------------------
    // Public entry point — evaluate all open neutral predictions
    // -----------------------------------------------------------------------

    public async Task<int> EvaluateOpenNeutralPredictionsAsync(List<string> errors)
    {
        var openPredictions = await _researchRepo.GetOpenPredictionsAsync();
        var neutralPredictions = openPredictions
            .Where(p => NeutralTypes.Contains(p.PredictionType))
            .ToList();

        var evaluated = 0;

        foreach (var pred in neutralPredictions)
        {
            try
            {
                var ok = await EvaluateNeutralPredictionAsync(pred);
                if (ok) evaluated++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[neutral-eval] Failed {Ticker}", pred.Ticker);
                errors.Add($"neutral-eval {pred.Ticker}: {ex.Message}");
            }
        }

        if (evaluated > 0)
            _logger.LogInformation("[neutral-eval] Evaluated {Count} neutral predictions", evaluated);

        return evaluated;
    }

    // -----------------------------------------------------------------------
    // Core evaluation
    // -----------------------------------------------------------------------

    private async Task<bool> EvaluateNeutralPredictionAsync(PredictionCandidate pred)
    {
        // Timeframe gate
        var ageHours = (DateTimeOffset.UtcNow - pred.CreatedAt).TotalHours;
        var minH = MinEvalHours.GetValueOrDefault(pred.TimeWindow, 6);
        var maxH = MaxEvalHours.GetValueOrDefault(pred.TimeWindow, 240);

        if (ageHours < minH) return false;

        if (ageHours > maxH)
        {
            // Expired without data — mark and move on
            await _researchRepo.UpdatePredictionStatusAsync(pred.Id, "expired");
            return false;
        }

        var entry = pred.EntryReferencePrice;
        if (entry is null || entry == 0)
        {
            _logger.LogDebug("[neutral-eval] {Ticker}: no entry price, skipping", pred.Ticker);
            return false;
        }

        // Fetch current quote + recent bars for volatility calc
        var quote = await _marketData.GetQuoteAsync(pred.Ticker);
        if (quote is null) return false; // try again next run

        var bars = await _marketData.GetRecentBarsAsync(pred.Ticker, 30);

        var exit = quote.Price;
        var move = (exit - entry.Value) / entry.Value * 100;
        var absMove = Math.Abs(move);

        // High/low over the full evaluation window from bars, not just today's quote
        var high = bars.Count > 0 ? Math.Max(bars.Max(b => b.High), quote.High) : quote.High;
        var low = bars.Count > 0 ? Math.Min(bars.Min(b => b.Low), quote.Low) : quote.Low;
        var maxRunUp = ((high - entry.Value) / entry.Value) * 100;
        var maxDrawdown = ((entry.Value - low) / entry.Value) * 100;

        // Realized volatility from recent bars (daily returns std dev)
        var realizedVol = ComputeRealizedVolatility(bars);

        // Type-specific evaluation
        var (neutralAccuracy, typeMetrics) = pred.PredictionType switch
        {
            PredictionType.neutral_high_volatility =>
                EvaluateHighVolatility(pred, move, absMove, realizedVol, maxRunUp, maxDrawdown),
            PredictionType.neutral_no_edge =>
                EvaluateNoEdge(pred, move, absMove, bars),
            PredictionType.neutral_range_bound =>
                EvaluateRangeBound(pred, move, entry.Value, high, low),
            _ =>
                EvaluateGenericNeutral(move, absMove),
        };

        // Counterfactual
        var (cfDirection, cfCorrect, opportunityScore) =
            ComputeCounterfactual(pred, move, absMove);

        var summary = BuildSummary(pred, move, realizedVol, neutralAccuracy, cfDirection, cfCorrect, opportunityScore);
        var lesson = BuildLesson(pred, move, neutralAccuracy, cfDirection, cfCorrect, opportunityScore);

        var outcome = new NeutralPredictionOutcome
        {
            PredictionId = pred.Id,
            Ticker = pred.Ticker,
            PredictionType = pred.PredictionType.ToString(),
            TimeWindow = pred.TimeWindow,
            EntryPrice = entry.Value,
            ExitPrice = exit,
            HighAfter = high,
            LowAfter = low,
            RealizedMovePercent = Math.Round(move, 2),
            AbsoluteMovePercent = Math.Round(absMove, 2),
            MaxRunUp = Math.Round(Math.Max(maxRunUp, 0), 2),
            MaxDrawdown = Math.Round(Math.Max(maxDrawdown, 0), 2),
            RealizedVolatility = Math.Round(realizedVol, 4),
            NeutralAccuracyScore = Math.Round(neutralAccuracy, 1),
            VolatilityPredictionAccuracy = typeMetrics.VolatilityAccuracy,
            RangeAdherencePercent = typeMetrics.RangeAdherence,
            SupportBroken = typeMetrics.SupportBroken,
            ResistanceBroken = typeMetrics.ResistanceBroken,
            MaxRangeExcursionPercent = typeMetrics.MaxRangeExcursion,
            BreakoutOccurred = typeMetrics.BreakoutOccurred,
            DirectionalPersistence = typeMetrics.DirectionalPersistence,
            CounterfactualDirection = cfDirection,
            CounterfactualCorrect = cfCorrect,
            OpportunityMissedScore = Math.Round(opportunityScore, 1),
            OriginalBullScore = pred.BullishScore,
            OriginalBearScore = pred.BearishScore,
            OutcomeSummary = summary,
            Lesson = lesson,
            EvaluationTime = DateTimeOffset.UtcNow,
        };

        // Find the paper stock candidate if one exists (watch_only)
        // so we can link the outcome for cross-referencing
        var candidates = await _stockRepo.GetRecentCandidatesAsync(100);
        var stockCandidate = candidates.FirstOrDefault(c =>
            c.PredictionId == pred.Id);
        if (stockCandidate is not null)
        {
            outcome = outcome with { PaperStockCandidateId = stockCandidate.Id };
            // Also update the paper stock candidate status
            await _stockRepo.UpdateCandidateStatusAsync(stockCandidate.Id, PaperStockStatus.evaluated);
        }

        await _neutralRepo.SaveOutcomeAsync(outcome);
        await _researchRepo.UpdatePredictionStatusAsync(pred.Id, "evaluated");

        _logger.LogInformation(
            "[neutral-eval] {Ticker} ({Type}): accuracy={Acc:F0}, opportunity_missed={Opp:F0}, move={Move:F2}%",
            pred.Ticker, pred.PredictionType, neutralAccuracy, opportunityScore, move);

        return true;
    }

    // -----------------------------------------------------------------------
    // Type-specific evaluators
    // -----------------------------------------------------------------------

    private (double neutralAccuracy, TypeMetrics metrics) EvaluateHighVolatility(
        PredictionCandidate pred, double move, double absMove,
        double realizedVol, double maxRunUp, double maxDrawdown)
    {
        // Was volatility actually high?
        var volAccuracy = realizedVol >= HighVolThreshold ? 80.0 : Math.Max(0, realizedVol / HighVolThreshold * 60);

        // Was direction truly unclear? (high reversals = good neutral call)
        var tradingRange = maxRunUp + maxDrawdown;
        var directionUnclear = tradingRange > 0
            ? 1.0 - (absMove / tradingRange) // closer to 0 = more whipsaw
            : 0.5;

        // Combined accuracy: vol was high AND direction was unclear
        var neutralAccuracy = (volAccuracy * 0.6) + (directionUnclear * 100 * 0.4);

        // Penalize if a clear directional move emerged despite "high volatility"
        if (absMove > SignificantMoveThreshold && directionUnclear < 0.3)
            neutralAccuracy *= 0.7;

        neutralAccuracy = Math.Clamp(neutralAccuracy, 0, 100);

        return (neutralAccuracy, new TypeMetrics { VolatilityAccuracy = Math.Round(volAccuracy, 1) });
    }

    private (double neutralAccuracy, TypeMetrics metrics) EvaluateNoEdge(
        PredictionCandidate pred, double move, double absMove,
        List<MarketSnapshotBar> bars)
    {
        // Did a meaningful breakout occur? If so, there WAS an edge — the neutral call was wrong.
        var breakoutOccurred = absMove >= BreakoutThreshold;

        // Directional persistence: what fraction of days moved in the same direction as the final move?
        var persistence = ComputeDirectionalPersistence(bars, move);

        // High persistence + big move = there was a clear edge
        double neutralAccuracy;
        if (breakoutOccurred && persistence > 0.6)
            neutralAccuracy = 20; // clear edge existed — bad neutral call
        else if (breakoutOccurred)
            neutralAccuracy = 40; // breakout but not persistent — debatable
        else if (absMove < 2.0)
            neutralAccuracy = 90; // barely moved — correct no-edge call
        else
            neutralAccuracy = Math.Max(20, 80 - absMove * 8); // scales down with move size

        return (Math.Clamp(neutralAccuracy, 0, 100), new TypeMetrics
        {
            BreakoutOccurred = breakoutOccurred,
            DirectionalPersistence = Math.Round(persistence, 3),
        });
    }

    private (double neutralAccuracy, TypeMetrics metrics) EvaluateRangeBound(
        PredictionCandidate pred, double move, double entry, double high, double low)
    {
        var support = pred.SupportLevel ?? (entry * 0.97);
        var resistance = pred.ResistanceLevel ?? (entry * 1.03);

        var supportBroken = low < support;
        var resistanceBroken = high > resistance;

        // Range adherence: how much of the price action stayed in range
        // Simplified: check if final price, high, and low all stayed inside
        var rangeWidth = resistance - support;
        if (rangeWidth <= 0) rangeWidth = entry * 0.06; // fallback 6% range

        var maxExcursionAbove = Math.Max(0, high - resistance);
        var maxExcursionBelow = Math.Max(0, support - low);
        var maxExcursionPct = Math.Max(maxExcursionAbove, maxExcursionBelow) / entry * 100;

        // If price stayed in range, good call
        double rangeAdherence;
        if (!supportBroken && !resistanceBroken)
            rangeAdherence = 100;
        else if (maxExcursionPct < 1)
            rangeAdherence = 80; // minor breach
        else
            rangeAdherence = Math.Max(0, 80 - maxExcursionPct * 15);

        var neutralAccuracy = rangeAdherence;

        return (Math.Clamp(neutralAccuracy, 0, 100), new TypeMetrics
        {
            RangeAdherence = Math.Round(rangeAdherence, 1),
            SupportBroken = supportBroken,
            ResistanceBroken = resistanceBroken,
            MaxRangeExcursion = Math.Round(maxExcursionPct, 2),
        });
    }

    private static (double neutralAccuracy, TypeMetrics metrics) EvaluateGenericNeutral(
        double move, double absMove)
    {
        // Legacy "neutral" type — just check if price stayed flat
        var accuracy = absMove < 2.0 ? 85 : Math.Max(10, 85 - absMove * 10);
        return (Math.Clamp(accuracy, 0, 100), new TypeMetrics());
    }

    // -----------------------------------------------------------------------
    // Counterfactual analysis
    // -----------------------------------------------------------------------

    private static (string? direction, bool? correct, double opportunityScore) ComputeCounterfactual(
        PredictionCandidate pred, double move, double absMove)
    {
        var bull = pred.BullishScore ?? 0;
        var bear = pred.BearishScore ?? 0;

        // Determine which direction the system leaned toward (if any)
        string? cfDirection = null;
        if (Math.Abs(bull - bear) > 3) // minimum margin to call it a lean
            cfDirection = bull > bear ? "bullish" : "bearish";

        if (cfDirection is null)
        {
            // No lean at all — opportunity score is just based on move magnitude
            var oppScore = absMove > SignificantMoveThreshold
                ? Math.Min(absMove * 10, 100)
                : 0;
            return (null, null, oppScore);
        }

        // Would that direction have been correct?
        var cfCorrect = cfDirection == "bullish" ? move > 0 : move < 0;

        // Opportunity missed score:
        // High if the lean was correct AND the move was significant
        double opportunity;
        if (cfCorrect && absMove >= SignificantMoveThreshold)
        {
            // Significant correct lean that was called neutral — real missed opportunity
            var leanStrength = Math.Abs(bull - bear);
            opportunity = Math.Min(100, absMove * 8 + leanStrength * 2);
        }
        else if (cfCorrect)
        {
            // Correct lean but small move — minor missed opportunity
            opportunity = absMove * 5;
        }
        else
        {
            // Wrong lean — the neutral call was actually protective
            opportunity = 0;
        }

        return (cfDirection, cfCorrect, Math.Round(Math.Clamp(opportunity, 0, 100), 1));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static double ComputeRealizedVolatility(List<MarketSnapshotBar> bars)
    {
        if (bars.Count < 3) return 0;

        var returns = new List<double>();
        for (int i = 1; i < bars.Count; i++)
        {
            var prevClose = bars[i - 1].Close;
            var curClose = bars[i].Close;
            if (prevClose > 0)
                returns.Add((curClose - prevClose) / prevClose * 100);
        }

        if (returns.Count < 2) return 0;

        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        return Math.Sqrt(variance); // daily std dev in %
    }

    private static double ComputeDirectionalPersistence(List<MarketSnapshotBar> bars, double finalMove)
    {
        if (bars.Count < 3 || Math.Abs(finalMove) < 0.01) return 0.5;

        var sameDirection = 0;
        for (int i = 1; i < bars.Count; i++)
        {
            var dayReturn = bars[i].Close - bars[i - 1].Close;
            if ((finalMove > 0 && dayReturn > 0) || (finalMove < 0 && dayReturn < 0))
                sameDirection++;
        }

        return (double)sameDirection / (bars.Count - 1);
    }

    private static string BuildSummary(PredictionCandidate pred, double move,
        double realizedVol, double neutralAccuracy, string? cfDirection,
        bool? cfCorrect, double oppScore)
    {
        var typeLabel = pred.PredictionType switch
        {
            PredictionType.neutral_high_volatility => "high volatility",
            PredictionType.neutral_no_edge => "no statistical edge",
            PredictionType.neutral_range_bound => "range-bound",
            _ => "neutral",
        };

        var parts = new List<string>
        {
            $"{pred.Ticker} was called {typeLabel}.",
            $"Price moved {move:F2}% (realized vol: {realizedVol:F2}% daily).",
            $"Neutral accuracy: {neutralAccuracy:F0}/100.",
        };

        if (cfDirection is not null)
        {
            parts.Add($"System leaned {cfDirection} (bull={pred.BullishScore:F0}, bear={pred.BearishScore:F0}).");
            parts.Add(cfCorrect == true
                ? $"That lean was correct — opportunity missed score: {oppScore:F0}."
                : "That lean was wrong — neutral call was protective.");
        }

        return string.Join(" ", parts);
    }

    private static string BuildLesson(PredictionCandidate pred, double move,
        double neutralAccuracy, string? cfDirection, bool? cfCorrect, double oppScore)
    {
        if (neutralAccuracy >= 75 && oppScore < 20)
            return $"Neutral call for {pred.Ticker} was well-justified. Price action confirmed the {pred.PredictionType} classification.";

        if (cfCorrect == true && oppScore >= 50)
            return $"Missed opportunity: {pred.Ticker} moved {move:F1}% in the direction the system leaned ({cfDirection}). " +
                   $"Consider lowering the neutrality threshold for this pattern.";

        if (cfCorrect == false && neutralAccuracy >= 50)
            return $"Neutral call was protective for {pred.Ticker}. System leaned {cfDirection} but price went the other way.";

        if (neutralAccuracy < 30)
            return $"Neutral classification was inaccurate for {pred.Ticker}. Price moved {move:F1}% — the system should have made a directional call.";

        return $"Mixed result for {pred.Ticker}: neutral accuracy {neutralAccuracy:F0}/100, move {move:F1}%.";
    }

    // Internal struct for type-specific metrics
    private record struct TypeMetrics
    {
        public double? VolatilityAccuracy;
        public double? RangeAdherence;
        public bool? SupportBroken;
        public bool? ResistanceBroken;
        public double? MaxRangeExcursion;
        public bool? BreakoutOccurred;
        public double? DirectionalPersistence;
    }
}
