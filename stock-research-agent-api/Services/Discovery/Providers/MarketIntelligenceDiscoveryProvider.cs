using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Discovery.Providers;

/// <summary>
/// Discovers tickers by scanning active research signals for convergence.
///
/// Unlike the other providers which reach out to external APIs, this provider
/// looks inward at the signal pipeline. When multiple signal types converge
/// on the same ticker, that ticker deserves deeper investigation — even if
/// no single signal is strong enough on its own.
///
/// Also surfaces tickers with high-strength individual signals that
/// haven't been picked up by other discovery sources.
/// </summary>
public class MarketIntelligenceDiscoveryProvider : IDiscoveryProvider
{
    private readonly SupabaseClient _db;
    private readonly ILogger<MarketIntelligenceDiscoveryProvider> _logger;

    private const double MinSignalStrength = 0.5;
    private const int MinConvergenceCount = 2;

    public string ProviderId => "market-intelligence";
    public bool IsConfigured => true; // Always available — reads from DB

    public MarketIntelligenceDiscoveryProvider(
        SupabaseClient db,
        ILogger<MarketIntelligenceDiscoveryProvider> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<DiscoveryEvent>> ScanAsync()
    {
        try
        {
            // Query all active signals directly from DB
            var rows = await _db.SelectAsync("research_signals", "active=eq.true", limit: 500);
            if (rows.Count == 0) return [];

            var signals = rows.Select(r => new
            {
                Ticker = r["ticker"]?.ToString() ?? "",
                SignalType = r["signal_type"]?.ToString() ?? "",
                SignalCategory = r["signal_category"]?.ToString() ?? "",
                Strength = double.TryParse(r["strength"]?.ToString(), out var s) ? s : 0.0,
                Confidence = double.TryParse(r["confidence"]?.ToString(), out var c) ? c : 0.5,
                Summary = r["summary"]?.ToString() ?? "",
                DetectedAt = DateTimeOffset.TryParse(r["detected_at"]?.ToString(), out var d) ? d : DateTimeOffset.UtcNow,
            }).ToList();

            var events = new List<DiscoveryEvent>();

            // Group signals by ticker
            var byTicker = signals
                .Where(s => s.Strength >= MinSignalStrength)
                .GroupBy(s => s.Ticker);

            foreach (var group in byTicker)
            {
                var tickerSignals = group.ToList();
                var distinctTypes = tickerSignals.Select(s => s.SignalType).Distinct().Count();
                var maxStrength = tickerSignals.Max(s => s.Strength);
                var avgConfidence = tickerSignals.Average(s => s.Confidence);

                // Signal convergence: multiple distinct signal types pointing at the same ticker
                if (distinctTypes >= MinConvergenceCount)
                {
                    var typeNames = string.Join(", ", tickerSignals.Select(s => s.SignalType).Distinct().Take(4));
                    var importance = Math.Clamp(distinctTypes * 25 + (int)(maxStrength * 20), 30, 95);

                    events.Add(new DiscoveryEvent
                    {
                        Ticker = group.Key,
                        Timestamp = DateTimeOffset.UtcNow,
                        Source = "market-intelligence",
                        Reason = $"Signal convergence: {distinctTypes} signal types ({typeNames}), " +
                                 $"max strength {maxStrength:F2}",
                        Importance = importance,
                        Category = DiscoveryCategory.CatalystAccumulation,
                        Confidence = Math.Clamp(avgConfidence, 0.4, 0.9),
                    });
                }
                // Single but very strong signal
                else if (maxStrength >= 0.8)
                {
                    var strongest = tickerSignals.OrderByDescending(s => s.Strength).First();
                    events.Add(new DiscoveryEvent
                    {
                        Ticker = group.Key,
                        Timestamp = strongest.DetectedAt,
                        Source = "market-intelligence",
                        Reason = $"Strong signal: {strongest.SignalType} (strength {strongest.Strength:F2}) — {strongest.Summary}",
                        Importance = Math.Clamp((int)(strongest.Strength * 70), 40, 80),
                        Category = DiscoveryCategory.General,
                        Confidence = strongest.Confidence,
                    });
                }
            }

            _logger.LogInformation(
                "[discovery:market-intelligence] {Signals} active signals → {Events} discovery events",
                signals.Count, events.Count);

            return events;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[discovery:market-intelligence] Scan failed");
            return [];
        }
    }
}
