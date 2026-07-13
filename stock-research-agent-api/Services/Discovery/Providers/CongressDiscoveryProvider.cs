using StockResearchAgent.Api.Models;

namespace StockResearchAgent.Api.Services.Discovery.Providers;

/// <summary>
/// Discovers tickers from congressional trading activity.
///
/// Wraps the existing CongressSignalProvider (which implements IResearchSignalProvider)
/// and maps its ResearchSignal output into DiscoveryEvents. Congressional buys/sells
/// are high-signal events — politicians trade on non-public information and their
/// filings are lagged, so any recent activity is noteworthy.
/// </summary>
public class CongressDiscoveryProvider : IDiscoveryProvider
{
    private readonly IResearchSignalProvider? _congress;
    private readonly ILogger<CongressDiscoveryProvider> _logger;

    public string ProviderId => "congress-signals";
    public bool IsConfigured => _congress?.IsConfigured == true;

    public CongressDiscoveryProvider(
        IEnumerable<IResearchSignalProvider> signalProviders,
        ILogger<CongressDiscoveryProvider> logger)
    {
        _congress = signalProviders.FirstOrDefault(p => p.ProviderId == "congress");
        _logger = logger;
    }

    public async Task<List<DiscoveryEvent>> ScanAsync()
    {
        if (!IsConfigured || _congress is null) return [];

        try
        {
            var signals = await _congress.CollectSignalsAsync();
            if (signals.Count == 0) return [];

            // Group by ticker to aggregate multiple trades
            var events = signals
                .GroupBy(s => s.Ticker)
                .Select(g =>
                {
                    var strongest = g.OrderByDescending(s => s.Strength).First();
                    var tradeCount = g.Count();
                    var avgStrength = g.Average(s => s.Strength);
                    var importance = Math.Clamp((int)(avgStrength * 60) + (tradeCount > 1 ? 20 : 0), 20, 90);

                    return new DiscoveryEvent
                    {
                        Ticker = g.Key,
                        Timestamp = strongest.DetectedAt,
                        Source = "congress-signals",
                        Reason = tradeCount == 1
                            ? strongest.Summary
                            : $"{tradeCount} congressional trades — {strongest.Summary}",
                        Importance = importance,
                        Category = DiscoveryCategory.InstitutionalActivity,
                        Confidence = Math.Clamp(strongest.Confidence, 0.3, 0.95),
                    };
                })
                .ToList();

            _logger.LogInformation(
                "[discovery:congress] {Signals} signals → {Events} ticker events",
                signals.Count, events.Count);

            return events;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[discovery:congress] Scan failed");
            return [];
        }
    }
}
