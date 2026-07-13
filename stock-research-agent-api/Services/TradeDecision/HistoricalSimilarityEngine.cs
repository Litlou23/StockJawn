using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Deterministic historical similarity engine.
///
/// Phase 1:  No database access — works with an in-memory case library
/// that is empty by default.  A future phase will inject a case provider
/// (e.g. from setup_learning_stats / trade_setups) without changing this
/// interface.
///
/// Similarity scoring (configurable weights, must sum to 1.0):
///   Trade Grade       30%
///   Direction match   20%
///   Feature overlap   25%
///   Evidence overlap  25%
///
/// Stateless, no I/O, safe to register as singleton.
/// </summary>
public class HistoricalSimilarityEngine : IHistoricalSimilarityEngine
{
    // ── Configurable similarity weights ──────────────────────────────
    private const double WeightTradeGrade       = 0.30;
    private const double WeightDirection        = 0.20;
    private const double WeightFeatureOverlap   = 0.25;
    private const double WeightEvidenceOverlap  = 0.25;

    /// <summary>
    /// In-memory case library.  Empty in Phase 1.
    /// Future: injected via constructor from a case provider service.
    /// </summary>
    private readonly List<HistoricalCaseRecord> _caseLibrary = [];

    public HistoricalSimilarityResult FindSimilar(HistoricalSimilarityRequest request)
    {
        var trade = request.Trade;

        // ── Score every case against the query ───────────────────────
        var scored = new List<HistoricalCaseSummary>();

        foreach (var historicalCase in _caseLibrary)
        {
            var score = ComputeSimilarity(trade, historicalCase);
            if (score < request.MinSimilarityScore)
                continue;

            scored.Add(new HistoricalCaseSummary
            {
                CaseId = historicalCase.CaseId,
                Ticker = historicalCase.Ticker,
                Date = historicalCase.Date,
                PredictionDirection = historicalCase.Direction,
                TradeGrade = historicalCase.Grade,
                MarketRegime = historicalCase.MarketRegime,
                Outcome = historicalCase.Outcome,
                ReturnPercent = historicalCase.ReturnPercent,
                HoldingPeriod = historicalCase.HoldingPeriod,
                SimilarityScore = score,
            });
        }

        // ── Rank and trim ────────────────────────────────────────────
        var matches = scored
            .OrderByDescending(c => c.SimilarityScore)
            .Take(request.MaxResults)
            .ToList();

        // ── Aggregate stats ──────────────────────────────────────────
        if (matches.Count == 0)
        {
            return new HistoricalSimilarityResult
            {
                Summary = "No similar historical cases found.",
            };
        }

        var returns = matches.Select(m => m.ReturnPercent).OrderBy(r => r).ToList();
        var avgReturn = Math.Round(returns.Average(), 2);
        var medianReturn = Math.Round(Median(returns), 2);
        var winRate = Math.Round(
            (double)matches.Count(m => m.Outcome == "win") / matches.Count, 4);
        var avgHolding = Math.Round(
            matches.Average(m => m.HoldingPeriod), 1);

        // ── Lessons (placeholder logic) ──────────────────────────────
        var lessons = GenerateLessons(matches, winRate, avgReturn, avgHolding);

        // ── Summary ──────────────────────────────────────────────────
        var summary = $"Found {matches.Count} similar historical " +
                      $"opportunit{(matches.Count == 1 ? "y" : "ies")} " +
                      $"with a {winRate:P0} win rate and an average return of {avgReturn:F1}%.";

        return new HistoricalSimilarityResult
        {
            MatchingCases = matches,
            AverageReturn = avgReturn,
            MedianReturn = medianReturn,
            WinRate = winRate,
            AverageHoldingPeriod = avgHolding,
            TopLessons = lessons,
            Summary = summary,
        };
    }

    // ═════════════════════════════════════════════════════════════════
    // Similarity scoring
    // ═════════════════════════════════════════════════════════════════

    private static double ComputeSimilarity(
        Models.TradeDecision query,
        HistoricalCaseRecord candidate)
    {
        var gradeScore = ScoreGrade(query.TradeGrade, candidate.Grade);
        var directionScore = ScoreDirection(query.Direction, candidate.Direction);
        var featureScore = ScoreOverlap(query.GradeResult?.Strengths, candidate.Features);
        var evidenceScore = ScoreOverlap(
            query.Explanation?.SupportingEvidence, candidate.Evidence);

        var raw = (gradeScore * WeightTradeGrade)
                + (directionScore * WeightDirection)
                + (featureScore * WeightFeatureOverlap)
                + (evidenceScore * WeightEvidenceOverlap);

        return Math.Round(raw * 100.0, 2);
    }

    /// <summary>
    /// 1.0 = exact grade match, linear decay for distance.
    /// </summary>
    private static double ScoreGrade(TradeGrade query, TradeGrade candidate)
    {
        var distance = Math.Abs((int)query - (int)candidate);
        // Max possible distance is 6 (Unspecified=0 → APlus=6)
        return Math.Max(0.0, 1.0 - (distance / 6.0));
    }

    /// <summary>
    /// 1.0 = same direction, 0.5 = one or both null/neutral, 0.0 = opposite.
    /// </summary>
    private static double ScoreDirection(string? query, string? candidate)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(candidate))
            return 0.5;

        var q = query.ToLowerInvariant();
        var c = candidate.ToLowerInvariant();

        if (q == c) return 1.0;
        if (q == "neutral" || c == "neutral") return 0.5;
        return 0.0; // opposite directions
    }

    /// <summary>
    /// Jaccard-style overlap: |intersection| / |union|.
    /// Returns 0.5 when either list is null/empty (no data to compare).
    /// </summary>
    private static double ScoreOverlap(
        IReadOnlyList<string>? queryItems,
        IReadOnlyList<string>? candidateItems)
    {
        if (queryItems is null or { Count: 0 } ||
            candidateItems is null or { Count: 0 })
            return 0.5; // neutral — no penalty for missing data

        var qSet = new HashSet<string>(
            queryItems.Select(s => s.ToLowerInvariant()));
        var cSet = new HashSet<string>(
            candidateItems.Select(s => s.ToLowerInvariant()));

        var intersection = qSet.Intersect(cSet).Count();
        var union = qSet.Union(cSet).Count();

        return union == 0 ? 0.5 : (double)intersection / union;
    }

    // ═════════════════════════════════════════════════════════════════
    // Lesson generation (placeholder)
    // ═════════════════════════════════════════════════════════════════

    private static List<string> GenerateLessons(
        List<HistoricalCaseSummary> matches,
        double winRate,
        double avgReturn,
        double avgHolding)
    {
        var lessons = new List<string>();

        if (winRate >= 0.70)
            lessons.Add("Historically reliable setup — above 70% win rate.");
        else if (winRate >= 0.50)
            lessons.Add("Moderate historical reliability — roughly coin-flip odds.");
        else
            lessons.Add("Below-average historical win rate — proceed with caution.");

        if (avgReturn > 10.0)
            lessons.Add("Strong average returns when this setup works.");
        else if (avgReturn > 0)
            lessons.Add("Positive but modest average returns historically.");
        else
            lessons.Add("Negative average returns — setup has struggled historically.");

        if (avgHolding <= 3)
            lessons.Add("Most similar cases resolved quickly (≤3 trading days).");
        else if (avgHolding <= 7)
            lessons.Add("Typical holding period around one trading week.");
        else
            lessons.Add("Longer holding periods expected — patience may be required.");

        // Grade distribution insight
        var highGradeCount = matches.Count(m =>
            m.TradeGrade is TradeGrade.A or TradeGrade.APlus);
        if (highGradeCount > matches.Count / 2)
            lessons.Add("Majority of similar cases were A-grade or higher.");

        return lessons;
    }

    // ═════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════

    private static double Median(List<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    // ═════════════════════════════════════════════════════════════════
    // Internal case record (future: replaced by DB-backed provider)
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Internal representation of a historical case.
    /// In Phase 1 the library is empty.  A future phase will populate
    /// this from setup_learning_stats / trade_setups via an injected
    /// case provider, without changing the engine's interface.
    /// </summary>
    internal record HistoricalCaseRecord
    {
        public string CaseId { get; init; } = "";
        public string? Ticker { get; init; }
        public DateTimeOffset Date { get; init; }
        public string? Direction { get; init; }
        public TradeGrade Grade { get; init; }
        public MarketRegimeType MarketRegime { get; init; }
        public string Outcome { get; init; } = "";
        public double ReturnPercent { get; init; }
        public int HoldingPeriod { get; init; }
        /// <summary>Features/strengths from the original trade grade.</summary>
        public List<string> Features { get; init; } = [];
        /// <summary>Supporting evidence from the original explanation.</summary>
        public List<string> Evidence { get; init; } = [];
    }
}
