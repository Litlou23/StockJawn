using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Services.ResearchEngine;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Controllers;

[ApiController]
[Route("api/learning")]
public class LearningController : ControllerBase
{
    private readonly ResearchRepository _repo;
    private readonly PatternDetectionService _patternDetection;

    public LearningController(ResearchRepository repo, PatternDetectionService patternDetection)
    {
        _repo = repo;
        _patternDetection = patternDetection;
    }

    [HttpGet("report/latest")]
    public async Task<IActionResult> GetLatestReport()
    {
        var report = await _repo.GetLatestLearningReportAsync();
        if (report is null)
            return Ok(new { available = false, message = "No learning report generated yet. Run an EOD review first." });

        return Ok(new
        {
            available = true,
            report.ReportDate,
            report.PredictionCount,
            report.OverallAccuracy,
            report.BullAccuracy,
            report.BearAccuracy,
            report.MarketRegime,
            report.TopSignals,
            report.WeakSignals,
            report.WeightChanges,
            report.ConfidenceCalibration,
            report.AiSummary,
            report.EvaluationWindowDays,
        });
    }

    [HttpGet("signals")]
    public async Task<IActionResult> GetSignalPerformance()
    {
        var signals = await _repo.GetAllSignalPerformanceAsync();
        return Ok(new
        {
            count = signals.Count,
            signals = signals.Select(s => new
            {
                s.SignalName,
                s.SignalType,
                s.Direction,
                s.TotalPredictions,
                s.CorrectPredictions,
                accuracy = Math.Round(s.Accuracy * 100, 1),
                s.AverageOutcomeScore,
                s.LastUpdatedAt,
            }).OrderByDescending(s => s.accuracy),
        });
    }

    [HttpGet("weights")]
    public async Task<IActionResult> GetWeightOverrides()
    {
        var overrides = await _repo.GetActiveWeightOverridesAsync();
        return Ok(new
        {
            count = overrides.Count,
            weights = overrides.Select(w => new
            {
                w.SignalName,
                w.BaseWeight,
                adjustmentPercent = Math.Round(w.AdjustmentPercent * 100, 2),
                w.EffectiveWeight,
                w.Confidence,
                w.SampleSize,
                w.Status,
                w.Reason,
                w.LastUpdated,
            }),
        });
    }

    [HttpGet("patterns/full-analysis")]
    public async Task<IActionResult> GetFullPatternAnalysis()
    {
        var analysis = await _patternDetection.RunFullPatternAnalysisAsync();
        return Ok(analysis);
    }

    [HttpGet("model-performance")]
    public async Task<IActionResult> GetModelPerformance()
    {
        var signals = await _repo.GetAllSignalPerformanceAsync();
        var overrides = await _repo.GetActiveWeightOverridesAsync();
        var overrideMap = overrides.ToDictionary(o => o.SignalName);

        // 1. Scoring buckets — the 8 independent scoring dimensions
        var bucketNames = new[] { "trend", "momentum", "volume", "volatility",
            "market_context", "catalyst", "learning", "research_signal" };

        var scoringBuckets = bucketNames.Select(name =>
        {
            var allDir = signals.FirstOrDefault(s => s.SignalName == name && s.Direction == "all");
            var bullDir = signals.FirstOrDefault(s => s.SignalName == name && s.Direction == "bullish");
            var bearDir = signals.FirstOrDefault(s => s.SignalName == name && s.Direction == "bearish");
            var weight = overrideMap.TryGetValue(name, out var ov) ? ov : null;

            return new
            {
                name,
                overallAccuracy = allDir is not null ? Math.Round(allDir.Accuracy * 100, 1) : (double?)null,
                overallSample = allDir?.TotalPredictions ?? 0,
                bullAccuracy = bullDir is not null ? Math.Round(bullDir.Accuracy * 100, 1) : (double?)null,
                bullSample = bullDir?.TotalPredictions ?? 0,
                bearAccuracy = bearDir is not null ? Math.Round(bearDir.Accuracy * 100, 1) : (double?)null,
                bearSample = bearDir?.TotalPredictions ?? 0,
                currentWeight = weight?.EffectiveWeight,
                baseWeight = weight?.BaseWeight ?? 1.0,
                adjustmentPercent = weight is not null ? Math.Round(weight.AdjustmentPercent * 100, 2) : 0.0,
                avgOutcomeScore = allDir?.AverageOutcomeScore,
            };
        }).ToList();

        // 2. Ensemble models — performance tracked as ensemble_* signals
        var ensembleModels = signals
            .Where(s => s.SignalName.StartsWith("ensemble_") && s.Direction == "all")
            .Select(s => new
            {
                modelName = s.SignalName.Replace("ensemble_", ""),
                accuracy = Math.Round(s.Accuracy * 100, 1),
                sample = s.TotalPredictions,
                avgOutcomeScore = s.AverageOutcomeScore,
                lastUpdated = s.LastUpdatedAt,
            }).ToList();

        // 3. Catalyst event types — performance tracked as catalyst_* signals
        var catalystTypes = signals
            .Where(s => s.SignalName.StartsWith("catalyst_") && s.Direction == "all")
            .OrderByDescending(s => s.TotalPredictions)
            .Select(s => new
            {
                eventType = s.SignalName.Replace("catalyst_", ""),
                accuracy = Math.Round(s.Accuracy * 100, 1),
                sample = s.TotalPredictions,
                avgOutcomeScore = s.AverageOutcomeScore,
            }).ToList();

        // 4. Signal synergies (from pattern detection)
        PatternDetectionService.SignalCombinationResult? synergies = null;
        try
        {
            synergies = await _patternDetection.AnalyzeSignalCombinationsAsync();
        }
        catch { /* non-critical */ }

        // 5. Pattern detection regime clusters (summary)
        PatternDetectionService.FailureClusterResult? clusters = null;
        try
        {
            clusters = await _patternDetection.AnalyzeFailureClustersAsync();
        }
        catch { /* non-critical */ }

        return Ok(new
        {
            scoringBuckets,
            ensembleModels,
            catalystTypes,
            synergies = synergies is not null ? new
            {
                bestCombinations = synergies.BestCombinations.Select(c => new
                {
                    c.Signal1, c.Signal2, c.CoOccurrences,
                    jointAccuracy = c.JointAccuracy,
                    signal1Alone = c.Signal1Alone,
                    signal2Alone = c.Signal2Alone,
                    synergyScore = c.SynergyScore,
                    c.Interpretation,
                }),
                worstCombinations = synergies.WorstCombinations.Select(c => new
                {
                    c.Signal1, c.Signal2, c.CoOccurrences,
                    jointAccuracy = c.JointAccuracy,
                    signal1Alone = c.Signal1Alone,
                    signal2Alone = c.Signal2Alone,
                    synergyScore = c.SynergyScore,
                    c.Interpretation,
                }),
            } : null,
            failureClusters = clusters is not null ? new
            {
                clusters.TotalFailures,
                clusters = clusters.Clusters.Select(c => new
                {
                    c.ClusterName, c.Count, c.CommonTraits,
                    avgConfidence = Math.Round(c.AvgConfidence, 1),
                    c.SuggestedAction,
                }),
            } : null,
        });
    }
}
