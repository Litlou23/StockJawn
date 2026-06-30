using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.OptionsData;

namespace StockResearchAgent.Api.Controllers;

[ApiController]
[Route("api/options-data")]
public class OptionsDataController : ControllerBase
{
    private readonly OptionsDataService _service;
    private readonly MarketDataOptionsProvider _provider;
    private readonly ILogger<OptionsDataController> _logger;

    public OptionsDataController(
        OptionsDataService service,
        MarketDataOptionsProvider provider,
        ILogger<OptionsDataController> logger)
    {
        _service = service;
        _provider = provider;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/options-data/chain/{symbol}
    /// Fetch full options chain from MarketData.app. Real data only.
    /// Optional query params: minDte, maxDte, side (call/put)
    /// </summary>
    [HttpGet("chain/{symbol}")]
    public async Task<IActionResult> GetChain(
        string symbol,
        [FromQuery] int? minDte,
        [FromQuery] int? maxDte,
        [FromQuery] string? side)
    {
        if (!_provider.IsConfigured)
            return StatusCode(503, new { error = "MarketData.app token not configured" });

        var chain = await _service.GetChainAsync(symbol, minDte, maxDte, side);

        return Ok(new OptionsChainResponse
        {
            Underlying = chain.Underlying,
            UnderlyingPrice = chain.UnderlyingPrice,
            TotalContracts = chain.TotalContracts,
            Contracts = chain.Contracts,
            Warnings = chain.Warnings,
            RetrievedAt = chain.RetrievedAt,
        });
    }

    /// <summary>
    /// GET /api/options-data/top/{symbol}
    /// Fetch chain, filter, score, and return top N contracts.
    /// </summary>
    [HttpGet("top/{symbol}")]
    public async Task<IActionResult> GetTopContracts(
        string symbol,
        [FromQuery] int topN = 10,
        [FromQuery] string? side = null,
        [FromQuery] int? minDte = null,
        [FromQuery] int? maxDte = null,
        [FromQuery] int? minOpenInterest = null,
        [FromQuery] double? maxSpreadPercent = null)
    {
        if (!_provider.IsConfigured)
            return StatusCode(503, new { error = "MarketData.app token not configured" });

        var filter = new OptionContractFilter
        {
            Side = side == "call" ? OptionSide.call : side == "put" ? OptionSide.put : null,
            MinDte = minDte ?? 5,
            MaxDte = maxDte ?? 60,
            MinOpenInterest = minOpenInterest ?? 10,
            MaxBidAskSpreadPercent = maxSpreadPercent ?? 30,
        };

        var result = await _service.GetTopContractsAsync(symbol, filter, topN);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/options-data/paper-candidate/{predictionId}
    /// Create a paper option candidate from a prediction using real chain data.
    /// </summary>
    [HttpPost("paper-candidate/{predictionId}")]
    public async Task<IActionResult> CreatePaperCandidate(string predictionId)
    {
        if (!_provider.IsConfigured)
            return StatusCode(503, new { error = "MarketData.app token not configured" });

        var result = await _service.CreatePaperCandidateFromPredictionAsync(predictionId);
        if (result is null)
            return NotFound(new { error = "Could not create paper candidate — prediction not found or no suitable contracts" });

        return Ok(result);
    }

    /// <summary>
    /// POST /api/options-data/paper-evaluate/{paperCandidateId}
    /// Evaluate a paper candidate against current market data.
    /// </summary>
    [HttpPost("paper-evaluate/{paperCandidateId}")]
    public async Task<IActionResult> EvaluatePaperCandidate(string paperCandidateId)
    {
        if (!_provider.IsConfigured)
            return StatusCode(503, new { error = "MarketData.app token not configured" });

        var outcome = await _service.EvaluatePaperCandidateAsync(paperCandidateId);
        if (outcome is null)
            return NotFound(new { error = "Paper candidate not found" });

        return Ok(outcome);
    }

    /// <summary>
    /// GET /api/options-data/paper-tracking
    /// Get all paper candidates with their latest outcomes.
    /// </summary>
    [HttpGet("paper-tracking")]
    public async Task<IActionResult> GetPaperTracking()
    {
        var result = await _service.GetPaperTrackingStatusAsync();
        return Ok(result);
    }

    /// <summary>
    /// GET /api/options-data/status
    /// Provider configuration status — no external calls.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            provider = "MarketData.app",
            configured = _provider.IsConfigured,
            endpoints = new[]
            {
                "GET /api/options-data/chain/{symbol}",
                "GET /api/options-data/top/{symbol}",
                "POST /api/options-data/paper-candidate/{predictionId}",
                "POST /api/options-data/paper-evaluate/{paperCandidateId}",
                "GET /api/options-data/paper-tracking",
                "GET /api/options-data/stock-quote/{symbol}",
                "GET /api/options-data/stock-candles/{symbol}",
            },
        });
    }

    /// <summary>
    /// GET /api/options-data/stock-quote/{symbol}
    /// Fetch a stock quote from MarketData.app — real data only.
    /// </summary>
    [HttpGet("stock-quote/{symbol}")]
    public async Task<IActionResult> GetStockQuote(string symbol)
    {
        if (!_provider.IsConfigured)
            return StatusCode(503, new { error = "MarketData.app token not configured" });

        var quote = await _provider.GetStockQuoteAsync(symbol);
        if (quote is null)
            return NotFound(new { error = $"No quote data for {symbol}" });

        return Ok(quote);
    }

    /// <summary>
    /// GET /api/options-data/stock-candles/{symbol}
    /// Fetch stock candles from MarketData.app — real data only.
    /// Optional: resolution (daily, weekly, monthly), limit (default 30)
    /// </summary>
    [HttpGet("stock-candles/{symbol}")]
    public async Task<IActionResult> GetStockCandles(
        string symbol,
        [FromQuery] string resolution = "daily",
        [FromQuery] int limit = 30)
    {
        if (!_provider.IsConfigured)
            return StatusCode(503, new { error = "MarketData.app token not configured" });

        var candles = await _provider.GetStockCandlesAsync(symbol, resolution, limit);
        if (candles.Count == 0)
            return NotFound(new { error = $"No candle data for {symbol}" });

        return Ok(new { symbol = symbol.ToUpperInvariant(), resolution, count = candles.Count, candles });
    }
}
