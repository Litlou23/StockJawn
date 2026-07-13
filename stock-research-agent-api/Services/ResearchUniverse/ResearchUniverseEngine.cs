using System.Diagnostics;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Evidence;

namespace StockResearchAgent.Api.Services.ResearchUniverse;

/// <summary>
/// Maintains every Research Asset through configurable lifecycle rules.
///
/// Runs as a periodic maintenance job. Each cycle:
///   1. Refreshes days-active for all assets
///   2. Decays interest scores on stale assets
///   3. Archives assets past staleness thresholds or below minimum score
///   4. Promotes assets that meet state transition criteria
///   5. Updates holding windows from dominant evidence types
///   6. Syncs thesis from evidence aggregation
/// </summary>
public class ResearchUniverseEngine : IResearchUniverseEngine
{
    private readonly IResearchUniverseService _service;
    private readonly IResearchUniverseRepository _repo;
    private readonly IEvidenceService _evidence;
    private readonly ResearchUniverseConfig _config;
    private readonly ILogger<ResearchUniverseEngine> _logger;

    public ResearchUniverseEngine(
        IResearchUniverseService service,
        IResearchUniverseRepository repo,
        IEvidenceService evidence,
        ResearchUniverseConfig config,
        ILogger<ResearchUniverseEngine> logger)
    {
        _service = service;
        _repo = repo;
        _evidence = evidence;
        _config = config;
        _logger = logger;
    }

    public ResearchUniverseConfig GetConfig() => _config;

    // ── Full maintenance cycle ──────────────────────────────────

    public async Task<UniverseMaintenanceResult> RunMaintenanceAsync()
    {
        var sw = Stopwatch.StartNew();
        var assets = await _service.GetActiveAssetsAsync(1000);

        // Pre-fetch all evidence snapshots in one HTTP call instead of N
        var tickers = assets.Select(a => a.Ticker).ToList();
        var snapshots = await _evidence.GetSnapshotsAsync(tickers);

        var promoted = 0;
        var decayed = 0;
        var archived = 0;
        var holdingUpdated = 0;
        var daysRefreshed = 0;
        var thesesUpdated = 0;

        // Collect batch updates to apply at the end
        var pendingUpdates = new List<(string Id, object Fields)>();

        foreach (var asset in assets)
        {
            try
            {
                var snapshot = snapshots.TryGetValue(asset.Ticker, out var s)
                    ? s : new EvidenceSnapshot { Ticker = asset.Ticker };

                var action = await EvaluateAssetWithSnapshotAsync(asset, snapshot);

                if (action.WasPromoted) promoted++;
                if (action.ScoreChanged) decayed++;
                if (action.WasArchived) archived++;
                if (action.HoldingWindowChanged) holdingUpdated++;
                if (action.ThesisUpdated) thesesUpdated++;

                // Collect days_active refresh
                var newDays = (int)(DateTimeOffset.UtcNow - asset.DateDiscovered).TotalDays;
                if (newDays != asset.DaysActive && !action.WasArchived)
                {
                    pendingUpdates.Add((asset.Id, new
                    {
                        days_active = newDays,
                        last_updated = DateTimeOffset.UtcNow.ToString("o"),
                    }));
                    daysRefreshed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[universe-engine] Failed to evaluate {Ticker}", asset.Ticker);
            }
        }

        // Batch-apply days_active updates
        if (pendingUpdates.Count > 0)
            await _repo.BatchUpdateFieldsAsync(pendingUpdates);

        sw.Stop();

        var result = new UniverseMaintenanceResult
        {
            AssetsEvaluated = assets.Count,
            Promoted = promoted,
            ScoresDecayed = decayed,
            Archived = archived,
            HoldingWindowsUpdated = holdingUpdated,
            DaysActiveRefreshed = daysRefreshed,
            ThesesUpdated = thesesUpdated,
            Duration = sw.Elapsed,
            Summary = $"Maintenance: {assets.Count} assets — " +
                      $"{promoted} promoted, {decayed} scores adjusted, {archived} archived, " +
                      $"{holdingUpdated} windows updated, {thesesUpdated} theses refreshed " +
                      $"({sw.Elapsed.TotalSeconds:F1}s)",
        };

        _logger.LogInformation("[universe-engine] {Summary}", result.Summary);
        return result;
    }

    // ── Single asset evaluation ─────────────────────────────────

    public async Task<AssetMaintenanceAction> EvaluateAssetAsync(ResearchAsset asset)
    {
        var snapshot = await _evidence.GetSnapshotAsync(asset.Ticker);
        return await EvaluateAssetWithSnapshotAsync(asset, snapshot);
    }

    /// <summary>
    /// Evaluate a single asset using a pre-fetched evidence snapshot.
    /// Avoids N+1 when called from RunMaintenanceAsync which batch-fetches all snapshots.
    /// </summary>
    private async Task<AssetMaintenanceAction> EvaluateAssetWithSnapshotAsync(
        ResearchAsset asset, EvidenceSnapshot snapshot)
    {
        var action = new AssetMaintenanceAction
        {
            AssetId = asset.Id,
            Ticker = asset.Ticker,
            PreviousState = asset.CurrentState,
            NewState = asset.CurrentState,
            PreviousScore = asset.InterestScore,
            NewScore = asset.InterestScore,
        };

        // 1. Check staleness — archive if past threshold
        var staleDays = _config.GetStaleDaysForState(asset.CurrentState);
        var inactiveDays = (int)(DateTimeOffset.UtcNow - asset.LastActivity).TotalDays;

        if (inactiveDays >= staleDays)
        {
            var reason = string.Format(_config.StaleArchiveReasonTemplate, inactiveDays);
            await _service.ArchiveAsync(asset.Id, reason);
            return action with
            {
                WasArchived = true,
                Action = reason,
                NewState = ResearchState.Archived,
            };
        }

        // 2. Decay interest score if stale but not yet archivable
        var newScore = asset.InterestScore;
        var scoreChanged = false;

        if (inactiveDays > _config.ScoreDecayGraceDays)
        {
            var decayDays = inactiveDays - _config.ScoreDecayGraceDays;
            var penalty = decayDays * _config.ScoreDecayPerStaleDay;
            newScore = Math.Clamp(asset.InterestScore - penalty, 0, _config.MaxInterestScore);
            scoreChanged = newScore != asset.InterestScore;
        }

        // 3. Archive if score dropped below minimum
        if (_config.ArchiveOnLowScore && newScore < _config.MinInterestScoreForActive)
        {
            var reason = string.Format(_config.LowScoreArchiveReasonTemplate, newScore);
            await _service.ArchiveAsync(asset.Id, reason);
            return action with
            {
                WasArchived = true,
                Action = reason,
                NewScore = newScore,
                ScoreChanged = scoreChanged,
                NewState = ResearchState.Archived,
            };
        }

        // 4. Use pre-fetched evidence snapshot for promotion and thesis decisions

        // 5. Sync interest score from evidence if evidence-based score is higher
        if (snapshot.InterestScore > newScore)
        {
            newScore = snapshot.InterestScore;
            scoreChanged = true;
        }

        // 6. Check for state promotion
        var promoted = false;
        var newState = asset.CurrentState;

        newState = TryPromote(asset.CurrentState, newScore, snapshot);
        promoted = newState != asset.CurrentState;

        // 7. Update holding window from dominant evidence type
        var holdingChanged = false;
        string? newHolding = asset.ExpectedHoldingWindow;

        if (snapshot.CountByType.Count > 0)
        {
            var dominantType = snapshot.CountByType
                .OrderByDescending(kvp => kvp.Value)
                .First().Key;

            if (_config.HoldingWindowByEvidenceType.TryGetValue(dominantType.ToString(), out var window))
            {
                if (window != asset.ExpectedHoldingWindow)
                {
                    newHolding = window;
                    holdingChanged = true;
                }
            }
        }

        // 8. Update thesis from evidence
        var thesisUpdated = false;
        var newThesis = asset.CurrentThesis;

        if (!string.IsNullOrEmpty(snapshot.CurrentThesis) && snapshot.CurrentThesis != asset.CurrentThesis)
        {
            newThesis = snapshot.CurrentThesis;
            thesisUpdated = true;
        }

        // 9. Apply updates if anything changed
        if (scoreChanged || promoted || holdingChanged || thesisUpdated)
        {
            var updated = asset with
            {
                InterestScore = Math.Clamp(newScore, 0, _config.MaxInterestScore),
                CurrentState = newState,
                ExpectedHoldingWindow = newHolding,
                CurrentThesis = newThesis,
                EvidenceCount = snapshot.EvidenceCount > asset.EvidenceCount
                    ? snapshot.EvidenceCount : asset.EvidenceCount,
                LastUpdated = DateTimeOffset.UtcNow,
            };

            await _repo.UpdateAsync(updated);

            // If promoted, also transition state via service (for logging)
            if (promoted)
            {
                _logger.LogInformation(
                    "[universe-engine] Promoted {Ticker}: {From} → {To} (score={Score}, evidence={Count})",
                    asset.Ticker, asset.CurrentState, newState, newScore, snapshot.EvidenceCount);
            }
        }

        return action with
        {
            NewState = newState,
            NewScore = newScore,
            WasPromoted = promoted,
            ScoreChanged = scoreChanged,
            HoldingWindowChanged = holdingChanged,
            ThesisUpdated = thesisUpdated,
            Action = promoted ? $"Promoted to {newState}" :
                     scoreChanged ? $"Score adjusted to {newScore}" : null,
        };
    }

    // ── Targeted operations ─────────────────────────────────────

    public async Task<int> DecayStaleScoresAsync()
    {
        var assets = await _service.GetActiveAssetsAsync(1000);
        var now = DateTimeOffset.UtcNow;

        // Compute all decays in memory, then batch update
        var updates = new List<(string Id, object Fields)>();

        foreach (var asset in assets)
        {
            var inactiveDays = (int)(now - asset.LastActivity).TotalDays;
            if (inactiveDays <= _config.ScoreDecayGraceDays) continue;

            var decayDays = inactiveDays - _config.ScoreDecayGraceDays;
            var penalty = decayDays * _config.ScoreDecayPerStaleDay;
            var newScore = Math.Clamp(asset.InterestScore - penalty, 0, _config.MaxInterestScore);

            if (newScore != asset.InterestScore)
            {
                updates.Add((asset.Id, new
                {
                    interest_score = newScore,
                    last_updated = now.ToString("o"),
                }));
            }
        }

        if (updates.Count > 0)
        {
            await _repo.BatchUpdateFieldsAsync(updates);
            _logger.LogInformation("[universe-engine] Decayed scores for {Count} assets", updates.Count);
        }

        return updates.Count;
    }

    public async Task<int> PromoteEligibleAssetsAsync()
    {
        var assets = await _service.GetActiveAssetsAsync(1000);

        // Pre-fetch all evidence snapshots in one HTTP call
        var tickers = assets.Select(a => a.Ticker).ToList();
        var snapshots = await _evidence.GetSnapshotsAsync(tickers);

        var updates = new List<(string Id, object Fields)>();

        foreach (var asset in assets)
        {
            var snapshot = snapshots.TryGetValue(asset.Ticker, out var s)
                ? s : new EvidenceSnapshot { Ticker = asset.Ticker };
            var newState = TryPromote(asset.CurrentState, asset.InterestScore, snapshot);

            if (newState != asset.CurrentState)
            {
                updates.Add((asset.Id, new
                {
                    current_state = newState.ToString(),
                    last_updated = DateTimeOffset.UtcNow.ToString("o"),
                }));

                _logger.LogInformation(
                    "[universe-engine] Promoted {Ticker}: {From} → {To}",
                    asset.Ticker, asset.CurrentState, newState);
            }
        }

        if (updates.Count > 0)
            await _repo.BatchUpdateFieldsAsync(updates);

        return updates.Count;
    }

    public async Task<int> ArchiveStaleAssetsAsync()
    {
        var assets = await _service.GetActiveAssetsAsync(1000);
        var now = DateTimeOffset.UtcNow;

        // Find all stale assets, then batch archive
        var staleIds = assets
            .Where(a =>
            {
                var staleDays = _config.GetStaleDaysForState(a.CurrentState);
                var inactiveDays = (int)(now - a.LastActivity).TotalDays;
                return inactiveDays >= staleDays;
            })
            .Select(a => a.Id)
            .ToList();

        if (staleIds.Count > 0)
        {
            await _repo.BatchArchiveAsync(staleIds, "Auto-archived: exceeded staleness threshold");
            _logger.LogInformation("[universe-engine] Archived {Count} stale assets", staleIds.Count);
        }

        return staleIds.Count;
    }

    public async Task<int> UpdateHoldingWindowsAsync()
    {
        var assets = await _service.GetActiveAssetsAsync(1000);

        // Pre-fetch all evidence snapshots in one HTTP call
        var tickers = assets.Select(a => a.Ticker).ToList();
        var snapshots = await _evidence.GetSnapshotsAsync(tickers);

        var updates = new List<(string Id, object Fields)>();

        foreach (var asset in assets)
        {
            var snapshot = snapshots.TryGetValue(asset.Ticker, out var s)
                ? s : new EvidenceSnapshot { Ticker = asset.Ticker };
            if (snapshot.CountByType.Count == 0) continue;

            var dominantType = snapshot.CountByType
                .OrderByDescending(kvp => kvp.Value)
                .First().Key;

            if (_config.HoldingWindowByEvidenceType.TryGetValue(dominantType.ToString(), out var window)
                && window != asset.ExpectedHoldingWindow)
            {
                updates.Add((asset.Id, new
                {
                    expected_holding_window = window,
                    last_updated = DateTimeOffset.UtcNow.ToString("o"),
                }));
            }
        }

        if (updates.Count > 0)
            await _repo.BatchUpdateFieldsAsync(updates);

        return updates.Count;
    }

    public async Task<int> RefreshAllAssetsAsync()
    {
        var assets = await _service.GetActiveAssetsAsync(1000);
        var now = DateTimeOffset.UtcNow;

        var updates = assets
            .Select(a => (Asset: a, DaysActive: (int)(now - a.DateDiscovered).TotalDays))
            .Where(x => x.DaysActive != x.Asset.DaysActive)
            .Select(x => (x.Asset.Id, (object)new
            {
                days_active = x.DaysActive,
                last_updated = now.ToString("o"),
            }))
            .ToList();

        if (updates.Count > 0)
            await _repo.BatchUpdateFieldsAsync(updates);

        return updates.Count;
    }

    // ── State promotion logic ───────────────────────────────────

    private ResearchState TryPromote(
        ResearchState current,
        int interestScore,
        EvidenceSnapshot snapshot)
    {
        return current switch
        {
            ResearchState.Discovered when
                snapshot.EvidenceCount >= _config.PromoteToMonitoringEvidenceCount &&
                interestScore >= _config.PromoteToMonitoringMinScore
                => ResearchState.Monitoring,

            ResearchState.Monitoring when
                snapshot.EvidenceCount >= _config.PromoteToBuildingThesisEvidenceCount &&
                interestScore >= _config.PromoteToBuildingThesisMinScore &&
                snapshot.CountByType.Count >= _config.PromoteToBuildingThesisMinTypes
                => ResearchState.BuildingThesis,

            ResearchState.BuildingThesis when
                snapshot.EvidenceCount >= _config.PromoteToReadyEvidenceCount &&
                interestScore >= _config.PromoteToReadyMinScore &&
                (!_config.RequireThesisForReady || !string.IsNullOrEmpty(snapshot.CurrentThesis))
                => ResearchState.ReadyForEvaluation,

            _ => current,
        };
    }
}
