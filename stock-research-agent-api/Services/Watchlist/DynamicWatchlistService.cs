using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Watchlist;

/// <summary>
/// Core dynamic watchlist engine. Scans the universe, scores candidates,
/// compares against the current active watchlist, and produces add/keep/
/// review/swap/archive decisions. Caps the active list at ~10 items.
///
/// Does NOT auto-trade, connect a brokerage, or give buy/sell advice.
/// </summary>
public class DynamicWatchlistService
{
    private const int MaxActiveItems = 10;
    // Lowered from 15 → 5 because catalystScore is always 0 (news not integrated yet).
    // With only technicals contributing, max possible score is ~40.
    private const double MinScoreForCandidate = 5.0;
    private const double SwapThresholdDelta = 20.0;
    private const int StaleDaysThreshold = 14;
    private const double HighRiskThreshold = 80.0;
    private const double LowConfidenceThreshold = 15.0;

    private readonly MarketDataService _marketData;
    private readonly WatchlistRepository _watchlistRepo;
    private readonly ResearchRepository _researchRepo;
    private readonly ILogger<DynamicWatchlistService> _logger;

    public DynamicWatchlistService(
        MarketDataService marketData,
        WatchlistRepository watchlistRepo,
        ResearchRepository researchRepo,
        ILogger<DynamicWatchlistService> logger)
    {
        _marketData = marketData;
        _watchlistRepo = watchlistRepo;
        _researchRepo = researchRepo;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Main entry point
    // -----------------------------------------------------------------------

    public async Task<WatchlistGenerationResult> BuildDynamicWatchlistAsync(
        string[] universe,
        string? userId = null,
        int maxActiveItems = MaxActiveItems)
    {
        _logger.LogInformation("[watchlist] Starting dynamic watchlist build for {Count} universe tickers", universe.Length);

        var warnings = new List<string>();
        var changeLogs = new List<object>();
        var dataQuality = new DataQualitySummary();
        var tickersWithData = 0;
        var tickersWithNews = 0;

        // 1. Load existing state
        var currentActive = await _watchlistRepo.GetActiveWatchlistAsync(userId);
        var currentReview = await _watchlistRepo.GetWatchlistByStatusAsync(WatchlistStatus.ReviewNeeded, userId);
        var allCurrent = currentActive.Concat(currentReview).ToList();
        var existingTickers = new HashSet<string>(allCurrent.Select(w => w.Ticker));

        // Load prior context
        var scoringWeights = (await _researchRepo.GetScoringWeightsAsync())
            .ToDictionary(w => w.SignalName, w => w.Weight);
        var recentInsights = await _researchRepo.GetRecentLearningInsightsAsync(10);
        var recentPredictions = await _researchRepo.GetRecentPredictionsAsync(100);
        var recentOutcomes = await _researchRepo.GetRecentOutcomesAsync(100);

        // Build prediction accuracy map per ticker
        var outcomeMap = recentOutcomes.ToDictionary(o => o.PredictionId);
        var tickerAccuracy = new Dictionary<string, (int Correct, int Total)>();
        foreach (var pred in recentPredictions)
        {
            if (!outcomeMap.TryGetValue(pred.Id, out var outcome) || outcome.DirectionCorrect is null) continue;
            var (correct, total) = tickerAccuracy.GetValueOrDefault(pred.Ticker);
            total++;
            if (outcome.DirectionCorrect == true) correct++;
            tickerAccuracy[pred.Ticker] = (correct, total);
        }

        // 2. Score all universe tickers as candidates
        _logger.LogInformation("[watchlist] Scoring {Count} universe tickers...", universe.Length);
        var candidates = new List<ScoredCandidate>();

        foreach (var ticker in universe)
        {
            var scored = await ScoreTickerAsync(ticker, scoringWeights, tickerAccuracy);
            candidates.Add(scored);
            if (scored.HasMarketData) tickersWithData++;
            if (scored.HasNews) tickersWithNews++;
            _logger.LogInformation("[watchlist] {Ticker}: score={Score:F1}, hasData={HasData}, confidence={Conf}, trend={Trend}, warnings={Warnings}",
                ticker, scored.TotalScore, scored.HasMarketData, scored.DataConfidence,
                scored.Technical?.TrendDirection ?? "none",
                string.Join("; ", scored.MissingWarnings));
        }

        candidates.Sort((a, b) => b.TotalScore.CompareTo(a.TotalScore));

        dataQuality = new DataQualitySummary
        {
            TickersScanned = universe.Length,
            TickersWithMarketData = tickersWithData,
            TickersWithNews = tickersWithNews,
            TickersWithOptionsData = 0,
            Warnings = tickersWithData < universe.Length
                ? [$"Market data missing for {universe.Length - tickersWithData} tickers"]
                : [],
        };

        // 3. Rescore existing watchlist items
        var existingScored = new Dictionary<string, ScoredCandidate>();
        foreach (var item in allCurrent)
        {
            var scored = candidates.FirstOrDefault(c => c.Ticker == item.Ticker);
            if (scored is null)
            {
                scored = await ScoreTickerAsync(item.Ticker, scoringWeights, tickerAccuracy);
                candidates.Add(scored);
            }
            existingScored[item.Ticker] = scored;
        }

        // 4. Evaluate existing items: keep / review_needed / swap_candidate / archive
        var kept = new List<WatchlistItem>();
        var reviewNeeded = new List<WatchlistItem>();
        var swapCandidates = new List<WatchlistItem>();
        var archived = new List<WatchlistItem>();

        foreach (var item in allCurrent)
        {
            var scored = existingScored[item.Ticker];
            var oldScore = item.TotalScore ?? 0;
            var newScore = scored.TotalScore;
            var decision = EvaluateExistingItem(item, scored, candidates);

            switch (decision.Action)
            {
                case "keep":
                    // Update score if changed
                    if (Math.Abs(newScore - oldScore) > 2)
                    {
                        await _watchlistRepo.UpdateWatchlistItemAsync(item.Id, new
                        {
                            total_score = newScore,
                            catalyst_score = scored.CatalystScore,
                            risk_score = scored.RiskScore,
                            data_confidence = scored.DataConfidence,
                            last_reviewed_at = DateTimeOffset.UtcNow.ToString("o"),
                            bullish_case = scored.BullishCase,
                            bearish_case = scored.BearishCase,
                            missing_data_warnings = scored.MissingWarnings.ToArray(),
                        });
                        changeLogs.Add(MakeChangeLog(item, WatchlistChangeType.ScoreChanged, item.Status, item.Status, oldScore, newScore, decision.Reason, userId));
                    }
                    kept.Add(item with { TotalScore = newScore });
                    break;

                case "review_needed":
                    await _watchlistRepo.UpdateWatchlistStatusAsync(item.Id, WatchlistStatus.ReviewNeeded, decision.Reason);
                    await _watchlistRepo.UpdateWatchlistItemAsync(item.Id, new
                    {
                        total_score = newScore, catalyst_score = scored.CatalystScore,
                        risk_score = scored.RiskScore, last_reviewed_at = DateTimeOffset.UtcNow.ToString("o"),
                    });
                    changeLogs.Add(MakeChangeLog(item, WatchlistChangeType.MarkedReviewNeeded, item.Status, WatchlistStatus.ReviewNeeded, oldScore, newScore, decision.Reason, userId));
                    reviewNeeded.Add(item with { Status = WatchlistStatus.ReviewNeeded, TotalScore = newScore, SwapReason = decision.Reason });
                    break;

                case "swap_candidate":
                    await _watchlistRepo.UpdateWatchlistStatusAsync(item.Id, WatchlistStatus.SwapCandidate, decision.Reason);
                    await _watchlistRepo.UpdateWatchlistItemAsync(item.Id, new
                    {
                        total_score = newScore, catalyst_score = scored.CatalystScore,
                        risk_score = scored.RiskScore, last_reviewed_at = DateTimeOffset.UtcNow.ToString("o"),
                    });
                    changeLogs.Add(MakeChangeLog(item, WatchlistChangeType.MarkedSwapCandidate, item.Status, WatchlistStatus.SwapCandidate, oldScore, newScore, decision.Reason, userId));
                    swapCandidates.Add(item with { Status = WatchlistStatus.SwapCandidate, TotalScore = newScore, SwapReason = decision.Reason });
                    break;

                case "archive":
                    await _watchlistRepo.ArchiveWatchlistItemAsync(item.Id, decision.Reason);
                    changeLogs.Add(MakeChangeLog(item, WatchlistChangeType.Archived, item.Status, WatchlistStatus.Archived, oldScore, newScore, decision.Reason, userId));
                    archived.Add(item with { Status = WatchlistStatus.Archived, SwapReason = decision.Reason });
                    break;
            }
        }

        // 5. Find new candidates to add
        var added = new List<WatchlistItem>();
        var activeCount = kept.Count;
        var slotsAvailable = maxActiveItems - activeCount;

        _logger.LogInformation("[watchlist] Candidate filter: MinScore={Min}, existingTickers=[{Existing}], all scores: {Scores}",
            MinScoreForCandidate,
            string.Join(",", existingTickers),
            string.Join(", ", candidates.Select(c => $"{c.Ticker}={c.TotalScore:F1}")));

        var newCandidates = candidates
            .Where(c => !existingTickers.Contains(c.Ticker) && c.TotalScore >= MinScoreForCandidate)
            .OrderByDescending(c => c.TotalScore)
            .ToList();

        _logger.LogInformation("[watchlist] {Count} candidates passed filter (score >= {Min})", newCandidates.Count, MinScoreForCandidate);

        // Check if new candidates are stronger than weakest kept items
        var weakestKept = kept.OrderBy(k => k.TotalScore ?? 0).FirstOrDefault();
        var weakestScore = weakestKept?.TotalScore ?? 0;

        foreach (var candidate in newCandidates)
        {
            if (activeCount >= maxActiveItems)
            {
                // Only add if significantly stronger than weakest kept
                if (weakestKept is not null && candidate.TotalScore > weakestScore + SwapThresholdDelta)
                {
                    // Swap: archive the weakest, add the new one
                    await _watchlistRepo.UpdateWatchlistStatusAsync(weakestKept.Id,
                        WatchlistStatus.SwapCandidate,
                        $"Replaced by {candidate.Ticker} (score {candidate.TotalScore:F0} vs {weakestScore:F0})");
                    changeLogs.Add(MakeChangeLog(weakestKept, WatchlistChangeType.MarkedSwapCandidate,
                        WatchlistStatus.Active, WatchlistStatus.SwapCandidate, weakestScore, weakestScore,
                        $"Replaced by stronger candidate {candidate.Ticker}", userId));
                    swapCandidates.Add(weakestKept with { Status = WatchlistStatus.SwapCandidate });
                    kept.Remove(weakestKept);
                    activeCount--;

                    weakestKept = kept.OrderBy(k => k.TotalScore ?? 0).FirstOrDefault();
                    weakestScore = weakestKept?.TotalScore ?? 0;
                }
                else break;
            }

            if (activeCount < maxActiveItems)
            {
                var newItem = await AddNewWatchlistItemAsync(candidate, userId);
                if (newItem is not null)
                {
                    added.Add(newItem);
                    changeLogs.Add(MakeChangeLog(newItem, WatchlistChangeType.Added,
                        null, WatchlistStatus.Active, null, candidate.TotalScore,
                        candidate.Reason, userId));
                    activeCount++;
                }
            }
        }

        // 6. Save candidates for history
        var candidateRows = candidates.Take(30).Select(c => (object)new
        {
            user_id = userId,
            ticker = c.Ticker,
            source = "weekly_research",
            category = c.Category,
            candidate_score = c.TotalScore,
            catalyst_score = c.CatalystScore,
            risk_score = c.RiskScore,
            data_confidence = c.DataConfidence,
            reason = c.Reason,
            selected_for_watchlist = added.Any(a => a.Ticker == c.Ticker),
        }).ToList();
        await _watchlistRepo.InsertCandidatesAsync(candidateRows);

        // 7. Save change logs
        await _watchlistRepo.InsertChangeLogsAsync(changeLogs);

        var activeWatchlist = kept.Concat(added).OrderByDescending(w => w.TotalScore).ToList();

        _logger.LogInformation("[watchlist] Build complete: {Active} active, {Added} added, {Review} review, {Swap} swap, {Archived} archived",
            activeWatchlist.Count, added.Count, reviewNeeded.Count, swapCandidates.Count, archived.Count);

        return new WatchlistGenerationResult
        {
            ActiveWatchlistCount = activeWatchlist.Count,
            Added = added,
            Kept = kept,
            ReviewNeeded = reviewNeeded,
            SwapCandidates = swapCandidates,
            ArchivedItems = archived,
            TopCandidates = candidateRows.Take(10).Select(c => new WatchlistCandidate
            {
                Ticker = candidates.First(x => x.Ticker == ((dynamic)c).ticker).Ticker,
            }).ToList(),
            ActiveWatchlist = activeWatchlist,
            ChangeLog = changeLogs.Select(c => new WatchlistChangeLog()).ToList(),
            Warnings = warnings.Concat(dataQuality.Warnings).ToList(),
            DataQuality = dataQuality,
            Persisted = true,
        };
    }

    // -----------------------------------------------------------------------
    // Scoring
    // -----------------------------------------------------------------------

    private record ScoredCandidate
    {
        public string Ticker { get; init; } = "";
        public double TotalScore { get; init; }
        public double CatalystScore { get; init; }
        public double RiskScore { get; init; }
        public string DataConfidence { get; init; } = "low";
        public string Category { get; init; } = "general";
        public string Reason { get; init; } = "";
        public string BullishCase { get; init; } = "";
        public string BearishCase { get; init; } = "";
        public List<string> MissingWarnings { get; init; } = [];
        public List<string> SourcesUsed { get; init; } = [];
        public bool HasMarketData { get; init; }
        public bool HasNews { get; init; }
        public MarketSnapshotQuote? Quote { get; init; }
        public MarketSnapshotTechnical? Technical { get; init; }
    }

    private async Task<ScoredCandidate> ScoreTickerAsync(
        string ticker,
        Dictionary<string, double> weights,
        Dictionary<string, (int Correct, int Total)> tickerAccuracy)
    {
        var (quote, bars, technical, mktWarnings) = await _marketData.GetFullContextAsync(ticker);

        double techScore = 0;
        double catalystScore = 0;
        double riskScore = 50;
        var signals = new List<string>();
        var bearishSignals = new List<string>();
        var sources = new List<string>();
        var missingWarnings = new List<string>(mktWarnings);

        // Technical scoring
        if (technical is not null)
        {
            sources.Add("twelve-data");
            var trendW = weights.GetValueOrDefault("technical_trend", 1.0);
            var momW = weights.GetValueOrDefault("technical_momentum", 1.0);
            var volW = weights.GetValueOrDefault("technical_volume", 1.0);

            if (technical.TrendDirection == "bullish") { techScore += 20 * trendW; signals.Add("Trend bullish"); }
            else if (technical.TrendDirection == "bearish") { techScore -= 15 * trendW; bearishSignals.Add("Trend bearish"); }

            if (technical.MomentumSummary.Contains("up", StringComparison.OrdinalIgnoreCase))
            { techScore += 10 * momW; signals.Add("Momentum positive"); }
            else if (technical.MomentumSummary.Contains("down", StringComparison.OrdinalIgnoreCase))
            { techScore -= 10 * momW; bearishSignals.Add("Momentum negative"); }

            if (technical.VolumeSummary.Contains("elevated", StringComparison.OrdinalIgnoreCase))
            { techScore += 10 * volW; signals.Add("Volume elevated"); }
            else if (technical.VolumeSummary.Contains("below", StringComparison.OrdinalIgnoreCase))
            { techScore -= 5 * volW; bearishSignals.Add("Volume below average"); }
        }
        else
        {
            missingWarnings.Add("No technical data available");
            riskScore += 10;
        }

        // Historical accuracy adjustment
        if (tickerAccuracy.TryGetValue(ticker, out var acc) && acc.Total >= 3)
        {
            var accuracy = (double)acc.Correct / acc.Total;
            if (accuracy > 0.6) { techScore += 10; signals.Add($"Prior accuracy {accuracy * 100:F0}%"); }
            else if (accuracy < 0.3) { techScore -= 10; bearishSignals.Add($"Prior accuracy only {accuracy * 100:F0}%"); riskScore += 10; }
        }

        // No real news integration yet -- note it
        missingWarnings.Add("RSS news not yet integrated into .NET API scoring");

        // Options data always missing (not connected)
        missingWarnings.Add("Options-chain data not connected -- options_readiness_score is null");

        var totalScore = techScore + catalystScore;

        // Data confidence
        var confidence = quote is not null ? "medium" : "low";
        if (quote is not null && technical is not null) confidence = "high";
        if (missingWarnings.Count > 2) confidence = "low";

        // Risk adjustments
        if (quote is null) riskScore += 15;

        return new ScoredCandidate
        {
            Ticker = ticker,
            TotalScore = totalScore,
            CatalystScore = catalystScore,
            RiskScore = Math.Min(riskScore, 100),
            DataConfidence = confidence,
            Category = WatchlistCategory.General,
            Reason = $"Score: {totalScore:F1}. {signals.Count} bullish signals, {bearishSignals.Count} bearish. {sources.Count} data sources.",
            BullishCase = signals.Count > 0 ? string.Join("; ", signals) : "No strong bullish signals",
            BearishCase = bearishSignals.Count > 0 ? string.Join("; ", bearishSignals) : "No strong bearish signals identified",
            MissingWarnings = missingWarnings,
            SourcesUsed = sources,
            HasMarketData = quote is not null,
            HasNews = false,
            Quote = quote,
            Technical = technical,
        };
    }

    // -----------------------------------------------------------------------
    // Evaluate existing item: keep / review / swap / archive
    // -----------------------------------------------------------------------

    private record ItemDecision(string Action, string Reason);

    private ItemDecision EvaluateExistingItem(
        WatchlistItem item, ScoredCandidate newScore, List<ScoredCandidate> allCandidates)
    {
        var reasons = new List<string>();
        var oldScore = item.TotalScore ?? 0;
        var scoreDrop = oldScore - newScore.TotalScore;

        // Check staleness
        var daysSinceReview = item.LastReviewedAt.HasValue
            ? (DateTimeOffset.UtcNow - item.LastReviewedAt.Value).TotalDays
            : (DateTimeOffset.UtcNow - item.CreatedAt).TotalDays;

        if (daysSinceReview > StaleDaysThreshold)
            reasons.Add($"Stale: not reviewed in {daysSinceReview:F0} days");

        // Check review_by_date
        if (item.ReviewByDate is not null && DateOnly.TryParse(item.ReviewByDate, out var reviewDate))
        {
            if (reviewDate <= DateOnly.FromDateTime(DateTime.UtcNow))
                reasons.Add("Review date has passed");
        }

        // Check risk
        if (newScore.RiskScore > HighRiskThreshold)
            reasons.Add($"High risk: {newScore.RiskScore:F0}");

        // Check data confidence
        if (newScore.DataConfidence == "low")
            reasons.Add("Data confidence is low");

        // Check score drop
        if (scoreDrop > 15)
            reasons.Add($"Score dropped significantly: {oldScore:F0} -> {newScore.TotalScore:F0}");

        // Check if better candidates exist
        var betterCandidates = allCandidates
            .Where(c => c.Ticker != item.Ticker && c.TotalScore > newScore.TotalScore + SwapThresholdDelta)
            .Take(3).ToList();
        if (betterCandidates.Count > 0)
            reasons.Add($"Stronger candidates available: {string.Join(", ", betterCandidates.Select(c => $"{c.Ticker} ({c.TotalScore:F0})"))}");

        // Decision logic
        if (reasons.Count == 0)
            return new ItemDecision("keep", "Score stable, no issues detected");

        // Archive if multiple strong reasons or score is very negative
        if (reasons.Count >= 3 || newScore.TotalScore < -10)
            return new ItemDecision("archive", string.Join(". ", reasons));

        // Swap candidate if better alternatives exist and score dropped
        if (betterCandidates.Count > 0 && scoreDrop > 5)
            return new ItemDecision("swap_candidate", string.Join(". ", reasons));

        // Review needed for 1-2 concerns
        return new ItemDecision("review_needed", string.Join(". ", reasons));
    }

    // -----------------------------------------------------------------------
    // Add new item
    // -----------------------------------------------------------------------

    private async Task<WatchlistItem?> AddNewWatchlistItemAsync(ScoredCandidate candidate, string? userId)
    {
        var item = new
        {
            user_id = userId,
            ticker = candidate.Ticker,
            status = WatchlistStatus.Active,
            category = candidate.Category,
            watch_reason = candidate.Reason,
            thesis_summary = $"Added based on automated scoring. {candidate.Reason}",
            bullish_case = candidate.BullishCase,
            bearish_case = candidate.BearishCase,
            data_confidence = candidate.DataConfidence,
            total_score = candidate.TotalScore,
            catalyst_score = candidate.CatalystScore,
            risk_score = candidate.RiskScore,
            added_at = DateTimeOffset.UtcNow.ToString("o"),
            last_reviewed_at = DateTimeOffset.UtcNow.ToString("o"),
            review_by_date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
            sources_used = candidate.SourcesUsed.ToArray(),
            missing_data_warnings = candidate.MissingWarnings.ToArray(),
        };

        var id = await _watchlistRepo.UpsertWatchlistItemAsync(item);
        if (id is null) return null;

        return new WatchlistItem
        {
            Id = id,
            UserId = userId,
            Ticker = candidate.Ticker,
            Status = WatchlistStatus.Active,
            Category = candidate.Category,
            WatchReason = candidate.Reason,
            ThesisSummary = item.thesis_summary,
            BullishCase = candidate.BullishCase,
            BearishCase = candidate.BearishCase,
            DataConfidence = candidate.DataConfidence,
            TotalScore = candidate.TotalScore,
            CatalystScore = candidate.CatalystScore,
            RiskScore = candidate.RiskScore,
            MissingDataWarnings = candidate.MissingWarnings.ToArray(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // -----------------------------------------------------------------------
    // Change log helper
    // -----------------------------------------------------------------------

    private static object MakeChangeLog(
        WatchlistItem item, string changeType, string? prevStatus, string? newStatus,
        double? prevScore, double? newScore, string reason, string? userId) => new
    {
        user_id = userId,
        watchlist_item_id = string.IsNullOrEmpty(item.Id) ? null : item.Id,
        ticker = item.Ticker,
        change_type = changeType,
        previous_status = prevStatus,
        new_status = newStatus,
        previous_score = prevScore,
        new_score = newScore,
        reason,
    };
}
