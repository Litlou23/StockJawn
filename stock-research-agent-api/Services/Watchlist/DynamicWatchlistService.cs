using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.ResearchSignals;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.Watchlist;

/// <summary>
/// Core dynamic watchlist engine. Scans the universe, scores candidates,
/// compares against the current active watchlist, and produces add/keep/
/// review/swap/archive decisions. Any candidate scoring above the minimum
/// threshold becomes active — the list is uncapped and pruned by the
/// existing review/archive logic (staleness, score drops, low confidence).
///
/// Does NOT auto-trade, connect a brokerage, or give buy/sell advice.
/// </summary>
public class DynamicWatchlistService
{
    private const int MinActiveTarget = 5;
    // With news-driven discovery, we have catalyst data. Tickers that were
    // discovered by news get a catalyst boost, so this threshold works.
    private const double MinScoreForCandidate = 15.0;
    /// <summary>Operational warning — logs when active count exceeds this. Does NOT block additions.</summary>
    private const int WarningThreshold = 25;
    private const int StaleDaysThreshold = 14;
    private const double HighRiskThreshold = 80.0;
    private const double LowConfidenceThreshold = 15.0;

    private readonly MarketDataService _marketData;
    private readonly WatchlistRepository _watchlistRepo;
    private readonly ResearchRepository _researchRepo;
    private readonly ResearchSignalService _signalService;
    private readonly ILogger<DynamicWatchlistService> _logger;

    public DynamicWatchlistService(
        MarketDataService marketData,
        WatchlistRepository watchlistRepo,
        ResearchRepository researchRepo,
        ResearchSignalService signalService,
        ILogger<DynamicWatchlistService> logger)
    {
        _marketData = marketData;
        _watchlistRepo = watchlistRepo;
        _researchRepo = researchRepo;
        _signalService = signalService;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Main entry point
    // -----------------------------------------------------------------------

    /// <summary>
    /// Discovery context passed from UniverseDiscoveryService, telling us
    /// WHY each ticker was discovered (news mentions, earnings, etc.)
    /// </summary>
    public record TickerDiscoveryContext(
        string Ticker,
        double DiscoveryScore,
        bool HasUpcomingEarnings,
        string? EarningsDate,
        int RssMentions,
        int FinnhubMentions,
        string TopReason);

    public async Task<WatchlistGenerationResult> BuildDynamicWatchlistAsync(
        string[] universe,
        string? userId = null,
        List<TickerDiscoveryContext>? discoveryContext = null)
    {
        _logger.LogInformation("[watchlist] Starting dynamic watchlist build for {Count} universe tickers", universe.Length);

        // Build lookup for discovery context
        var discoveryMap = (discoveryContext ?? [])
            .ToDictionary(d => d.Ticker, d => d, StringComparer.OrdinalIgnoreCase);

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
        var championId = await _researchRepo.GetChampionProfileIdAsync();
        var recentPredictions = await _researchRepo.GetRecentPredictionsAsync(100, profileId: championId);
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
            discoveryMap.TryGetValue(ticker, out var discovery);
            var scored = await ScoreTickerAsync(ticker, scoringWeights, tickerAccuracy, recentPredictions, discovery);
            candidates.Add(scored);
            if (scored.HasMarketData) tickersWithData++;
            if (scored.HasNews) tickersWithNews++;
            _logger.LogInformation("[watchlist] {Ticker}: score={Score:F1}, catalyst={Catalyst:F1}, hasData={HasData}, confidence={Conf}, trend={Trend}, discoveryReason={Reason}",
                ticker, scored.TotalScore, scored.CatalystScore, scored.HasMarketData, scored.DataConfidence,
                scored.Technical?.TrendDirection ?? "none",
                discovery?.TopReason ?? "none");
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
                scored = await ScoreTickerAsync(item.Ticker, scoringWeights, tickerAccuracy, recentPredictions);
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
                            watch_reason = scored.Reason,
                            bullish_case = scored.BullishCase,
                            bearish_case = scored.BearishCase,
                            missing_data_warnings = scored.MissingWarnings.ToArray(),
                            raw_context = new { score_breakdown = scored.ScoreBreakdown },
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
                        raw_context = new { score_breakdown = scored.ScoreBreakdown },
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
                        raw_context = new { score_breakdown = scored.ScoreBreakdown },
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

        // 5. Find new candidates to add — no cap, any qualifying ticker gets in
        var added = new List<WatchlistItem>();
        var activeCount = kept.Count;

        _logger.LogInformation("[watchlist] Candidate filter: MinScore={Min}, existingTickers=[{Existing}], all scores: {Scores}",
            MinScoreForCandidate,
            string.Join(",", existingTickers),
            string.Join(", ", candidates.Select(c => $"{c.Ticker}={c.TotalScore:F1}")));

        var newCandidates = candidates
            .Where(c => !existingTickers.Contains(c.Ticker) && c.TotalScore >= MinScoreForCandidate)
            .OrderByDescending(c => c.TotalScore)
            .ToList();

        _logger.LogInformation("[watchlist] {Count} candidates passed filter (score >= {Min})", newCandidates.Count, MinScoreForCandidate);

        foreach (var candidate in newCandidates)
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

        // Operational telemetry — warn if watchlist is getting large
        if (activeCount > WarningThreshold)
        {
            var allActive = kept.Concat(added).ToList();
            var avgScore = allActive.Count > 0 ? allActive.Average(w => w.TotalScore ?? 0) : 0;
            var oldestReview = allCurrent
                .Where(w => w.LastReviewedAt is not null)
                .Select(w => (DateTimeOffset.UtcNow - w.LastReviewedAt.GetValueOrDefault()).TotalDays)
                .DefaultIfEmpty(0)
                .Max();
            _logger.LogWarning(
                "[watchlist] Active count ({ActiveCount}) exceeds warning threshold ({WarningThreshold}). " +
                "AvgScore={AvgScore:F1}, OldestReviewAgeDays={OldestReviewAge:F0}, NewlyAdded={Added}",
                activeCount, WarningThreshold, avgScore, oldestReview, added.Count);
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
            TopCandidates = candidates.Take(10).Select(c => new WatchlistCandidate
            {
                Ticker = c.Ticker,
                CandidateScore = c.TotalScore,
                CatalystScore = c.CatalystScore,
                RiskScore = c.RiskScore,
                DataConfidence = c.DataConfidence,
                Reason = c.Reason,
                SelectedForWatchlist = added.Any(a => a.Ticker == c.Ticker),
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
        public double BullishScore { get; init; }
        public double BearishScore { get; init; }
        public string WinningDirection { get; init; } = "neutral";
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
        public List<object> ScoreBreakdown { get; init; } = [];
    }

    private async Task<ScoredCandidate> ScoreTickerAsync(
        string ticker,
        Dictionary<string, double> weights,
        Dictionary<string, (int Correct, int Total)> tickerAccuracy,
        List<PredictionCandidate> recentPredictions,
        TickerDiscoveryContext? discovery = null)
    {
        var (quote, bars, technical, mktWarnings) = await _marketData.GetFullContextAsync(ticker);

        // Independent accumulators — each signal contributes to one side only
        double bullScore = 0, bearScore = 0;
        double catalystScore = 0;
        double riskScore = 25;
        var bullishSignals = new List<string>();
        var bearishSignals = new List<string>();
        var sources = new List<string>();
        var missingWarnings = new List<string>(mktWarnings);
        var hasNews = false;
        var scoreBreakdown = new List<object>();

        void AddBull(double pts, string signal, string category = "technical", double weight = 1.0)
        {
            bullScore += pts;
            bullishSignals.Add(signal);
            scoreBreakdown.Add(new { signal, points = pts, category, weight, direction = "bullish" });
        }
        void AddBear(double pts, string signal, string category = "technical", double weight = 1.0)
        {
            bearScore += pts;
            bearishSignals.Add(signal);
            scoreBreakdown.Add(new { signal, points = pts, category, weight, direction = "bearish" });
        }

        // =================================================================
        // Technical scoring — symmetric magnitudes
        // =================================================================
        if (technical is not null)
        {
            sources.Add("twelve-data");
            var trendW = weights.GetValueOrDefault("technical_trend", 1.0);
            var momW = weights.GetValueOrDefault("technical_momentum", 1.0);
            var volW = weights.GetValueOrDefault("technical_volume", 1.0);

            // Trend direction (symmetric ±25)
            if (technical.TrendDirection == "bullish")
                AddBull(Math.Round(25 * trendW, 1), "Trend bullish", weight: trendW);
            else if (technical.TrendDirection == "bearish")
                AddBear(Math.Round(25 * trendW, 1), "Trend bearish", weight: trendW);

            // Momentum (symmetric ±15)
            if (technical.MomentumSummary.Contains("up", StringComparison.OrdinalIgnoreCase))
                AddBull(Math.Round(15 * momW, 1), "Momentum positive", weight: momW);
            else if (technical.MomentumSummary.Contains("down", StringComparison.OrdinalIgnoreCase))
                AddBear(Math.Round(15 * momW, 1), "Momentum negative", weight: momW);

            // Volume (symmetric ±12)
            if (technical.VolumeSummary.Contains("elevated", StringComparison.OrdinalIgnoreCase))
                AddBull(Math.Round(12 * volW, 1), "Volume elevated", weight: volW);
            else if (technical.VolumeSummary.Contains("below", StringComparison.OrdinalIgnoreCase))
                AddBear(Math.Round(12 * volW, 1), "Volume below average", weight: volW);

            // Moving average alignment (symmetric ±10)
            if (!string.IsNullOrEmpty(technical.MovingAverageSummary))
            {
                var maSummary = technical.MovingAverageSummary.ToLowerInvariant();
                if (maSummary.Contains("above") && maSummary.Contains("bullish"))
                    AddBull(10, "Price above key moving averages");
                else if (maSummary.Contains("below") && maSummary.Contains("bearish"))
                    AddBear(10, "Price below key moving averages");
                else if (maSummary.Contains("above"))
                    AddBull(5, "Price near moving averages (leaning bullish)");
                else if (maSummary.Contains("below"))
                    AddBear(5, "Price near moving averages (leaning bearish)");
            }

            // Relative strength (symmetric ±8)
            if (!string.IsNullOrEmpty(technical.RelativeStrengthNote))
            {
                var rsNote = technical.RelativeStrengthNote.ToLowerInvariant();
                if (rsNote.Contains("outperform") || rsNote.Contains("strong"))
                    AddBull(8, "Outperforming the broader market");
                else if (rsNote.Contains("underperform") || rsNote.Contains("weak"))
                    AddBear(8, "Underperforming the broader market");
            }
        }
        else
        {
            missingWarnings.Add("No technical data available");
            riskScore += 10;
        }

        // =================================================================
        // Price action scoring (symmetric)
        // =================================================================
        if (quote is not null)
        {
            var changePct = quote.ChangePercent;
            var isBullishTrend = technical?.TrendDirection == "bullish";
            var isBearishTrend = technical?.TrendDirection == "bearish";

            if (Math.Abs(changePct) >= 1.0)
            {
                if (changePct > 0 && isBullishTrend)
                    AddBull(8, $"Price confirming bullish trend ({changePct:+0.0}% today)");
                else if (changePct < 0 && isBearishTrend)
                    AddBear(8, $"Price confirming bearish trend ({changePct:+0.0;-0.0}% today)");
                else if (changePct < 0 && isBullishTrend)
                { AddBear(5, $"Price moving against bullish trend ({changePct:+0.0;-0.0}% today)"); riskScore += 5; }
                else if (changePct > 0 && isBearishTrend)
                { AddBull(5, $"Price moving against bearish trend ({changePct:+0.0}% today)"); riskScore += 5; }
            }

            if (quote.High > 0 && quote.Low > 0 && (quote.High - quote.Low) > 0)
            {
                var positionInRange = (quote.Price - quote.Low) / (quote.High - quote.Low);
                if (positionInRange >= 0.8)
                    AddBull(5, "Trading near high of day");
                else if (positionInRange <= 0.2)
                    AddBear(5, "Trading near low of day");
            }
        }

        // =================================================================
        // Multi-day pattern (symmetric)
        // =================================================================
        if (bars.Count >= 5)
        {
            var recent = bars.TakeLast(3).ToList();
            if (recent.Count == 3)
            {
                if (recent[1].Low > recent[0].Low && recent[2].Low > recent[1].Low)
                    AddBull(8, "Making higher lows (3 days)");
                else if (recent[1].High < recent[0].High && recent[2].High < recent[1].High)
                    AddBear(8, "Making lower highs (3 days)");
            }

            var fiveDayBars = bars.TakeLast(5).ToList();
            var highest = fiveDayBars.Max(b => b.High);
            var lowest = fiveDayBars.Min(b => b.Low);
            if (lowest > 0)
            {
                var rangePercent = (highest - lowest) / lowest * 100;
                if (rangePercent < 3.0)
                {
                    // Tight range is direction-neutral — adds to both
                    bullScore += 3; bearScore += 3;
                    bullishSignals.Add($"Tight 5-day range ({rangePercent:F1}%) — potential breakout");
                    bearishSignals.Add($"Tight 5-day range ({rangePercent:F1}%) — potential breakdown");
                    scoreBreakdown.Add(new { signal = $"Tight 5-day range ({rangePercent:F1}%)", points = 3.0, category = "technical", weight = 1.0, direction = "both" });
                }
            }

            if (fiveDayBars.Count == 5)
            {
                var firstHalfVol = fiveDayBars.Take(2).Average(b => b.Volume);
                var secondHalfVol = fiveDayBars.Skip(3).Average(b => b.Volume);
                if (firstHalfVol > 0 && secondHalfVol > firstHalfVol * 1.5)
                {
                    // Rising volume is direction-neutral
                    bullScore += 3; bearScore += 3;
                    bullishSignals.Add("Volume increasing over last 5 days");
                    scoreBreakdown.Add(new { signal = "Volume increasing over last 5 days", points = 3.0, category = "technical", weight = 1.0, direction = "both" });
                }
            }
        }

        // =================================================================
        // Historical accuracy (direction-neutral boost/penalty)
        // =================================================================
        if (tickerAccuracy.TryGetValue(ticker, out var acc) && acc.Total >= 3)
        {
            var accuracy = (double)acc.Correct / acc.Total;
            if (accuracy >= 0.7)
            {
                bullScore += 10; bearScore += 10;
                bullishSignals.Add($"Strong prior accuracy {accuracy * 100:F0}%");
                scoreBreakdown.Add(new { signal = $"Strong prior accuracy {accuracy * 100:F0}%", points = 10.0, category = "technical", weight = 1.0, direction = "both" });
            }
            else if (accuracy > 0.5)
            {
                bullScore += 5; bearScore += 5;
                bullishSignals.Add($"Decent prior accuracy {accuracy * 100:F0}%");
                scoreBreakdown.Add(new { signal = $"Decent prior accuracy {accuracy * 100:F0}%", points = 5.0, category = "technical", weight = 1.0, direction = "both" });
            }
            else if (accuracy < 0.3)
            {
                riskScore += 10;
                scoreBreakdown.Add(new { signal = $"Poor prior accuracy {accuracy * 100:F0}%", points = 0.0, category = "technical", weight = 1.0, direction = "risk" });
            }
        }

        // =================================================================
        // Catalyst scoring (direction-neutral — catalysts indicate attention)
        // =================================================================
        if (discovery is not null)
        {
            var catalystW = weights.GetValueOrDefault("catalyst_news", 1.0);
            hasNews = discovery.RssMentions > 0 || discovery.FinnhubMentions > 0;
            if (hasNews) sources.Add("news-discovery");

            if (discovery.HasUpcomingEarnings)
            {
                var pts = Math.Round(20 * catalystW, 1);
                catalystScore += pts;
                // Earnings boost both sides — it's a catalyst regardless of direction
                bullScore += pts * 0.5; bearScore += pts * 0.5;
                bullishSignals.Add($"Earnings on {discovery.EarningsDate}");
                sources.Add("finnhub-earnings");
                scoreBreakdown.Add(new { signal = $"Earnings on {discovery.EarningsDate}", points = pts, category = "catalyst", weight = catalystW, direction = "both" });
            }

            if (discovery.RssMentions >= 5)
            {
                var pts = Math.Round(15 * catalystW, 1);
                catalystScore += pts;
                bullScore += pts * 0.5; bearScore += pts * 0.5;
                bullishSignals.Add($"High news volume ({discovery.RssMentions} mentions)");
                scoreBreakdown.Add(new { signal = $"High news volume ({discovery.RssMentions} mentions)", points = pts, category = "catalyst", weight = catalystW, direction = "both" });
            }
            else if (discovery.RssMentions >= 2)
            {
                var pts = Math.Round(8 * catalystW, 1);
                catalystScore += pts;
                bullScore += pts * 0.5; bearScore += pts * 0.5;
                bullishSignals.Add($"News mentions ({discovery.RssMentions})");
                scoreBreakdown.Add(new { signal = $"News mentions ({discovery.RssMentions})", points = pts, category = "catalyst", weight = catalystW, direction = "both" });
            }

            if (discovery.FinnhubMentions >= 5)
            {
                var pts = Math.Round(10 * catalystW, 1);
                catalystScore += pts;
                bullScore += pts * 0.5; bearScore += pts * 0.5;
                bullishSignals.Add($"Heavy Finnhub coverage ({discovery.FinnhubMentions} articles)");
                scoreBreakdown.Add(new { signal = $"Heavy Finnhub coverage ({discovery.FinnhubMentions} articles)", points = pts, category = "catalyst", weight = catalystW, direction = "both" });
            }
            else if (discovery.FinnhubMentions >= 3)
            {
                var pts = Math.Round(5 * catalystW, 1);
                catalystScore += pts;
                bullScore += pts * 0.5; bearScore += pts * 0.5;
                bullishSignals.Add($"Finnhub coverage ({discovery.FinnhubMentions} articles)");
                scoreBreakdown.Add(new { signal = $"Finnhub coverage ({discovery.FinnhubMentions} articles)", points = pts, category = "catalyst", weight = catalystW, direction = "both" });
            }

            if (discovery.DiscoveryScore >= 8)
            {
                catalystScore += 10;
                bullScore += 5; bearScore += 5;
                bullishSignals.Add("High discovery score — multiple reasons this ticker surfaced");
                scoreBreakdown.Add(new { signal = "High discovery score", points = 10.0, category = "catalyst", weight = 1.0, direction = "both" });
            }
            else if (discovery.DiscoveryScore >= 5)
            {
                catalystScore += 5;
                bullScore += 2.5; bearScore += 2.5;
                bullishSignals.Add("Moderate discovery score");
                scoreBreakdown.Add(new { signal = "Moderate discovery score", points = 5.0, category = "catalyst", weight = 1.0, direction = "both" });
            }
        }
        else
        {
            missingWarnings.Add("No discovery context — ticker not found in recent news");
        }

        missingWarnings.Add("Options-chain data not connected -- options_readiness_score is null");

        // =================================================================
        // Research signal scoring (congress, etc.)
        // =================================================================
        var researchSignals = await _signalService.GetActiveSignalsForTickerAsync(ticker);
        if (researchSignals.Count > 0)
        {
            sources.Add("research-signals");
            foreach (var sig in researchSignals)
            {
                var sigWeight = weights.GetValueOrDefault($"research_{sig.SignalType}", 1.0);
                var pts = sig.Strength * sig.Confidence * 15 * sigWeight;

                if (sig.SignalType.Contains("buy") || sig.SignalType.Contains("cluster"))
                {
                    AddBull(Math.Round(pts, 1), $"Research: {sig.Summary ?? sig.SignalType}", "research", sigWeight);
                }
                else if (sig.SignalType.Contains("sell"))
                {
                    AddBear(Math.Round(pts, 1), $"Research: {sig.Summary ?? sig.SignalType}", "research", sigWeight);
                }
                else
                {
                    // Unknown direction — split contribution
                    var halfPts = Math.Round(pts * 0.5, 1);
                    bullScore += halfPts; bearScore += halfPts;
                    scoreBreakdown.Add(new { signal = $"Research: {sig.Summary ?? sig.SignalType}", points = halfPts, category = "research", weight = sigWeight, direction = "both" });
                }
            }
        }

        // =================================================================
        // Blend with prediction confidence (direction-aware)
        // =================================================================
        var latestPrediction = recentPredictions
            .Where(p => p.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefault();

        if (latestPrediction is not null)
        {
            sources.Add("prediction-engine");
            var predConf = (double)latestPrediction.ConfidenceScore;

            // Blend prediction into the correct direction's score
            if (latestPrediction.PredictionType == PredictionType.bullish)
            {
                bullScore = (bullScore * 0.50) + (predConf * 0.50);
                bullishSignals.Add($"Prediction: bullish conf={latestPrediction.ConfidenceScore}/100");
            }
            else if (latestPrediction.PredictionType == PredictionType.bearish)
            {
                bearScore = (bearScore * 0.50) + (predConf * 0.50);
                bearishSignals.Add($"Prediction: bearish conf={latestPrediction.ConfidenceScore}/100");
            }
            scoreBreakdown.Add(new { signal = $"Prediction {latestPrediction.PredictionType} conf={latestPrediction.ConfidenceScore}/100", points = Math.Round(predConf * 0.50, 1), category = "prediction", weight = 1.0, direction = latestPrediction.PredictionType.ToString() });

            riskScore = (riskScore * 0.40) + (latestPrediction.RiskScore * 0.60);
            if (latestPrediction.RiskScore >= 70)
                bearishSignals.Add($"Prediction flagged high risk ({latestPrediction.RiskScore}/100)");
        }

        // Clamp both scores
        bullScore = Math.Clamp(bullScore, 0, 100);
        bearScore = Math.Clamp(bearScore, 0, 100);

        // TotalScore = highest conviction regardless of direction
        var totalScore = Math.Max(bullScore, bearScore);
        var winningDirection = bullScore > bearScore + 10 ? "bullish"
            : bearScore > bullScore + 10 ? "bearish"
            : "neutral";

        var confidence = quote is not null ? "medium" : "low";
        if (quote is not null && technical is not null) confidence = "high";
        if (missingWarnings.Count > 2) confidence = "low";

        if (quote is null) riskScore += 15;

        var allSignals = bullishSignals.Concat(bearishSignals).Distinct().ToList();

        return new ScoredCandidate
        {
            Ticker = ticker,
            TotalScore = totalScore,
            BullishScore = bullScore,
            BearishScore = bearScore,
            WinningDirection = winningDirection,
            CatalystScore = catalystScore,
            RiskScore = Math.Min(riskScore, 100),
            DataConfidence = confidence,
            Category = WatchlistCategory.General,
            Reason = allSignals.Count > 0
                ? $"Direction: {winningDirection}. Top signals: {string.Join(", ", allSignals.Take(3))}."
                : "On the radar but no strong edge yet",
            BullishCase = bullishSignals.Count > 0 ? string.Join("; ", bullishSignals) : "No strong bullish signals",
            BearishCase = bearishSignals.Count > 0 ? string.Join("; ", bearishSignals) : "No strong bearish signals identified",
            MissingWarnings = missingWarnings,
            SourcesUsed = sources,
            HasMarketData = quote is not null,
            HasNews = hasNews,
            Quote = quote,
            Technical = technical,
            ScoreBreakdown = scoreBreakdown,
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

        // Check if significantly better candidates exist (informational flag for review)
        var betterCandidates = allCandidates
            .Where(c => c.Ticker != item.Ticker && c.TotalScore > newScore.TotalScore + 25)
            .Take(3).ToList();
        if (betterCandidates.Count > 0)
            reasons.Add($"Stronger candidates available: {string.Join(", ", betterCandidates.Select(c => $"{c.Ticker} ({c.TotalScore:F0})"))}");

        // Decision logic
        if (reasons.Count == 0)
            return new ItemDecision("keep", "Score stable, no issues detected");

        // Archive if multiple strong reasons or score is very negative
        if (reasons.Count >= 3 || newScore.TotalScore < -10)
            return new ItemDecision("archive", string.Join(". ", reasons));

        // Flag for review if better alternatives exist and score dropped
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
            raw_context = new { score_breakdown = candidate.ScoreBreakdown },
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
