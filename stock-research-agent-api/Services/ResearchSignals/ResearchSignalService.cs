using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchSignals;

/// <summary>
/// Orchestrates signal collection from all registered providers,
/// deduplicates, persists, and expires stale signals.
/// </summary>
public class ResearchSignalService
{
    private readonly IEnumerable<IResearchSignalProvider> _providers;
    private readonly ResearchSignalRepository _repo;
    private readonly ILogger<ResearchSignalService> _logger;

    public ResearchSignalService(
        IEnumerable<IResearchSignalProvider> providers,
        ResearchSignalRepository repo,
        ILogger<ResearchSignalService> logger)
    {
        _providers = providers;
        _repo = repo;
        _logger = logger;
    }

    /// <summary>
    /// Collect signals from all configured providers. Called before
    /// watchlist scoring so signals are available for the scoring engine.
    /// </summary>
    public async Task<SignalCollectionResult> CollectAllSignalsAsync()
    {
        var allSignals = new List<ResearchSignal>();
        var errors = new List<string>();

        foreach (var provider in _providers.Where(p => p.IsConfigured))
        {
            try
            {
                var signals = await provider.CollectSignalsAsync();
                allSignals.AddRange(signals);
                _logger.LogInformation("[signals] {Provider}: {Count} signals collected",
                    provider.ProviderId, signals.Count);
            }
            catch (Exception ex)
            {
                errors.Add($"{provider.ProviderId}: {ex.Message}");
                _logger.LogError(ex, "[signals] {Provider} failed", provider.ProviderId);
            }
        }

        // Deduplicate: same (ticker, signal_type, event day) = same signal
        var deduplicated = allSignals
            .GroupBy(s => (s.Ticker, s.SignalType, s.EventTimestamp.Date))
            .Select(g => g.OrderByDescending(s => s.Confidence).First())
            .ToList();

        var persisted = await _repo.UpsertSignalsAsync(deduplicated);
        var expired = await _repo.ExpireStaleSignalsAsync();
        await SeedNewWeightsAsync();

        return new SignalCollectionResult(persisted, expired, errors);
    }

    /// <summary>
    /// Get active signals for a set of tickers. Used by watchlist scoring.
    /// </summary>
    public async Task<Dictionary<string, List<ResearchSignal>>> GetActiveSignalsAsync(
        IEnumerable<string> tickers) =>
        await _repo.GetActiveSignalsByTickersAsync(tickers);

    /// <summary>
    /// Get active signals for a single ticker. Used by prediction context.
    /// </summary>
    public async Task<List<ResearchSignal>> GetActiveSignalsForTickerAsync(string ticker) =>
        await _repo.GetActiveSignalsForTickerAsync(ticker);

    /// <summary>
    /// Get signals that were active for a ticker at a given time.
    /// Used by the learning engine to determine which signals influenced a prediction.
    /// </summary>
    public async Task<List<ResearchSignal>> GetSignalsActiveAtTimeAsync(
        string ticker, DateTimeOffset asOf) =>
        await _repo.GetSignalsActiveAtTimeAsync(ticker, asOf);

    /// <summary>
    /// Auto-seed scoring weights for any new signal types declared by providers.
    /// </summary>
    private async Task SeedNewWeightsAsync()
    {
        var existing = (await _repo.GetExistingScoringWeightNamesAsync()).ToHashSet();

        foreach (var provider in _providers)
        {
            foreach (var st in provider.SignalTypes)
            {
                var weightKey = $"research_{st.SignalType}";
                if (existing.Contains(weightKey)) continue;

                await _repo.InsertScoringWeightAsync(weightKey, st.DefaultWeight,
                    $"Auto-seeded from {provider.ProviderId} provider");
                _logger.LogInformation("[signals] Seeded scoring weight: {Key} = {Weight}",
                    weightKey, st.DefaultWeight);
            }
        }
    }
}
