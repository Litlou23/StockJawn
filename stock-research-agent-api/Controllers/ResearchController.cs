using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.ResearchSignals;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// Read-only endpoints for querying research engine data.
/// No authentication required -- these return public research data.
/// </summary>
[ApiController]
[Route("api/research")]
public class ResearchController : ControllerBase
{
    private readonly ResearchRepository _repo;
    private readonly ResearchSignalService _signalService;

    public ResearchController(ResearchRepository repo, ResearchSignalService signalService)
    {
        _repo = repo;
        _signalService = signalService;
    }

    [HttpGet("predictions")]
    public async Task<IActionResult> GetPredictions(
        [FromQuery] string? status = null,
        [FromQuery] int limit = 30)
    {
        var predictions = await _repo.GetRecentPredictionsAsync(limit, status);
        return Ok(new { count = predictions.Count, predictions });
    }

    [HttpGet("outcomes")]
    public async Task<IActionResult> GetOutcomes([FromQuery] int limit = 50)
    {
        var outcomes = await _repo.GetRecentOutcomesAsync(limit);
        return Ok(new { count = outcomes.Count, outcomes });
    }

    [HttpGet("predictions-with-outcomes")]
    public async Task<IActionResult> GetPredictionsWithOutcomes(
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] int? limit = null,
        [FromQuery] string? category = null)
    {
        var categoryFilter = BuildCategoryFilter(category);

        List<PredictionCandidate> predictions;

        if (from is not null && DateTimeOffset.TryParse(from, out var fromDate))
        {
            var toDate = to is not null && DateTimeOffset.TryParse(to, out var td)
                ? td
                : DateTimeOffset.UtcNow;
            predictions = await _repo.GetPredictionsByDateRangeAsync(fromDate, toDate, extraFilter: categoryFilter);
        }
        else
        {
            predictions = await _repo.GetRecentPredictionsAsync(limit ?? 500, extraFilter: categoryFilter);
        }

        var predictionIds = predictions.Select(p => p.Id).ToList();
        var outcomes = predictionIds.Count > 0
            ? await _repo.GetOutcomesForPredictionsAsync(predictionIds)
            : [];

        var outcomeMap = outcomes
            .GroupBy(o => o.PredictionId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.EvaluationTime).First());

        var directional = predictions.Where(p => PredictionCategoryHelper.IsDirectional(p.PredictionType)).ToList();

        var joined = predictions.Select(p =>
        {
            outcomeMap.TryGetValue(p.Id, out var outcome);
            return new
            {
                prediction = p,
                outcome = outcome,
                hasOutcome = outcome is not null,
                wasCorrect = outcome?.DirectionCorrect,
                category = PredictionCategoryHelper.Categorize(p.PredictionType, p.TimeWindow).ToString(),
            };
        }).ToList();

        var directionalJoined = joined.Where(j =>
            PredictionCategoryHelper.IsDirectional(j.prediction.PredictionType)).ToList();

        var stats = new
        {
            total = joined.Count,
            evaluated = directionalJoined.Count(j => j.hasOutcome),
            correct = directionalJoined.Count(j => j.wasCorrect == true),
            incorrect = directionalJoined.Count(j => j.wasCorrect == false),
            pending = directionalJoined.Count(j => !j.hasOutcome),
            scanResults = joined.Count(j => !PredictionCategoryHelper.IsDirectional(j.prediction.PredictionType)),
            accuracy = directionalJoined.Count(j => j.hasOutcome) > 0
                ? Math.Round(100.0 * directionalJoined.Count(j => j.wasCorrect == true)
                    / directionalJoined.Count(j => j.hasOutcome), 1)
                : 0,
        };

        return Ok(new { stats, items = joined });
    }

    private static string? BuildCategoryFilter(string? category) => category switch
    {
        "short_term" => "prediction_type=in.(bullish,bearish)&time_window=in.(intraday,1_day,3_day,1_week)",
        "long_term" => "prediction_type=in.(bullish,bearish)&time_window=in.(1_month,3_month,6_month,1_year)",
        "scan" => "prediction_type=in.(neutral_no_edge,neutral_range_bound,neutral_high_volatility,watch_only,rejected,unavailable,neutral)",
        _ => null,
    };

    [HttpGet("signals")]
    public async Task<IActionResult> GetResearchSignals([FromQuery] string? tickers = null)
    {
        if (string.IsNullOrWhiteSpace(tickers))
            return Ok(new { count = 0, signals = new Dictionary<string, object>() });

        var tickerList = tickers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var grouped = await _signalService.GetActiveSignalsAsync(tickerList);

        var result = grouped.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Select(s => new
            {
                s.Id,
                s.Ticker,
                signalType = s.SignalType,
                signalCategory = s.SignalCategory,
                provider = s.Provider,
                strength = s.Strength,
                confidence = s.Confidence,
                eventTimestamp = s.EventTimestamp,
                detectedAt = s.DetectedAt,
                expiresAt = s.ExpiresAt,
                summary = s.Summary,
                metadata = s.Metadata,
            }).ToList()
        );

        var totalCount = result.Values.Sum(v => v.Count);
        return Ok(new { count = totalCount, signals = result });
    }

    [HttpGet("latest-report")]
    public async Task<IActionResult> GetLatestReport()
    {
        var run = await _repo.GetLatestResearchRunAsync();
        if (run is null) return Ok(new { report = "No research runs found.", run = (object?)null });
        return Ok(new { report = run.Summary ?? "No summary available.", run });
    }
}

/// <summary>
/// Debug endpoints for the research engine and market data.
/// </summary>
[ApiController]
[Route("api/debug")]
public class ResearchDebugController : ControllerBase
{
    private readonly ResearchRepository _repo;
    private readonly MarketDataService _marketData;

    public ResearchDebugController(ResearchRepository repo, MarketDataService marketData)
    {
        _repo = repo;
        _marketData = marketData;
    }

    [HttpGet("research-engine")]
    public async Task<IActionResult> GetResearchEngineStatus()
    {
        var runs = await _repo.GetRecentResearchRunsAsync(5);
        var predictions = await _repo.GetRecentPredictionsAsync(10);
        var outcomes = await _repo.GetRecentOutcomesAsync(10);
        var signalPerf = await _repo.GetAllSignalPerformanceAsync();
        var weights = await _repo.GetScoringWeightsAsync();
        var insights = await _repo.GetRecentLearningInsightsAsync(10);

        return Ok(new
        {
            supabaseConfigured = _repo.IsConfigured,
            recentRuns = runs,
            recentPredictions = new { count = predictions.Count, items = predictions },
            recentOutcomes = new { count = outcomes.Count, items = outcomes },
            signalPerformance = signalPerf,
            scoringWeights = weights,
            recentInsights = insights,
        });
    }

    [HttpGet("market-data")]
    public async Task<IActionResult> GetMarketDataStatus([FromQuery] string ticker = "AAPL")
    {
        var health = await _marketData.GetProviderHealthAsync();
        var quote = await _marketData.GetQuoteAsync(ticker);
        var bars = await _marketData.GetRecentBarsAsync(ticker, 5);
        var technical = await _marketData.GetTechnicalContextAsync(ticker);

        return Ok(new
        {
            providerHealth = health,
            sampleTicker = ticker,
            quote,
            barsPreview = bars.Take(3),
            technicalContext = technical,
        });
    }
}
