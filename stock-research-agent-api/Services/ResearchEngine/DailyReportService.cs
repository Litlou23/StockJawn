using StockResearchAgent.Api.Models;
using static StockResearchAgent.Api.Services.ResearchEngine.OutcomeEvaluator;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Generates human-readable text reports for morning scans and EOD reviews.
/// </summary>
public class DailyReportService
{
    // -----------------------------------------------------------------------
    // Morning Report
    // -----------------------------------------------------------------------

    public string GenerateMorningReport(List<PredictionCandidate> predictions, List<MarketSnapshot> snapshots)
    {
        var parts = new List<string>();
        var now = DateTime.UtcNow;
        parts.Add($"Morning Research Scan - {now:dddd, MMMM d, yyyy}");
        parts.Add("");

        // Market overview
        var spy = snapshots.FirstOrDefault(s => s.Ticker == "SPY");
        var qqq = snapshots.FirstOrDefault(s => s.Ticker == "QQQ");
        if (spy?.Quote is not null || qqq?.Quote is not null)
        {
            parts.Add("MARKET OVERVIEW");
            if (spy?.Quote is not null)
                parts.Add($"  SPY: ${spy.Quote.Price:F2} ({(spy.Quote.ChangePercent > 0 ? "+" : "")}{spy.Quote.ChangePercent:F2}%)");
            if (qqq?.Quote is not null)
                parts.Add($"  QQQ: ${qqq.Quote.Price:F2} ({(qqq.Quote.ChangePercent > 0 ? "+" : "")}{qqq.Quote.ChangePercent:F2}%)");
            parts.Add("");
        }

        // Data warnings
        var unavailable = snapshots.Where(s => !s.DataAvailability.MarketDataAvailable).Select(s => s.Ticker).ToList();
        if (unavailable.Count > 0)
        {
            parts.Add($"DATA WARNINGS: Market data unavailable for {string.Join(", ", unavailable)}");
            parts.Add("");
        }

        // Predictions summary
        var bullish = predictions.Count(p => p.PredictionType == PredictionType.bullish);
        var bearish = predictions.Count(p => p.PredictionType == PredictionType.bearish);
        var neutral = predictions.Count(p => p.PredictionType == PredictionType.neutral);
        var watchOnly = predictions.Count(p => p.PredictionType == PredictionType.watch_only);

        parts.Add($"PREDICTIONS GENERATED: {predictions.Count} total");
        parts.Add($"  Bullish: {bullish} | Bearish: {bearish} | Neutral: {neutral} | Watch-only: {watchOnly}");
        parts.Add("");

        // Top predictions
        var topPicks = predictions.Where(p => p.ConfidenceScore >= 20)
            .OrderByDescending(p => p.ConfidenceScore).Take(5).ToList();
        if (topPicks.Count > 0)
        {
            parts.Add("TOP PREDICTIONS (by confidence):");
            foreach (var p in topPicks)
            {
                parts.Add($"  {p.Ticker} - {p.PredictionType.ToString().ToUpper()} (conf: {p.ConfidenceScore}, risk: {p.RiskScore})");
                if (p.EntryReferencePrice is not null) parts.Add($"    Entry ref: ${p.EntryReferencePrice:F2}");
                parts.Add($"    Reason: {(p.PredictionReason.Length > 150 ? p.PredictionReason[..150] : p.PredictionReason)}");
                if (p.MissingDataWarnings.Count > 0) parts.Add($"    Missing: {string.Join("; ", p.MissingDataWarnings)}");
            }
            parts.Add("");
        }

        // High-impact catalysts
        var allNews = snapshots.SelectMany(s => s.NewsContext.Select(n => (n, s.Ticker))).ToList();
        var highImpact = allNews.Where(x => x.n.ImportanceScore >= 7).Take(5).ToList();
        if (highImpact.Count > 0)
        {
            parts.Add("HIGH-IMPACT CATALYSTS:");
            foreach (var (n, ticker) in highImpact)
                parts.Add($"  [{ticker}] {n.Title} ({n.CatalystType ?? "news"}, imp={n.ImportanceScore})");
            parts.Add("");
        }

        parts.Add("This is automated research, not financial advice. All predictions are watchlist candidates only.");
        return string.Join("\n", parts);
    }

    // -----------------------------------------------------------------------
    // End-of-Day Report
    // -----------------------------------------------------------------------

    public string GenerateEndOfDayReport(List<EvaluationResult> evaluated, List<string> skipped)
    {
        var parts = new List<string>();
        var now = DateTime.UtcNow;
        parts.Add($"End-of-Day Review - {now:dddd, MMMM d, yyyy}");
        parts.Add("");
        parts.Add($"EVALUATIONS: {evaluated.Count} predictions scored, {skipped.Count} skipped");
        parts.Add("");

        if (evaluated.Count > 0)
        {
            var correct = evaluated.Count(e => e.Outcome.DirectionCorrect == true);
            var wrong = evaluated.Count(e => e.Outcome.DirectionCorrect == false);
            var neutralCount = evaluated.Count(e => e.Outcome.DirectionCorrect is null);
            var avgScore = evaluated.Average(e => e.Outcome.OutcomeScore ?? 50);

            parts.Add($"RESULTS: {correct} correct, {wrong} wrong, {neutralCount} neutral/N-A");
            parts.Add($"Average outcome score: {avgScore:F1}/100");
            if (correct + wrong > 0)
                parts.Add($"Direction accuracy: {(double)correct / (correct + wrong) * 100:F1}%");
            parts.Add("");

            parts.Add("DETAILS:");
            foreach (var e in evaluated)
            {
                var o = e.Outcome;
                var moveStr = o.PercentMove is not null ? $"{(o.PercentMove > 0 ? "+" : "")}{o.PercentMove:F2}%" : "N/A";
                var dirStr = o.DirectionCorrect == true ? "CORRECT" : o.DirectionCorrect == false ? "WRONG" : "N/A";
                parts.Add($"  {e.Ticker}: {moveStr} ({dirStr}, score: {o.OutcomeScore?.ToString("F0") ?? "N/A"})");
                if (o.Lesson is not null)
                    parts.Add($"    Lesson: {(o.Lesson.Length > 120 ? o.Lesson[..120] : o.Lesson)}");
            }
            parts.Add("");
        }

        if (skipped.Count > 0)
        {
            parts.Add("SKIPPED:");
            foreach (var s in skipped.Take(10)) parts.Add($"  {s}");
            parts.Add("");
        }

        parts.Add("This is automated research review, not financial advice.");
        return string.Join("\n", parts);
    }
}
