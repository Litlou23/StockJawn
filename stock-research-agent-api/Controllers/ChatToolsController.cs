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
    private readonly LearningEngine _learning;
    private readonly TradeSetupEngine _setupEngine;
    private readonly ILogger<ChatToolsController> _logger;

    public ChatToolsController(
        ResearchRepository researchRepo,
        PaperStockCandidateRepository stockRepo,
        OptionsDataRepository optionsRepo,
        CandidateGenerationAuditRepository auditRepo,
        DynamicPickOrchestrator orchestrator,
        LearningEngine learning,
        TradeSetupEngine setupEngine,
        ILogger<ChatToolsController> logger)
    {
        _researchRepo = researchRepo;
        _stockRepo = stockRepo;
        _optionsRepo = optionsRepo;
        _auditRepo = auditRepo;
        _orchestrator = orchestrator;
        _learning = learning;
        _setupEngine = setupEngine;
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

    // -----------------------------------------------------------------------
    // 6. get_setup_performance — top/degraded trade setups
    // -----------------------------------------------------------------------

    [HttpGet("get_setup_performance")]
    public async Task<IActionResult> GetSetupPerformance(
        [FromQuery] string? filter = null,
        [FromQuery] int limit = 10)
    {
        var stats = await _researchRepo.GetAllSetupLearningStatsAsync();
        var warnings = new List<string>();

        if (stats.Count == 0)
        {
            return Ok(new
            {
                tool_name = "get_setup_performance",
                as_of = Now(),
                summary = "No setup performance data yet. The system needs more evaluated predictions to build setup statistics.",
                data = new { count = 0 },
                warnings,
            });
        }

        var filtered = filter switch
        {
            "top" => stats.Where(s => s.TotalOccurrences >= 8 && s.ExpectedValuePercent > 0).OrderByDescending(s => s.ExpectedValuePercent).ToList(),
            "degraded" => stats.Where(s => !s.IsTrusted && s.TotalOccurrences >= 5).ToList(),
            "negative" => stats.Where(s => s.ExpectedValuePercent < 0 && s.TotalOccurrences >= 5).OrderBy(s => s.ExpectedValuePercent).ToList(),
            _ => stats.OrderByDescending(s => s.ExpectedValuePercent).ToList(),
        };

        var summaryText = $"{stats.Count} setup fingerprints tracked. " +
                          $"{stats.Count(s => s.ExpectedValuePercent > 0 && s.TotalOccurrences >= 8)} with positive EV, " +
                          $"{stats.Count(s => !s.IsTrusted)} degraded.";

        var items = filtered.Take(limit).Select(s => new
        {
            fingerprint = s.SetupFingerprint,
            description = s.Description,
            direction = s.Direction,
            occurrences = s.TotalOccurrences,
            win_rate = $"{s.WinRate * 100:F1}%",
            avg_win = $"+{s.AverageWinPercent:F2}%",
            avg_loss = $"{s.AverageLossPercent:F2}%",
            expected_value = $"{(s.ExpectedValuePercent >= 0 ? "+" : "")}{s.ExpectedValuePercent:F2}%",
            confidence = s.Confidence,
            risk_rating = s.RiskRating,
            is_trusted = s.IsTrusted,
        });

        return Ok(new
        {
            tool_name = "get_setup_performance",
            as_of = Now(),
            summary = summaryText,
            data = new { total_setups = stats.Count, filter = filter ?? "all", items },
            warnings,
        });
    }

    // -----------------------------------------------------------------------
    // 7. get_learning_stats — signal performance + calibration + weights
    // -----------------------------------------------------------------------

    [HttpGet("get_learning_stats")]
    public async Task<IActionResult> GetLearningStats()
    {
        var warnings = new List<string>();
        var signalPerf = await _researchRepo.GetAllSignalPerformanceAsync();
        var calibration = await _learning.ComputeConfidenceCalibrationAsync();
        var weightOverrides = await _researchRepo.GetActiveWeightOverridesAsync();

        var calFactor = weightOverrides
            .FirstOrDefault(o => o.SignalName == "calibration_factor");

        var summaryParts = new List<string>
        {
            $"{signalPerf.Count} signal performance records",
            $"Calibration: {calibration.Summary}",
        };
        if (calFactor is not null)
            summaryParts.Add($"Active calibration factor: {calFactor.EffectiveWeight:F4}");

        var topSignals = signalPerf
            .Where(s => s.Direction == "all" && s.TotalPredictions >= 10)
            .OrderByDescending(s => s.Accuracy)
            .Take(5)
            .Select(s => new { signal = s.SignalName, accuracy = $"{s.Accuracy * 100:F1}%", n = s.TotalPredictions });

        var worstSignals = signalPerf
            .Where(s => s.Direction == "all" && s.TotalPredictions >= 10)
            .OrderBy(s => s.Accuracy)
            .Take(5)
            .Select(s => new { signal = s.SignalName, accuracy = $"{s.Accuracy * 100:F1}%", n = s.TotalPredictions });

        var activeOverrides = weightOverrides
            .Where(o => o.SignalName != "calibration_factor")
            .Select(o => new { signal = o.SignalName, effective_weight = o.EffectiveWeight, adjustment = $"{o.AdjustmentPercent * 100:F1}%", reason = Truncate(o.Reason, 80) });

        return Ok(new
        {
            tool_name = "get_learning_stats",
            as_of = Now(),
            summary = string.Join(". ", summaryParts) + ".",
            data = new
            {
                calibration = new
                {
                    is_overconfident = calibration.IsOverconfident,
                    calibration_factor = calFactor?.EffectiveWeight,
                    buckets = calibration.Buckets.Select(b => new
                    {
                        range = b.Range,
                        count = b.Count,
                        actual_accuracy = $"{b.ActualAccuracy * 100:F1}%",
                        expected_accuracy = $"{b.ExpectedAccuracy * 100:F1}%",
                        error = $"{b.CalibrationError * 100:F1}%",
                    }),
                },
                top_signals = topSignals,
                worst_signals = worstSignals,
                weight_overrides = activeOverrides,
            },
            warnings,
        });
    }

    // -----------------------------------------------------------------------
    // 8. run_learning_update — trigger a full learning cycle
    // -----------------------------------------------------------------------

    [HttpPost("run_learning_update")]
    public async Task<IActionResult> RunLearningUpdate()
    {
        _logger.LogInformation("[chat-tools] Learning update triggered via chat");
        var result = await _orchestrator.RunDynamicLearningUpdateAsync();

        return Ok(new
        {
            tool_name = "run_learning_update",
            as_of = Now(),
            summary = result.Report,
            data = new
            {
                stock_stats = result.StockStatsUpdated,
                option_stats = result.OptionStatsUpdated,
                weights_adjusted = result.WeightsAdjusted,
                insights = result.InsightsGenerated,
            },
            warnings = result.Errors,
        });
    }

    // -----------------------------------------------------------------------
    // 9. run_morning_scan — trigger morning picks
    // -----------------------------------------------------------------------

    [HttpPost("run_morning_scan")]
    public async Task<IActionResult> RunMorningScan()
    {
        _logger.LogInformation("[chat-tools] Morning scan triggered via chat");
        var result = await _orchestrator.RunDynamicMorningPicksAsync();

        return Ok(new
        {
            tool_name = "run_morning_scan",
            as_of = Now(),
            summary = result.Report,
            data = new
            {
                run_id = result.RunId,
                predictions = result.PredictionsGenerated,
                stock_candidates = result.StockCandidatesGenerated,
                option_candidates = result.OptionCandidatesGenerated,
            },
            warnings = result.Errors,
        });
    }

    // -----------------------------------------------------------------------
    // 10. run_eod_review — trigger end-of-day evaluation
    // -----------------------------------------------------------------------

    [HttpPost("run_eod_review")]
    public async Task<IActionResult> RunEodReview()
    {
        _logger.LogInformation("[chat-tools] EOD review triggered via chat");
        var result = await _orchestrator.RunDynamicEodReviewAsync();

        return Ok(new
        {
            tool_name = "run_eod_review",
            as_of = Now(),
            summary = result.Report,
            data = new
            {
                stock_outcomes = result.StockOutcomesEvaluated,
                option_outcomes = result.OptionOutcomesEvaluated,
            },
            warnings = result.Errors,
        });
    }

    // -----------------------------------------------------------------------
    // 11. explain_scoring — break down why a ticker got its score
    // -----------------------------------------------------------------------

    [HttpGet("explain_scoring")]
    public async Task<IActionResult> ExplainScoring([FromQuery] string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker))
            return BadRequest(new { error = "ticker is required" });

        ticker = ticker.Trim().ToUpperInvariant();
        var warnings = new List<string>();

        var predictions = await _researchRepo.GetRecentPredictionsAsync(50);
        var pred = predictions.FirstOrDefault(p => p.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase));

        if (pred is null)
        {
            return Ok(new
            {
                tool_name = "explain_scoring",
                as_of = Now(),
                summary = $"No recent prediction found for {ticker}.",
                data = new { ticker, found = false },
                warnings,
            });
        }

        // Parse the score debug JSON for full breakdown
        ScoringBreakdown? breakdown = null;
        if (!string.IsNullOrEmpty(pred.ScoreDebugJson))
        {
            try
            {
                breakdown = System.Text.Json.JsonSerializer.Deserialize<ScoringBreakdown>(
                    pred.ScoreDebugJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { warnings.Add("Could not parse scoring breakdown"); }
        }

        // Get setup fingerprint if available
        string? fingerprint = null;
        SetupPerformance? setupPerf = null;
        if (breakdown is not null)
        {
            var evidence = TradeSetupEngine.BuildSignalEvidenceFromBreakdown(breakdown);
            var fp = TradeSetupEngine.GenerateFingerprint(evidence, pred.WinningDirection ?? "neutral");
            fingerprint = fp.Fingerprint;
            if (!string.IsNullOrEmpty(fingerprint))
                setupPerf = await _setupEngine.LookupSetupPerformanceAsync(fingerprint);
        }

        var summaryParts = new List<string>
        {
            $"{ticker}: {pred.PredictionType} (conf={pred.ConfidenceScore}, risk={pred.RiskScore})",
        };
        if (fingerprint is not null)
            summaryParts.Add($"Setup: {fingerprint}");
        if (setupPerf is not null)
            summaryParts.Add($"Historical: {setupPerf.WinRate * 100:F0}% WR, {setupPerf.ExpectedValuePercent:F2}% EV over {setupPerf.SampleSize} occurrences");

        return Ok(new
        {
            tool_name = "explain_scoring",
            as_of = Now(),
            summary = string.Join(". ", summaryParts) + ".",
            data = new
            {
                ticker,
                found = true,
                prediction_type = pred.PredictionType.ToString(),
                confidence = pred.ConfidenceScore,
                risk = pred.RiskScore,
                bullish_score = pred.BullishScore,
                bearish_score = pred.BearishScore,
                winning_direction = pred.WinningDirection,
                rr_ratio = pred.RiskRewardRatio,
                actionability_tier = pred.ActionabilityTier.ToString(),
                downgrade_reasons = pred.DowngradeReasons,
                breakdown = breakdown is null ? null : new
                {
                    trend = new { bull = breakdown.TrendBullish, bear = breakdown.TrendBearish, net = breakdown.TrendScore },
                    momentum = new { bull = breakdown.MomentumBullish, bear = breakdown.MomentumBearish, net = breakdown.MomentumScore },
                    volume = new { bull = breakdown.VolumeBullish, bear = breakdown.VolumeBearish, net = breakdown.VolumeScore },
                    volatility = new { bull = breakdown.VolatilityBullish, bear = breakdown.VolatilityBearish, net = breakdown.VolatilitySetupScore },
                    market_context = new { bull = breakdown.MarketContextBullish, bear = breakdown.MarketContextBearish, net = breakdown.MarketContextScore },
                    catalyst = new { bull = breakdown.CatalystBullish, bear = breakdown.CatalystBearish, net = breakdown.CatalystScore },
                    research_signal = new { bull = breakdown.ResearchSignalBullish, bear = breakdown.ResearchSignalBearish, net = breakdown.ResearchSignalScore },
                    confirmation_multiplier = breakdown.ConfirmationMultiplier,
                    data_quality = breakdown.DataQualityFactor,
                    calibration = breakdown.CalibrationFactor,
                    confidence_cap = breakdown.ConfidenceCap,
                },
                setup = fingerprint is null ? null : new
                {
                    fingerprint,
                    historical_win_rate = setupPerf?.WinRate,
                    historical_ev = setupPerf?.ExpectedValuePercent,
                    historical_sample = setupPerf?.SampleSize,
                    is_favorable = setupPerf is not null && TradeSetupEngine.IsHistoricallyFavorable(setupPerf, null),
                    is_trusted = setupPerf?.IsTrusted,
                },
            },
            warnings,
        });
    }

    // -----------------------------------------------------------------------
    // 12. get_ticker_accuracy — per-ticker historical win/loss record
    // -----------------------------------------------------------------------

    [HttpGet("get_ticker_accuracy")]
    public async Task<IActionResult> GetTickerAccuracy(
        [FromQuery] string? ticker = null,
        [FromQuery] int limit = 10)
    {
        var warnings = new List<string>();
        var allStats = await _stockRepo.GetAllLearningStatsAsync();
        var tickerStats = allStats
            .Where(s => s.StatType == "ticker")
            .OrderByDescending(s => s.TotalCandidates)
            .ToList();

        if (!string.IsNullOrWhiteSpace(ticker))
        {
            var match = tickerStats.FirstOrDefault(s =>
                s.StatKey.Equals(ticker.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return Ok(new
                {
                    tool_name = "get_ticker_accuracy",
                    as_of = Now(),
                    summary = $"No historical accuracy data for {ticker.Trim().ToUpperInvariant()}.",
                    data = new { ticker = ticker.Trim().ToUpperInvariant(), found = false },
                    warnings,
                });
            }

            var correct = (int)Math.Round(match.Accuracy * match.TotalCandidates);
            var wrong = match.TotalCandidates - correct;

            // Per-bucket accuracy for this ticker
            var bucketStats = allStats
                .Where(s => s.StatType == "ticker_bucket"
                    && s.StatKey.StartsWith(ticker.Trim().ToUpperInvariant() + "|", StringComparison.OrdinalIgnoreCase))
                .Select(s =>
                {
                    var bucketName = s.StatKey.Split('|').Last();
                    var bCorrect = (int)Math.Round(s.Accuracy * s.TotalCandidates);
                    return new
                    {
                        bucket = bucketName,
                        total = s.TotalCandidates,
                        correct = bCorrect,
                        accuracy = $"{s.Accuracy * 100:F1}%",
                    };
                })
                .OrderBy(b => b.accuracy)
                .ToList();

            // Compute reliability factor (same formula as PredictionGenerator)
            double? reliabilityFactor = null;
            if (match.TotalCandidates >= 5)
            {
                var globalStats = allStats
                    .Where(s => s.StatType == "prediction_type"
                        && (s.StatKey == "bullish" || s.StatKey == "bearish"))
                    .ToList();
                double globalAccuracy = globalStats.Count > 0
                    ? globalStats.Average(s => s.Accuracy) : 0.55;
                double tickerAcc = match.Accuracy;
                int n = match.TotalCandidates;
                double sampleWeight = (double)n / (n + 10);
                double effectiveAcc = sampleWeight * tickerAcc + (1 - sampleWeight) * globalAccuracy;
                reliabilityFactor = Math.Round(0.6 + 0.4 * Math.Clamp(effectiveAcc / 0.8, 0, 1), 3);
            }

            return Ok(new
            {
                tool_name = "get_ticker_accuracy",
                as_of = Now(),
                summary = $"{match.StatKey}: {correct}/{match.TotalCandidates} correct ({match.Accuracy * 100:F1}% accuracy). "
                    + $"Reliability factor: {(reliabilityFactor.HasValue ? $"{reliabilityFactor:F2}" : "n/a (< 5 samples)")}. "
                    + (bucketStats.Count > 0
                        ? $"Weakest bucket: {bucketStats.First().bucket} ({bucketStats.First().accuracy})."
                        : "No per-bucket data yet."),
                data = new
                {
                    ticker = match.StatKey,
                    found = true,
                    total_predictions = match.TotalCandidates,
                    correct,
                    wrong,
                    accuracy = $"{match.Accuracy * 100:F1}%",
                    avg_outcome_score = match.AverageOutcomeScore,
                    reliability_factor = reliabilityFactor,
                    bucket_accuracy = bucketStats,
                },
                warnings = match.Accuracy < 0.4 && match.TotalCandidates >= 5
                    ? new List<string> { $"WARNING: {match.StatKey} accuracy is critically low ({match.Accuracy * 100:F0}%). Confidence is being reduced via reliability factor {reliabilityFactor:F2}." }
                    : warnings,
            });
        }

        // No ticker specified — return all ticker stats
        var items = tickerStats.Take(limit).Select(s =>
        {
            var correct = (int)Math.Round(s.Accuracy * s.TotalCandidates);
            return new
            {
                ticker = s.StatKey,
                total = s.TotalCandidates,
                correct,
                wrong = s.TotalCandidates - correct,
                accuracy = $"{s.Accuracy * 100:F1}%",
                avg_score = s.AverageOutcomeScore,
            };
        });

        var worst = tickerStats.Where(s => s.TotalCandidates >= 3).OrderBy(s => s.Accuracy).Take(3)
            .Select(s => $"{s.StatKey} ({s.Accuracy * 100:F0}%)");
        var best = tickerStats.Where(s => s.TotalCandidates >= 3).OrderByDescending(s => s.Accuracy).Take(3)
            .Select(s => $"{s.StatKey} ({s.Accuracy * 100:F0}%)");

        return Ok(new
        {
            tool_name = "get_ticker_accuracy",
            as_of = Now(),
            summary = $"{tickerStats.Count} tickers tracked. Best: {string.Join(", ", best)}. Worst: {string.Join(", ", worst)}.",
            data = new { count = tickerStats.Count, items },
            warnings,
        });
    }

    // -----------------------------------------------------------------------
    // 14. get_config — view current system configuration
    // -----------------------------------------------------------------------

    [HttpGet("get_config")]
    public async Task<IActionResult> GetConfig()
    {
        var weights = await _researchRepo.GetActiveWeightOverridesAsync();
        var calFactor = weights.FirstOrDefault(o => o.SignalName == "calibration_factor");

        return Ok(new
        {
            tool_name = "get_config",
            as_of = Now(),
            summary = "Current system configuration and thresholds.",
            data = new
            {
                calibration_factor = calFactor?.EffectiveWeight ?? 1.0,
                weight_overrides = weights.Where(o => o.SignalName != "calibration_factor")
                    .Select(o => new { signal = o.SignalName, weight = o.EffectiveWeight, adjustment = $"{o.AdjustmentPercent * 100:F1}%" }),
                thresholds = new
                {
                    min_observations_for_weight_adjustment = 50,
                    max_daily_weight_movement = "1%",
                    max_weight_adjustment = "±20%",
                    calibration_factor_range = "0.85 - 1.15",
                    setup_min_sample_for_trust = 8,
                    setup_min_sample_for_favorable = 12,
                    setup_min_ev_for_favorable = "0.5%",
                    setup_degradation_threshold = "15% drop",
                },
            },
            warnings = new List<string>(),
        });
    }

    // -----------------------------------------------------------------------
    // 15. update_config — change system configuration
    // -----------------------------------------------------------------------

    [HttpPost("update_config")]
    public async Task<IActionResult> UpdateConfig(
        [FromQuery] string setting,
        [FromQuery] double value)
    {
        var warnings = new List<string>();

        if (setting == "calibration_factor")
        {
            value = Math.Clamp(value, 0.85, 1.15);
            await _researchRepo.UpsertWeightOverrideAsync(new ScoringWeightOverride
            {
                SignalName = "calibration_factor",
                BaseWeight = 1.0,
                AdjustmentPercent = value - 1.0,
                EffectiveWeight = value,
                Confidence = 1.0,
                SampleSize = 0,
                Status = "active",
                Reason = "Manually set via chat",
            });

            return Ok(new
            {
                tool_name = "update_config",
                as_of = Now(),
                summary = $"Calibration factor set to {value:F4}. This will affect all future confidence scores.",
                data = new { setting, value, applied = true },
                warnings,
            });
        }

        return Ok(new
        {
            tool_name = "update_config",
            as_of = Now(),
            summary = $"Unknown setting: {setting}. Available: calibration_factor.",
            data = new { setting, applied = false },
            warnings = new List<string> { "Setting not recognized" },
        });
    }

    private static string Truncate(string? s, int max) =>
        s is null ? "" : s.Length <= max ? s : s[..max] + "...";
}
