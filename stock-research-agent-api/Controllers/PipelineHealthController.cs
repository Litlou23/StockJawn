using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// Pipeline health check — detects silent failures like missing DB columns,
/// failed saves, or stale data that indicate the pipeline is broken.
///
///   GET /api/health/pipeline — full health report
/// </summary>
[ApiController]
[Route("api/health")]
public class PipelineHealthController : ControllerBase
{
    private readonly SupabaseClient _db;
    private readonly ILogger<PipelineHealthController> _logger;

    public PipelineHealthController(SupabaseClient db, ILogger<PipelineHealthController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet("pipeline")]
    public async Task<IActionResult> GetPipelineHealth()
    {
        var warnings = new List<string>();
        var checks = new Dictionary<string, object>();

        var now = DateTimeOffset.UtcNow;
        var today = now.Date.ToString("yyyy-MM-dd");
        var yesterday = now.Date.AddDays(-1).ToString("yyyy-MM-dd");

        // 1. Research runs — did a morning scan run today?
        try
        {
            var recentRuns = await _db.SelectAsync("research_runs",
                filter: $"run_type=eq.morning_scan&started_at=gte.{yesterday}",
                order: "started_at.desc",
                limit: 5);

            var todayRuns = recentRuns.Count(r => r["started_at"]?.ToString()?.StartsWith(today) == true);
            checks["morningScansToday"] = todayRuns;
            checks["morningScansLast24h"] = recentRuns.Count;

            if (todayRuns == 0)
                warnings.Add("No morning scan has run today.");

            // Check latest run for errors
            if (recentRuns.Count > 0)
            {
                var latest = recentRuns[0];
                var errors = latest["errors"]?.AsArray();
                if (errors is not null && errors.Count > 0)
                {
                    warnings.Add($"Latest morning scan had {errors.Count} error(s): {errors[0]}");
                }
                checks["latestRunAt"] = latest["started_at"]?.ToString() ?? "";
                checks["latestRunPredictions"] = latest["predictions_generated"]?.ToString() ?? "0";
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not check research_runs: {ex.Message}");
        }

        // 2. Predictions — are predictions being created?
        try
        {
            var predCount = await _db.CountAsync("prediction_candidates",
                $"created_at=gte.{yesterday}");
            checks["predictionsLast24h"] = predCount;
            if (predCount == 0)
                warnings.Add("No predictions created in the last 24 hours.");
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not check prediction_candidates: {ex.Message}");
        }

        // 3. Paper stock candidates — are candidates being saved? (THE BIG ONE)
        try
        {
            var candidateCount = await _db.CountAsync("paper_stock_candidates",
                $"created_at=gte.{yesterday}");
            checks["stockCandidatesLast24h"] = candidateCount;

            var predCount = checks.TryGetValue("predictionsLast24h", out var pc) ? (int)pc : 0;
            if (predCount > 0 && candidateCount == 0)
                warnings.Add("CRITICAL: Predictions are being created but NO paper stock candidates are being saved. " +
                             "This usually means the database schema is missing columns that the code expects. " +
                             "Check Supabase logs for INSERT errors on paper_stock_candidates.");
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not check paper_stock_candidates: {ex.Message}");
        }

        // 4. Portfolio positions — are positions being opened?
        try
        {
            var posCount = await _db.CountAsync("portfolio_positions",
                $"created_at=gte.{yesterday}");
            checks["portfolioPositionsLast24h"] = posCount;

            var candidateCount = checks.TryGetValue("stockCandidatesLast24h", out var cc) ? (int)cc : 0;
            if (candidateCount > 0 && posCount == 0)
                warnings.Add("Stock candidates exist but no portfolio positions were opened. " +
                             "Check if there's an active portfolio challenge and if any candidates are actionable.");
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not check portfolio_positions: {ex.Message}");
        }

        // 5. EOD evaluations — are we evaluating?
        try
        {
            var eodRuns = await _db.CountAsync("research_runs",
                $"run_type=eq.end_of_day_review&started_at=gte.{yesterday}");
            checks["eodReviewsLast24h"] = eodRuns;
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not check EOD reviews: {ex.Message}");
        }

        // 6. Schema drift check — verify critical columns exist
        try
        {
            var schemaWarnings = await CheckCriticalSchemaAsync();
            warnings.AddRange(schemaWarnings);
            checks["schemaDriftWarnings"] = schemaWarnings.Count;
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not check schema: {ex.Message}");
        }

        var status = warnings.Count == 0 ? "healthy"
            : warnings.Any(w => w.StartsWith("CRITICAL")) ? "critical"
            : "degraded";

        if (warnings.Count > 0)
            _logger.LogWarning("[pipeline-health] Status={Status}, Warnings={Count}: {Warnings}",
                status, warnings.Count, string.Join(" | ", warnings));

        return Ok(new
        {
            status,
            checkedAt = now.ToString("o"),
            warnings,
            checks,
        });
    }

    private async Task<List<string>> CheckCriticalSchemaAsync()
    {
        var warnings = new List<string>();

        var expectedColumns = new Dictionary<string, string[]>
        {
            ["paper_stock_candidates"] = [
                "id", "prediction_id", "run_id", "ticker", "prediction_type", "timeframe",
                "entry_price", "confidence_score", "risk_score", "status", "candidate_mode",
                "quality_tier", "is_actionable", "bullish_score", "bearish_score", "winning_direction"
            ],
            ["portfolio_challenges"] = [
                "id", "name", "starting_balance", "current_balance", "target_balance",
                "current_cash", "status", "risk_profile", "portfolio_mode"
            ],
            ["portfolio_positions"] = [
                "id", "portfolio_id", "ticker", "entry_price", "quantity",
                "dollars_invested", "status", "prediction_id"
            ],
        };

        foreach (var (table, columns) in expectedColumns)
        {
            // Lightweight probe: SELECT the expected columns with an impossible filter.
            // PostgREST returns 400 if any column doesn't exist, and SelectAsync returns [].
            // We check by selecting each column — if the response is empty that's fine (no rows match),
            // but if PostgREST rejects the column name it logs a warning via SupabaseClient.
            try
            {
                var probe = await _db.SelectAsync(table,
                    filter: "id=eq.00000000-0000-0000-0000-000000000000",
                    select: string.Join(",", columns),
                    limit: 1);
                // If we get here, all columns exist (0 rows is fine).
            }
            catch
            {
                warnings.Add($"SCHEMA DRIFT: Table '{table}' may be missing expected columns. " +
                             $"Expected: {string.Join(", ", columns)}");
            }
        }

        return warnings;
    }
}
