using System.Diagnostics;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Evidence;
using StockResearchAgent.Api.Services.ResearchUniverse;

namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// Lightweight continuous discovery engine.
///
/// Keeps the Research Universe current throughout the trading day by
/// running incremental discovery cycles on a configurable interval.
/// Each cycle only processes NEW evidence since the previous checkpoint.
///
/// The checkpoint is persisted to Supabase so it survives app restarts.
/// On cold start with no saved checkpoint, defaults to 2 hours ago.
///
/// This engine does NOT:
///   - Generate predictions
///   - Run the Morning Scan
///   - Trigger Learning
///   - Rebuild the entire Research Universe
///
/// It DOES:
///   - Check all providers for new events since last checkpoint
///   - Normalize events into Evidence
///   - Update Research Assets (score, thesis, evidence count, last activity)
///   - Append immutable ResearchTimelineEvents
///   - Build HistoricalResearchProfile for first-time discoveries
///   - Refresh stale HistoricalResearchProfiles on schedule
///   - Advance the checkpoint timestamp (persisted to Supabase)
/// </summary>
public class ContinuousDiscoveryEngine : IContinuousDiscoveryEngine
{
    private readonly IEnumerable<IDiscoveryProvider> _providers;
    private readonly IResearchUniverseService _universe;
    private readonly IDiscoveryEventRepository _eventRepo;
    private readonly IEvidenceService _evidence;
    private readonly IResearchTimelineRepository _timelineRepo;
    private readonly IHistoricalProfileBuilder _profileBuilder;
    private readonly IDiscoveryCheckpointRepository _checkpointRepo;
    private readonly ContinuousDiscoveryConfig _config;
    private readonly ILogger<ContinuousDiscoveryEngine> _logger;

    private const string CheckpointName = "continuous_discovery";

    /// <summary>In-memory cache of the checkpoint. Loaded from Supabase on first access,
    /// then kept in sync. Falls back to 2 hours ago if no persisted checkpoint exists.</summary>
    private DateTimeOffset? _cachedCheckpoint;

    public ContinuousDiscoveryEngine(
        IEnumerable<IDiscoveryProvider> providers,
        IResearchUniverseService universe,
        IDiscoveryEventRepository eventRepo,
        IEvidenceService evidence,
        IResearchTimelineRepository timelineRepo,
        IHistoricalProfileBuilder profileBuilder,
        IDiscoveryCheckpointRepository checkpointRepo,
        ContinuousDiscoveryConfig config,
        ILogger<ContinuousDiscoveryEngine> logger)
    {
        _providers = providers;
        _universe = universe;
        _eventRepo = eventRepo;
        _evidence = evidence;
        _timelineRepo = timelineRepo;
        _profileBuilder = profileBuilder;
        _checkpointRepo = checkpointRepo;
        _config = config;
        _logger = logger;
    }

    public ContinuousDiscoveryConfig GetConfig() => _config;

    public async Task<DateTimeOffset> GetLastCheckpointAsync()
    {
        if (_cachedCheckpoint is not null)
            return _cachedCheckpoint.Value;

        var persisted = await _checkpointRepo.GetCheckpointAsync(CheckpointName);
        _cachedCheckpoint = persisted ?? DateTimeOffset.UtcNow.AddHours(-2);
        return _cachedCheckpoint.Value;
    }

    public async Task ResetCheckpointAsync()
    {
        await _checkpointRepo.ResetCheckpointAsync(CheckpointName);
        _cachedCheckpoint = null;
        _logger.LogInformation("[continuous-discovery] Checkpoint reset — next cycle will rescan from 2 hours ago");
    }

    public bool ShouldRunNow()
    {
        if (_config.ScheduleMode == DiscoveryScheduleMode.Always)
            return true;

        // Check if we're in US market hours (ET)
        var et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var nowET = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, et);
        var hour = nowET.Hour;
        var minute = nowET.Minute;
        var dayOfWeek = nowET.DayOfWeek;

        // Weekdays only
        if (dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        // Market hours with 30-min pre-market buffer
        return (hour > _config.MarketOpenHourET || (hour == _config.MarketOpenHourET && minute >= 0))
            && hour < _config.MarketCloseHourET;
    }

    public async Task<ContinuousDiscoveryResult> RunCycleAsync()
    {
        var cycleStart = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // ── Schedule gate ──────────────────────────────────────────
        if (!ShouldRunNow())
        {
            return new ContinuousDiscoveryResult
            {
                CycleStart = cycleStart,
                CycleEnd = DateTimeOffset.UtcNow,
                WasSkipped = true,
                SkipReason = "Outside scheduled hours",
                Duration = sw.Elapsed,
                Summary = "Cycle skipped: outside scheduled hours",
            };
        }

        var checkpoint = await GetLastCheckpointAsync();
        _logger.LogInformation(
            "[continuous-discovery] Starting cycle, checkpoint={Checkpoint}",
            checkpoint.ToString("o"));

        // ── Run all providers ──────────────────────────────────────
        var allEvents = new List<DiscoveryEvent>();
        var providersScanned = 0;
        var providersSkipped = 0;
        var providersFailed = 0;

        foreach (var provider in _providers)
        {
            if (!provider.IsConfigured)
            {
                providersSkipped++;
                continue;
            }

            try
            {
                var events = await provider.ScanAsync();

                // Filter to only events AFTER the checkpoint
                var newEvents = events
                    .Where(e => e.Timestamp > checkpoint)
                    .ToList();

                allEvents.AddRange(newEvents);
                providersScanned++;

                _logger.LogDebug(
                    "[continuous-discovery] {Provider}: {Total} total, {New} new since checkpoint",
                    provider.ProviderId, events.Count, newEvents.Count);
            }
            catch (Exception ex)
            {
                providersFailed++;
                _logger.LogWarning(ex,
                    "[continuous-discovery] Provider {Provider} failed",
                    provider.ProviderId);
            }
        }

        // ── Cap events per cycle for performance ───────────────────
        if (allEvents.Count > _config.MaxEventsPerCycle)
        {
            _logger.LogWarning(
                "[continuous-discovery] Capping events from {Count} to {Max}",
                allEvents.Count, _config.MaxEventsPerCycle);

            allEvents = allEvents
                .OrderByDescending(e => e.Importance)
                .Take(_config.MaxEventsPerCycle)
                .ToList();
        }

        // ── Persist discovery events for audit trail ───────────────
        try
        {
            if (allEvents.Count > 0)
                await _eventRepo.PersistEventsAsync(allEvents);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[continuous-discovery] Failed to persist {Count} events",
                allEvents.Count);
        }

        // ── Deduplicate by ticker (keep highest importance) ────────
        var deduplicated = allEvents
            .GroupBy(e => e.Ticker.ToUpperInvariant())
            .Select(g => g.OrderByDescending(e => e.Importance).First())
            .ToList();

        // ── Process: create/update assets, record evidence, timeline ──
        var activeTickers = await _universe.GetActiveTickerSetAsync();
        var newAssets = 0;
        var updatedAssets = 0;
        var evidenceCreated = 0;
        var timelineCreated = 0;
        var profilesBuilt = 0;
        var profilesRefreshed = 0;

        foreach (var evt in deduplicated)
        {
            try
            {
                var ticker = evt.Ticker.ToUpperInvariant();
                var isNew = !activeTickers.Contains(ticker);

                // 1. Create or update Research Asset
                var asset = await _universe.DiscoverAsync(ticker, evt.Source, evt.Reason);
                if (asset is null) continue;

                if (isNew)
                {
                    newAssets++;
                    activeTickers.Add(ticker);

                    // Build historical profile for first-time discoveries
                    if (_config.BuildHistoricalProfileOnDiscovery)
                    {
                        try
                        {
                            await _profileBuilder.BuildProfileAsync(ticker, asset.Id);
                            profilesBuilt++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "[continuous-discovery] Failed to build historical profile for {Ticker}",
                                ticker);
                        }
                    }
                }
                else
                {
                    updatedAssets++;

                    // Check if existing profile needs refresh (stale or corporate event)
                    try
                    {
                        var refreshed = await _profileBuilder.RefreshIfNeededAsync(
                            ticker, asset.Id, evt.Category);
                        if (refreshed)
                            profilesRefreshed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "[continuous-discovery] Failed to check profile refresh for {Ticker}",
                            ticker);
                    }
                }

                // 2. Convert to Evidence and persist
                try
                {
                    await _evidence.RecordFromDiscoveryAsync(evt);
                    evidenceCreated++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[continuous-discovery] Failed to record evidence for {Ticker}",
                        ticker);
                }

                // 3. Append immutable timeline event
                try
                {
                    var timelineEvent = new ResearchTimelineEvent
                    {
                        Id = Guid.NewGuid().ToString(),
                        Ticker = ticker,
                        Timestamp = evt.Timestamp,
                        EventType = isNew ? TimelineEventType.Discovered : TimelineEventType.EvidenceAdded,
                        Description = evt.Reason,
                        Source = evt.Source,
                        RelatedEntityId = evt.Id,
                        RelatedEntityType = "discovery_event",
                        InterestScoreSnapshot = asset.InterestScore,
                        ResearchStateSnapshot = asset.CurrentState.ToString(),
                        ThesisSnapshot = asset.CurrentThesis,
                    };

                    await _timelineRepo.AppendAsync(timelineEvent);
                    timelineCreated++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[continuous-discovery] Failed to append timeline for {Ticker}",
                        ticker);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[continuous-discovery] Failed to process event for {Ticker}",
                    evt.Ticker);
            }
        }

        // ── Advance checkpoint (persisted to Supabase) ────────────
        await _checkpointRepo.SaveCheckpointAsync(CheckpointName, cycleStart);
        _cachedCheckpoint = cycleStart;

        sw.Stop();

        var result = new ContinuousDiscoveryResult
        {
            CycleStart = cycleStart,
            CycleEnd = DateTimeOffset.UtcNow,
            CheckpointUsed = checkpoint,
            NewEventsFound = allEvents.Count,
            NewAssetsCreated = newAssets,
            ExistingAssetsUpdated = updatedAssets,
            EvidenceRecordsCreated = evidenceCreated,
            TimelineEventsCreated = timelineCreated,
            HistoricalProfilesBuilt = profilesBuilt,
            HistoricalProfilesRefreshed = profilesRefreshed,
            ProvidersScanned = providersScanned,
            ProvidersSkipped = providersSkipped,
            ProvidersFailed = providersFailed,
            Duration = sw.Elapsed,
            Summary = $"Continuous discovery: {allEvents.Count} new events from " +
                      $"{providersScanned} providers → {newAssets} new + {updatedAssets} updated assets, " +
                      $"{evidenceCreated} evidence, {timelineCreated} timeline events, " +
                      $"{profilesBuilt} profiles built, {profilesRefreshed} refreshed ({sw.Elapsed.TotalSeconds:F1}s)",
        };

        _logger.LogInformation("[continuous-discovery] {Summary}", result.Summary);
        return result;
    }
}
