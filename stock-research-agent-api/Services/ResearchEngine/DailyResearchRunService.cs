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
    public async Task<MorningScanResult> RunMorningScanAsync(string? existingRunId = null, CancellationToken cancellationToken = default)
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
            await _repo.LogProgressAsync(run.Id, "load_candidates", "Loading research candidates...");
            var (tickers, assetLookup) = await GetResearchCandidatesAsync();

            if (tickers.Length == 0)
            {
                _logger.LogWarning("[research-engine] No research candidates — Research Universe is empty and watchlist fallback returned nothing");
                await _repo.LogProgressAsync(run.Id, "no_candidates", "No research candidates found — aborting");
                await _repo.CompleteResearchRunAsync(run.Id, "No research candidates. Run discovery first to populate the Research Universe.", 0, 0,
                    ["No research candidates"]);
                return new MorningScanResult { RunId = run.Id, Report = "No research candidates. Run discovery first to populate the Research Universe.", Errors = ["No research candidates"] };
            }

            await _repo.LogProgressAsync(run.Id, "candidates_loaded", $"Loaded {tickers.Length} research candidates",
                new { count = tickers.Length, tickers = string.Join(", ", tickers.Take(30)) });

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
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    return await _predGen.BuildMarketSnapshotAsync(t, run.Id);
                }
                finally
                {
                    throttle.Release();
                }
            });
            await _repo.LogProgressAsync(run.Id, "building_snapshots", $"Building market snapshots for {tickers.Length} tickers (concurrency={maxConcurrency})...");
            var snapshots = (await Task.WhenAll(snapshotTasks)).ToList();
            await _repo.LogProgressAsync(run.Id, "snapshots_built", $"Built {snapshots.Count} market snapshots",
                new { count = snapshots.Count });

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
            foreach (var p in allProfiles.Where(p => p.Role == ProfileRole.challenger && p.ExperimentStatus is ExperimentStatus.testing or ExperimentStatus.active && p.IsEnabled))
                profilesToRun.Add((p.Id, p.ProfileName));

            _logger.LogInformation("[research-engine] Generating predictions for {Count} profile(s): {Names}",
                profilesToRun.Count, string.Join(", ", profilesToRun.Select(p => p.Name)));

            await _repo.LogProgressAsync(run.Id, "generating_predictions",
                $"Generating predictions for {profilesToRun.Count} profile(s): {string.Join(", ", profilesToRun.Select(p => p.Name))}");

            // 3. Generate predictions per profile
            //    Each profile can define a ticker pool via profile configs:
            //      config_key = "ticker_pool", description = "AAPL,MSFT,NVDA,..."
            //      config_value = 0 (include only) or 1 (exclude these)
            //    If no ticker_pool is set, the profile evaluates all tickers.
            var allPredictions = new List<PredictionCandidate>();
            var totalAllInputs = new List<PredictionInput>();
            var totalSupersessions = new List<PredictionGenerator.PendingSupersession>();

            foreach (var (profileId, profileName) in profilesToRun)
            {
                // Filter tickers for this profile based on its ticker pool
                var profileTickers = tickers;
                var profileSnapshots = snapshots;
                var profileAssetLookup = assetLookup;

                var tickerPool = await _profileRepo.GetTickerPoolAsync(profileId);
                if (tickerPool is not null)
                {
                    var (pool, mode) = tickerPool.Value;
                    if (pool.Count > 0)
                    {
                        if (mode == 0)
                        {
                            // Include mode: only these tickers
                            profileTickers = tickers.Where(t => pool.Contains(t)).ToArray();
                            profileSnapshots = snapshots.Where(s => pool.Contains(s.Ticker)).ToList();
                        }
                        else
                        {
                            // Exclude mode: everything except these tickers
                            profileTickers = tickers.Where(t => !pool.Contains(t)).ToArray();
                            profileSnapshots = snapshots.Where(s => !pool.Contains(s.Ticker)).ToList();
                        }

                        var filteredTickerSet = new HashSet<string>(profileTickers, StringComparer.OrdinalIgnoreCase);
                        profileAssetLookup = assetLookup
                            .Where(kv => filteredTickerSet.Contains(kv.Key))
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                        _logger.LogInformation(
                            "[research-engine] Profile '{Name}': ticker pool {Mode} filter — {PoolCount} pool tickers, {ResultCount} after filter (from {TotalCount} total)",
                            profileName, mode == 0 ? "include" : "exclude", pool.Count, profileTickers.Length, tickers.Length);
                    }
                }

                _logger.LogInformation("[research-engine] Generating predictions for profile '{Name}' ({TickerCount} tickers)...",
                    profileName, profileTickers.Length);
                await _repo.LogProgressAsync(run.Id, "profile_start", $"Starting predictions for profile '{profileName}' ({profileTickers.Length} tickers)");
                var (predictions, allInputs, pendingSupersessions) = await _predGen.GeneratePredictionsForWatchlistAsync(
                    profileTickers, run.Id, profileSnapshots, profileAssetLookup, profileId: profileId);

                _logger.LogInformation("[research-engine] Profile '{Name}': {Count} predictions", profileName, predictions.Count);
                await _repo.LogProgressAsync(run.Id, "profile_done", $"Profile '{profileName}': {predictions.Count} predictions");
                allPredictions.AddRange(predictions);
                totalAllInputs.AddRange(allInputs);
                totalSupersessions.AddRange(pendingSupersessions);
            }

            // Save all predictions
            await _repo.LogProgressAsync(run.Id, "saving_predictions",
                $"Saving {allPredictions.Count} predictions to database...");
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
                indicators_json = p.IndicatorsJson,
                weights_snapshot_json = p.WeightsSnapshotJson,
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

            // Track save failures
            if (!persisted || ids.Count == 0)
            {
                var msg = $"CRITICAL: Prediction save failed — {allPredictions.Count} predictions generated in memory but {ids.Count} persisted to database.";
                _logger.LogError("[research-engine] {Message}", msg);
                errors.Add(msg);
            }
            else if (ids.Count < allPredictions.Count)
            {
                var msg = $"Partial save: {ids.Count}/{allPredictions.Count} predictions persisted.";
                _logger.LogWarning("[research-engine] {Message}", msg);
                errors.Add(msg);
            }

            await _repo.LogProgressAsync(run.Id, "predictions_saved",
                $"Saved {ids.Count}/{allPredictions.Count} predictions",
                new { saved = ids.Count, total = allPredictions.Count, errors = errors.Count });

            // 4. Report
            var report = _reports.GenerateMorningReport(allPredictions, snapshots);

            // 5. Complete run — report actual persisted count, not in-memory count
            await _repo.LogProgressAsync(run.Id, "scan_complete", $"Morning scan complete: {ids.Count} predictions");
            await _repo.CompleteResearchRunAsync(run.Id, report, ids.Count, 0, errors);

            _logger.LogInformation("[research-engine] Morning scan complete: {Count} predictions across {Profiles} profile(s)",
                allPredictions.Count, profilesToRun.Count);
            return new MorningScanResult { RunId = run.Id, PredictionsGenerated = allPredictions.Count, Report = report, Errors = errors };
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            await _repo.LogProgressAsync(run.Id, "scan_failed", $"Morning scan FAILED: {ex.Message}",
                new { exceptionType = ex.GetType().Name, stackTrace = ex.StackTrace?[..Math.Min(500, ex.StackTrace?.Length ?? 0)] });
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
        // ── Load DB-configurable universe quality filters ──
        var overrides = await _repo.GetActiveWeightOverridesAsync();
        var weights = overrides.ToDictionary(o => o.SignalName, o => o.EffectiveWeight);
        var minInterestScore = (int)weights.GetValueOrDefault("universe_min_interest_score", 50);
        var maxCandidates = (int)weights.GetValueOrDefault("universe_max_candidates", 60);

        var activeAssets = await _universe.GetActiveAssetsAsync(500);
        // Deduplicate by ticker (case-insensitive), keeping highest InterestScore
        var deduped = activeAssets
            .GroupBy(a => a.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.InterestScore).First())
            .ToList();

        var qualified = deduped
            .Where(a => !IsUntradeable(a.Ticker))
            .Where(a => a.CurrentState != ResearchState.Discovered || a.InterestScore >= minInterestScore)
            .OrderByDescending(a => a.InterestScore)
            .ToList();

        // ── Quality tier gate ──
        // Tickers discovered ONLY from earnings calendars are low-quality (penny stocks,
        // SPACs, etc. get identical treatment as blue chips). Prioritize tickers from
        // quality discovery sources OR known large-cap names. Earnings-only junk fills
        // remaining slots, capped.
        var earningsOnlySources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "finnhub-earnings", "fmp-earnings" };

        // Known quality large-caps — mirrors BaseUniverse in UniverseDiscoveryService.
        // These get quality treatment even when discovered via earnings calendar.
        var knownQualityTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AAPL", "MSFT", "AMZN", "GOOGL", "META", "NVDA", "TSLA", "AVGO", "ORCL", "CRM",
            "SHOP", "PLTR", "TTD", "NFLX", "AMD", "UBER", "SQ", "SNOW", "DDOG", "NET",
            "CRWD", "ZS", "PANW", "FTNT", "MDB", "COIN", "RBLX", "PINS", "SNAP", "APP",
            "AXON", "CAT", "DE", "GE", "HON", "LMT", "RTX",
            "COST", "WMT", "TGT", "NKE", "SBUX", "MCD", "CMG",
            "LLY", "MRNA", "ABBV", "BMY", "GILD",
            "MU", "QCOM", "MRVL", "KLAC", "LRCX", "AMAT",
            "JPM", "GS", "MS", "V", "MA",
        };

        bool IsQuality(ResearchAsset a) =>
            !earningsOnlySources.Contains(a.DiscoverySource)
            || knownQualityTickers.Contains(a.Ticker);

        var qualityTickers = qualified.Where(a => IsQuality(a)).ToList();
        var earningsOnlyTickers = qualified.Where(a => !IsQuality(a)).ToList();

        // DB-configurable: how many slots to reserve for earnings-discovered tickers
        var maxEarningsSlots = (int)weights.GetValueOrDefault("universe_max_earnings_slots", 15);

        _logger.LogInformation(
            "[research-engine] Quality gate: {Quality} quality tickers, {EarningsOnly} earnings-only (cap {EarningsCap})",
            qualityTickers.Count, earningsOnlyTickers.Count, maxEarningsSlots);

        // Always include active watchlist tickers first — these are the ones
        // the portfolio actually trades. Fill remaining slots from top universe tickers.
        var activeWatchlist = await _watchlistRepo.GetActiveWatchlistAsync();
        var watchlistTickers = new HashSet<string>(
            activeWatchlist.Select(w => w.Ticker),
            StringComparer.OrdinalIgnoreCase);

        var watchlistCandidates = qualified.Where(a => watchlistTickers.Contains(a.Ticker)).ToList();

        // Quality tickers (non-earnings sources) get priority for remaining slots
        var qualityNonWatchlist = qualityTickers
            .Where(a => !watchlistTickers.Contains(a.Ticker))
            .ToList();
        // Earnings-only tickers fill last, capped
        var earningsNonWatchlist = earningsOnlyTickers
            .Where(a => !watchlistTickers.Contains(a.Ticker))
            .Take(maxEarningsSlots)
            .ToList();

        // Watchlist tickers that aren't in the universe yet still get scanned
        var missingWatchlistTickers = watchlistTickers
            .Where(t => !watchlistCandidates.Any(a => a.Ticker.Equals(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var reservedSlots = watchlistCandidates.Count + missingWatchlistTickers.Count;
        var remainingSlots = maxCandidates - reservedSlots;

        // Fill: quality first, then earnings-only
        var universeFill = qualityNonWatchlist
            .Concat(earningsNonWatchlist)
            .Take(Math.Max(0, remainingSlots))
            .ToList();

        var finalCandidates = watchlistCandidates
            .Concat(universeFill)
            .ToList();

        var skipped = deduped.Count - finalCandidates.Count - missingWatchlistTickers.Count;
        _logger.LogInformation(
            "[research-engine] Candidates: {Watchlist} watchlist + {Universe} universe = {Total} (skipped {Skipped}, cap {Cap})",
            watchlistCandidates.Count + missingWatchlistTickers.Count,
            Math.Max(0, finalCandidates.Count - watchlistCandidates.Count),
            finalCandidates.Count + missingWatchlistTickers.Count,
            skipped, maxCandidates);

        var assetLookup = finalCandidates.ToDictionary(
            a => a.Ticker,
            a => a,
            StringComparer.OrdinalIgnoreCase);

        // Add watchlist tickers that weren't in universe (no ResearchAsset, but still scan them)
        foreach (var t in missingWatchlistTickers)
            assetLookup.TryAdd(t, new ResearchAsset
            {
                Ticker = t,
                CurrentState = ResearchState.Monitoring,
                InterestScore = 50,
            });

        if (assetLookup.Count > 0)
        {
            return (assetLookup.Keys.ToArray(), assetLookup);
        }

        // Fallback: if both universe and watchlist are empty
        _logger.LogWarning("[research-engine] No candidates from universe or watchlist");
        return ([], new Dictionary<string, ResearchAsset>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Filters out warrants, preferred shares, units, rights, and other
    /// untradeable ticker patterns that waste API quota and AI tokens.
    /// </summary>
    private static bool IsUntradeable(string ticker)
    {
        if (string.IsNullOrEmpty(ticker)) return true;

        // Tickers longer than 5 chars are almost always warrants, units, or preferred
        if (ticker.Length > 5) return true;

        if (ticker.Length == 5)
        {
            // Warrants: end in W, U (units), Z (when-issued)
            if (ticker.EndsWith('W') || ticker.EndsWith('U') || ticker.EndsWith('Z'))
                return true;
            // Warrant suffixes: WS, RT (rights)
            if (ticker.EndsWith("WS") || ticker.EndsWith("RT"))
                return true;
        }

        // Preferred shares: tickers containing a hyphen (e.g. BAC-PL) or
        // 4-5 char tickers ending in common preferred patterns
        if (ticker.Contains('-')) return true;

        if (ticker.Length >= 4)
        {
            // Preferred share suffixes: PN, PR, PRA-PRZ pattern
            if (ticker.EndsWith("PN") || ticker.EndsWith("PR"))
                return true;
            // 5-char tickers ending in P + letter (e.g. BPYPN, GOOGL excluded by not matching)
            if (ticker.Length == 5 && ticker[3] == 'P' && char.IsLetter(ticker[4])
                && ticker[4] != 'L' && ticker[4] != 'E' && ticker[4] != 'T')
                return true;
        }

        return false;
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
