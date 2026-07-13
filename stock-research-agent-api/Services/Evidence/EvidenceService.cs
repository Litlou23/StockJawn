using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchUniverse;

namespace StockResearchAgent.Api.Services.Evidence;

/// <summary>
/// Default implementation of the Evidence Engine.
///
/// Converts events into evidence, persists them, aggregates snapshots,
/// and syncs computed values back to Research Assets.
/// </summary>
public class EvidenceService : IEvidenceService
{
    private readonly IEvidenceRepository _repo;
    private readonly IEvidenceAggregator _aggregator;
    private readonly IEvidenceDecayStrategy _decay;
    private readonly IResearchUniverseService _universe;
    private readonly ILogger<EvidenceService> _logger;

    /// <summary>
    /// Maps DiscoveryCategory → EvidenceType for event conversion.
    /// </summary>
    private static readonly Dictionary<DiscoveryCategory, EvidenceType> CategoryToEvidence = new()
    {
        [DiscoveryCategory.News] = EvidenceType.News,
        [DiscoveryCategory.Earnings] = EvidenceType.Catalyst,
        [DiscoveryCategory.InstitutionalActivity] = EvidenceType.Congress,
        [DiscoveryCategory.PriceAction] = EvidenceType.Technical,
        [DiscoveryCategory.Filing] = EvidenceType.SEC,
        [DiscoveryCategory.AnalystAction] = EvidenceType.Research,
        [DiscoveryCategory.OptionsFlow] = EvidenceType.Options,
        [DiscoveryCategory.RegulatoryEvent] = EvidenceType.Catalyst,
        [DiscoveryCategory.InsiderActivity] = EvidenceType.Congress,
        [DiscoveryCategory.SectorMomentum] = EvidenceType.Momentum,
        [DiscoveryCategory.CatalystAccumulation] = EvidenceType.Catalyst,
        [DiscoveryCategory.General] = EvidenceType.Research,
    };

    public EvidenceService(
        IEvidenceRepository repo,
        IEvidenceAggregator aggregator,
        IEvidenceDecayStrategy decay,
        IResearchUniverseService universe,
        ILogger<EvidenceService> logger)
    {
        _repo = repo;
        _aggregator = aggregator;
        _decay = decay;
        _universe = universe;
        _logger = logger;
    }

    // ── Recording ───────────────────────────────────────────────

    public async Task<EvidenceRecord> RecordAsync(EvidenceRecord record)
    {
        // Apply default expiration if none set
        var withExpiration = record.Expiration.HasValue
            ? record
            : ApplyDefaultExpiration(record);

        var persisted = await _repo.AddAsync(withExpiration);
        _logger.LogDebug("[evidence] Recorded {Type} for {Ticker}: {Summary}",
            record.EvidenceType, record.Ticker, record.Summary);

        return persisted;
    }

    public async Task<int> RecordManyAsync(List<EvidenceRecord> records)
    {
        var withExpirations = records
            .Select(r => r.Expiration.HasValue ? r : ApplyDefaultExpiration(r))
            .ToList();

        var count = await _repo.AddManyAsync(withExpirations);
        _logger.LogInformation("[evidence] Recorded {Count} evidence items", count);
        return count;
    }

    public async Task<EvidenceRecord> RecordFromDiscoveryAsync(DiscoveryEvent discoveryEvent)
    {
        var record = ConvertFromDiscovery(discoveryEvent);
        return await RecordAsync(record);
    }

    public async Task<int> RecordFromDiscoveryBatchAsync(List<DiscoveryEvent> events)
    {
        var records = events.Select(ConvertFromDiscovery).ToList();
        return await RecordManyAsync(records);
    }

    // ── Aggregation ─────────────────────────────────────────────

    public async Task<EvidenceSnapshot> GetSnapshotAsync(string ticker)
    {
        var allRecords = await _repo.GetByTickerAsync(ticker.ToUpperInvariant());
        return _aggregator.Aggregate(ticker.ToUpperInvariant(), allRecords);
    }

    public async Task<Dictionary<string, EvidenceSnapshot>> GetSnapshotsAsync(IReadOnlyList<string> tickers)
    {
        if (tickers.Count == 0) return new();

        // One HTTP call for all tickers instead of N
        var allByTicker = await _repo.GetByTickersAsync(tickers);
        var result = new Dictionary<string, EvidenceSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var ticker in tickers)
        {
            var records = allByTicker.TryGetValue(ticker.ToUpperInvariant(), out var r) ? r : [];
            result[ticker.ToUpperInvariant()] = _aggregator.Aggregate(ticker.ToUpperInvariant(), records);
        }

        return result;
    }

    public async Task<int> GetInterestScoreAsync(string ticker)
    {
        var active = await _repo.GetActiveByTickerAsync(ticker.ToUpperInvariant());
        return _aggregator.ComputeInterestScore(active);
    }

    public async Task<string> GetThesisAsync(string ticker)
    {
        var active = await _repo.GetActiveByTickerAsync(ticker.ToUpperInvariant());
        return _aggregator.GenerateThesis(ticker.ToUpperInvariant(), active);
    }

    public async Task<List<EvidenceRecord>> GetTimelineAsync(string ticker, int limit = 50)
    {
        return await _repo.GetActiveByTickerAsync(ticker.ToUpperInvariant(), limit);
    }

    // ── Research Asset sync ─────────────────────────────────────

    public async Task SyncToResearchAssetAsync(string ticker)
    {
        ticker = ticker.ToUpperInvariant();
        var asset = await _universe.GetByTickerAsync(ticker);
        if (asset is null) return;

        var snapshot = await GetSnapshotAsync(ticker);

        // Set Interest Score directly from the aggregator (sole owner of score computation)
        await _universe.UpdateInterestScoreAsync(asset.Id, snapshot.InterestScore);

        if (!string.IsNullOrEmpty(snapshot.CurrentThesis))
            await _universe.UpdateThesisAsync(asset.Id, snapshot.CurrentThesis);

        _logger.LogDebug(
            "[evidence] Synced {Ticker}: score={Score}, count={Count}, thesis={Thesis}",
            ticker, snapshot.InterestScore, snapshot.EvidenceCount,
            snapshot.CurrentThesis[..Math.Min(60, snapshot.CurrentThesis.Length)]);
    }

    public async Task<int> SyncAllResearchAssetsAsync()
    {
        var assets = await _universe.GetActiveAssetsAsync();
        if (assets.Count == 0) return 0;

        // Pre-fetch all evidence snapshots in one HTTP call instead of N
        var tickers = assets.Select(a => a.Ticker).ToList();
        var snapshots = await GetSnapshotsAsync(tickers);
        var count = 0;

        foreach (var asset in assets)
        {
            try
            {
                var snapshot = snapshots.TryGetValue(asset.Ticker, out var s)
                    ? s : new EvidenceSnapshot { Ticker = asset.Ticker };

                // Set Interest Score directly from the aggregator (sole owner of score computation)
                await _universe.UpdateInterestScoreAsync(asset.Id, snapshot.InterestScore);

                if (!string.IsNullOrEmpty(snapshot.CurrentThesis))
                    await _universe.UpdateThesisAsync(asset.Id, snapshot.CurrentThesis);

                count++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[evidence] Failed to sync {Ticker}", asset.Ticker);
            }
        }

        _logger.LogInformation("[evidence] Synced {Count}/{Total} research assets",
            count, assets.Count);
        return count;
    }

    // ── Maintenance ─────────────────────────────────────────────

    public async Task<int> ApplyDefaultExpirationsAsync()
    {
        // Find records with no expiration and apply defaults based on type
        // This is a maintenance task — run periodically
        var allActive = await _repo.GetExpiredAsync(0); // trick: get records we haven't tagged
        // Actually, we need records WITHOUT expiration. Query all and filter.
        // For now, this is a no-op until we add a query for null-expiration records.
        // The RecordAsync path already applies defaults on write.
        return 0;
    }

    public async Task<EvidenceStats> GetStatsAsync()
    {
        // Lightweight stats from expired records count
        var expired = await _repo.GetExpiredAsync(1);

        return new EvidenceStats
        {
            ExpiredRecords = expired.Count,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────

    private EvidenceRecord ConvertFromDiscovery(DiscoveryEvent evt)
    {
        var evidenceType = CategoryToEvidence.TryGetValue(evt.Category, out var et)
            ? et : EvidenceType.Research;

        var weight = evt.Confidence * (evt.Importance / 100.0);

        return new EvidenceRecord
        {
            Ticker = evt.Ticker.ToUpperInvariant(),
            Timestamp = evt.Timestamp,
            EvidenceType = evidenceType,
            Source = evt.Source,
            Weight = weight,
            Importance = evt.Importance,
            Summary = evt.Reason,
            RelatedEventId = string.IsNullOrEmpty(evt.Id) ? null : evt.Id,
        };
    }

    private EvidenceRecord ApplyDefaultExpiration(EvidenceRecord record)
    {
        var config = _decay.GetConfig(record.EvidenceType);
        if (config.DefaultTtlDays is null) return record;

        return record with
        {
            Expiration = record.Timestamp.AddDays(config.DefaultTtlDays.Value),
        };
    }
}

