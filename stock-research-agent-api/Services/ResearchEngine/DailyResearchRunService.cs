using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Knowledge;
using StockResearchAgent.Api.Services.ResearchUniverse;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Orchestrates the daily research loop:
///   1. Morning scan: gather data -> generate predictions -> save -> report
///   2. EOD review: evaluate open predictions -> score outcomes -> report
///   3. Learning update: update signal stats -> adjust weights -> insights
/// </summary>
public class DailyResearchRunService
{
    private readonly PredictionGenerator _predGen;
    private readonly OutcomeEvaluator _outcomeEval;
    private readonly LearningEngine _learning;
    private readonly IKnowledgeEngine _knowledge;
    private readonly DailyReportService _reports;
    private readonly ResearchRepository _repo;
    private readonly WatchlistRepository _watchlistRepo;
    private readonly PredictionProfileRepository _profileRepo;
    private readonly IResearchUniverseService _universe;
    private readonly ILogger<DailyResearchRunService> _logger;

    public DailyResearchRunService(
        PredictionGenerator predGen,
        OutcomeEvaluator outcomeEval,
        LearningEngine learning,
        IKnowledgeEngine knowledge,
        DailyReportService reports,
        ResearchRepository repo,
        WatchlistRepository watchlistRepo,
        PredictionProfileRepository profileRepo,
        IResearchUniverseService universe,
        ILogger<DailyResearchRunService> logger)
    {
        _predGen = predGen;
        _outcomeEval = outcomeEval;
        _learning = learning;
        _knowledge = knowledge;
        _reports = reports;
        _repo = repo;
        _watchlistRepo = watchlistRepo;
        _profileRepo = profileRepo;
        _universe = universe;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Morning Scan
    // -----------------------------------------------------------------------

    /// <summary>
    /// Run the morning scan. If <paramref name="existingRunId"/> is provided, uses that
    /// already-created research_runs row instead of creating a new one (background-job pattern).
    /// </summary>
    public async Task<MorningScanResult> RunMorningScanAsync(string? existingRunId = null)
    {
        _logger.LogInformation("[research-engine] Starting morning scan...");
        var errors = new List<string>();

        // Clean up any runs stuck in 'started' for >2 hours (process was likely killed).
        // At 430+ tickers with TwelveData rate limiting (7 req/min), a full scan can
        // legitimately take 60–90 minutes.
        try
        {
            var cleaned = await _repo.CleanupStuckRunsAsync(TimeSpan.FromMinutes(120));
            if (cleaned > 0)
                _logger.LogWarning("[research-engine] Cleaned up {Count} stuck research run(s)", cleaned);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[research-engine] Stuck-run cleanup failed (non-blocking)");
        }

        ResearchRun? run;
        if (existingRunId is not null)
        {
            run = await _repo.GetResearchRunByIdAsync(existingRunId);
            if (run is null)
                return new MorningScanResult { Report = $"Research run {existingRunId} not found", Errors = [$"Research run {existingRunId} not found"] };
        }
        else
        {
            run = await _repo.CreateResearchRunAsync("morning_scan");
        }

        if (run is null)
            return new MorningScanResult { Report = "Failed to create research run (Supabase not configured?)", Errors = ["Failed to create research run"] };

        try
        {
            // 1. Build market snapshots from research candidates
            var (tickers, assetLookup) = await GetResearchCandidatesAsync();

            if (tickers.Length == 0)
            {
                _logger.LogWarning("[research-engine] No research candidates — Research Universe is empty and watchlist fallback returned nothing");
                await _repo.CompleteResearchRunAsync(run.Id, "No research candidates. Run discovery first to populate the Research Universe.", 0, 0,
                    ["No research candidates"]);
                return new MorningScanResult { RunId = run.Id, Report = "No research candidates. Run discovery first to populate the Research Universe.", Errors = ["No research candidates"] };
            }

            _logger.LogInformation("[research-engine] Building snapshots for {Count} research candidates: [{Tickers}]",
                tickers.Length, string.Join(", ", tickers));

            // Process tickers with limited concurrency. Each snapshot hits ~6 API
            // calls across TwelveData (7/min), StockFit, and Finnhub. The per-provider
            // throttles enforce minimum gaps between requests, so high concurrency just
            // means more tasks waiting. Concurrency of 2 lets one ticker's StockFit/Finnhub
            // calls overlap with another's TwelveData wait.
            const int maxConcurrency = 2;
            var throttle = new SemaphoreSlim(maxConcurrency);
            var snapshotTasks = tickers.Select(async t =>
            {
                await throttle.WaitAsync();
                try
                {
                    return await _predGen.BuildMarketSnapshotAsync(t, run.Id);
                }
                finally
                {
                    throttle.Release();
                }
            });
            var snapshots = (await Task.WhenAll(snapshotTasks)).ToList();

            // Save snapshots
            var snapshotRows = snapshots.Select(s => (object)new
            {
                run_id = s.RunId,
                ticker = s.Ticker,
                quote = s.Quote,
                recent_bars = s.RecentBars,
                technical_context = s.TechnicalContext,
                news_context = s.NewsContext,
                data_availability = s.DataAvailability,
            }).ToList();
            await _repo.SaveMarketSnapshotsAsync(snapshotRows);

            // 2. Determine which profiles to generate predictions for
            //    Champion always runs; challengers in "testing" status also run.
            var profilesToRun = new List<(string Id, string Name)>();
            var champion = await _profileRepo.GetChampionProfileAsync();
            if (champion is not null)
                profilesToRun.Add((champion.Id, champion.ProfileName));

            var allProfiles = await _profileRepo.GetAllProfilesAsync();
            foreach (var p in allProfiles.Where(p => p.Role == ProfileRole.challenger && p.ExperimentStatus == ExperimentStatus.testing && p.IsEnabled))
                profilesToRun.Add((p.Id, p.ProfileName));

            _logger.LogInformation("[research-engine] Generating predictions for {Count} profile(s): {Names}",
                profilesToRun.Count, string.Join(", ", profilesToRun.Select(p => p.Name)));

            // 3. Generate predictions per profile (snapshots are shared)
            var allPredictions = new List<PredictionCandidate>();
            var totalAllInputs = new List<PredictionInput>();
            var totalSupersessions = new List<PredictionGenerator.PendingSupersession>();

            foreach (var (profileId, profileName) in profilesToRun)
            {
                _logger.LogInformation("[research-engine] Generating predictions for profile '{Name}'...", profileName);
                var (predictions, allInputs, pendingSupersessions) = await _predGen.GeneratePredictionsForWatchlistAsync(
                    tickers, run.Id, snapshots, assetLookup, profileId: profileId);

                _logger.LogInformation("[research-engine] Profile '{Name}': {Count} predictions", profileName, predictions.Count);
                allPredictions.AddRange(predictions);
                totalAllInputs.AddRange(allInputs);
                totalSupersessions.AddRange(pendingSupersessions);
            }

            // Save all predictions
            var predRows = allPredictions.Select(p => (object)new
            {
                run_id = p.RunId,
                ticker = p.Ticker,
                prediction_type = p.PredictionType.ToString(),
                asset_type = p.AssetType.ToString(),
                time_window = p.TimeWindow,
                confidence_score = p.ConfidenceScore,
                importance_score = p.ImportanceScore,
                risk_score = p.RiskScore,
                entry_reference_price = p.EntryReferencePrice,
                atr14 = p.Atr14,
                atr_percent = p.AtrPercent,
                timeframe_multiplier = p.TimeframeMultiplier,
                signal_modifier = p.SignalModifier,
                expected_move_dollar = p.ExpectedMoveDollar,
                expected_move_percent = p.ExpectedMovePercent,
                predicted_price = p.PredictedPrice,
                predicted_move_percent = p.PredictedMovePercent,
                projected_price_low = p.ProjectedPriceLow,
                projected_price_high = p.ProjectedPriceHigh,
                target_price = p.TargetPrice,
                stop_price = p.StopPrice,
                invalidation_price = p.InvalidationPrice,
                support_level = p.SupportLevel,
                resistance_level = p.ResistanceLevel,
                risk_reward_ratio = p.RiskRewardRatio,
                expected_value_percent = p.ExpectedValuePercent,
                price_prediction_method = p.PricePredictionMethod,
                price_prediction_warnings = p.PricePredictionWarnings.ToArray(),
                score_debug_json = p.ScoreDebugJson,
                bullish_score = p.BullishScore,
                bearish_score = p.BearishScore,
                winning_direction = p.WinningDirection,
                direction_confidence = p.DirectionConfidence,
                actionability_score = p.ActionabilityScore,
                actionability_tier = p.ActionabilityTier?.ToString(),
                bullish_case = p.BullishCase,
                bearish_case = p.BearishCase,
                prediction_reason = p.PredictionReason,
                invalidation_rule = p.InvalidationRule,
                data_sources_used = p.DataSourcesUsed.ToArray(),
                missing_data_warnings = p.MissingDataWarnings.ToArray(),
                downgrade_reasons = p.DowngradeReasons.ToArray(),
                status = p.Status,
                profile_id = p.ProfileId,
            }).ToList();
            var (persisted, ids) = await _repo.SavePredictionsAsync(predRows);

            // Link inputs to saved prediction IDs
            if (ids.Count > 0 && totalAllInputs.Count > 0)
            {
                var inputIdx = 0;
                var linkedInputs = new List<object>();
                for (int i = 0; i < allPredictions.Count && i < ids.Count; i++)
                {
                    while (inputIdx < totalAllInputs.Count)
                    {
                        var input = totalAllInputs[inputIdx];
                        if (string.IsNullOrEmpty(input.PredictionId) || input.PredictionId == allPredictions[i].RunId)
                        {
                            linkedInputs.Add(new
                            {
                                prediction_id = ids[i],
                                input_type = input.InputType,
                                source_name = input.SourceName,
                                source_url = input.SourceUrl,
                                source_record_id = input.SourceRecordId,
                                summary = input.Summary,
                            });
                            inputIdx++;
                        }
                        else break;
                    }
                }
                while (inputIdx < totalAllInputs.Count)
                {
                    linkedInputs.Add(new
                    {
                        prediction_id = ids[^1],
                        input_type = totalAllInputs[inputIdx].InputType,
                        source_name = totalAllInputs[inputIdx].SourceName,
                        source_url = totalAllInputs[inputIdx].SourceUrl,
                        source_record_id = totalAllInputs[inputIdx].SourceRecordId,
                        summary = totalAllInputs[inputIdx].Summary,
                    });
                    inputIdx++;
                }
                await _repo.SavePredictionInputsAsync(linkedInputs);
            }

            // Execute deferred neutral supersessions now that we have DB-assigned IDs
            if (persisted && ids.Count > 0 && totalSupersessions.Count > 0)
            {
                foreach (var sup in totalSupersessions)
                {
                    var idx = allPredictions.FindIndex(p =>
                        p.Ticker.Equals(sup.ReplacementTicker, StringComparison.OrdinalIgnoreCase)
                        && p.TimeWindow.Equals(sup.ReplacementTimeWindow, StringComparison.OrdinalIgnoreCase));

                    if (idx >= 0 && idx < ids.Count)
                    {
                        await _repo.SupersedePredictionAsync(sup.NeutralPredictionId, ids[idx], sup.Reason);
                        _logger.LogInformation(
                            "[research-engine] Superseded neutral prediction {OldId} → replacement {NewId}",
                            sup.NeutralPredictionId, ids[idx]);
                    }
                }
            }

            // 4. Report
            var report = _reports.GenerateMorningReport(allPredictions, snapshots);

            // 5. Complete run
            await _repo.CompleteResearchRunAsync(run.Id, report, allPredictions.Count, 0, errors);

            _logger.LogInformation("[research-engine] Morning scan complete: {Count} predictions across {Profiles} profile(s)",
                allPredictions.Count, profilesToRun.Count);
            return new MorningScanResult { RunId = run.Id, PredictionsGenerated = allPredictions.Count, Report = report, Errors = errors };
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            await _repo.CompleteResearchRunAsync(run.Id, $"Morning scan failed: {ex.Message}", 0, 0, errors);
            _logger.LogError(ex, "[research-engine] Morning scan failed");
            return new MorningScanResult { RunId = run.Id, Report = $"Morning scan failed: {ex.Message}", Errors = errors };
        }
    }

    // -----------------------------------------------------------------------
    // End-of-Day Review
    // -----------------------------------------------------------------------

    public async Task<EndOfDayReviewResult> RunEndOfDayReviewAsync(string? existingRunId = null)
    {
        _logger.LogInformation("[research-engine] Starting end-of-day review...");
        var errors = new List<string>();

        // Clean up any runs stuck in 'started' for >2 hours (process was likely killed).
        // At 430+ tickers with TwelveData rate limiting (7 req/min), a full scan can
        // legitimately take 60–90 minutes.
        try
        {
            var cleaned = await _repo.CleanupStuckRunsAsync(TimeSpan.FromMinutes(120));
            if (cleaned > 0)
                _logger.LogWarning("[research-engine] Cleaned up {Count} stuck research run(s)", cleaned);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[research-engine] Stuck-run cleanup failed (non-blocking)");
        }

        ResearchRun? run;
        if (existingRunId is not null)
        {
            run = await _repo.GetResearchRunByIdAsync(existingRunId);
            if (run is null)
                return new EndOfDayReviewResult { Report = $"Research run {existingRunId} not found", Errors = [$"Research run {existingRunId} not found"] };
        }
        else
        {
            run = await _repo.CreateResearchRunAsync("end_of_day_review");
        }

        if (run is null)
            return new EndOfDayReviewResult { Report = "Failed to create research run", Errors = ["Failed to create research run"] };

        try
        {
            var (evaluated, skipped, evalErrors) = await _outcomeEval.EvaluateOpenPredictionsAsync();
            errors.AddRange(evalErrors);

            var report = _reports.GenerateEndOfDayReport(evaluated, skipped);
            await _repo.CompleteResearchRunAsync(run.Id, report, 0, evaluated.Count, errors);

            _logger.LogInformation("[research-engine] EOD review complete: {Count} evaluated", evaluated.Count);
            return new EndOfDayReviewResult { RunId = run.Id, PredictionsEvaluated = evaluated.Count, Report = report, Errors = errors };
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            await _repo.CompleteResearchRunAsync(run.Id, $"EOD review failed: {ex.Message}", 0, 0, errors);
            return new EndOfDayReviewResult { RunId = run.Id, Report = $"EOD review failed: {ex.Message}", Errors = errors };
        }
    }

    // -----------------------------------------------------------------------
    // Research Candidate Selection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the research candidates that Morning Scan should evaluate.
    /// Sources candidates exclusively from the Research Universe — only
    /// active (non-archived) Research Assets are evaluated. The watchlist
    /// is used as a fallback if the Research Universe is empty, to avoid
    /// a completely silent run during the transition period.
    ///
    /// Returns full ResearchAsset objects so the prediction pipeline can
    /// access InterestScore, EvidenceCount, ResearchState, and other
    /// Research Universe metadata during scoring.
    /// </summary>
    private async Task<(string[] Tickers, Dictionary<string, ResearchAsset> AssetLookup)> GetResearchCandidatesAsync()
    {
        var activeAssets = await _universe.GetActiveAssetsAsync(500);
        // Deduplicate by ticker (case-insensitive), keeping highest InterestScore
        var deduped = activeAssets
            .GroupBy(a => a.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.InterestScore).First())
            .ToList();

        // Filter: skip low-value tickers that would waste API quota.
        // Keep tickers that are either:
        //   - Past the Discovered stage (actively being researched), OR
        //   - Have an interest score >= 15 (meaningful evidence accumulated)
        // Then cap total to stay within API rate limits.
        const int minInterestScore = 15;
        const int maxCandidates = 150;

        var qualified = deduped
            .Where(a => a.CurrentState != ResearchState.Discovered || a.InterestScore >= minInterestScore)
            .OrderByDescending(a => a.InterestScore)
            .Take(maxCandidates)
            .ToList();

        var skipped = deduped.Count - qualified.Count;
        if (skipped > 0)
            _logger.LogInformation(
                "[research-engine] Filtered {Skipped} low-priority tickers " +
                "(score < {MinScore} in Discovered state or beyond cap of {Cap}). " +
                "{Qualified} candidates proceeding.",
                skipped, minInterestScore, maxCandidates, qualified.Count);

        var assetLookup = qualified.ToDictionary(
            a => a.Ticker,
            a => a,
            StringComparer.OrdinalIgnoreCase);

        if (assetLookup.Count > 0)
        {
            _logger.LogInformation(
                "[research-engine] Sourced {Count} candidates from Research Universe",
                assetLookup.Count);
            return (assetLookup.Keys.ToArray(), assetLookup);
        }

        // Fallback: if Research Universe is empty, use watchlist so we don't
        // produce zero predictions during the bootstrap period.
        _logger.LogWarning(
            "[research-engine] Research Universe is empty — falling back to watchlist");
        var activeWatchlist = await _watchlistRepo.GetActiveWatchlistAsync();
        var tickers = activeWatchlist.Select(w => w.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (tickers, new Dictionary<string, ResearchAsset>(StringComparer.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // Learning Update
    // -----------------------------------------------------------------------

    public async Task<LearningUpdateResult> RunLearningUpdateAsync(string? existingRunId = null)
    {
        _logger.LogInformation("[research-engine] Starting full learning cycle...");

        ResearchRun? run;
        if (existingRunId is not null)
        {
            run = await _repo.GetResearchRunByIdAsync(existingRunId);
            if (run is null)
                return new LearningUpdateResult { Report = $"Research run {existingRunId} not found", Errors = [$"Research run {existingRunId} not found"] };
        }
        else
        {
            run = await _repo.CreateResearchRunAsync("learning_update");
        }

        if (run is null)
            return new LearningUpdateResult { Report = "Failed to create research run", Errors = ["Failed to create research run"] };

        try
        {
            // Run learning for all enabled profiles (champion + challengers)
            var result = await _learning.RunLearningForAllProfilesAsync();
            var knowledge = await _knowledge.RunKnowledgeCycleAsync();
            result = result with { RunId = run.Id };
            result = result with
            {
                KnowledgeCasesIndexed = knowledge.CasesIndexed,
                KnowledgePatternsDetected = knowledge.PatternsDetected,
                KnowledgeRulesGenerated = knowledge.RulesGenerated,
                Report = $"{result.Report} {knowledge.Summary}",
            };

            await _repo.CompleteResearchRunAsync(run.Id, result.Report, 0, 0, result.Errors);

            _logger.LogInformation("[research-engine] Learning cycle complete: {Obs} observations, {Insights} insights, {Weights} weight changes, {Cases} knowledge cases, {Patterns} patterns",
                result.ObservationsCreated, result.InsightsGenerated, result.WeightsAdjusted,
                result.KnowledgeCasesIndexed, result.KnowledgePatternsDetected);
            return result;
        }
        catch (Exception ex)
        {
            var errors = new List<string> { ex.Message };
            await _repo.CompleteResearchRunAsync(run.Id, $"Learning cycle failed: {ex.Message}", 0, 0, errors);
            return new LearningUpdateResult { RunId = run.Id, Report = $"Learning cycle failed: {ex.Message}", Errors = errors };
        }
    }
}
