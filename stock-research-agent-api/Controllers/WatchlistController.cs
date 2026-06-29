using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.Watchlist;
using StockResearchAgent.Api.Services.UniverseDiscovery;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// GET endpoints for watchlist data (no auth required for dev).
/// POST endpoints for triggering watchlist generation (job-secret protected).
/// </summary>
[ApiController]
[Route("api/watchlist")]
public class WatchlistController : ControllerBase
{
    private readonly WatchlistRepository _repo;
    private readonly DynamicWatchlistService _watchlistService;
    private readonly IConfiguration _config;
    private readonly ILogger<WatchlistController> _logger;

    public WatchlistController(
        WatchlistRepository repo,
        DynamicWatchlistService watchlistService,
        IConfiguration config,
        ILogger<WatchlistController> logger)
    {
        _repo = repo;
        _watchlistService = watchlistService;
        _config = config;
        _logger = logger;
    }

    /// <summary>GET /api/watchlist — full watchlist grouped by status</summary>
    [HttpGet]
    public async Task<IActionResult> GetWatchlist()
    {
        var active = await _repo.GetWatchlistByStatusAsync(WatchlistStatus.Active);
        var reviewNeeded = await _repo.GetWatchlistByStatusAsync(WatchlistStatus.ReviewNeeded);
        var swapCandidates = await _repo.GetWatchlistByStatusAsync(WatchlistStatus.SwapCandidate);
        var archived = await _repo.GetWatchlistByStatusAsync(WatchlistStatus.Archived);

        return Ok(new
        {
            active = new { count = active.Count, items = active },
            reviewNeeded = new { count = reviewNeeded.Count, items = reviewNeeded },
            swapCandidates = new { count = swapCandidates.Count, items = swapCandidates },
            archived = new { count = archived.Count, items = archived },
        });
    }

    /// <summary>GET /api/watchlist/active — just the active items</summary>
    [HttpGet("active")]
    public async Task<IActionResult> GetActiveWatchlist()
    {
        var items = await _repo.GetActiveWatchlistAsync();
        return Ok(new { count = items.Count, items });
    }

    /// <summary>GET /api/watchlist/changes — recent change history</summary>
    [HttpGet("changes")]
    public async Task<IActionResult> GetChangeHistory([FromQuery] int limit = 50)
    {
        var changes = await _repo.GetRecentChangeLogsAsync(limit);
        return Ok(new { count = changes.Count, changes });
    }

    /// <summary>GET /api/watchlist/candidates — recent scored candidates</summary>
    [HttpGet("candidates")]
    public async Task<IActionResult> GetCandidates([FromQuery] int limit = 30)
    {
        var candidates = await _repo.GetRecentCandidatesAsync(limit);
        return Ok(new { count = candidates.Count, candidates });
    }
}

/// <summary>
/// Weekly research job that builds the dynamic watchlist.
/// Protected by x-job-secret header.
/// </summary>
[ApiController]
[Route("api/jobs")]
public class WatchlistJobController : ControllerBase
{
    private readonly DynamicWatchlistService _watchlistService;
    private readonly UniverseDiscoveryService _universeDiscovery;
    private readonly JobStatusTracker _jobStatus;
    private readonly IConfiguration _config;
    private readonly ILogger<WatchlistJobController> _logger;

    public WatchlistJobController(
        DynamicWatchlistService watchlistService,
        UniverseDiscoveryService universeDiscovery,
        JobStatusTracker jobStatus,
        IConfiguration config,
        ILogger<WatchlistJobController> logger)
    {
        _watchlistService = watchlistService;
        _universeDiscovery = universeDiscovery;
        _jobStatus = jobStatus;
        _config = config;
        _logger = logger;
    }

    /// <summary>GET /api/jobs/status — check status of background jobs</summary>
    [HttpGet("status")]
    public IActionResult GetJobStatuses()
    {
        var statuses = _jobStatus.GetAllStatuses();
        return Ok(statuses);
    }

    /// <summary>GET /api/jobs/status/{jobName} — check status of a specific job</summary>
    [HttpGet("status/{jobName}")]
    public IActionResult GetJobStatus(string jobName)
    {
        var status = _jobStatus.GetStatus(jobName);
        if (status is null) return Ok(new { state = "idle" });
        return Ok(status);
    }

    private bool ValidateJobSecret()
    {
        var expected = _config["JOB_RUN_SECRET"];
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var provided = Request.Headers["x-job-secret"].FirstOrDefault();
        return !string.IsNullOrEmpty(provided) && provided == expected;
    }

    /// <summary>
    /// POST /api/jobs/run-weekly-research
    /// Scans the universe, scores candidates, builds the dynamic watchlist.
    /// </summary>
    [HttpPost("run-weekly-research")]
    public async Task<IActionResult> RunWeeklyResearch([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        _logger.LogInformation("[jobs] Weekly research triggered by {Trigger}", trigger?.Trigger ?? "unknown");
        _jobStatus.MarkStarted("run-weekly-research");

        try
        {
            // Discover universe from news + earnings + market data
            var discovery = await _universeDiscovery.DiscoverUniverseAsync();
            var universe = discovery.Universe.Select(t => t.Ticker).ToArray();
            _logger.LogInformation("[jobs] Discovered {Count} tickers: [{Tickers}]", universe.Length, string.Join(", ", universe));

            if (universe.Length == 0)
            {
                _jobStatus.MarkCompleted("run-weekly-research", "0 tickers discovered");
                return Ok(new
                {
                    runId = (string?)null,
                    activeWatchlistCount = 0,
                    added = Array.Empty<object>(),
                    warnings = new[] { "Universe discovery returned 0 tickers. Check RSS feeds and Finnhub API key." },
                    discovery = new
                    {
                        tickersDiscovered = 0,
                        rssArticlesScanned = discovery.RssArticlesScanned,
                        earningsFound = discovery.EarningsFound,
                        errors = discovery.Errors,
                    },
                });
            }

            // Pass discovery context so scoring can use news/earnings data
            var discoveryContext = discovery.Universe.Select(t =>
                new DynamicWatchlistService.TickerDiscoveryContext(
                    t.Ticker, t.DiscoveryScore, t.HasUpcomingEarnings, t.EarningsDate,
                    t.RssMentions, t.FinnhubMentions, t.TopReason)).ToList();

            var result = await _watchlistService.BuildDynamicWatchlistAsync(universe, discoveryContext: discoveryContext);

            _jobStatus.MarkCompleted("run-weekly-research",
                $"{result.ActiveWatchlistCount} active, {result.Added.Count} added, {result.ArchivedItems.Count} archived");

            return Ok(new
            {
                runId = result.RunId,
                activeWatchlistCount = result.ActiveWatchlistCount,
                added = result.Added.Select(i => new { i.Ticker, i.TotalScore, i.WatchReason }),
                kept = result.Kept.Select(i => new { i.Ticker, i.TotalScore }),
                reviewNeeded = result.ReviewNeeded.Select(i => new { i.Ticker, i.TotalScore, i.SwapReason }),
                swapCandidates = result.SwapCandidates.Select(i => new { i.Ticker, i.TotalScore, i.SwapReason }),
                archived = result.ArchivedItems.Select(i => new { i.Ticker, i.SwapReason }),
                warnings = result.Warnings,
                dataQualitySummary = result.DataQuality,
                discovery = new
                {
                    tickersDiscovered = discovery.Universe.Count,
                    rssArticlesScanned = discovery.RssArticlesScanned,
                    earningsFound = discovery.EarningsFound,
                    topTickers = discovery.Universe.Take(10).Select(t => new { t.Ticker, t.DiscoveryScore, t.TopReason }),
                    errors = discovery.Errors,
                },
                persisted = result.Persisted,
            });
        }
        catch (Exception ex)
        {
            _jobStatus.MarkFailed("run-weekly-research", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// POST /api/jobs/run-watchlist-refresh
    /// Same as weekly research but can be triggered manually for testing.
    /// </summary>
    [HttpPost("run-watchlist-refresh")]
    public async Task<IActionResult> RunWatchlistRefresh([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        _logger.LogInformation("[jobs] Watchlist refresh triggered by {Trigger}", trigger?.Trigger ?? "unknown");
        _jobStatus.MarkStarted("run-watchlist-refresh");

        try
        {
            var discovery = await _universeDiscovery.DiscoverUniverseAsync();
            var universe = discovery.Universe.Select(t => t.Ticker).ToArray();
            _logger.LogInformation("[jobs] Refresh using {Count} discovered tickers", universe.Length);

            var discoveryContext = discovery.Universe.Select(t =>
                new DynamicWatchlistService.TickerDiscoveryContext(
                    t.Ticker, t.DiscoveryScore, t.HasUpcomingEarnings, t.EarningsDate,
                    t.RssMentions, t.FinnhubMentions, t.TopReason)).ToList();

            var result = await _watchlistService.BuildDynamicWatchlistAsync(universe, discoveryContext: discoveryContext);

            _jobStatus.MarkCompleted("run-watchlist-refresh",
                $"{result.ActiveWatchlistCount} active, {result.Added.Count} added");

            return Ok(result);
        }
        catch (Exception ex)
        {
            _jobStatus.MarkFailed("run-watchlist-refresh", ex.Message);
            throw;
        }
    }
}
