using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.TradeDecision;

/// <summary>
/// Provides cached aggregate prediction outcome statistics for use
/// by the TradeDecisionEngine. Stats are loaded lazily and cached
/// for 1 hour to avoid hammering the database on every Decide call.
///
/// Replaces the hardcoded WinRate=0.55, AvgWin=8%, AvgLoss=5%
/// placeholders with real historical performance data.
/// </summary>
public class TradeStatsProvider
{
    private readonly ResearchRepository _repo;
    private readonly ILogger<TradeStatsProvider> _logger;

    private TradeStats? _cached;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TradeStatsProvider(ResearchRepository repo, ILogger<TradeStatsProvider> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public record TradeStats(
        double WinRate,
        double AverageWinPercent,
        double AverageLossPercent,
        int SampleSize,
        bool IsReal);

    /// <summary>
    /// Default fallback stats when no outcomes exist yet.
    /// Conservative estimates — intentionally worse than the old hardcoded values
    /// so the system doesn't get overconfident before it has data.
    /// </summary>
    private static readonly TradeStats DefaultStats = new(
        WinRate: 0.50,
        AverageWinPercent: 5.0,
        AverageLossPercent: 5.0,
        SampleSize: 0,
        IsReal: false);

    /// <summary>
    /// Get aggregate outcome stats. Cached for 1 hour.
    /// Returns real stats from prediction_outcomes if available,
    /// otherwise returns conservative defaults.
    /// </summary>
    public async Task<TradeStats> GetStatsAsync()
    {
        if (_cached is not null && DateTimeOffset.UtcNow < _cacheExpiry)
            return _cached;

        await _lock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_cached is not null && DateTimeOffset.UtcNow < _cacheExpiry)
                return _cached;

            var stats = await ComputeStatsFromOutcomesAsync();
            _cached = stats;
            _cacheExpiry = DateTimeOffset.UtcNow.AddHours(1);
            return stats;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<TradeStats> ComputeStatsFromOutcomesAsync()
    {
        try
        {
            // Pull recent outcomes (last 500) — enough for reliable stats
            var outcomes = await _repo.GetRecentOutcomesAsync(500);

            // Filter to outcomes with direction_correct and percent_move data
            var valid = outcomes
                .Where(o => o.DirectionCorrect is not null && o.PercentMove is not null)
                .ToList();

            if (valid.Count < 10)
            {
                _logger.LogInformation(
                    "[trade-stats] Only {Count} valid outcomes, using defaults", valid.Count);
                return DefaultStats;
            }

            var wins = valid.Where(o => o.DirectionCorrect == true).ToList();
            var losses = valid.Where(o => o.DirectionCorrect == false).ToList();

            var winRate = (double)wins.Count / valid.Count;

            // Average favorable move on wins (absolute value)
            var avgWin = wins.Count > 0
                ? wins.Average(o => Math.Abs(o.PercentMove ?? 0))
                : 5.0;

            // Average adverse move on losses (absolute value)
            var avgLoss = losses.Count > 0
                ? losses.Average(o => Math.Abs(o.PercentMove ?? 0))
                : 5.0;

            // Clamp to reasonable ranges
            avgWin = Math.Clamp(avgWin, 0.5, 50.0);
            avgLoss = Math.Clamp(avgLoss, 0.5, 50.0);

            var stats = new TradeStats(
                WinRate: Math.Round(winRate, 4),
                AverageWinPercent: Math.Round(avgWin, 2),
                AverageLossPercent: Math.Round(avgLoss, 2),
                SampleSize: valid.Count,
                IsReal: true);

            _logger.LogInformation(
                "[trade-stats] Computed from {Count} outcomes: WR={WinRate:P1}, AvgWin={Win:F1}%, AvgLoss={Loss:F1}%",
                valid.Count, stats.WinRate, stats.AverageWinPercent, stats.AverageLossPercent);

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[trade-stats] Failed to compute stats, using defaults");
            return DefaultStats;
        }
    }
}
