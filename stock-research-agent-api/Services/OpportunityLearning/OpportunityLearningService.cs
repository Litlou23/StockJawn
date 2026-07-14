using System.Diagnostics;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Discovery;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.ResearchUniverse;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.OpportunityLearning;

/// <summary>
/// Scans for significant stock movements and evaluates how well our pipeline
/// anticipated each one. Persists every finding for analytics.
///
/// This is observation only — no weight updates.
/// </summary>
public class OpportunityLearningService : IOpportunityLearningService
{
    private readonly IOpportunityLearningRepository _repo;
    private readonly IDiscoveryEventRepository _discoveryRepo;
    private readonly IResearchUniverseService _universe;
    private readonly ResearchRepository _researchRepo;
    private readonly MarketDataService _marketData;
    private readonly OpportunityLearningConfig _config;
    private readonly ILogger<OpportunityLearningService> _logger;

    public OpportunityLearningService(
        IOpportunityLearningRepository repo,
        IDiscoveryEventRepository discoveryRepo,
        IResearchUniverseService universe,
        ResearchRepository researchRepo,
        MarketDataService marketData,
        OpportunityLearningConfig config,
        ILogger<OpportunityLearningService> logger)
    {
        _repo = repo;
        _discoveryRepo = discoveryRepo;
        _universe = universe;
        _researchRepo = researchRepo;
        _marketData = marketData;
        _config = config;
        _logger = logger;
    }

    public OpportunityLearningConfig GetConfig() => _config;

    // ── Full scan ──────────────────────────────────────────────

    public async Task<OpportunityScanResult> ScanForMissedOpportunitiesAsync(
        List<string>? tickersToScan = null)
    {
        var sw = Stopwatch.StartNew();
        var errors = new List<string>();
        var allRecords = new List<OpportunityLearningRecord>();
        var skipped = 0;

        // Determine which tickers to scan
        var tickers = tickersToScan ?? _config.ScanTickers;
        if (tickers.Count == 0)
        {
            // Build a combined list from Research Universe + recent discoveries
            var universeAssets = await _universe.GetActiveAssetsAsync(500);
            var universeTickers = universeAssets.Select(a => a.Ticker).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var recentDiscoveries = await _discoveryRepo.GetRecentAsync(200);
            foreach (var d in recentDiscoveries)
                universeTickers.Add(d.Ticker.ToUpperInvariant());

            tickers = universeTickers.ToList();
        }

        _logger.LogInformation("[opportunity-learning] Scanning {Count} tickers for missed opportunities",
            tickers.Count);

        // Pre-fetch all existing keys for today in one HTTP call instead of N ExistsAsync checks
        var existingKeys = await _repo.GetExistingKeysAsync(DateTimeOffset.UtcNow);
        var newRecords = new List<OpportunityLearningRecord>();

        foreach (var ticker in tickers)
        {
            try
            {
                var records = await ScanTickerAsync(ticker);
                if (records.Count == 0)
                {
                    skipped++;
                    continue;
                }

                // Deduplicate — skip if we already have a record for this ticker+date+period
                foreach (var r in records)
                {
                    var key = $"{r.Ticker}|{r.MeasurementPeriod}";
                    if (!existingKeys.Contains(key))
                    {
                        newRecords.Add(r);
                        allRecords.Add(r);
                        existingKeys.Add(key); // prevent duplicates within this batch
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[opportunity-learning] Failed to scan {Ticker}", ticker);
                errors.Add($"{ticker}: {ex.Message}");
            }
        }

        // Batch insert all new records in one HTTP call
        if (newRecords.Count > 0)
            await _repo.PersistManyAsync(newRecords);

        sw.Stop();

        var captured = allRecords.Count(r => r.CaptureStatus == OpportunityCaptureStatus.Captured);
        var partial = allRecords.Count(r => r.CaptureStatus == OpportunityCaptureStatus.PartiallyCaptured);
        var missed = allRecords.Count(r => r.CaptureStatus == OpportunityCaptureStatus.CompletelyMissed);
        var wrong = allRecords.Count(r => r.CaptureStatus == OpportunityCaptureStatus.WrongDirection);
        var neutralCount = allRecords.Count(r => r.CaptureStatus == OpportunityCaptureStatus.NeutralPrediction);

        var summary = $"Scanned {tickers.Count} tickers in {sw.Elapsed.TotalSeconds:F1}s. " +
                      $"Found {allRecords.Count} significant movers: " +
                      $"{captured} captured, {partial} partially captured, " +
                      $"{wrong} wrong direction, {neutralCount} neutral, {missed} completely missed. " +
                      $"{skipped} skipped (duplicate or no significant move).";

        _logger.LogInformation("[opportunity-learning] {Summary}", summary);

        return new OpportunityScanResult
        {
            TickersScanned = tickers.Count,
            SignificantMoversFound = allRecords.Count,
            RecordsCreated = allRecords.Count,
            Captured = captured,
            PartiallyCaptured = partial,
            CompletelyMissed = missed,
            WrongDirection = wrong,
            NeutralPrediction = neutralCount,
            Skipped = skipped,
            Errors = errors,
            Summary = summary,
        };
    }

    // ── Single ticker scan ─────────────────────────────────────

    /// <summary>
    /// Check a ticker's recent price history for significant moves across all
    /// configured measurement periods.
    /// </summary>
    private async Task<List<OpportunityLearningRecord>> ScanTickerAsync(string ticker)
    {
        var records = new List<OpportunityLearningRecord>();
        var minThreshold = _config.MovementThresholds.Min();

        // Get recent bars (enough for the longest measurement period)
        var maxDays = _config.MeasurementPeriods.Values.Max() + 1;
        var bars = await _marketData.GetRecentBarsAsync(ticker, maxDays + 5);

        if (bars.Count < 2) return records;

        // Check each measurement period
        foreach (var (periodName, tradingDays) in _config.MeasurementPeriods)
        {
            if (bars.Count <= tradingDays) continue;

            var currentBar = bars[0]; // Most recent
            var comparisonBar = bars[Math.Min(tradingDays, bars.Count - 1)];

            if (comparisonBar.Close <= 0) continue;

            var percentMove = ((currentBar.Close - comparisonBar.Close) / comparisonBar.Close) * 100;
            var absMove = Math.Abs(percentMove);

            if (absMove < minThreshold) continue;

            // Significant move detected — evaluate it
            var direction = percentMove > 0 ? "up" : "down";
            var evaluated = await EvaluateTickerAsync(
                ticker, percentMove, direction,
                comparisonBar.Close, currentBar.Close, periodName);

            records.AddRange(evaluated);
        }

        return records;
    }

    // ── Evaluate a single move ─────────────────────────────────

    public async Task<List<OpportunityLearningRecord>> EvaluateTickerAsync(
        string ticker, double percentMove, string direction,
        double startPrice, double endPrice, string measurementPeriod)
    {
        ticker = ticker.ToUpperInvariant();
        var absMove = Math.Abs(percentMove);
        var highestTier = DetermineHighestTier(absMove);
        var now = DateTimeOffset.UtcNow;

        // ── 1. Check discovery awareness ───────────────────────
        var discoveryEvents = await _discoveryRepo.GetByTickerAsync(ticker, 10);
        var wasDiscovered = discoveryEvents.Count > 0;
        var firstDiscovery = discoveryEvents
            .OrderBy(e => e.Timestamp)
            .FirstOrDefault();
        var discoveryDate = firstDiscovery?.Timestamp;
        var daysBeforeMove = discoveryDate.HasValue
            ? (int)(now - discoveryDate.Value).TotalDays
            : (int?)null;
        var discoverySource = firstDiscovery?.Source;

        // ── 2. Check Research Universe awareness ───────────────
        var researchAsset = await _universe.GetByTickerAsync(ticker);
        var wasInUniverse = researchAsset is not null;
        string? researchState = researchAsset?.CurrentState.ToString();
        int? interestScore = researchAsset?.InterestScore;
        int? evidenceCount = researchAsset?.EvidenceCount;

        // ── 3. Check prediction awareness ──────────────────────
        var lookbackFrom = now.AddDays(-_config.PredictionLookbackDays);
        var recentPredictions = await _researchRepo.GetPredictionsByDateRangeAsync(
            lookbackFrom, now, extraFilter: $"ticker=eq.{ticker}");

        var hadPrediction = recentPredictions.Count > 0;
        PredictionCandidate? bestPrediction = null;
        bool? predictionCorrectDirection = null;
        var hadNeutralPrediction = false;

        if (hadPrediction)
        {
            // Prefer the highest-confidence directional prediction; fall back to neutral
            bestPrediction = recentPredictions
                .Where(p => PredictionCategoryHelper.IsDirectional(p.PredictionType))
                .OrderByDescending(p => p.ConfidenceScore)
                .FirstOrDefault()
                ?? recentPredictions
                    .OrderByDescending(p => p.ConfidenceScore)
                    .First();

            if (PredictionCategoryHelper.IsDirectional(bestPrediction.PredictionType))
            {
                var predBullish = bestPrediction.PredictionType == PredictionType.bullish;
                var moveBullish = direction == "up";
                predictionCorrectDirection = predBullish == moveBullish;
            }
            else
            {
                // Neutral predictions intentionally express no direction
                hadNeutralPrediction = true;
                predictionCorrectDirection = null;
            }
        }

        // ── 4. Determine capture status ────────────────────────
        var captureStatus = DetermineCaptureStatus(
            wasDiscovered, wasInUniverse, hadPrediction, hadNeutralPrediction, predictionCorrectDirection);

        // ── 5. Determine miss reasons ──────────────────────────
        var missReasons = DetermineMissReasons(
            wasDiscovered, wasInUniverse, hadPrediction, hadNeutralPrediction,
            predictionCorrectDirection, bestPrediction, researchAsset);

        // ── 6. Build summary ───────────────────────────────────
        var summary = BuildSummary(
            ticker, percentMove, direction, measurementPeriod,
            captureStatus, wasDiscovered, wasInUniverse, hadPrediction,
            bestPrediction, missReasons);

        var record = new OpportunityLearningRecord
        {
            Ticker = ticker,
            ScanDate = now,
            PercentMove = Math.Round(percentMove, 2),
            MoveDirection = direction,
            StartPrice = Math.Round(startPrice, 2),
            EndPrice = Math.Round(endPrice, 2),
            HighestTier = highestTier,
            MeasurementPeriod = measurementPeriod,
            // Discovery
            WasDiscovered = wasDiscovered,
            DiscoveryDate = discoveryDate,
            DaysBeforeMove = daysBeforeMove,
            DiscoverySource = discoverySource,
            // Research Universe
            WasInResearchUniverse = wasInUniverse,
            ResearchState = researchState,
            InterestScoreAtMove = interestScore,
            EvidenceCountAtMove = evidenceCount,
            // Prediction
            HadPrediction = hadPrediction,
            PredictionCorrectDirection = predictionCorrectDirection,
            PredictionConfidence = bestPrediction?.ConfidenceScore,
            PredictionRisk = bestPrediction?.RiskScore,
            PredictionType = bestPrediction?.PredictionType.ToString(),
            PredictionId = bestPrediction?.Id,
            // Analysis
            CaptureStatus = captureStatus,
            MissReasons = missReasons,
            Summary = summary,
        };

        return [record];
    }

    // ── Analytics ──────────────────────────────────────────────

    public async Task<OpportunityAnalytics> GetAnalyticsAsync(
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var actualFrom = from ?? DateTimeOffset.UtcNow.AddDays(-30);
        var actualTo = to ?? DateTimeOffset.UtcNow;

        var records = await _repo.GetByDateRangeAsync(actualFrom, actualTo, _config.MaxAnalyticsResults);

        if (records.Count == 0)
        {
            return new OpportunityAnalytics
            {
                FromDate = actualFrom,
                ToDate = actualTo,
            };
        }

        var captured = records.Count(r => r.CaptureStatus == OpportunityCaptureStatus.Captured);
        var partial = records.Count(r => r.CaptureStatus == OpportunityCaptureStatus.PartiallyCaptured);
        var wrong = records.Count(r => r.CaptureStatus == OpportunityCaptureStatus.WrongDirection);
        var missed = records.Count(r => r.CaptureStatus == OpportunityCaptureStatus.CompletelyMissed);
        var neutral = records.Count(r => r.CaptureStatus == OpportunityCaptureStatus.NeutralPrediction);
        var total = records.Count;

        // By tier
        var byTier = records
            .GroupBy(r => r.HighestTier.ToString())
            .ToDictionary(
                g => g.Key,
                g => new TierBreakdown
                {
                    Total = g.Count(),
                    Captured = g.Count(r => r.CaptureStatus == OpportunityCaptureStatus.Captured),
                    Missed = g.Count(r => r.CaptureStatus != OpportunityCaptureStatus.Captured),
                    CaptureRate = g.Count() > 0
                        ? Math.Round(100.0 * g.Count(r => r.CaptureStatus == OpportunityCaptureStatus.Captured) / g.Count(), 1)
                        : 0,
                });

        // By period
        var byPeriod = records
            .GroupBy(r => r.MeasurementPeriod)
            .ToDictionary(
                g => g.Key,
                g => new TierBreakdown
                {
                    Total = g.Count(),
                    Captured = g.Count(r => r.CaptureStatus == OpportunityCaptureStatus.Captured),
                    Missed = g.Count(r => r.CaptureStatus != OpportunityCaptureStatus.Captured),
                    CaptureRate = g.Count() > 0
                        ? Math.Round(100.0 * g.Count(r => r.CaptureStatus == OpportunityCaptureStatus.Captured) / g.Count(), 1)
                        : 0,
                });

        // Top miss reasons
        var topMissReasons = records
            .SelectMany(r => r.MissReasons)
            .GroupBy(reason => reason)
            .OrderByDescending(g => g.Count())
            .Select(g => new MissReasonCount(g.Key, g.Count()))
            .Take(10)
            .ToList();

        // Average discovery lead days (only for discovered opportunities)
        var discoveredWithLead = records
            .Where(r => r.WasDiscovered && r.DaysBeforeMove.HasValue)
            .Select(r => (double)r.DaysBeforeMove!.Value)
            .ToList();
        double? avgLead = discoveredWithLead.Count > 0
            ? Math.Round(discoveredWithLead.Average(), 1)
            : null;

        return new OpportunityAnalytics
        {
            TotalOpportunities = total,
            Captured = captured,
            PartiallyCaptured = partial,
            WrongDirection = wrong,
            CompletelyMissed = missed,
            NeutralPrediction = neutral,
            CaptureRate = total > 0 ? Math.Round(100.0 * captured / total, 1) : 0,
            AwarenessRate = total > 0 ? Math.Round(100.0 * (captured + partial + neutral) / total, 1) : 0,
            ByTier = byTier,
            ByPeriod = byPeriod,
            TopMissReasons = topMissReasons,
            AverageDiscoveryLeadDays = avgLead,
            FromDate = actualFrom,
            ToDate = actualTo,
        };
    }

    // ── Helpers ─────────────────────────────────────────────────

    private MovementTier DetermineHighestTier(double absPercentMove)
    {
        var thresholds = _config.MovementThresholds.OrderByDescending(t => t).ToList();

        if (thresholds.Count >= 4 && absPercentMove >= thresholds[0]) return MovementTier.Tier4;
        if (thresholds.Count >= 3 && absPercentMove >= thresholds[1]) return MovementTier.Tier3;
        if (thresholds.Count >= 2 && absPercentMove >= thresholds[2]) return MovementTier.Tier2;
        return MovementTier.Tier1;
    }

    private static OpportunityCaptureStatus DetermineCaptureStatus(
        bool wasDiscovered, bool wasInUniverse, bool hadPrediction,
        bool hadNeutralPrediction, bool? predictionCorrect)
    {
        if (hadPrediction && hadNeutralPrediction)
            return OpportunityCaptureStatus.NeutralPrediction;

        if (hadPrediction && predictionCorrect == true)
            return OpportunityCaptureStatus.Captured;

        if (hadPrediction && predictionCorrect == false)
            return OpportunityCaptureStatus.WrongDirection;

        if (wasDiscovered || wasInUniverse)
            return OpportunityCaptureStatus.PartiallyCaptured;

        return OpportunityCaptureStatus.CompletelyMissed;
    }

    private static List<string> DetermineMissReasons(
        bool wasDiscovered, bool wasInUniverse, bool hadPrediction,
        bool hadNeutralPrediction, bool? predictionCorrect,
        PredictionCandidate? prediction, ResearchAsset? researchAsset)
    {
        var reasons = new List<string>();

        if (!wasDiscovered)
        {
            reasons.Add(MissedOpportunityReason.NeverDiscovered.ToString());
            reasons.Add(MissedOpportunityReason.MissingWatchlistEntry.ToString());
            return reasons;
        }

        if (!wasInUniverse)
        {
            reasons.Add(MissedOpportunityReason.NotInResearchUniverse.ToString());
            return reasons;
        }

        if (researchAsset?.CurrentState == ResearchState.Archived)
        {
            reasons.Add(MissedOpportunityReason.ArchivedTooEarly.ToString());
        }

        if (!hadPrediction)
        {
            reasons.Add(MissedOpportunityReason.NoPredictionGenerated.ToString());

            // Try to determine why no prediction was generated
            if (researchAsset?.InterestScore < 15)
                reasons.Add(MissedOpportunityReason.MissingCatalyst.ToString());

            if (researchAsset?.EvidenceCount < 2)
                reasons.Add(MissedOpportunityReason.MissingNews.ToString());

            return reasons;
        }

        // Neutral prediction — the engine saw the ticker but chose not to express a direction
        if (hadNeutralPrediction)
        {
            reasons.Add(MissedOpportunityReason.NeutralPrediction.ToString());
            return reasons;
        }

        // Had a directional prediction — analyze its quality
        if (predictionCorrect == false)
        {
            reasons.Add(MissedOpportunityReason.WrongDirection.ToString());
        }

        if (prediction is not null)
        {
            if (prediction.ConfidenceScore < 30)
                reasons.Add(MissedOpportunityReason.LowConfidence.ToString());

            if (prediction.RiskScore > 70)
                reasons.Add(MissedOpportunityReason.HighRisk.ToString());

            // Check data sources for missing signals
            var sources = prediction.DataSourcesUsed;
            if (!sources.Any(s => s.Contains("news", StringComparison.OrdinalIgnoreCase)))
                reasons.Add(MissedOpportunityReason.MissingNews.ToString());

            if (!sources.Any(s => s.Contains("twelve-data", StringComparison.OrdinalIgnoreCase)))
                reasons.Add(MissedOpportunityReason.MissingTechnicalConfirmation.ToString());

            if (prediction.MissingDataWarnings.Any(w => w.Contains("volume", StringComparison.OrdinalIgnoreCase)))
                reasons.Add(MissedOpportunityReason.MissingVolume.ToString());
        }

        return reasons;
    }

    private static string BuildSummary(
        string ticker, double percentMove, string direction, string period,
        OpportunityCaptureStatus status, bool discovered, bool inUniverse,
        bool hadPrediction, PredictionCandidate? prediction, List<string> missReasons)
    {
        var parts = new List<string>
        {
            $"{ticker} moved {percentMove:+0.0;-0.0}% ({direction}) over {period}.",
        };

        switch (status)
        {
            case OpportunityCaptureStatus.Captured:
                parts.Add($"CAPTURED: predicted {prediction!.PredictionType} with {prediction.ConfidenceScore}% confidence.");
                break;

            case OpportunityCaptureStatus.WrongDirection:
                parts.Add($"WRONG DIRECTION: predicted {prediction!.PredictionType} but stock went {direction}.");
                break;

            case OpportunityCaptureStatus.NeutralPrediction:
                parts.Add($"NEUTRAL: predicted {prediction!.PredictionType} — no directional opinion expressed.");
                break;

            case OpportunityCaptureStatus.PartiallyCaptured:
                parts.Add(discovered && inUniverse
                    ? $"PARTIALLY CAPTURED: in Research Universe (state={prediction?.PredictionType.ToString() ?? "n/a"}) but no prediction generated."
                    : "PARTIALLY CAPTURED: discovered but not in Research Universe.");
                break;

            case OpportunityCaptureStatus.CompletelyMissed:
                parts.Add("COMPLETELY MISSED: never discovered by any provider.");
                break;
        }

        if (missReasons.Count > 0)
            parts.Add($"Miss reasons: {string.Join(", ", missReasons)}.");

        return string.Join(" ", parts);
    }
}
