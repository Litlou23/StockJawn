using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.ResearchEngine;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// Compact data endpoints for the AI chat tool-calling loop.
/// Each returns a small JSON envelope: { tool_name, as_of, summary, data, warnings }.
/// Designed to stay under ~500 tokens per response so the AI context stays lean.
/// </summary>
[ApiController]
[Route("api/chat-tools")]
public class ChatToolsController : ControllerBase
{
    private readonly ResearchRepository _researchRepo;
    private readonly PaperStockCandidateRepository _stockRepo;
    private readonly OptionsDataRepository _optionsRepo;
    private readonly CandidateGenerationAuditRepository _auditRepo;
    private readonly DynamicPickOrchestrator _orchestrator;
    private readonly ILogger<ChatToolsController> _logger;

    public ChatToolsController(
        ResearchRepository researchRepo,
        PaperStockCandidateRepository stockRepo,
        OptionsDataRepository optionsRepo,
        CandidateGenerationAuditRepository auditRepo,
        DynamicPickOrchestrator orchestrator,
        ILogger<ChatToolsController> logger)
    {
        _researchRepo = researchRepo;
        _stockRepo = stockRepo;
        _optionsRepo = optionsRepo;
        _auditRepo = auditRepo;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("o");

    // -----------------------------------------------------------------------
    // 1. get_dashboard_summary
    // -----------------------------------------------------------------------

    [HttpGet("get_dashboard_summary")]
    public async Task<IActionResult> GetDashboardSummary()
    {
        var summary = await _orchestrator.GetDashboardSummaryAsync();
        var warnings = new List<string>();

        if (summary.LatestRunId is null)
            warnings.Add("No morning scan has run yet. Run 'Morning Scan' from the dashboard.");

        var mode = "Learning Mode / Paper Only / Not Actionable";

        var summaryText = summary.LatestRunId is null
            ? "No morning scan has been recorded yet. The system has no predictions or candidates."
            : $"Latest run at {summary.LatestRunStartedAt:HH:mm:ss UTC} produced {summary.LatestRunPredictionCandidatesGenerated} predictions, " +
              $"{summary.LatestRunPaperStockCandidatesCreated} stock candidates, {summary.LatestRunPaperOptionCandidatesCreated} option candidates. " +
              $"{summary.LatestRunBlockedOptionCandidates} options blocked" +
              (summary.LatestRunTopOptionBlockReason is not null ? $" (top reason: {summary.LatestRunTopOptionBlockReason})" : "") +
              $". Outcomes today: {summary.StockOutcomesAddedToday + summary.OptionOutcomesAddedToday}. " +
              $"Open candidates awaiting EOD: {summary.CandidatesAwaitingEodEvaluation}.";

        return Ok(new
        {
            tool_name = "get_dashboard_summary",
            as_of = Now(),
            summary = summaryText,
            data = new
            {
                mode,
                latest_run_id = summary.LatestRunId,
                latest_run_time = summary.LatestRunStartedAt?.ToString("o"),
                predictions = summary.LatestRunPredictionCandidatesGenerated,
                stock_candidates = summary.LatestRunPaperStockCandidatesCreated,
                option_candidates = summary.LatestRunPaperOptionCandidatesCreated,
                blocked_options = summary.LatestRunBlockedOptionCandidates,
                top_block_reason = summary.LatestRunTopOptionBlockReason,
                outcomes_today = summary.StockOutcomesAddedToday + summary.OptionOutcomesAddedToday,
                awaiting_eod = summary.CandidatesAwaitingEodEvaluation,
                total_stock_outcomes = summary.TotalStockOutcomes,
                total_option_outcomes = summary.TotalOptionOutcomes,
            },
            warnings,
        });
    }

    // -----------------------------------------------------------------------
    // 2. get_predictions
    // -----------------------------------------------------------------------

    [HttpGet("get_predictions")]
    public async Task<IActionResult> GetPredictions(
        [FromQuery] string? ticker = null,
        [FromQuery] string? prediction_type = null,
        [FromQuery] string? run_id = null,
        [FromQuery] bool count_only = false,
        [FromQuery] int limit = 10)
    {
        var warnings = new List<string>();

        // Determine which run to query
        string? effectiveRunId = run_id;
        if (effectiveRunId is null)
        {
            var latest = await _researchRepo.GetLatestResearchRunAsync("morning_scan");
            effectiveRunId = latest?.Id;
            if (effectiveRunId is null)
            {
                return Ok(new
                {
                    tool_name = "get_predictions",
                    as_of = Now(),
                    summary = "No morning scan has run yet. There are no predictions.",
                    data = new { count = 0 },
                    warnings = new[] { "No morning scan found." },
                });
            }
        }

        var predictions = await _researchRepo.GetPredictionsByRunAsync(effectiveRunId);

        // Apply filters
        if (!string.IsNullOrWhiteSpace(ticker))
            predictions = predictions.Where(p => p.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(prediction_type) && Enum.TryParse<PredictionType>(prediction_type, true, out var pt))
            predictions = predictions.Where(p => p.PredictionType == pt).ToList();

        // Group by type for summary
        var grouped = predictions.GroupBy(p => p.PredictionType).ToDictionary(g => g.Key.ToString(), g => g.Count());
        var summaryText = $"Run {effectiveRunId[..8]} has {predictions.Count} predictions: " +
                          string.Join(", ", grouped.Select(kv => $"{kv.Value} {kv.Key}")) + ".";

        if (count_only)
        {
            return Ok(new
            {
                tool_name = "get_predictions",
                as_of = Now(),
                summary = summaryText,
                data = new { run_id = effectiveRunId, count = predictions.Count, by_type = grouped },
                warnings,
            });
        }

        var items = predictions.Take(limit).Select(p => new
        {
            ticker = p.Ticker,
            type = p.PredictionType.ToString(),
            confidence = p.ConfidenceScore,
            risk = p.RiskScore,
            time_window = p.TimeWindow,
            reason = Truncate(p.PredictionReason, 120),
            entry_price = p.EntryReferencePrice,
            target_price = p.TargetPrice,
            stop_price = p.StopPrice,
            rr_ratio = p.RiskRewardRatio,
        });

        return Ok(new
        {
            tool_name = "get_predictions",
            as_of = Now(),
            summary = summaryText,
            data = new { run_id = effectiveRunId, count = predictions.Count, by_type = grouped, items },
            warnings,
        });
    }

    // -----------------------------------------------------------------------
    // 3. get_stock_candidates
    // -----------------------------------------------------------------------

    [HttpGet("get_stock_candidates")]
    public async Task<IActionResult> GetStockCandidates(
        [FromQuery] string? ticker = null,
        [FromQuery] string? candidate_mode = null,
        [FromQuery] string? quality_tier = null,
        [FromQuery] string? run_id = null,
        [FromQuery] bool count_only = false,
        [FromQuery] int limit = 10)
    {
        var warnings = new List<string>();
        List<PaperStockCandidate> candidates;

        if (!string.IsNullOrWhiteSpace(run_id))
        {
            candidates = await _stockRepo.GetCandidatesByRunAsync(run_id);
        }
        else
        {
            candidates = await _stockRepo.GetRecentCandidatesAsync(200);
            // Default: filter to latest run
            var latestRunId = candidates.FirstOrDefault()?.RunId;
            if (latestRunId is not null)
                candidates = candidates.Where(c => c.RunId == latestRunId).ToList();
        }

        if (candidates.Count == 0)
        {
            return Ok(new
            {
                tool_name = "get_stock_candidates",
                as_of = Now(),
                summary = "No stock candidates found.",
                data = new { count = 0 },
                warnings = new[] { "No stock candidates exist for the queried run." },
            });
        }

        // Apply filters
        if (!string.IsNullOrWhiteSpace(ticker))
            candidates = candidates.Where(c => c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(candidate_mode) && Enum.TryParse<CandidateMode>(candidate_mode, true, out var cm))
            candidates = candidates.Where(c => c.CandidateMode == cm).ToList();
        if (!string.IsNullOrWhiteSpace(quality_tier) && Enum.TryParse<QualityTier>(quality_tier, true, out var qt))
            candidates = candidates.Where(c => c.QualityTier == qt).ToList();

        var byMode = candidates.GroupBy(c => c.CandidateMode.ToString()).ToDictionary(g => g.Key, g => g.Count());
        var byTier = candidates.GroupBy(c => c.QualityTier.ToString()).ToDictionary(g => g.Key, g => g.Count());
        var optionEligible = candidates.Count(c => c.QualifiesForOptions);

        var summaryText = $"{candidates.Count} stock candidates. " +
                          $"Modes: {string.Join(", ", byMode.Select(kv => $"{kv.Value} {kv.Key}"))}. " +
                          $"{optionEligible} option-eligible.";

        if (count_only)
        {
            return Ok(new
            {
                tool_name = "get_stock_candidates",
                as_of = Now(),
                summary = summaryText,
                data = new { count = candidates.Count, by_mode = byMode, by_tier = byTier, option_eligible = optionEligible },
                warnings,
            });
        }

        var items = candidates.Take(limit).Select(c => new
        {
            ticker = c.Ticker,
            prediction_type = c.PredictionType.ToString(),
            status = c.Status.ToString(),
            candidate_mode = c.CandidateMode.ToString(),
            quality_tier = c.QualityTier.ToString(),
            total_score = c.TotalScore,
            confidence = c.ConfidenceScore,
            risk = c.RiskScore,
            entry_price = c.EntryPrice,
            target_price = c.TargetPrice,
            stop_price = c.StopPrice,
            qualifies_for_options = c.QualifiesForOptions,
            exclusion_reason = c.ExclusionReason,
        });

        return Ok(new
        {
            tool_name = "get_stock_candidates",
            as_of = Now(),
            summary = summaryText,
            data = new { count = candidates.Count, by_mode = byMode, by_tier = byTier, option_eligible = optionEligible, items },
            warnings,
        });
    }

    // -----------------------------------------------------------------------
    // 4. get_option_candidates
    // -----------------------------------------------------------------------

    [HttpGet("get_option_candidates")]
    public async Task<IActionResult> GetOptionCandidates(
        [FromQuery] string? ticker = null,
        [FromQuery] string? option_type = null,
        [FromQuery] bool count_only = false,
        [FromQuery] bool include_block_reasons = false,
        [FromQuery] int limit = 10)
    {
        var warnings = new List<string>();
        var candidates = await _optionsRepo.GetAllPaperCandidatesAsync(200);

        if (!string.IsNullOrWhiteSpace(ticker))
            candidates = candidates.Where(c => c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(option_type) && Enum.TryParse<OptionSide>(option_type, true, out var os))
            candidates = candidates.Where(c => c.Side == os).ToList();

        // Get block reasons from audit if requested
        List<CandidateGenerationAuditEntry>? auditEntries = null;
        if (include_block_reasons || candidates.Count == 0)
        {
            var latestRun = await _researchRepo.GetLatestResearchRunAsync("morning_scan");
            if (latestRun is not null)
            {
                auditEntries = await _auditRepo.GetByRunAsync(latestRun.Id);
            }
        }

        var blockReasons = auditEntries?
            .Where(a => !a.OptionCandidateCreated && !string.IsNullOrWhiteSpace(a.OptionBlockReason))
            .GroupBy(a => a.OptionBlockReason!)
            .ToDictionary(g => g.Key, g => g.Count());

        var summaryText = candidates.Count == 0
            ? "No option candidates were created." +
              (blockReasons is { Count: > 0 }
                  ? $" Top block reasons: {string.Join(", ", blockReasons.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} ({kv.Value})"))}"
                  : "")
            : $"{candidates.Count} option candidates found.";

        if (count_only)
        {
            return Ok(new
            {
                tool_name = "get_option_candidates",
                as_of = Now(),
                summary = summaryText,
                data = new
                {
                    count = candidates.Count,
                    block_reasons = blockReasons,
                },
                warnings,
            });
        }

        var items = candidates.Take(limit).Select(c => new
        {
            ticker = c.Ticker,
            side = c.Side.ToString(),
            strike = c.Strike,
            expiration = c.Expiration,
            option_symbol = c.OptionSymbol,
            status = c.Status.ToString(),
            entry_mid = c.EntryMid,
            entry_iv = c.EntryIv,
            entry_delta = c.EntryDelta,
            contract_score = c.ContractScore,
        });

        return Ok(new
        {
            tool_name = "get_option_candidates",
            as_of = Now(),
            summary = summaryText,
            data = new
            {
                count = candidates.Count,
                block_reasons = blockReasons,
                items,
            },
            warnings,
        });
    }

    // -----------------------------------------------------------------------
    // 5. get_ticker_detail
    // -----------------------------------------------------------------------

    [HttpGet("get_ticker_detail")]
    public async Task<IActionResult> GetTickerDetail([FromQuery] string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker))
            return BadRequest(new { error = "ticker is required" });

        ticker = ticker.Trim().ToUpperInvariant();
        var warnings = new List<string>();

        // Latest prediction for this ticker
        var predictions = await _researchRepo.GetRecentPredictionsAsync(50);
        var pred = predictions.FirstOrDefault(p => p.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase));

        // Stock candidate
        var stockCandidates = await _stockRepo.GetRecentCandidatesAsync(100);
        var stockCandidate = stockCandidates.FirstOrDefault(c => c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase));

        // Option candidate
        var optionCandidates = await _optionsRepo.GetAllPaperCandidatesAsync(100);
        var optionCandidate = optionCandidates.FirstOrDefault(c => c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase));

        // Option block reason from audit
        string? optionBlockReason = null;
        if (optionCandidate is null && pred is not null)
        {
            var audits = await _auditRepo.GetByRunAsync(pred.RunId);
            var audit = audits.FirstOrDefault(a => a.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase));
            optionBlockReason = audit?.OptionBlockReason;
        }

        // Stock outcome
        PaperStockOutcome? stockOutcome = null;
        if (stockCandidate is not null)
        {
            var outcomes = await _stockRepo.GetRecentOutcomesAsync(100);
            stockOutcome = outcomes.FirstOrDefault(o => o.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase));
        }

        if (pred is null && stockCandidate is null)
        {
            return Ok(new
            {
                tool_name = "get_ticker_detail",
                as_of = Now(),
                summary = $"No data found for {ticker}. It may not be on the active watchlist.",
                data = new { ticker, found = false },
                warnings = new[] { $"{ticker} not found in recent predictions or candidates." },
            });
        }

        var parts = new List<string> { $"{ticker}:" };
        if (pred is not null)
            parts.Add($"Prediction: {pred.PredictionType} (conf={pred.ConfidenceScore}, risk={pred.RiskScore}, R:R={pred.RiskRewardRatio:F2}). {Truncate(pred.PredictionReason, 100)}");
        if (stockCandidate is not null)
            parts.Add($"Stock candidate: {stockCandidate.Status} / {stockCandidate.CandidateMode} / {stockCandidate.QualityTier}. Score={stockCandidate.TotalScore:F1}. Options eligible={stockCandidate.QualifiesForOptions}.");
        if (optionCandidate is not null)
            parts.Add($"Option: {optionCandidate.Side} {optionCandidate.Strike} exp {optionCandidate.Expiration:yyyy-MM-dd}.");
        else if (optionBlockReason is not null)
            parts.Add($"Option blocked: {optionBlockReason}.");
        if (stockOutcome is not null)
            parts.Add($"Outcome: {stockOutcome.PercentMove:F2}% move, direction {(stockOutcome.DirectionCorrect == true ? "correct" : stockOutcome.DirectionCorrect == false ? "wrong" : "n/a")}.");

        return Ok(new
        {
            tool_name = "get_ticker_detail",
            as_of = Now(),
            summary = string.Join(" ", parts),
            data = new
            {
                ticker,
                found = true,
                prediction = pred is null ? null : new
                {
                    type = pred.PredictionType.ToString(),
                    confidence = pred.ConfidenceScore,
                    risk = pred.RiskScore,
                    rr_ratio = pred.RiskRewardRatio,
                    entry_price = pred.EntryReferencePrice,
                    target_price = pred.TargetPrice,
                    stop_price = pred.StopPrice,
                    time_window = pred.TimeWindow,
                    reason = Truncate(pred.PredictionReason, 150),
                    bullish_case = Truncate(pred.BullishCase, 100),
                    bearish_case = Truncate(pred.BearishCase, 100),
                    data_sources = pred.DataSourcesUsed,
                    missing_warnings = pred.MissingDataWarnings,
                },
                stock_candidate = stockCandidate is null ? null : new
                {
                    status = stockCandidate.Status.ToString(),
                    candidate_mode = stockCandidate.CandidateMode.ToString(),
                    quality_tier = stockCandidate.QualityTier.ToString(),
                    total_score = stockCandidate.TotalScore,
                    entry_price = stockCandidate.EntryPrice,
                    target_price = stockCandidate.TargetPrice,
                    stop_price = stockCandidate.StopPrice,
                    qualifies_for_options = stockCandidate.QualifiesForOptions,
                    exclusion_reason = stockCandidate.ExclusionReason,
                    is_actionable = stockCandidate.IsActionable,
                },
                option_candidate = optionCandidate is null ? null : new
                {
                    side = optionCandidate.Side.ToString(),
                    strike = optionCandidate.Strike,
                    expiration = optionCandidate.Expiration,
                    option_symbol = optionCandidate.OptionSymbol,
                    status = optionCandidate.Status.ToString(),
                    entry_mid = optionCandidate.EntryMid,
                    entry_iv = optionCandidate.EntryIv,
                    entry_delta = optionCandidate.EntryDelta,
                    contract_score = optionCandidate.ContractScore,
                    selection_reason = Truncate(optionCandidate.SelectionReason, 100),
                },
                option_block_reason = optionBlockReason,
                outcome = stockOutcome is null ? null : new
                {
                    percent_move = stockOutcome.PercentMove,
                    direction_correct = stockOutcome.DirectionCorrect,
                    target_hit = stockOutcome.TargetHit,
                    stop_hit = stockOutcome.StopHit,
                    outcome_score = stockOutcome.OutcomeScore,
                    lesson = Truncate(stockOutcome.Lesson, 100),
                },
            },
            warnings,
        });
    }

    private static string Truncate(string? s, int max) =>
        s is null ? "" : s.Length <= max ? s : s[..max] + "...";
}
