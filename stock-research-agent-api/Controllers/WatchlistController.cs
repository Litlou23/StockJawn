using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.Watchlist;
using StockResearchAgent.Api.Services.UniverseDiscovery;
using StockResearchAgent.Api.Services.ResearchEngine;
using StockResearchAgent.Api.Services.ResearchSignals;

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
    private readonly ResearchSignalService _signalService;
    private readonly JobStatusTracker _jobStatus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<WatchlistJobController> _logger;

    public WatchlistJobController(
        DynamicWatchlistService watchlistService,
        UniverseDiscoveryService universeDiscovery,
        ResearchSignalService signalService,
        JobStatusTracker jobStatus,
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<WatchlistJobController> logger)
    {
        _watchlistService = watchlistService;
        _universeDiscovery = universeDiscovery;
        _signalService = signalService;
        _jobStatus = jobStatus;
        _scopeFactory = scopeFactory;
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
    /// Now runs in background (fire-and-forget) to avoid HTTP timeouts.
    /// </summary>
    [HttpPost("run-weekly-research")]
    public IActionResult RunWeeklyResearch([FromBody] JobTriggerRequest? trigger)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        _logger.LogInformation("[jobs] Weekly research triggered by {Trigger} — running in background", trigger?.Trigger ?? "unknown");
        _jobStatus.MarkStarted("run-weekly-research");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var universeDiscovery = scope.ServiceProvider.GetRequiredService<UniverseDiscoveryService>();
                var signalService = scope.ServiceProvider.GetRequiredService<ResearchSignalService>();
                var watchlistService = scope.ServiceProvider.GetRequiredService<DynamicWatchlistService>();

                // Discover universe from news + earnings + market data
                var discovery = await universeDiscovery.DiscoverUniverseAsync();
                var universe = discovery.Universe.Select(t => t.Ticker).ToArray();
                _logger.LogInformation("[jobs] Discovered {Count} tickers: [{Tickers}]", universe.Length, string.Join(", ", universe));

                if (universe.Length == 0)
                {
                    _jobStatus.MarkCompleted("run-weekly-research", "0 tickers discovered");
                }
                else
                {
                    // Collect research signals (congress, etc.) before scoring
                    var signalResult = await signalService.CollectAllSignalsAsync();
                    _logger.LogInformation("[jobs] Research signals: {Persisted} persisted, {Expired} expired, {Errors} errors",
                        signalResult.Persisted, signalResult.Expired, signalResult.Errors.Count);

                    // Pass discovery context so scoring can use news/earnings data
                    var discoveryContext = discovery.Universe.Select(t =>
                        new DynamicWatchlistService.TickerDiscoveryContext(
                            t.Ticker, t.DiscoveryScore, t.HasUpcomingEarnings, t.EarningsDate,
                            t.RssMentions, t.FinnhubMentions, t.TopReason)).ToList();

                    var result = await watchlistService.BuildDynamicWatchlistAsync(universe, discoveryContext: discoveryContext);

                    _jobStatus.MarkCompleted("run-weekly-research",
                        $"{result.ActiveWatchlistCount} active, {result.Added.Count} added, {result.ArchivedItems.Count} archived");

                    _logger.LogInformation("[jobs] Weekly research completed: {Active} active, {Added} added, {Archived} archived",
                        result.ActiveWatchlistCount, result.Added.Count, result.ArchivedItems.Count);
                }

                // ── Chain: trigger morning scan after weekly research ────────
                // On Mondays the morning scan cron is disabled (Tue-Fri only).
                // Instead we chain it here so it always runs on a fresh watchlist
                // and never collides with weekly research.
                _logger.LogInformation("[jobs] Weekly research done — chaining morning scan");
                _jobStatus.MarkStarted("morning_scan");
                try
                {
                    var orchestrator = scope.ServiceProvider.GetRequiredService<DynamicPickOrchestrator>();
                    var scanResult = await orchestrator.RunDynamicMorningPicksAsync();

                    if (scanResult.Errors.Count > 0)
                        _jobStatus.MarkFailed("morning_scan", string.Join("; ", scanResult.Errors.Take(5)));
                    else
                        _jobStatus.MarkCompleted("morning_scan", scanResult.Report);

                    _logger.LogInformation("[jobs] Chained morning scan completed: {Predictions} predictions",
                        scanResult.PredictionsGenerated);
                }
                catch (Exception scanEx)
                {
                    _logger.LogError(scanEx, "[jobs] Chained morning scan failed");
                    _jobStatus.MarkFailed("morning_scan", scanEx.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[jobs] Weekly research failed");
                _jobStatus.MarkFailed("run-weekly-research", ex.Message);
            }
        });

        return Accepted(new
        {
            ok = true,
            accepted = true,
            runType = "weekly_research",
            status = "running",
            message = "Weekly research accepted — running in background. Poll /api/jobs/status for progress.",
        });
    }
}
