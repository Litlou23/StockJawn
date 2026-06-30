using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.OptionsData;

/// <summary>
/// Enhanced paper-options orchestration for the /paper-options page.
///
/// Pipeline:
///   1. Load a saved prediction.
///   2. Pick the target DTE band from duration preference + confidence/risk.
///   3. Fetch the real option chain from MarketData.app.
///   4. Apply the standard paper-trading filters (volume, OI, spread, delta).
///   5. Score and rank the surviving contracts.
///   6. Return enhanced PaperCandidate objects (no save unless autoSave).
///
/// Never invents contract data — every price, IV, Greek, volume, OI value
/// comes directly from the MarketData.app response.
/// </summary>
public class PaperOptionsService
{
    private const int DefaultTopN = 5;
    private const double EstContractMultiplier = 100.0; // 1 option contract = 100 shares

    private readonly MarketDataOptionsProvider _provider;
    private readonly OptionContractFilterService _filterService;
    private readonly OptionsDataRepository _repo;
    private readonly ResearchRepository _researchRepo;
    private readonly ILogger<PaperOptionsService> _logger;

    public PaperOptionsService(
        MarketDataOptionsProvider provider,
        OptionContractFilterService filterService,
        OptionsDataRepository repo,
        ResearchRepository researchRepo,
        ILogger<PaperOptionsService> logger)
    {
        _provider = provider;
        _filterService = filterService;
        _repo = repo;
        _researchRepo = researchRepo;
        _logger = logger;
    }

    public bool MarketDataConfigured => _provider.IsConfigured;

    // -----------------------------------------------------------------------
    // 1. List eligible predictions for the selector
    // -----------------------------------------------------------------------

    public async Task<List<PredictionCandidate>> GetEligiblePredictionsAsync(int limit = 30)
    {
        var recent = await _researchRepo.GetRecentPredictionsAsync(limit);
        // Eligible = open and either bullish or bearish (skip pure neutral for naked C/P).
        return recent
            .Where(p => p.Status == "open"
                     && (p.PredictionType == PredictionType.bullish
                         || p.PredictionType == PredictionType.bearish))
            .ToList();
    }

    // -----------------------------------------------------------------------
    // 2. Generate ranked candidates from a prediction
    // -----------------------------------------------------------------------

    public async Task<GenerateCandidatesResponse?> GenerateCandidatesAsync(GenerateCandidatesRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.PredictionId))
            return null;

        // Load prediction
        var predictions = await _researchRepo.GetRecentPredictionsAsync(200);
        var prediction = predictions.FirstOrDefault(p => p.Id == req.PredictionId);
        if (prediction is null)
        {
            _logger.LogWarning("[paper-options] Prediction {Id} not found", req.PredictionId);
            return null;
        }

        // Neutral predictions are not supported for naked call/put paper trades yet.
        if (prediction.PredictionType == PredictionType.neutral)
        {
            return new GenerateCandidatesResponse
            {
                PredictionId = req.PredictionId,
                Ticker = prediction.Ticker,
                PredictionType = prediction.PredictionType.ToString(),
                Warnings = ["Neutral predictions are not supported for naked call/put paper candidates."],
            };
        }

        if (!_provider.IsConfigured)
        {
            return new GenerateCandidatesResponse
            {
                PredictionId = req.PredictionId,
                Ticker = prediction.Ticker,
                PredictionType = prediction.PredictionType.ToString(),
                Warnings = ["MarketData.app token not configured — cannot generate candidates."],
            };
        }

        var predictionType = prediction.PredictionType.ToString();
        var refPrice = prediction.EntryReferencePrice ?? 0;

        // Pick the duration band + filter
        var (filter, targetDte, durationBucket) = OptionContractFilterService.DefaultFilterForDuration(
            predictionType,
            refPrice,
            req.DurationPreference,
            prediction.ConfidenceScore,
            prediction.RiskScore);

        var warnings = new List<string>();

        // High-risk rule: nudge towards two-week or refuse short DTE
        if (prediction.RiskScore > 75 && req.DurationPreference == DurationPreference.one_week)
        {
            warnings.Add("Risk score >75 — one-week candidates are higher-risk. Consider two-week or no trade.");
        }

        // Fetch chain
        var chain = await _provider.GetOptionsChainAsync(
            prediction.Ticker,
            minDte: filter.MinDte,
            maxDte: filter.MaxDte,
            side: filter.Side?.ToString());
        warnings.AddRange(chain.Warnings);

        var underlyingPrice = chain.UnderlyingPrice > 0 ? chain.UnderlyingPrice : refPrice;

        if (chain.Contracts.Count == 0)
        {
            warnings.Add("No option contracts returned from MarketData.app.");
            return new GenerateCandidatesResponse
            {
                PredictionId = req.PredictionId,
                Ticker = prediction.Ticker,
                PredictionType = predictionType,
                UnderlyingPrice = underlyingPrice,
                DurationBucket = durationBucket,
                TargetDte = targetDte,
                Warnings = warnings,
            };
        }

        // Filter
        var filtered = _filterService.Filter(chain.Contracts, filter);
        // Drop missing-bid/ask
        filtered = filtered.Where(c => c.Bid > 0 && c.Ask > 0 && !string.IsNullOrWhiteSpace(c.OptionSymbol)).ToList();
        // Estimated-cost cap (200 default => mid <= 2.00)
        filtered = filtered.Where(c => c.Mid * EstContractMultiplier <= 200).ToList();

        if (filtered.Count == 0)
        {
            warnings.Add("No contracts passed default filters (volume, OI, spread, delta, cost).");
        }

        var ranked = _filterService.ScoreAndRankEnhanced(filtered, predictionType, DefaultTopN);

        var delayLabel = chain.Warnings.Any(w => w.Contains("203") || w.Contains("delayed"))
            ? "delayed"
            : "real-time";

        var candidates = ranked.Select((s, i) =>
        {
            var c = s.Contract;
            var contractWarnings = new List<string>();
            if (c.Volume < 10) contractWarnings.Add("Low volume (<10).");
            if (c.OpenInterest < 100) contractWarnings.Add("Low open interest (<100).");
            if (c.BidAskSpreadPercent > 20) contractWarnings.Add($"Wide spread ({c.BidAskSpreadPercent:F1}%).");
            if (c.Iv * 100 > 80) contractWarnings.Add($"High IV ({c.Iv * 100:F0}%) — pricey premium.");
            if (Math.Abs(c.Theta) * 100 > c.Mid * 0.10) contractWarnings.Add("Theta decay risk.");
            if (c.Bid <= 0 || c.Ask <= 0) contractWarnings.Add("Missing bid/ask.");

            var bucket = OptionContractFilterService.GetPriceBucket(c.Mid);
            var contractCost = Math.Round(c.Mid * EstContractMultiplier, 2);
            var spreadPct = Math.Round(c.BidAskSpreadPercent, 2);

            var reason = $"Score {s.OverallScore:F1}: {s.ScoreExplanation}. " +
                         $"DTE {c.Dte}, Δ {c.Delta:F2}, IV {c.Iv * 100:F0}%, " +
                         $"OI {c.OpenInterest:N0}, vol {c.Volume:N0}. Direction matches {predictionType}.";

            return new PaperCandidateEnhanced
            {
                PredictionId = req.PredictionId,
                Ticker = prediction.Ticker,
                OptionSymbol = c.OptionSymbol,
                Side = c.Side,
                Strike = c.Strike,
                Expiration = c.Expiration,
                DteAtEntry = c.Dte,
                EntryUnderlyingPrice = c.UnderlyingPrice > 0 ? c.UnderlyingPrice : underlyingPrice,
                EntryBid = c.Bid,
                EntryAsk = c.Ask,
                EntryMid = c.Mid,
                EntryLast = c.Last,
                EntryIv = c.Iv,
                EntryDelta = c.Delta,
                EntryGamma = c.Gamma,
                EntryTheta = c.Theta,
                EntryVega = c.Vega,
                EntryOpenInterest = c.OpenInterest,
                EntryVolume = c.Volume,
                ContractScore = s.OverallScore,
                SelectionReason = reason,
                Provider = "marketdata",
                EstimatedContractCost = contractCost,
                SpreadPercent = spreadPct,
                DurationBucket = durationBucket,
                PriceBucket = bucket,
                DataDelayLabel = delayLabel,
                Rank = i + 1,
                Warnings = contractWarnings,
                Status = PaperCandidateStatus.open,
            };
        }).ToList();

        var resp = new GenerateCandidatesResponse
        {
            PredictionId = req.PredictionId,
            Ticker = prediction.Ticker,
            PredictionType = predictionType,
            UnderlyingPrice = underlyingPrice,
            DurationBucket = durationBucket,
            TargetDte = targetDte,
            Candidates = candidates,
            Warnings = warnings,
        };

        // Auto-save best ranked, if requested
        if (req.AutoSave && candidates.Count > 0)
        {
            await _repo.SavePaperCandidateEnhancedAsync(candidates[0]);
        }

        return resp;
    }

    // -----------------------------------------------------------------------
    // 3. Save a chosen candidate
    // -----------------------------------------------------------------------

    public async Task<PaperCandidateEnhanced?> SaveCandidateAsync(SaveCandidateRequest req)
    {
        if (req.Candidate is null) return null;
        var toSave = req.Candidate with { PredictionId = req.PredictionId };
        return await _repo.SavePaperCandidateEnhancedAsync(toSave);
    }

    // -----------------------------------------------------------------------
    // 4. List open candidates
    // -----------------------------------------------------------------------

    public async Task<List<PaperCandidateEnhanced>> GetOpenCandidatesAsync()
    {
        return await _repo.GetOpenPaperCandidatesEnhancedAsync();
    }

    // -----------------------------------------------------------------------
    // 5. Evaluate a single candidate
    // -----------------------------------------------------------------------

    public async Task<PaperOutcomeEnhanced?> EvaluateCandidateAsync(string paperCandidateId)
    {
        var candidate = await _repo.GetPaperCandidateEnhancedAsync(paperCandidateId);
        if (candidate is null) return null;

        var warnings = new List<string>();

        if (!_provider.IsConfigured)
        {
            warnings.Add("MarketData.app token not configured — cannot evaluate.");
            return null;
        }

        var chain = await _provider.GetOptionsChainAsync(candidate.Ticker);
        warnings.AddRange(chain.Warnings);

        var current = chain.Contracts.FirstOrDefault(c => c.OptionSymbol == candidate.OptionSymbol);

        // If the contract isn't in the chain — likely expired/delisted
        if (current is null)
        {
            warnings.Add("Contract no longer in chain — may have expired or been delisted.");
            var missingOutcome = new PaperOutcomeEnhanced
            {
                PaperCandidateId = candidate.Id,
                PredictionId = candidate.PredictionId,
                Ticker = candidate.Ticker,
                OptionSymbol = candidate.OptionSymbol,
                EvaluationTime = DateTimeOffset.UtcNow,
                CurrentUnderlyingPrice = chain.UnderlyingPrice,
                OutcomeSummary = "Contract no longer in chain — may have expired or been delisted.",
                DirectionCorrect = false,
                ContractProfitable = false,
                SpreadStillAcceptable = false,
                VolumeStillAcceptable = false,
                OutcomeScore = 0,
                Lesson = "Contract disappeared from the chain before evaluation. Use longer DTE or check liquidity earlier.",
                Warnings = warnings,
            };

            await _repo.SavePaperOutcomeEnhancedAsync(missingOutcome);
            if (candidate.Expiration <= DateTimeOffset.UtcNow)
                await _repo.UpdatePaperCandidateStatusAsync(candidate.Id, "expired");
            else
                await _repo.UpdatePaperCandidateStatusAsync(candidate.Id, "evaluated");

            await UpdateLearningStatsFromOutcomeAsync(candidate, missingOutcome);

            return missingOutcome;
        }

        // Compute outcome from real current data
        var pnlPerShare = current.Mid - candidate.EntryMid;
        var pnlPct = candidate.EntryMid > 0 ? pnlPerShare / candidate.EntryMid * 100 : 0;
        var estimatedDollarReturn = Math.Round(pnlPerShare * EstContractMultiplier, 2);
        var underlyingMovePct = candidate.EntryUnderlyingPrice > 0
            ? (current.UnderlyingPrice - candidate.EntryUnderlyingPrice) / candidate.EntryUnderlyingPrice * 100
            : 0;

        var directionCorrect =
            (candidate.Side == OptionSide.call && underlyingMovePct > 0) ||
            (candidate.Side == OptionSide.put && underlyingMovePct < 0);

        var contractProfitable = pnlPerShare > 0;
        var spreadStillOk = current.BidAskSpreadPercent <= 30;
        var volumeStillOk = current.Volume >= 5 && current.OpenInterest >= 50;

        // Outcome score: blends direction correctness + profitability + magnitude
        double outcomeScore;
        if (contractProfitable && directionCorrect)
            outcomeScore = Math.Min(100, 70 + Math.Abs(pnlPct) * 0.5);
        else if (contractProfitable)
            outcomeScore = 55; // profitable despite wrong direction (IV crush, etc.)
        else if (directionCorrect)
            outcomeScore = 35; // right direction, lost money (theta, spread)
        else
            outcomeScore = Math.Max(0, 20 - Math.Abs(pnlPct) * 0.2);

        outcomeScore = Math.Round(outcomeScore, 1);

        var lesson = BuildLesson(candidate, current, contractProfitable, directionCorrect, pnlPct, underlyingMovePct);

        var outcome = new PaperOutcomeEnhanced
        {
            PaperCandidateId = candidate.Id,
            PredictionId = candidate.PredictionId,
            Ticker = candidate.Ticker,
            OptionSymbol = candidate.OptionSymbol,
            EvaluationTime = DateTimeOffset.UtcNow,
            CurrentUnderlyingPrice = current.UnderlyingPrice,
            CurrentBid = current.Bid,
            CurrentAsk = current.Ask,
            CurrentMid = current.Mid,
            CurrentLast = current.Last,
            CurrentIv = current.Iv,
            CurrentDelta = current.Delta,
            CurrentOpenInterest = current.OpenInterest,
            CurrentVolume = current.Volume,
            PaperPnlPerContract = estimatedDollarReturn,
            PaperPnlPercent = Math.Round(pnlPct, 2),
            UnderlyingMovePercent = Math.Round(underlyingMovePct, 2),
            IvChange = Math.Round(current.Iv - candidate.EntryIv, 4),
            DirectionCorrect = directionCorrect,
            ContractProfitable = contractProfitable,
            SpreadStillAcceptable = spreadStillOk,
            VolumeStillAcceptable = volumeStillOk,
            OutcomeScore = outcomeScore,
            Lesson = lesson,
            OutcomeSummary = $"Paper P&L: {(pnlPerShare >= 0 ? "+" : "")}${estimatedDollarReturn:F2}/contract ({pnlPct:F1}%). " +
                             $"Underlying {(underlyingMovePct >= 0 ? "+" : "")}{underlyingMovePct:F2}%. " +
                             $"Direction {(directionCorrect ? "correct" : "wrong")}.",
            Warnings = warnings,
        };

        await _repo.SavePaperOutcomeEnhancedAsync(outcome);
        await _repo.UpdatePaperCandidateStatusAsync(candidate.Id, "evaluated");
        await UpdateLearningStatsFromOutcomeAsync(candidate, outcome);

        return outcome;
    }

    // -----------------------------------------------------------------------
    // 6. Evaluate all open
    // -----------------------------------------------------------------------

    public async Task<List<PaperOutcomeEnhanced>> EvaluateAllOpenAsync()
    {
        var open = await _repo.GetOpenPaperCandidatesEnhancedAsync();
        var outcomes = new List<PaperOutcomeEnhanced>();
        foreach (var c in open)
        {
            var oc = await EvaluateCandidateAsync(c.Id);
            if (oc is not null) outcomes.Add(oc);
        }
        return outcomes;
    }

    // -----------------------------------------------------------------------
    // 7. List outcomes
    // -----------------------------------------------------------------------

    public Task<List<PaperOutcomeEnhanced>> GetOutcomesAsync(int limit = 100)
        => _repo.GetRecentOutcomesEnhancedAsync(limit);

    // -----------------------------------------------------------------------
    // 8. Debug status
    // -----------------------------------------------------------------------

    public async Task<PaperOptionsDebugResponse> GetDebugAsync()
    {
        var allCandidates = await _repo.GetAllPaperCandidatesEnhancedAsync(200);
        var totalOutcomes = await _repo.GetRecentOutcomesEnhancedAsync(500);
        var stats = await _repo.GetAllOptionLearningStatsAsync();

        return new PaperOptionsDebugResponse
        {
            TotalCandidates = allCandidates.Count,
            OpenCandidates = allCandidates.Count(c => c.Status == PaperCandidateStatus.open),
            EvaluatedCandidates = allCandidates.Count(c => c.Status == PaperCandidateStatus.evaluated),
            TotalOutcomes = totalOutcomes.Count,
            LearningStats = stats,
            MarketDataConfigured = _provider.IsConfigured,
        };
    }

    // -----------------------------------------------------------------------
    // Learning engine: paper-options stat updates
    // -----------------------------------------------------------------------

    /// <summary>
    /// Updates the option_learning_stats table after an outcome is saved.
    /// Tracks: ticker, side, duration bucket, price bucket, dte bucket,
    /// confidence bucket, liquidity bucket, spread bucket.
    /// Mirrors the existing prediction learning loop — outcomes -> stats -> weights.
    /// </summary>
    private async Task UpdateLearningStatsFromOutcomeAsync(
        PaperCandidateEnhanced candidate,
        PaperOutcomeEnhanced outcome)
    {
        var keys = new List<(string Type, string Key)>
        {
            ("ticker", candidate.Ticker),
            ("side", candidate.Side.ToString()),
            ("contract_type", candidate.Side.ToString()),
            ("duration_bucket", candidate.DurationBucket),
            ("price_bucket", candidate.PriceBucket ?? "unknown"),
            ("dte_bucket", DteBucket(candidate.DteAtEntry)),
            ("confidence_bucket", ConfidenceBucketFromScore(candidate.ContractScore)),
            ("liquidity_bucket", LiquidityBucket(candidate.EntryOpenInterest, candidate.EntryVolume)),
            ("spread_bucket", SpreadBucket(candidate.SpreadPercent)),
        };

        foreach (var (statType, statKey) in keys)
        {
            if (string.IsNullOrWhiteSpace(statKey)) continue;
            await _repo.UpsertOptionLearningStatAsync(
                statType,
                statKey,
                isProfitable: outcome.ContractProfitable,
                optionMovePercent: outcome.PaperPnlPercent,
                underlyingMovePercent: outcome.UnderlyingMovePercent,
                outcomeScore: outcome.OutcomeScore);
        }
    }

    private static string DteBucket(int dte) => dte switch
    {
        <= 10 => "1_week",
        <= 21 => "2_week",
        <= 45 => "1_month",
        _ => "long_term",
    };

    private static string ConfidenceBucketFromScore(double score) => score switch
    {
        < 50 => "low",
        < 70 => "mid",
        < 85 => "high",
        _ => "very_high",
    };

    private static string LiquidityBucket(int oi, int vol)
    {
        if (oi >= 1000 && vol >= 500) return "high";
        if (oi >= 100 && vol >= 10) return "ok";
        return "low";
    }

    private static string SpreadBucket(double spreadPct) => spreadPct switch
    {
        <= 5 => "tight",
        <= 15 => "ok",
        _ => "wide",
    };

    private static string BuildLesson(
        PaperCandidateEnhanced candidate,
        OptionContract current,
        bool profitable,
        bool directionCorrect,
        double pnlPct,
        double underlyingMovePct)
    {
        if (profitable && directionCorrect)
            return $"Direction was right ({underlyingMovePct:F1}%) and the contract gained {pnlPct:F1}%. " +
                   $"This {candidate.PriceBucket} {candidate.Side} setup at DTE {candidate.DteAtEntry} worked.";

        if (!profitable && directionCorrect)
            return $"Underlying moved the right way ({underlyingMovePct:F1}%) but the contract lost {pnlPct:F1}% " +
                   $"— theta/IV crush ate the move. Consider longer DTE or higher delta.";

        if (profitable && !directionCorrect)
            return $"Underlying moved the wrong way ({underlyingMovePct:F1}%) but the contract still profited — " +
                   $"IV expansion or favorable Greek shift. Outcome is not a clean signal.";

        return $"Wrong direction ({underlyingMovePct:F1}%), lost {pnlPct:F1}%. " +
               $"Reconsider the {candidate.DurationBucket} signal for this ticker/setup.";
    }
}
