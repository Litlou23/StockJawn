using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.OptionsData;

/// <summary>
/// Orchestrates options data: fetch chain, filter, score, create paper candidates,
/// evaluate outcomes. Never invents data — all contract data comes from MarketData.app.
/// </summary>
public class OptionsDataService
{
    private readonly MarketDataOptionsProvider _provider;
    private readonly OptionContractFilterService _filterService;
    private readonly OptionsDataRepository _repo;
    private readonly ResearchRepository _researchRepo;
    private readonly ILogger<OptionsDataService> _logger;

    public OptionsDataService(
        MarketDataOptionsProvider provider,
        OptionContractFilterService filterService,
        OptionsDataRepository repo,
        ResearchRepository researchRepo,
        ILogger<OptionsDataService> logger)
    {
        _provider = provider;
        _filterService = filterService;
        _repo = repo;
        _researchRepo = researchRepo;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Chain fetch + filter
    // -----------------------------------------------------------------------

    public async Task<OptionsChain> GetChainAsync(string underlying, int? minDte = null, int? maxDte = null, string? side = null)
    {
        return await _provider.GetOptionsChainAsync(underlying, minDte, maxDte, side);
    }

    public async Task<TopContractsResponse> GetTopContractsAsync(
        string underlying,
        OptionContractFilter? filter = null,
        int topN = 10)
    {
        var chain = await _provider.GetOptionsChainAsync(underlying);
        if (chain.Contracts.Count == 0)
        {
            return new TopContractsResponse
            {
                Underlying = underlying,
                UnderlyingPrice = chain.UnderlyingPrice,
                Warnings = chain.Warnings,
                FilterUsed = filter ?? new OptionContractFilter(),
            };
        }

        filter ??= new OptionContractFilter { MinDte = 5, MaxDte = 60, MinOpenInterest = 10 };
        var filtered = _filterService.Filter(chain.Contracts, filter);
        var scored = _filterService.ScoreAndRank(filtered, topN);

        return new TopContractsResponse
        {
            Underlying = underlying,
            UnderlyingPrice = chain.UnderlyingPrice,
            TopContracts = scored,
            FilterUsed = filter,
            Warnings = chain.Warnings,
        };
    }

    // -----------------------------------------------------------------------
    // Paper candidate creation from a prediction
    // -----------------------------------------------------------------------

    public async Task<PaperCandidateResponse?> CreatePaperCandidateFromPredictionAsync(string predictionId)
    {
        // 1. Look up the prediction
        var predictions = await _researchRepo.GetRecentPredictionsAsync(limit: 100);
        var prediction = predictions.FirstOrDefault(p => p.Id == predictionId);
        if (prediction is null)
        {
            _logger.LogWarning("[options-data] Prediction {Id} not found", predictionId);
            return null;
        }

        // 2. Fetch real chain
        var chain = await _provider.GetOptionsChainAsync(prediction.Ticker);
        if (chain.Contracts.Count == 0)
        {
            _logger.LogWarning("[options-data] No contracts for {Ticker}", prediction.Ticker);
            return null;
        }

        // 3. Filter based on prediction direction
        var filter = OptionContractFilterService.DefaultFilterForPrediction(
            prediction.PredictionType.ToString(), chain.UnderlyingPrice);
        var filtered = _filterService.Filter(chain.Contracts, filter);
        var scored = _filterService.ScoreAndRank(filtered, topN: 1);

        if (scored.Count == 0)
        {
            _logger.LogWarning("[options-data] No contracts passed filter for {Ticker}", prediction.Ticker);
            return null;
        }

        var best = scored[0];
        var c = best.Contract;

        // 4. Create paper candidate (all values from real data)
        var candidate = new PaperOptionCandidate
        {
            PredictionId = predictionId,
            Ticker = prediction.Ticker,
            OptionSymbol = c.OptionSymbol,
            Side = c.Side,
            Strike = c.Strike,
            Expiration = c.Expiration,
            DteAtEntry = c.Dte,
            EntryUnderlyingPrice = c.UnderlyingPrice,
            EntryBid = c.Bid,
            EntryAsk = c.Ask,
            EntryMid = c.Mid,
            EntryIv = c.Iv,
            EntryDelta = c.Delta,
            EntryOpenInterest = c.OpenInterest,
            EntryVolume = c.Volume,
            ContractScore = best.OverallScore,
            SelectionReason = $"Top scored contract ({best.OverallScore:F1}): {best.ScoreExplanation}",
        };

        // 5. Persist
        var saved = await _repo.SavePaperCandidateAsync(candidate);

        return new PaperCandidateResponse
        {
            Candidate = saved ?? candidate,
            LinkedPrediction = prediction,
        };
    }

    // -----------------------------------------------------------------------
    // Paper outcome evaluation
    // -----------------------------------------------------------------------

    public async Task<PaperOptionOutcome?> EvaluatePaperCandidateAsync(string paperCandidateId)
    {
        var candidate = await _repo.GetPaperCandidateAsync(paperCandidateId);
        if (candidate is null)
        {
            _logger.LogWarning("[options-data] Paper candidate {Id} not found", paperCandidateId);
            return null;
        }

        // Fetch current chain to find current contract data
        var chain = await _provider.GetOptionsChainAsync(candidate.Ticker);
        var currentContract = chain.Contracts.FirstOrDefault(c => c.OptionSymbol == candidate.OptionSymbol);

        if (currentContract is null)
        {
            // Contract may have expired or be delisted
            var outcome = new PaperOptionOutcome
            {
                PaperCandidateId = paperCandidateId,
                EvaluationTime = DateTimeOffset.UtcNow,
                CurrentUnderlyingPrice = chain.UnderlyingPrice,
                OutcomeSummary = "Contract no longer in chain — may have expired or been delisted.",
            };
            await _repo.SavePaperOutcomeAsync(outcome);

            if (candidate.Expiration <= DateTimeOffset.UtcNow)
                await _repo.UpdatePaperCandidateStatusAsync(paperCandidateId, "expired");

            return outcome;
        }

        var pnl = currentContract.Mid - candidate.EntryMid;
        var pnlPct = candidate.EntryMid > 0 ? pnl / candidate.EntryMid * 100 : 0;
        var underlyingMove = candidate.EntryUnderlyingPrice > 0
            ? (currentContract.UnderlyingPrice - candidate.EntryUnderlyingPrice) / candidate.EntryUnderlyingPrice * 100
            : 0;

        var evalOutcome = new PaperOptionOutcome
        {
            PaperCandidateId = paperCandidateId,
            EvaluationTime = DateTimeOffset.UtcNow,
            CurrentUnderlyingPrice = currentContract.UnderlyingPrice,
            CurrentBid = currentContract.Bid,
            CurrentAsk = currentContract.Ask,
            CurrentMid = currentContract.Mid,
            CurrentIv = currentContract.Iv,
            CurrentDelta = currentContract.Delta,
            CurrentOpenInterest = currentContract.OpenInterest,
            CurrentVolume = currentContract.Volume,
            PaperPnlPerContract = Math.Round(pnl * 100, 2), // options are per 100 shares
            PaperPnlPercent = Math.Round(pnlPct, 2),
            UnderlyingMovePercent = Math.Round(underlyingMove, 2),
            IvChange = Math.Round(currentContract.Iv - candidate.EntryIv, 4),
            OutcomeSummary = $"Paper P&L: {(pnl >= 0 ? "+" : "")}{pnl * 100:F2}/contract ({pnlPct:F1}%). " +
                             $"Underlying moved {underlyingMove:F2}%. IV changed {(currentContract.Iv - candidate.EntryIv) * 100:F1}pp.",
        };

        await _repo.SavePaperOutcomeAsync(evalOutcome);

        return evalOutcome;
    }

    // -----------------------------------------------------------------------
    // Paper tracking status
    // -----------------------------------------------------------------------

    public async Task<PaperTrackingStatusResponse> GetPaperTrackingStatusAsync()
    {
        var candidates = await _repo.GetAllPaperCandidatesAsync();
        var results = new List<PaperCandidateWithOutcome>();

        foreach (var c in candidates)
        {
            var latestOutcome = await _repo.GetLatestPaperOutcomeAsync(c.Id);
            results.Add(new PaperCandidateWithOutcome
            {
                Candidate = c,
                LatestOutcome = latestOutcome,
            });
        }

        return new PaperTrackingStatusResponse
        {
            TotalCandidates = candidates.Count,
            OpenCandidates = candidates.Count(c => c.Status == PaperCandidateStatus.open),
            ClosedCandidates = candidates.Count(c => c.Status == PaperCandidateStatus.closed),
            ExpiredCandidates = candidates.Count(c => c.Status == PaperCandidateStatus.expired),
            Candidates = results,
        };
    }
}
