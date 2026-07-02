using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.OptionsData;
using StockResearchAgent.Api.Services.Providers.StockFit;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.UniverseDiscovery;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// Provider-health debug endpoints.
///
///   GET /api/debug/providers            — configured/unconfigured for every provider
///   GET /api/debug/stockfit?ticker=AMD  — deep StockFit probe (per-endpoint results)
///
/// Never exposes API keys. Every network call is wrapped so a bad response
/// shows as { records, warnings, status } instead of a 500.
/// </summary>
[ApiController]
[Route("api/debug")]
public class DebugProvidersController : ControllerBase
{
    private readonly StockFitProvider _stockFit;
    private readonly SupabaseClient _supabase;
    private readonly TwelveDataProvider _twelveData;
    private readonly IOpenAiCompletionService _openAi;
    private readonly FinnhubProvider _finnhub;
    private readonly MarketDataOptionsProvider _marketData;
    private readonly ILogger<DebugProvidersController> _logger;

    public DebugProvidersController(
        StockFitProvider stockFit,
        SupabaseClient supabase,
        TwelveDataProvider twelveData,
        IOpenAiCompletionService openAi,
        FinnhubProvider finnhub,
        MarketDataOptionsProvider marketData,
        ILogger<DebugProvidersController> logger)
    {
        _stockFit = stockFit;
        _supabase = supabase;
        _twelveData = twelveData;
        _openAi = openAi;
        _finnhub = finnhub;
        _marketData = marketData;
        _logger = logger;
    }

    [HttpGet("providers")]
    public IActionResult ListProviders()
    {
        return Ok(new
        {
            timestamp = DateTimeOffset.UtcNow,
            providers = new object[]
            {
                new { name = "Supabase", configured = _supabase.IsConfigured, usedFor = "database" },
                new { name = "Twelve Data", configured = _twelveData.IsConfigured, usedFor = "stock quotes + OHLCV bars" },
                new { name = "OpenAI", configured = _openAi.IsConfigured, usedFor = "prediction explanations" },
                new { name = "Finnhub", configured = _finnhub.IsConfigured, usedFor = "universe discovery + earnings calendar" },
                new { name = "MarketData.app", configured = _marketData.IsConfigured, usedFor = "real options chains" },
                new { name = "StockFit", configured = _stockFit.IsConfigured, baseUrl = _stockFit.BaseUrl,
                      usedFor = "company news, SEC filings, 8-K events, earnings calendar, key metrics, insider trades, institutional ownership" },
                new { name = "RSS", configured = true, usedFor = "news catalyst discovery" },
            },
        });
    }

    [HttpGet("stockfit")]
    public async Task<IActionResult> StockFitProbe([FromQuery] string ticker = "AMD")
    {
        ticker = ticker.Trim().ToUpperInvariant();
        var results = new List<object>();
        var warnings = new List<string>();

        if (!_stockFit.IsConfigured)
        {
            warnings.Add("STOCKFIT_API_KEY not set — provider is marked unavailable. Set the env var and redeploy.");
            return Ok(new
            {
                configured = false,
                baseUrl = _stockFit.BaseUrl,
                ticker,
                endpointsTested = results,
                warnings,
                note = "No API key exposed. This endpoint never returns the STOCKFIT_API_KEY value.",
            });
        }

        var news = await _stockFit.GetNewsAsync(ticker, limit: 10);
        results.Add(BuildProbe("news", news.EndpointCalled, news.StatusCode, news.Data?.Count ?? 0, news.Warnings));

        var filings = await _stockFit.GetFilingsAsync(ticker, limit: 10);
        results.Add(BuildProbe("filings", filings.EndpointCalled, filings.StatusCode, filings.Data?.Count ?? 0, filings.Warnings));

        var earnings = await _stockFit.GetEarningsCalendarAsync(ticker);
        results.Add(BuildProbe("earnings", earnings.EndpointCalled, earnings.StatusCode, earnings.Data?.Count ?? 0, earnings.Warnings));

        var metrics = await _stockFit.GetKeyMetricsAsync(ticker);
        results.Add(BuildProbe("metrics", metrics.EndpointCalled, metrics.StatusCode, metrics.Data is null ? 0 : 1, metrics.Warnings));

        var insider = await _stockFit.GetInsiderTradesAsync(ticker);
        results.Add(BuildProbe("insider", insider.EndpointCalled, insider.StatusCode, insider.Data?.Count ?? 0, insider.Warnings));

        var inst = await _stockFit.GetInstitutionalOwnershipAsync(ticker);
        results.Add(BuildProbe("institutional", inst.EndpointCalled, inst.StatusCode, inst.Data?.Count ?? 0, inst.Warnings));

        return Ok(new
        {
            configured = true,
            baseUrl = _stockFit.BaseUrl,
            ticker,
            endpointsTested = results,
            warnings,
            samples = new
            {
                latestNews = news.Data?.Take(3),
                latestFilings = filings.Data?.Take(3),
                nextEarnings = earnings.Data?.FirstOrDefault(),
                metrics = metrics.Data,
                latestInsiderTrades = insider.Data?.Take(3),
                topInstitutionalHoldings = inst.Data?.Take(3),
            },
            note = "No API key exposed. STOCKFIT_API_KEY is only present on the .NET server as an env var.",
        });
    }

    private static object BuildProbe(string endpointName, string? url, int? status, int records, List<string> warnings)
        => new
        {
            endpoint = endpointName,
            url = MaskKey(url),
            statusCode = status,
            recordsReturned = records,
            warnings,
        };

    /// <summary>
    /// Guardrail: even though the client never puts the raw key in the URL
    /// when auth mode is "header" or "bearer", strip anything that looks
    /// like a query-string apikey= before echoing the URL to the caller.
    /// </summary>
    private static string? MaskKey(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        return System.Text.RegularExpressions.Regex.Replace(url,
            @"([?&](apikey|api_key|token|key)=)[^&]*", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
