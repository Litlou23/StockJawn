using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Services.MarketData;
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

    public ResearchController(ResearchRepository repo) => _repo = repo;

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
    public async Task<IActionResult> GetPredictionsWithOutcomes([FromQuery] int limit = 50)
    {
        var predictions = await _repo.GetRecentPredictionsAsync(limit);
        var outcomes = await _repo.GetRecentOutcomesAsync(200);

        // Build a lookup from prediction_id -> outcome
        var outcomeMap = outcomes
            .GroupBy(o => o.PredictionId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.EvaluationTime).First());

        var joined = predictions.Select(p =>
        {
            outcomeMap.TryGetValue(p.Id, out var outcome);
            return new
            {
                prediction = p,
                outcome = outcome,
                hasOutcome = outcome is not null,
                wasCorrect = outcome?.DirectionCorrect,
            };
        }).ToList();

        var stats = new
        {
            total = joined.Count,
            evaluated = joined.Count(j => j.hasOutcome),
            correct = joined.Count(j => j.wasCorrect == true),
            incorrect = joined.Count(j => j.wasCorrect == false),
            pending = joined.Count(j => !j.hasOutcome),
            accuracy = joined.Count(j => j.hasOutcome) > 0
                ? Math.Round(100.0 * joined.Count(j => j.wasCorrect == true) / joined.Count(j => j.hasOutcome), 1)
                : 0,
        };

        return Ok(new { stats, items = joined });
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
