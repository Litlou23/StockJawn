using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.OptionsData;

/// <summary>
/// Updates option_learning_stats after paper option outcomes are evaluated.
/// Tracks performance across ticker, duration, price bucket, side, and confidence.
/// </summary>
public class OptionLearningService
{
    private readonly OptionLearningRepository _repo;
    private readonly ILogger<OptionLearningService> _logger;

    public OptionLearningService(OptionLearningRepository repo, ILogger<OptionLearningService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>
    /// Update learning stats from an evaluated outcome across multiple dimensions.
    /// </summary>
    public async Task UpdateLearningFromOutcomeAsync(
        PaperCandidateEnhanced candidate,
        PaperOutcomeEnhanced outcome,
        int confidenceScore)
    {
        var dimensions = new List<(string type, string key)>
        {
            ("ticker", candidate.Ticker),
            ("contract_type", candidate.Side.ToString()),
            ("duration_bucket", candidate.DurationBucket),
        };

        if (!string.IsNullOrEmpty(candidate.PriceBucket))
            dimensions.Add(("price_bucket", candidate.PriceBucket));

        // Confidence bucket: 0-30, 30-50, 50-70, 70-100
        var confBucket = confidenceScore switch
        {
            >= 70 => "70-100",
            >= 50 => "50-70",
            >= 30 => "30-50",
            _ => "0-30",
        };
        dimensions.Add(("confidence_bucket", confBucket));

        foreach (var (statType, statKey) in dimensions)
        {
            try
            {
                var existing = await _repo.GetStatAsync(statType, statKey);

                var total = (existing?.TotalCandidates ?? 0) + 1;
                var profitable = (existing?.ProfitableCandidates ?? 0) + (outcome.ContractProfitable ? 1 : 0);
                var winRate = total > 0 ? (double)profitable / total : 0;

                // Running average for move percentages and scores
                var prevTotal = existing?.TotalCandidates ?? 0;
                var avgOption = prevTotal > 0
                    ? ((existing!.AverageOptionMovePercent * prevTotal) + outcome.PaperPnlPercent) / total
                    : outcome.PaperPnlPercent;
                var avgUnderlying = prevTotal > 0
                    ? ((existing!.AverageUnderlyingMovePercent * prevTotal) + outcome.UnderlyingMovePercent) / total
                    : outcome.UnderlyingMovePercent;
                var avgScore = prevTotal > 0
                    ? ((existing!.AverageOutcomeScore * prevTotal) + outcome.OutcomeScore) / total
                    : outcome.OutcomeScore;

                await _repo.UpsertStatAsync(statType, statKey, total, profitable, winRate,
                    avgOption, avgUnderlying, avgScore);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[learning] Failed to update stat {Type}/{Key}", statType, statKey);
            }
        }

        _logger.LogInformation("[learning] Updated {Count} learning dimensions for {Ticker}",
            dimensions.Count, candidate.Ticker);
    }

    public async Task<List<OptionLearningStat>> GetLearningStatsAsync()
    {
        return await _repo.GetAllStatsAsync();
    }
}
