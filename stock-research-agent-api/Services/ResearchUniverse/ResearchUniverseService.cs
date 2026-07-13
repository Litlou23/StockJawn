using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.ResearchUniverse;

/// <summary>
/// Business logic layer for managing Research Assets through their lifecycle.
/// Wraps the repository with state transition rules, score calculations,
/// and staleness management.
/// </summary>
public class ResearchUniverseService : IResearchUniverseService
{
    private readonly IResearchUniverseRepository _repo;
    private readonly ILogger<ResearchUniverseService> _logger;

    public ResearchUniverseService(
        IResearchUniverseRepository repo,
        ILogger<ResearchUniverseService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    // ── Discovery ───────────────────────────────────────────────

    public async Task<ResearchAsset?> DiscoverAsync(string ticker, string source, string reason)
    {
        ticker = ticker.ToUpperInvariant();

        // Idempotent: if already active, update it instead
        var existing = await _repo.GetActiveByTickerAsync(ticker);
        if (existing is not null)
        {
            // Bump activity and evidence
            await RecordEvidenceAsync(existing.Id, source, 5);
            _logger.LogDebug("[research-universe] {Ticker} already active, updated evidence", ticker);
            return await _repo.GetByIdAsync(existing.Id);
        }

        var asset = new ResearchAsset
        {
            Id = Guid.NewGuid().ToString(),
            Ticker = ticker,
            DateDiscovered = DateTimeOffset.UtcNow,
            DiscoverySource = source,
            DiscoveryReason = reason,
            CurrentState = ResearchState.Discovered,
            LastActivity = DateTimeOffset.UtcNow,
            InterestScore = 10,
            EvidenceCount = 1,
            DaysActive = 0,
            Status = ResearchAssetStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow,
        };

        await _repo.AddAsync(asset);
        _logger.LogInformation("[research-universe] Discovered {Ticker} via {Source}: {Reason}",
            ticker, source, reason);
        return asset;
    }

    public async Task<bool> IsUnderInvestigationAsync(string ticker)
    {
        var asset = await _repo.GetActiveByTickerAsync(ticker.ToUpperInvariant());
        return asset is not null;
    }

    public async Task<HashSet<string>> GetActiveTickerSetAsync()
    {
        return await _repo.GetActiveTickerSetAsync();
    }

    // ── Lifecycle management ────────────────────────────────────

    public async Task<bool> StartMonitoringAsync(string assetId)
    {
        return await _repo.TransitionStateAsync(assetId, ResearchState.Monitoring);
    }

    public async Task<bool> StartBuildingThesisAsync(string assetId, string thesis)
    {
        var transitioned = await _repo.TransitionStateAsync(assetId, ResearchState.BuildingThesis);
        if (transitioned)
            await UpdateThesisAsync(assetId, thesis);
        return transitioned;
    }

    public async Task<bool> MarkReadyForEvaluationAsync(string assetId)
    {
        return await _repo.TransitionStateAsync(assetId, ResearchState.ReadyForEvaluation);
    }

    public async Task<bool> ArchiveAsync(string assetId, string reason)
    {
        return await _repo.ArchiveAsync(assetId, reason);
    }

    // ── Evidence accumulation ───────────────────────────────────

    public async Task<bool> RecordEvidenceAsync(string assetId, string evidenceType, int scoreImpact)
    {
        var asset = await _repo.GetByIdAsync(assetId);
        if (asset is null) return false;

        var updated = asset with
        {
            EvidenceCount = asset.EvidenceCount + 1,
            InterestScore = Math.Clamp(asset.InterestScore + scoreImpact, 0, 100),
            LastActivity = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow,
        };

        await _repo.UpdateAsync(updated);
        return true;
    }

    public async Task<bool> UpdateThesisAsync(string assetId, string thesis)
    {
        var asset = await _repo.GetByIdAsync(assetId);
        if (asset is null) return false;

        var updated = asset with
        {
            CurrentThesis = thesis,
            LastUpdated = DateTimeOffset.UtcNow,
        };

        await _repo.UpdateAsync(updated);
        return true;
    }

    // ── Queries ─────────────────────────────────────────────────

    public async Task<List<ResearchAsset>> GetActiveAssetsAsync(int limit = 200)
    {
        return await _repo.GetActiveAsync(limit);
    }

    public async Task<List<ResearchAsset>> GetEvaluationCandidatesAsync(int limit = 50)
    {
        return await _repo.GetReadyForEvaluationAsync(limit);
    }

    public async Task<List<ResearchAsset>> GetStaleAssetsAsync(int staleDays = 7)
    {
        return await _repo.GetStaleAsync(staleDays);
    }

    public async Task<ResearchAsset?> GetByTickerAsync(string ticker)
    {
        return await _repo.GetActiveByTickerAsync(ticker.ToUpperInvariant());
    }

    // ── Maintenance ─────────────────────────────────────────────

    public async Task<int> ArchiveStaleAssetsAsync(int staleDays = 7)
    {
        var stale = await _repo.GetStaleAsync(staleDays);
        if (stale.Count == 0) return 0;

        // Batch archive: one HTTP call instead of N
        var ids = stale.Select(a => a.Id).ToList();
        await _repo.BatchArchiveAsync(ids, $"Stale: no activity for {staleDays}+ days");

        _logger.LogInformation("[research-universe] Archived {Count} stale assets", stale.Count);
        return stale.Count;
    }

    public async Task<int> RefreshDaysActiveAsync()
    {
        var active = await _repo.GetActiveAsync(1000);
        var now = DateTimeOffset.UtcNow;

        // Compute all needed updates in memory, then batch
        var updates = active
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

    // ── Stats ───────────────────────────────────────────────────

    public async Task<ResearchUniverseStats> GetStatsAsync()
    {
        return await _repo.GetStatsAsync();
    }
}
