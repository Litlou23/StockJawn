using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.Backtesting;
using StockResearchAgent.Api.Services.ResearchUniverse;

namespace StockResearchAgent.Api.Controllers;

/// <summary>
/// Backtest engine endpoints — data loading, backtest runs, results.
/// Long-running operations use fire-and-forget + JobStatusTracker.
/// </summary>
[ApiController]
[Route("api/backtest")]
public class BacktestController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JobStatusTracker _jobs;
    private readonly ILogger<BacktestController> _logger;

    public BacktestController(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        JobStatusTracker jobs,
        ILogger<BacktestController> logger)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _jobs = jobs;
        _logger = logger;
    }

    // ── Data Loading ────────────────────────────────────────────

    /// <summary>
    /// Start downloading historical candles for all research universe tickers.
    /// Fire-and-forget — returns 202 immediately, poll /api/backtest/download-status.
    /// </summary>
    [HttpPost("download-history")]
    public IActionResult DownloadHistory(
        [FromQuery] int months = 6,
        [FromQuery] string? tickers = null)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        var jobName = "backtest-download-history";

        if (_jobs.GetStatus(jobName)?.State == "running")
            return Conflict(new { error = "Download already in progress" });

        _jobs.MarkStarted(jobName);

        var endDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = endDate.AddMonths(-months);
        var specificTickers = tickers?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var loader = scope.ServiceProvider.GetRequiredService<HistoricalDataLoader>();
            var universeRepo = scope.ServiceProvider.GetRequiredService<IResearchUniverseRepository>();

            try
            {
                // Get ticker list — either specific tickers or full universe
                IReadOnlyList<string> tickerList;
                if (specificTickers != null && specificTickers.Length > 0)
                {
                    tickerList = specificTickers;
                }
                else
                {
                    var activeSet = await universeRepo.GetActiveTickerSetAsync();
                    tickerList = activeSet.ToList();
                }

                _logger.LogInformation("[backtest] Starting historical download for {Count} tickers ({Start} → {End})",
                    tickerList.Count, startDate, endDate);

                var result = await loader.LoadHistoryAsync(tickerList, startDate, endDate);

                _jobs.MarkCompleted(jobName,
                    $"Loaded {result.Loaded} tickers, {result.CandlesInserted} candles. " +
                    $"Skipped {result.Skipped}, failed {result.Failed}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[backtest] Historical download failed");
                _jobs.MarkFailed(jobName, ex.Message);
            }
        });

        return Accepted(new
        {
            message = "Historical data download started",
            startDate = startDate.ToString("yyyy-MM-dd"),
            endDate = endDate.ToString("yyyy-MM-dd"),
            months,
            tickerSource = specificTickers != null ? "specified" : "research_universe",
            statusUrl = "/api/backtest/download-status"
        });
    }

    /// <summary>Check status of the historical data download job.</summary>
    [HttpGet("download-status")]
    public IActionResult DownloadStatus()
    {
        var status = _jobs.GetStatus("backtest-download-history");
        if (status is null)
            return Ok(new { state = "not_started" });
        return Ok(status);
    }

    /// <summary>Get summary of stored historical data.</summary>
    [HttpGet("data-summary")]
    public async Task<IActionResult> DataSummary()
    {
        using var scope = _scopeFactory.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<HistoricalDataLoader>();

        var counts = await loader.GetStoredTickerCountsAsync();
        return Ok(new
        {
            tickersWithData = counts.Count,
            totalCandles = counts.Values.Sum(),
            avgCandlesPerTicker = counts.Count > 0 ? counts.Values.Average() : 0,
            sampleTickers = counts.OrderByDescending(kv => kv.Value).Take(10)
                .Select(kv => new { ticker = kv.Key, candles = kv.Value })
        });
    }

    /// <summary>Get candles for a specific ticker.</summary>
    [HttpGet("candles/{ticker}")]
    public async Task<IActionResult> GetCandles(
        string ticker,
        [FromQuery] string? startDate = null,
        [FromQuery] string? endDate = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<HistoricalDataLoader>();

        var start = startDate != null ? DateOnly.Parse(startDate) : DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-6));
        var end = endDate != null ? DateOnly.Parse(endDate) : DateOnly.FromDateTime(DateTime.UtcNow);

        var candles = await loader.GetCandlesAsync(ticker, start, end);
        return Ok(new { ticker, count = candles.Count, candles });
    }

    // ── Backtest Runs ─────────────────────────────────────────────

    /// <summary>
    /// Start a backtest run. Fire-and-forget — returns 202, poll /api/backtest/run-status.
    /// </summary>
    [HttpPost("run")]
    public IActionResult StartRun([FromBody] BacktestRunRequest request)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        var jobName = "backtest-run";

        if (_jobs.GetStatus(jobName)?.State == "running")
            return Conflict(new { error = "Backtest already in progress" });

        _jobs.MarkStarted(jobName);

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<BacktestEngine>();
            var universeRepo = scope.ServiceProvider.GetRequiredService<IResearchUniverseRepository>();

            try
            {
                var startDate = DateOnly.Parse(request.StartDate);
                var endDate = DateOnly.Parse(request.EndDate);

                // Get ticker list — full universe by default; caller can cap
                // via BacktestConfig.MaxTickersPerDay (see request.MaxTickers).
                List<string> tickers;
                if (request.Tickers is { Count: > 0 })
                {
                    tickers = request.Tickers;
                }
                else
                {
                    var activeSet = await universeRepo.GetActiveTickerSetAsync();
                    tickers = activeSet.ToList();
                }

                var config = new BacktestConfig
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    Tickers = tickers,
                    ParameterOverrides = request.ParameterOverrides,
                    MinConfidence = request.MinConfidence,
                    MaxTickersPerDay = request.MaxTickersPerDay,
                    MetaProbabilityThreshold = request.MetaProbabilityThreshold,
                };

                var progress = new Progress<string>(msg => _jobs.UpdateProgress(jobName, msg));

                var result = await engine.RunAsync(config, progress);

                if (result.Error is not null)
                    _jobs.MarkFailed(jobName, result.Error);
                else
                    _jobs.MarkCompleted(jobName,
                        $"Run {result.RunId}: {result.Metrics?.Summary ?? "completed"}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[backtest] Run failed");
                _jobs.MarkFailed(jobName, ex.Message);
            }
        });

        return Accepted(new
        {
            message = "Backtest run started",
            statusUrl = "/api/backtest/run-status",
        });
    }

    /// <summary>Check status of backtest run.</summary>
    [HttpGet("run-status")]
    public IActionResult RunStatus()
    {
        var status = _jobs.GetStatus("backtest-run");
        if (status is null)
            return Ok(new { state = "not_started" });
        return Ok(status);
    }

    /// <summary>Get all backtest runs.</summary>
    [HttpGet("runs")]
    public async Task<IActionResult> ListRuns()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Services.Supabase.SupabaseClient>();

        var rows = await db.SelectAsync("backtest_runs",
            order: "created_at.desc", limit: 50);

        return Ok(rows);
    }

    /// <summary>Get trades for a specific backtest run.</summary>
    [HttpGet("runs/{runId}/trades")]
    public async Task<IActionResult> GetRunTrades(string runId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Services.Supabase.SupabaseClient>();

        var rows = await db.SelectAsync("backtest_trades",
            $"run_id=eq.{runId}",
            order: "entry_date.asc", limit: 500);

        return Ok(rows);
    }

    /// <summary>Single-run detail (headline row from backtest_runs).</summary>
    [HttpGet("runs/{runId}")]
    public async Task<IActionResult> GetRunDetail(string runId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Services.Supabase.SupabaseClient>();

        var row = await db.SelectSingleAsync("backtest_runs", $"id=eq.{runId}");
        if (row is null) return NotFound(new { error = "Backtest run not found" });
        return Ok(row);
    }

    // ── Parameter Sweeps (Phase 5) ───────────────────────────────

    /// <summary>
    /// Start a parameter sweep. Fire-and-forget — returns 202 with the
    /// sweep id, poll /api/backtest/sweep-status or /api/backtest/sweeps/{id}.
    /// </summary>
    [HttpPost("sweep")]
    public IActionResult StartSweep([FromBody] BacktestSweepRequest request)
    {
        if (!ValidateJobSecret())
            return Unauthorized(new { error = "Invalid or missing x-job-secret header" });

        var jobName = "backtest-sweep";
        if (_jobs.GetStatus(jobName)?.State == "running")
            return Conflict(new { error = "Parameter sweep already in progress" });

        if (request.ParameterSpace is null || request.ParameterSpace.Count == 0)
            return BadRequest(new { error = "parameterSpace is required and must have at least one entry" });

        _jobs.MarkStarted(jobName);

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sweeper = scope.ServiceProvider.GetRequiredService<ParameterSweepEngine>();
            var universeRepo = scope.ServiceProvider.GetRequiredService<IResearchUniverseRepository>();

            try
            {
                var startDate = DateOnly.Parse(request.StartDate);
                var endDate = DateOnly.Parse(request.EndDate);

                List<string> tickers;
                if (request.Tickers is { Count: > 0 })
                    tickers = request.Tickers;
                else
                {
                    var activeSet = await universeRepo.GetActiveTickerSetAsync();
                    tickers = activeSet.ToList();
                }

                var config = new SweepConfig
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    Tickers = tickers,
                    ParameterSpace = request.ParameterSpace,
                    MinConfidence = request.MinConfidence,
                    MaxTickersPerDay = request.MaxTickersPerDay,
                    StartingBalance = request.StartingBalance ?? 1000,
                    UseEnsemble = request.UseEnsemble ?? false,
                    UseSetupHistory = request.UseSetupHistory ?? true,
                    MetaProbabilityThreshold = request.MetaProbabilityThreshold,
                    RankBy = request.RankBy,
                };

                var progress = new Progress<string>(msg => _jobs.UpdateProgress(jobName, msg));
                var result = await sweeper.RunSweepAsync(config, progress);

                if (result.Error is not null)
                    _jobs.MarkFailed(jobName, result.Error);
                else
                    _jobs.MarkCompleted(jobName,
                        $"Sweep {result.SweepId}: {result.RunsCompleted} runs, best expectancy " +
                        $"{result.Best?.Expectancy:+0.00;-0.00}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[backtest] Sweep failed");
                _jobs.MarkFailed(jobName, ex.Message);
            }
        });

        return Accepted(new
        {
            message = "Parameter sweep started",
            statusUrl = "/api/backtest/sweep-status",
        });
    }

    /// <summary>Check status of the current parameter sweep.</summary>
    [HttpGet("sweep-status")]
    public IActionResult SweepStatus()
    {
        var status = _jobs.GetStatus("backtest-sweep");
        if (status is null) return Ok(new { state = "not_started" });
        return Ok(status);
    }

    /// <summary>List recent parameter sweeps (most recent first).</summary>
    [HttpGet("sweeps")]
    public async Task<IActionResult> ListSweeps()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Services.Supabase.SupabaseClient>();
        var rows = await db.SelectAsync("backtest_sweeps",
            order: "created_at.desc", limit: 30);
        return Ok(rows);
    }

    /// <summary>Single-sweep detail (headline + ranking).</summary>
    [HttpGet("sweeps/{sweepId}")]
    public async Task<IActionResult> GetSweepDetail(string sweepId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Services.Supabase.SupabaseClient>();
        var row = await db.SelectSingleAsync("backtest_sweeps", $"id=eq.{sweepId}");
        if (row is null) return NotFound(new { error = "Sweep not found" });
        return Ok(row);
    }

    /// <summary>All child runs of a sweep (joined via sweep_id).</summary>
    [HttpGet("sweeps/{sweepId}/runs")]
    public async Task<IActionResult> GetSweepRuns(string sweepId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Services.Supabase.SupabaseClient>();
        var rows = await db.SelectAsync("backtest_runs",
            $"sweep_id=eq.{sweepId}",
            order: "created_at.asc",
            limit: 500);
        return Ok(rows);
    }

    // ── Dev hook (GET-only sweep trigger) ────────────────────────
    //
    // Purpose: my Claude sandbox can GET this API but cannot POST to it
    // (outbound proxy 403s POST to azurewebsites.net). This endpoint gives
    // GET access to a small library of predefined sweeps so I can drive the
    // backtest from the sandbox.
    //
    // Not a security hole per se — still requires the same JOB_RUN_SECRET
    // token, just passed as ?token= instead of an x-job-secret header. Only
    // predefined presets can be started; arbitrary parameter spaces cannot.

    /// <summary>
    /// Start a preset sweep via GET. Requires ?token=&lt;JOB_RUN_SECRET&gt;.
    /// Presets:
    ///   quick10       — 10 blue chips, 8 combos of exit-risk params
    ///   full12        — full universe, 12 combos of exit-risk params
    ///   regime_tune   — 10 blue chips, 9 combos varying the regime gate
    ///                   (ADX floor + realized-vol upper band). Uses the
    ///                   winning scoring params (conf=45, tp=0.06, sl=0.02).
    /// </summary>
    [HttpGet("dev/start-sweep")]
    public IActionResult DevStartSweep(
        [FromQuery] string? token,
        [FromQuery] string? preset,
        [FromQuery] string? startDate,
        [FromQuery] string? endDate)
    {
        var expected = _configuration["JOB_RUN_SECRET"];
        if (string.IsNullOrWhiteSpace(expected) || token != expected)
            return Unauthorized(new { error = "Invalid or missing ?token=" });

        // Resolve preset → BacktestSweepRequest body
        var start = startDate ?? DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-3).ToString("yyyy-MM-dd");
        var end = endDate ?? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        BacktestSweepRequest req = preset switch
        {
            "quick10" => new BacktestSweepRequest
            {
                StartDate = start,
                EndDate = end,
                Tickers = new List<string> { "SPY", "QQQ", "AAPL", "MSFT", "NVDA", "META", "GOOGL", "AMZN", "TSLA", "AMD" },
                ParameterSpace = new Dictionary<string, double[]>
                {
                    ["min_confidence_threshold"] = new[] { 35.0, 45.0 },
                    ["risk_tp_swing"] = new[] { 0.04, 0.06 },
                    ["risk_sl_swing"] = new[] { 0.02, 0.03 },
                },
                UseSetupHistory = true,
            },
            "full12" => new BacktestSweepRequest
            {
                StartDate = start,
                EndDate = end,
                Tickers = null,
                ParameterSpace = new Dictionary<string, double[]>
                {
                    ["min_confidence_threshold"] = new[] { 30.0, 40.0, 50.0 },
                    ["risk_tp_swing"] = new[] { 0.04, 0.06 },
                    ["risk_sl_swing"] = new[] { 0.02, 0.03 },
                },
                UseSetupHistory = true,
            },
            "regime_tune" => new BacktestSweepRequest
            {
                // Tune the trend-quality gate thresholds — keep the winning
                // scoring params fixed and vary ADX floor + realized-vol band.
                // 3 × 3 = 9 combinations. Small enough to complete quickly on
                // 10 blue chips; big enough to find the sweet spot.
                StartDate = start,
                EndDate = end,
                Tickers = new List<string> { "SPY", "QQQ", "AAPL", "MSFT", "NVDA", "META", "GOOGL", "AMZN", "TSLA", "AMD" },
                ParameterSpace = new Dictionary<string, double[]>
                {
                    ["regime_adx_floor"] = new[] { 15.0, 20.0, 25.0 },
                    ["regime_rv_high"] = new[] { 1.2, 1.3, 1.4 },
                    ["min_confidence_threshold"] = new[] { 45.0 },
                    ["risk_tp_swing"] = new[] { 0.06 },
                    ["risk_sl_swing"] = new[] { 0.02 },
                },
                UseSetupHistory = true,
            },
            _ => throw new ArgumentException("unknown preset"),
        };

        // Delegate to the same logic as the POST endpoint via internal call.
        // Copy of StartSweep body — kept small so this stays a "single dev switch".
        var jobName = "backtest-sweep";
        if (_jobs.GetStatus(jobName)?.State == "running")
            return Conflict(new { error = "Parameter sweep already in progress" });

        _jobs.MarkStarted(jobName);
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sweeper = scope.ServiceProvider.GetRequiredService<ParameterSweepEngine>();
            var universeRepo = scope.ServiceProvider.GetRequiredService<IResearchUniverseRepository>();
            try
            {
                var startD = DateOnly.Parse(req.StartDate);
                var endD = DateOnly.Parse(req.EndDate);
                List<string> tickers = req.Tickers is { Count: > 0 }
                    ? req.Tickers
                    : (await universeRepo.GetActiveTickerSetAsync()).ToList();

                var config = new SweepConfig
                {
                    StartDate = startD,
                    EndDate = endD,
                    Tickers = tickers,
                    ParameterSpace = req.ParameterSpace,
                    MinConfidence = req.MinConfidence,
                    MaxTickersPerDay = req.MaxTickersPerDay,
                    StartingBalance = req.StartingBalance ?? 1000,
                    UseEnsemble = req.UseEnsemble ?? false,
                    UseSetupHistory = req.UseSetupHistory ?? true,
                };

                var progress = new Progress<string>(msg => _jobs.UpdateProgress(jobName, msg));
                var result = await sweeper.RunSweepAsync(config, progress);

                if (result.Error is not null)
                    _jobs.MarkFailed(jobName, result.Error);
                else
                    _jobs.MarkCompleted(jobName,
                        $"Sweep {result.SweepId}: {result.RunsCompleted} runs, best expectancy {result.Best?.Expectancy:+0.00;-0.00}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[backtest] Dev sweep failed");
                _jobs.MarkFailed(jobName, ex.Message);
            }
        });

        return Ok(new
        {
            message = "Preset sweep started via GET",
            preset,
            startDate = req.StartDate,
            endDate = req.EndDate,
            statusUrl = "/api/backtest/sweep-status",
        });
    }

    // ── Helpers ──────────────────────────────────────────────────

    private bool ValidateJobSecret()
    {
        var expected = _configuration["JOB_RUN_SECRET"];
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var provided = Request.Headers["x-job-secret"].FirstOrDefault();
        return !string.IsNullOrEmpty(provided) && provided == expected;
    }
}

/// <summary>Request body for POST /api/backtest/run.</summary>
public class BacktestRunRequest
{
    public string StartDate { get; init; } = "";
    public string EndDate { get; init; } = "";
    public List<string>? Tickers { get; init; }
    public Dictionary<string, double>? ParameterOverrides { get; init; }
    public int? MinConfidence { get; init; }
    /// <summary>Cap tickers scored per day — null = engine default (500).</summary>
    public int? MaxTickersPerDay { get; init; }
    /// <summary>Meta-labeler probability floor (0.0–1.0). Null = advisory only.</summary>
    public double? MetaProbabilityThreshold { get; init; }
}

/// <summary>
/// Request body for POST /api/backtest/sweep. ParameterSpace is a
/// dictionary of override-key → array-of-values which is expanded into
/// the Cartesian product of combinations to test.
///
/// Example:
///   { "min_confidence": [30, 35, 40],
///     "rr_target":      [1.5, 2.0, 2.5],
///     "trail_pct":      [1.5, 2.0] }
/// runs 3 × 3 × 2 = 18 combinations.
/// </summary>
public class BacktestSweepRequest
{
    public string StartDate { get; init; } = "";
    public string EndDate { get; init; } = "";
    public List<string>? Tickers { get; init; }
    public Dictionary<string, double[]> ParameterSpace { get; init; } = new();
    public int? MinConfidence { get; init; }
    public int? MaxTickersPerDay { get; init; }
    public double? StartingBalance { get; init; }
    public bool? UseEnsemble { get; init; }
    public bool? UseSetupHistory { get; init; }
    /// <summary>Baseline meta-labeler threshold for every child run; can be overridden per-combo via parameter_space["meta_probability_threshold"].</summary>
    public double? MetaProbabilityThreshold { get; init; }
    /// <summary>How to rank child runs: "pnl" (default), "expectancy", "sharpe", or "profit_factor".</summary>
    public string? RankBy { get; init; }
}
