using System.Diagnostics;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Evidence;
using StockResearchAgent.Api.Services.ResearchUniverse;

namespace StockResearchAgent.Api.Services.Discovery;

/// <summary>
/// Orchestrates all discovery providers, deduplicates events,
/// and creates or updates Research Assets idempotently.
///
/// This engine does NOT generate predictions. It only feeds the
/// Research Universe with tickers that deserve investigation.
///
/// Designed to be called from a scheduled job (e.g. every 30 minutes
/// during market hours, or once daily for non-time-sensitive sources).
/// </summary>
public class DiscoveryEngine : IDiscoveryEngine
{
    private readonly IEnumerable<IDiscoveryProvider> _providers;
    private readonly IResearchUniverseService _universe;
    private readonly IDiscoveryEventRepository _eventRepo;
    private readonly IEvidenceService _evidence;
    private readonly ILogger<DiscoveryEngine> _logger;

    public DiscoveryEngine(
        IEnumerable<IDiscoveryProvider> providers,
        IResearchUniverseService universe,
        IDiscoveryEventRepository eventRepo,
        IEvidenceService evidence,
        ILogger<DiscoveryEngine> logger)
    {
        _providers = providers;
        _universe = universe;
        _eventRepo = eventRepo;
        _evidence = evidence;
        _logger = logger;
    }

    public async Task<DiscoveryRunResult> RunDiscoveryAsync()
    {
        var sw = Stopwatch.StartNew();
        var providerResults = new List<DiscoveryProviderResult>();
        var allEvents = new List<DiscoveryEvent>();

        // ── Run all configured providers ────────────────────────
        foreach (var provider in _providers)
        {
            var result = await RunProviderAsync(provider);
            providerResults.Add(result);

            if (result.Success)
                allEvents.AddRange(result.Events);
        }

        // ── Deduplicate by ticker (keep highest importance) ─────
        var deduplicated = allEvents
            .GroupBy(e => e.Ticker)
            .Select(g => g.OrderByDescending(e => e.Importance).First())
            .ToList();

        // ── Persist all events for audit trail ──────────────────
        try
        {
            if (allEvents.Count > 0)
                await _eventRepo.PersistEventsAsync(allEvents);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[discovery] Failed to persist {Count} events", allEvents.Count);
        }

        // ── Create or update Research Assets (batch-aware) ──────
        // Pre-fetch all active tickers in one HTTP call instead of
        // calling IsUnderInvestigationAsync per ticker (was N+1).
        var activeTickers = await _universe.GetActiveTickerSetAsync();
        var newAssets = 0;
        var updatedAssets = 0;

        var evidenceCreated = 0;

        foreach (var evt in deduplicated)
        {
            try
            {
                var ticker = evt.Ticker.ToUpperInvariant();
                var existing = activeTickers.Contains(ticker);
                var asset = await _universe.DiscoverAsync(evt.Ticker, evt.Source, evt.Reason);

                if (asset is not null)
                {
                    if (existing)
                        updatedAssets++;
                    else
                    {
                        newAssets++;
                        activeTickers.Add(ticker);
                    }

                    // Record evidence and sync Interest Score from aggregator
                    try
                    {
                        await _evidence.RecordFromDiscoveryAsync(evt);
                        await _evidence.SyncToResearchAssetAsync(ticker);
                        evidenceCreated++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "[discovery] Failed to record evidence for {Ticker}", ticker);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[discovery] Failed to process event for {Ticker}", evt.Ticker);
            }
        }

        sw.Stop();

        var succeeded = providerResults.Count(r => r.Success);
        var failed = providerResults.Count(r => !r.Success);

        var result2 = new DiscoveryRunResult
        {
            TotalEventsDiscovered = allEvents.Count,
            NewAssetsCreated = newAssets,
            ExistingAssetsUpdated = updatedAssets,
            ProvidersSucceeded = succeeded,
            ProvidersFailed = failed,
            ProviderResults = providerResults,
            TotalDuration = sw.Elapsed,
            Summary = $"Discovery complete: {allEvents.Count} events from {succeeded}/{succeeded + failed} providers → " +
                      $"{newAssets} new + {updatedAssets} updated assets, {evidenceCreated} evidence ({sw.Elapsed.TotalSeconds:F1}s)",
        };

        _logger.LogInformation("[discovery] {Summary}", result2.Summary);
        return result2;
    }

    public async Task<DiscoveryProviderResult> RunProviderAsync(string providerId)
    {
        var provider = _providers.FirstOrDefault(p => p.ProviderId == providerId);
        if (provider is null)
        {
            return new DiscoveryProviderResult
            {
                ProviderId = providerId,
                Success = false,
                Error = $"Provider '{providerId}' not found",
            };
        }

        return await RunProviderAsync(provider);
    }

    private async Task<DiscoveryProviderResult> RunProviderAsync(IDiscoveryProvider provider)
    {
        if (!provider.IsConfigured)
        {
            _logger.LogDebug("[discovery] Skipping unconfigured provider: {Provider}", provider.ProviderId);
            return new DiscoveryProviderResult
            {
                ProviderId = provider.ProviderId,
                Success = true,
                Events = [],
            };
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var events = await provider.ScanAsync();
            sw.Stop();

            _logger.LogInformation(
                "[discovery] {Provider}: {Count} events in {Ms}ms",
                provider.ProviderId, events.Count, sw.ElapsedMilliseconds);

            return new DiscoveryProviderResult
            {
                ProviderId = provider.ProviderId,
                Events = events,
                Success = true,
                Duration = sw.Elapsed,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[discovery] {Provider} failed after {Ms}ms",
                provider.ProviderId, sw.ElapsedMilliseconds);

            return new DiscoveryProviderResult
            {
                ProviderId = provider.ProviderId,
                Success = false,
                Error = ex.Message,
                Duration = sw.Elapsed,
            };
        }
    }
}
