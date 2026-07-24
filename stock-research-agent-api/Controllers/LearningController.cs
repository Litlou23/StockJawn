using System.Text.Json.Nodes;
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

    /// <summary>
    /// Feature importance analysis — synthesizes correlation, influence, calibration,
    /// and performance data into a ranked report showing which scoring buckets
    /// are actually predictive vs adding noise.
    /// </summary>
    [HttpGet("feature-importance")]
    public async Task<IActionResult> GetFeatureImportance()
    {
        var bucketNames = new[] { "trend", "momentum", "volume", "volatility",
            "market_context", "catalyst", "learning", "research_signal" };

        // Fetch all analysis data in parallel
        var signalsTask = _repo.GetAllSignalPerformanceAsync();
        var correlationsTask = _repo.GetSignalCorrelationsAsync();
        var influenceTask = _repo.GetSignalInfluenceAsync();
        var calibrationTask = _repo.GetCalibrationBucketsAsync();
        var weightsTask = _repo.GetActiveWeightOverridesAsync();

        await Task.WhenAll(signalsTask, correlationsTask, influenceTask, calibrationTask, weightsTask);

        var signals = signalsTask.Result;
        var correlations = correlationsTask.Result;
        var influence = influenceTask.Result;
        var calibration = calibrationTask.Result;
        var weights = weightsTask.Result;
        var weightMap = weights.ToDictionary(w => w.SignalName);

        // Build correlation lookup: signal_name → correlation_r
        var corrMap = new Dictionary<string, double>();
        foreach (var row in correlations)
        {
            var name = row["signal_name"]?.GetValue<string>() ?? "";
            var r = row["correlation_r"]?.GetValue<double>() ?? 0;
            corrMap[name] = r;
        }

        // Build influence lookup: signal_name → { decisive, reinforcing, redundant, decisiveAccuracy, avgMarginImpact }
        var inflMap = new Dictionary<string, (int Decisive, int Reinforcing, int Redundant, double? DecisiveAccuracy, double AvgMarginImpact)>();
        foreach (var row in influence)
        {
            var name = row["signal_name"]?.GetValue<string>() ?? "";
            inflMap[name] = (
                row["decisive_count"]?.GetValue<int>() ?? 0,
                row["reinforcing_count"]?.GetValue<int>() ?? 0,
                row["redundant_count"]?.GetValue<int>() ?? 0,
                row["decisive_accuracy"]?.GetValue<double>(),
                row["avg_margin_impact"]?.GetValue<double>() ?? 0
            );
        }

        // Build calibration lookup: signal_name → list of { bucket, accuracy, avgReturn, sample }
        var calMap = new Dictionary<string, List<object>>();
        foreach (var row in calibration)
        {
            var name = row["signal_name"]?.GetValue<string>() ?? "";
            if (!calMap.ContainsKey(name)) calMap[name] = [];
            calMap[name].Add(new
            {
                scoreBucket = row["score_bucket"]?.GetValue<string>() ?? "",
                accuracy = row["accuracy"]?.GetValue<double>() ?? 0,
                avgReturnPercent = row["avg_return_percent"]?.GetValue<double>() ?? 0,
                sampleCount = row["sample_count"]?.GetValue<int>() ?? 0,
            });
        }

        // Synthesize per-bucket feature importance
        var features = bucketNames.Select(name =>
        {
            var perf = signals.FirstOrDefault(s => s.SignalName == name && s.Direction == "all");
            var accuracy = perf?.Accuracy ?? 0;
            var sample = perf?.TotalPredictions ?? 0;
            var correlation = corrMap.GetValueOrDefault(name, 0);
            var infl = inflMap.GetValueOrDefault(name);
            var weight = weightMap.TryGetValue(name, out var w) ? w : null;

            // Composite importance score (0-100):
            // 40% correlation strength, 30% accuracy, 20% influence, 10% sample size
            var corrScore = Math.Clamp((correlation + 0.3) / 0.6, 0, 1); // map [-0.3, 0.3] → [0, 1]
            var accScore = Math.Clamp((accuracy - 0.3) / 0.4, 0, 1);    // map [30%, 70%] → [0, 1]
            var decisiveRate = infl.Decisive + infl.Reinforcing + infl.Redundant > 0
                ? (double)(infl.Decisive + infl.Reinforcing) / (infl.Decisive + infl.Reinforcing + infl.Redundant)
                : 0.5;
            var sampleScore = Math.Clamp(sample / 200.0, 0, 1);

            var importanceScore = Math.Round(
                (corrScore * 40 + accScore * 30 + decisiveRate * 20 + sampleScore * 10), 1);

            // Generate verdict
            string verdict;
            if (importanceScore >= 65) verdict = "strong_predictor";
            else if (importanceScore >= 45) verdict = "moderate_predictor";
            else if (importanceScore >= 25) verdict = "weak_predictor";
            else verdict = "noise";

            // Generate recommendation
            string recommendation;
            var effectiveWt = weight?.EffectiveWeight ?? 1.0;
            if (verdict == "noise" && effectiveWt > 0.5)
                recommendation = $"Downweight — low correlation ({correlation:+0.00;-0.00}) and mostly redundant. Consider reducing to {Math.Max(0.3, effectiveWt * 0.7):F2}.";
            else if (verdict == "strong_predictor" && effectiveWt < 1.2)
                recommendation = $"Upweight — strong correlation ({correlation:+0.00;-0.00}) and high influence. Consider increasing to {Math.Min(2.0, effectiveWt * 1.2):F2}.";
            else if (correlation < -0.1 && sample >= 30)
                recommendation = $"Warning — negative correlation ({correlation:+0.00;-0.00}) means higher scores predict WORSE outcomes. This signal may be inverted or miscalibrated.";
            else
                recommendation = "No change needed — performing as expected.";

            return new
            {
                name,
                importanceScore,
                verdict,
                accuracy = Math.Round(accuracy * 100, 1),
                sampleSize = sample,
                correlation = Math.Round(correlation, 4),
                decisiveCount = infl.Decisive,
                reinforcingCount = infl.Reinforcing,
                redundantCount = infl.Redundant,
                decisiveAccuracy = infl.DecisiveAccuracy is not null
                    ? Math.Round(infl.DecisiveAccuracy.Value * 100, 1) : (double?)null,
                avgMarginImpact = Math.Round(infl.AvgMarginImpact, 2),
                currentWeight = weight?.EffectiveWeight,
                baseWeight = weight?.BaseWeight ?? 1.0,
                calibration = calMap.GetValueOrDefault(name),
                recommendation,
            };
        })
        .OrderByDescending(f => f.importanceScore)
        .ToList();

        // Summary insights
        var strongPredictors = features.Where(f => f.verdict == "strong_predictor").Select(f => f.name).ToList();
        var noiseSignals = features.Where(f => f.verdict == "noise").Select(f => f.name).ToList();
        var negativeCorrelations = features.Where(f => f.correlation < -0.05 && f.sampleSize >= 20)
            .Select(f => new { f.name, f.correlation }).ToList();

        return Ok(new
        {
            features,
            summary = new
            {
                strongPredictors,
                noiseSignals,
                negativeCorrelations = negativeCorrelations.Select(n => new { n.name, correlation = n.correlation }),
                totalSample = features.Sum(f => f.sampleSize) / bucketNames.Length, // predictions analyzed
                actionItems = features.Where(f => !f.recommendation.StartsWith("No change"))
                    .Select(f => new { signal = f.name, f.recommendation }).ToList(),
            },
        });
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
