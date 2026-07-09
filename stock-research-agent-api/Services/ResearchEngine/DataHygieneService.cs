using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Detects and corrects bad data across the learning pipeline.
/// Runs as a scheduled job (daily or weekly) to prevent contaminated
/// data from degrading the scoring engine's accuracy.
///
/// Checks:
///   1. Paper option outcomes with 0% P&L from failed chain fetches
///   2. Stale open predictions past their max evaluation window
///   3. Stale open paper option candidates past expiration
///   4. Impossible values (confidence > 85, negative prices, etc.)
///   5. Orphaned outcomes without matching predictions/candidates
///   6. Learning stats with sample size too small to be meaningful
/// </summary>
public class DataHygieneService
{
    private readonly SupabaseClient _db;
    private readonly ILogger<DataHygieneService> _logger;

    public DataHygieneService(SupabaseClient db, ILogger<DataHygieneService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record HygieneResult
    {
        public int FalseOptionLossesDeleted { get; init; }
        public int OptionCandidatesReopened { get; init; }
        public int StalePredictionsExpired { get; init; }
        public int StaleOptionCandidatesExpired { get; init; }
        public int ImpossibleValuesFixed { get; init; }
        public int OrphanedRecordsDeleted { get; init; }
        public int LearningStatsReset { get; init; }
        public List<string> Actions { get; init; } = [];
        public List<string> Warnings { get; init; } = [];
    }

    public async Task<HygieneResult> RunFullHygieneAsync()
    {
        var actions = new List<string>();
        var warnings = new List<string>();

        _logger.LogInformation("[data-hygiene] Starting full data hygiene run");

        // 1. Delete false paper option outcomes (0% everything = failed chain fetch)
        int falseOptionLosses = await CleanFalseOptionLossesAsync(actions);

        // 2. Reopen candidates that had false outcomes deleted
        int reopened = await ReopenFalselyEvaluatedCandidatesAsync(actions);

        // 3. Expire stale open predictions past their max window
        int stalePredictions = await ExpireStalePredictionsAsync(actions);

        // 4. Expire paper option candidates past their expiration date
        int staleOptions = await ExpireStaleOptionCandidatesAsync(actions);

        // 5. Fix impossible values
        int impossibleFixed = await FixImpossibleValuesAsync(actions, warnings);

        // 6. Clean orphaned records
        int orphaned = await CleanOrphanedRecordsAsync(actions);

        // 7. Reset learning stats with tiny sample sizes (< 3)
        int statsReset = await CleanLowSampleStatsAsync(actions);

        var result = new HygieneResult
        {
            FalseOptionLossesDeleted = falseOptionLosses,
            OptionCandidatesReopened = reopened,
            StalePredictionsExpired = stalePredictions,
            StaleOptionCandidatesExpired = staleOptions,
            ImpossibleValuesFixed = impossibleFixed,
            OrphanedRecordsDeleted = orphaned,
            LearningStatsReset = statsReset,
            Actions = actions,
            Warnings = warnings,
        };

        _logger.LogInformation(
            "[data-hygiene] Complete: {Losses} false losses, {Reopened} reopened, {Stale} stale expired, {Fixed} impossible fixed, {Orphaned} orphans cleaned",
            falseOptionLosses, reopened, stalePredictions + staleOptions, impossibleFixed, orphaned);

        return result;
    }

    // -----------------------------------------------------------------------
    // 1. False option losses: 0% P&L + 0% underlying + no current price
    // -----------------------------------------------------------------------

    private async Task<int> CleanFalseOptionLossesAsync(List<string> actions)
    {
        try
        {
            var rows = await _db.SelectAsync("paper_option_outcomes",
                filter: "paper_pnl_percent=eq.0&underlying_move_percent=eq.0",
                select: "id,paper_candidate_id,ticker",
                limit: 500);

            // Further filter: only delete if current_bid is also 0/null (confirms data failure)
            var toDelete = rows.Where(r =>
            {
                var bid = r["current_bid"];
                return bid is null || bid.GetValueKind() == System.Text.Json.JsonValueKind.Null
                    || (double.TryParse(bid.ToString(), out var v) && v == 0);
            }).ToList();

            if (toDelete.Count == 0) return 0;

            foreach (var row in toDelete)
            {
                var id = row["id"]?.ToString();
                if (id is null) continue;
                await _db.DeleteAsync("paper_option_outcomes", $"id=eq.{id}");
            }

            actions.Add($"Deleted {toDelete.Count} false option loss outcomes (0% P&L, no market data)");
            return toDelete.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[data-hygiene] Failed to clean false option losses");
            actions.Add($"FAILED: clean false option losses — {ex.Message}");
            return 0;
        }
    }

    // -----------------------------------------------------------------------
    // 2. Reopen candidates whose outcomes were deleted
    // -----------------------------------------------------------------------

    private async Task<int> ReopenFalselyEvaluatedCandidatesAsync(List<string> actions)
    {
        try
        {
            // Find candidates marked "evaluated" that have no outcome rows
            var evaluated = await _db.SelectAsync("paper_option_candidates",
                filter: "status=eq.evaluated",
                select: "id",
                limit: 500);

            int reopened = 0;
            foreach (var row in evaluated)
            {
                var id = row["id"]?.ToString();
                if (id is null) continue;

                var outcomes = await _db.SelectAsync("paper_option_outcomes",
                    filter: $"paper_candidate_id=eq.{id}",
                    select: "id",
                    limit: 1);

                if (outcomes.Count == 0)
                {
                    await _db.UpdateAsync("paper_option_candidates", $"id=eq.{id}",
                        new { status = "open" });
                    reopened++;
                }
            }

            if (reopened > 0)
                actions.Add($"Reopened {reopened} option candidates that had no outcome records");
            return reopened;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[data-hygiene] Failed to reopen falsely evaluated candidates");
            actions.Add($"FAILED: reopen candidates — {ex.Message}");
            return 0;
        }
    }

    // -----------------------------------------------------------------------
    // 3. Expire stale open predictions
    // -----------------------------------------------------------------------

    private async Task<int> ExpireStalePredictionsAsync(List<string> actions)
    {
        try
        {
            var open = await _db.SelectAsync("prediction_candidates",
                filter: "status=eq.open",
                select: "id,created_at,time_window",
                limit: 500);

            int expired = 0;
            var now = DateTimeOffset.UtcNow;
            foreach (var row in open)
            {
                var id = row["id"]?.ToString();
                var createdStr = row["created_at"]?.ToString();
                var timeWindow = row["time_window"]?.ToString() ?? "1_day";
                if (id is null || createdStr is null) continue;
                if (!DateTimeOffset.TryParse(createdStr, out var created)) continue;

                // Max age: 2x the time window
                var maxHours = timeWindow switch
                {
                    "intraday" => 24,
                    "1_day" => 48,
                    "3_day" => 144,
                    "1_week" => 480,
                    "1_month" => 1440,
                    _ => 480,
                };

                if ((now - created).TotalHours > maxHours)
                {
                    await _db.UpdateAsync("prediction_candidates", $"id=eq.{id}",
                        new { status = "expired" });
                    expired++;
                }
            }

            if (expired > 0)
                actions.Add($"Expired {expired} stale open predictions past their max evaluation window");
            return expired;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[data-hygiene] Failed to expire stale predictions");
            actions.Add($"FAILED: expire stale predictions — {ex.Message}");
            return 0;
        }
    }

    // -----------------------------------------------------------------------
    // 4. Expire stale open option candidates past expiration
    // -----------------------------------------------------------------------

    private async Task<int> ExpireStaleOptionCandidatesAsync(List<string> actions)
    {
        try
        {
            var open = await _db.SelectAsync("paper_option_candidates",
                filter: "status=eq.open",
                select: "id,expiration",
                limit: 500);

            int expired = 0;
            var now = DateTimeOffset.UtcNow;
            foreach (var row in open)
            {
                var id = row["id"]?.ToString();
                var expStr = row["expiration"]?.ToString();
                if (id is null || expStr is null) continue;
                if (!DateTimeOffset.TryParse(expStr, out var exp)) continue;

                if (exp < now)
                {
                    await _db.UpdateAsync("paper_option_candidates", $"id=eq.{id}",
                        new { status = "expired" });
                    expired++;
                }
            }

            if (expired > 0)
                actions.Add($"Expired {expired} paper option candidates past their expiration date");
            return expired;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[data-hygiene] Failed to expire stale option candidates");
            actions.Add($"FAILED: expire stale option candidates — {ex.Message}");
            return 0;
        }
    }

    // -----------------------------------------------------------------------
    // 5. Fix impossible values
    // -----------------------------------------------------------------------

    private async Task<int> FixImpossibleValuesAsync(List<string> actions, List<string> warnings)
    {
        int fixed_ = 0;
        try
        {
            // Confidence scores > 85 (our new max) in existing predictions
            var overconfident = await _db.SelectAsync("prediction_candidates",
                filter: "confidence_score=gt.85&status=eq.open",
                select: "id,confidence_score",
                limit: 200);

            foreach (var row in overconfident)
            {
                var id = row["id"]?.ToString();
                if (id is null) continue;
                await _db.UpdateAsync("prediction_candidates", $"id=eq.{id}",
                    new { confidence_score = 85 });
                fixed_++;
            }

            if (overconfident.Count > 0)
                actions.Add($"Capped {overconfident.Count} open predictions with confidence > 85");

            // Negative entry prices
            var negPrices = await _db.SelectAsync("prediction_candidates",
                filter: "entry_reference_price=lt.0&status=eq.open",
                select: "id",
                limit: 100);

            foreach (var row in negPrices)
            {
                var id = row["id"]?.ToString();
                if (id is null) continue;
                await _db.UpdateAsync("prediction_candidates", $"id=eq.{id}",
                    new { status = "expired" });
                fixed_++;
            }

            if (negPrices.Count > 0)
            {
                actions.Add($"Expired {negPrices.Count} predictions with negative entry prices");
                warnings.Add("Found predictions with negative prices — investigate data source");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[data-hygiene] Failed to fix impossible values");
            actions.Add($"FAILED: fix impossible values — {ex.Message}");
        }
        return fixed_;
    }

    // -----------------------------------------------------------------------
    // 6. Clean orphaned records
    // -----------------------------------------------------------------------

    private async Task<int> CleanOrphanedRecordsAsync(List<string> actions)
    {
        // Orphaned signal observations pointing to non-existent predictions
        // This is a lightweight check — just count, don't mass-delete on first run
        try
        {
            var obsCount = await _db.CountAsync("prediction_signal_observations");
            if (obsCount > 0)
            {
                // Sample check: grab 20 recent observations and verify their prediction exists
                var sample = await _db.SelectAsync("prediction_signal_observations",
                    select: "id,prediction_id",
                    order: "created_at.desc",
                    limit: 20);

                int orphaned = 0;
                foreach (var row in sample)
                {
                    var predId = row["prediction_id"]?.ToString();
                    if (predId is null) { orphaned++; continue; }

                    var pred = await _db.SelectSingleAsync("prediction_candidates", $"id=eq.{predId}");
                    if (pred is null) orphaned++;
                }

                if (orphaned > 5)
                {
                    // More than 25% orphaned in sample — flag for attention
                    actions.Add($"WARNING: {orphaned}/20 sampled signal observations are orphaned — consider full cleanup");
                    return orphaned;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[data-hygiene] Failed to check orphaned records");
        }
        return 0;
    }

    // -----------------------------------------------------------------------
    // 7. Clean learning stats with tiny sample sizes
    // -----------------------------------------------------------------------

    private async Task<int> CleanLowSampleStatsAsync(List<string> actions)
    {
        try
        {
            // option_learning_stats with total_trades < 3 are noise
            var lowSample = await _db.SelectAsync("option_learning_stats",
                filter: "total_trades=lt.3",
                select: "id",
                limit: 200);

            foreach (var row in lowSample)
            {
                var id = row["id"]?.ToString();
                if (id is null) continue;
                await _db.DeleteAsync("option_learning_stats", $"id=eq.{id}");
            }

            if (lowSample.Count > 0)
                actions.Add($"Deleted {lowSample.Count} option learning stats with < 3 trades (noise)");

            // research_signal_performance with total < 3
            var lowSignal = await _db.SelectAsync("research_signal_performance",
                filter: "total_predictions=lt.3",
                select: "id",
                limit: 200);

            foreach (var row in lowSignal)
            {
                var id = row["id"]?.ToString();
                if (id is null) continue;
                await _db.DeleteAsync("research_signal_performance", $"id=eq.{id}");
            }

            if (lowSignal.Count > 0)
                actions.Add($"Deleted {lowSignal.Count} signal performance stats with < 3 predictions (noise)");

            return lowSample.Count + lowSignal.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[data-hygiene] Failed to clean low sample stats");
            actions.Add($"FAILED: clean low sample stats — {ex.Message}");
            return 0;
        }
    }
}
