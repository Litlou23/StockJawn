using StockResearchAgent.Api.Dashboard;
using StockResearchAgent.Api.Diagnostics;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.ResearchEngine;
using StockResearchAgent.Api.Services.Watchlist;
using StockResearchAgent.Api.Services.UniverseDiscovery;
using StockResearchAgent.Api.Services.OptionsLab;
using StockResearchAgent.Api.Services.OptionsData;

// =====================================================================
// TOP-LEVEL TRY/CATCH — catches fatal startup exceptions and writes
// them to the bootstrap log before the process exits.
// =====================================================================
try
{
    BootstrapLogger.Init(); // BOOT 001 + 002 inside

    // CORS-allowed frontend origins. Reads FRONTEND_ORIGINS (comma-separated)
    // from configuration so dev and Azure App Service can differ. Falls back
    // to localhost:3000 for local dev. The dashboard displays the joined list.
    var frontendOriginsRaw =
        builder.Configuration["FRONTEND_ORIGINS"]
        ?? builder.Configuration["FRONTEND_ORIGIN"]
        ?? "http://localhost:3000";

    var frontendOrigins = frontendOriginsRaw
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToArray();

    string FrontendOrigin = string.Join(", ", frontendOrigins);

    BootstrapLogger.Log("BOOT 003", $"Creating builder...");
    var builder = WebApplication.CreateBuilder(args);

    BootstrapLogger.Log("BOOT 003", $"Environment: {builder.Environment.EnvironmentName}");
    BootstrapLogger.Log("BOOT 004", $"Content root: {builder.Environment.ContentRootPath}");
    BootstrapLogger.Log("BOOT 005", $"Application: {builder.Environment.ApplicationName}");

    // ---- Safe config flag check (values never logged) ----
    BootstrapLogger.Log("BOOT 006", "Checking safe config flags...");
    var tempConfig = builder.Configuration;
    BootstrapLogger.Log("BOOT 006", $"  TwelveDataConfigured: {!string.IsNullOrWhiteSpace(tempConfig["TWELVE_DATA_API_KEY"])}");
    BootstrapLogger.Log("BOOT 006", $"  FinnhubConfigured: {!string.IsNullOrWhiteSpace(tempConfig["FINNHUB_API_KEY"])}");
    BootstrapLogger.Log("BOOT 006", $"  OpenAiConfigured: {!string.IsNullOrWhiteSpace(tempConfig["OPENAI_API_KEY"])}");
    BootstrapLogger.Log("BOOT 006", $"  SupabaseUrlConfigured: {!string.IsNullOrWhiteSpace(tempConfig["SUPABASE_URL"])}");
    BootstrapLogger.Log("BOOT 006", $"  SupabaseServiceKeyConfigured: {!string.IsNullOrWhiteSpace(tempConfig["SUPABASE_SERVICE_KEY"])}");
    BootstrapLogger.Log("BOOT 006", $"  JobSecretConfigured: {!string.IsNullOrWhiteSpace(tempConfig["JOB_RUN_SECRET"])}");
    BootstrapLogger.Log("BOOT 006", $"  MarketDataConfigured: {!string.IsNullOrWhiteSpace(tempConfig["MARKETDATA_TOKEN"])}");

    BootstrapLogger.Log("BOOT 007", "Builder created successfully");

    // =================================================================
    // SERVICE REGISTRATION — no external calls happen here, only DI wiring
    // =================================================================
    BootstrapLogger.Log("BOOT 008", "Services registration started...");

    builder.Services.AddControllers();
    builder.Services.AddOpenApi();

    builder.Services.AddSingleton<IOpenAiCompletionService, OpenAiCompletionService>();

    // Research engine services — Supabase, Twelve Data, prediction loop.
    builder.Services.AddSingleton<SupabaseClient>();
    builder.Services.AddSingleton<ResearchRepository>();
    builder.Services.AddSingleton<TwelveDataProvider>();
    builder.Services.AddSingleton<MarketDataService>();
    builder.Services.AddSingleton<PredictionGenerator>();
    builder.Services.AddSingleton<OutcomeEvaluator>();
    builder.Services.AddSingleton<LearningEngine>();
    builder.Services.AddSingleton<DailyReportService>();
    builder.Services.AddSingleton<DailyResearchRunService>();

    // Universe discovery services
    builder.Services.AddSingleton<RssFeedService>();
    builder.Services.AddSingleton<FinnhubProvider>();
    builder.Services.AddSingleton<UniverseDiscoveryService>();

    // Dynamic watchlist services
    builder.Services.AddSingleton<WatchlistRepository>();
    builder.Services.AddSingleton<DynamicWatchlistService>();
    builder.Services.AddSingleton<JobStatusTracker>();

    // Options Lab — theoretical simulation only
    builder.Services.AddSingleton<TheoreticalOptionsSimulator>();
    builder.Services.AddSingleton<AutomaticScenarioGenerator>();

    // Options Data — real MarketData.app integration
    builder.Services.AddSingleton<MarketDataOptionsProvider>();
    builder.Services.AddSingleton<OptionContractFilterService>();
    builder.Services.AddSingleton<OptionsDataRepository>();
    builder.Services.AddSingleton<OptionsDataService>();

    // Paper Options — enhanced flow for /paper-options page
    builder.Services.AddSingleton<PaperOptionsService>();

    // Dev-only in-memory request counter for the "/" dashboard — see
    // Dashboard/RequestMetrics.cs for why this is never trusted in production.
    builder.Services.AddSingleton<RequestMetrics>();

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("NextJsDev", policy =>
            policy.WithOrigins(frontendOrigins).AllowAnyHeader().AllowAnyMethod());
    });

    BootstrapLogger.Log("BOOT 008b", $"CORS allowed origins: {FrontendOrigin}");

    BootstrapLogger.Log("BOOT 009", "Services registration completed");

    // =================================================================
    // BUILD APP
    // =================================================================
    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseMiddleware<RequestMetricsMiddleware>();
    }

    app.UseHttpsRedirection();

    app.UseCors("NextJsDev");

    app.UseAuthorization();

    app.MapControllers();

    // =================================================================
    // ROUTE MAPPING
    // =================================================================
    BootstrapLogger.Log("BOOT 010", "Routes mapping started...");

    var apiEndpoints = new List<EndpointInfo>
    {
        new("GET", "/", "This landing/status dashboard.", false, "Humans, browser only", "This server"),
        new("GET", "/health", "JSON health check (also available at /api/health).", false, "Uptime checks, monitoring", "This server"),
        new("POST", "/api/ai/complete", "Forwards a built message list to OpenAI, returns the completion text. Holds the OpenAI API key server-side.", false, "Next.js app, server-to-server only — never from a browser", "This server"),
        new("POST", "/api/jobs/run-morning-scan", "Morning research scan: gathers market data, generates predictions.", true, "Scheduled (pg_cron -> Edge Function), x-job-secret required", "This server"),
        new("POST", "/api/jobs/run-end-of-day-review", "EOD review: evaluates open predictions against current prices.", true, "Scheduled (pg_cron -> Edge Function), x-job-secret required", "This server"),
        new("POST", "/api/jobs/run-learning-update", "Learning update: updates signal performance, adjusts weights, generates insights.", true, "Scheduled (pg_cron -> Edge Function), x-job-secret required", "This server"),
        new("GET", "/api/research/predictions", "Query predictions with optional ?status=open and ?limit=N.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/research/outcomes", "Query recent prediction outcomes with optional ?limit=N.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/research/latest-report", "Latest research run report.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/debug/research-engine", "Full research engine status: runs, predictions, outcomes, signal perf, weights, insights.", false, "Dev only", "This server"),
        new("GET", "/api/debug/market-data", "Market data provider health and sample quote.", false, "Dev only", "This server"),
        new("GET", "/api/watchlist", "Full watchlist grouped by status (active, review_needed, swap_candidate, archived).", false, "Next.js app, browser", "This server"),
        new("GET", "/api/watchlist/active", "Active watchlist items only.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/watchlist/changes", "Recent watchlist change history.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/watchlist/candidates", "Recent scored candidates from watchlist generation.", false, "Next.js app, browser", "This server"),
        new("POST", "/api/jobs/run-weekly-research", "Weekly research: scans universe, scores candidates, builds dynamic watchlist.", true, "Scheduled (pg_cron -> Edge Function), x-job-secret required", "This server"),
        new("POST", "/api/jobs/run-watchlist-refresh", "Manual watchlist refresh (same as weekly research).", true, "Manual trigger, x-job-secret required", "This server"),
        new("GET", "/api/dashboard/summary", "Aggregated dashboard data: watchlist overview, job statuses, predictions, learning, data quality.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/paper-options/predictions", "Eligible saved predictions for the Paper Options page.", false, "Next.js app, browser", "This server"),
        new("POST", "/api/paper-options/generate-candidates", "Score real option contracts for a saved prediction. Body: { predictionId, durationPreference, autoSave }.", false, "Next.js app, browser", "This server"),
        new("POST", "/api/paper-options/save-candidate", "Persist a chosen paper candidate. Body: { predictionId, candidate }.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/paper-options/open-candidates", "Currently open paper candidates.", false, "Next.js app, browser", "This server"),
        new("POST", "/api/paper-options/evaluate-candidate", "Evaluate one paper candidate against current market data. Body: { paperCandidateId }.", false, "Next.js app, browser", "This server"),
        new("POST", "/api/paper-options/evaluate-open-candidates", "Evaluate every open paper candidate.", false, "Next.js app, browser or scheduled job", "This server"),
        new("GET", "/api/paper-options/outcomes", "Recent paper option outcomes.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/paper-options/debug", "Counts, learning stats, and provider config for paper options.", false, "Dev only", "This server"),
    };

    var frontendAppEndpoints = new List<EndpointInfo>
    {
        new("POST", "/api/agent-chat", "Live chat agent — builds context, calls this API's /api/ai/complete, saves to Supabase.", false, "Browser (chat UI)", "Next.js app"),
        new("POST", "/api/jobs/intake-catalysts", "Pulls latest RSS/news catalysts into Supabase.", false, "Manual trigger", "Next.js app"),
        new("POST", "/api/jobs/score-watchlist", "Scores today's watchlist candidates.", false, "Manual trigger", "Next.js app"),
        new("POST", "/api/jobs/generate-daily-report", "Generates the daily market/report summary.", false, "Manual trigger", "Next.js app"),
        new("POST", "/api/jobs/analyze-learning", "Summarizes signal performance and learning patterns.", false, "Manual trigger", "Next.js app"),
        new("POST", "/api/jobs/run-weekly-research", "Weekly research run — scores the stock universe, saves candidates.", true, "Scheduled (pg_cron -> Supabase Edge Function), x-job-secret required", "Next.js app"),
    };

    DashboardData BuildDashboardData() => new(
        ServiceName: "Stock Research Agent API",
        Status: "Online",
        ServerTimeUtc: DateTimeOffset.UtcNow,
        Environment: app.Environment.EnvironmentName,
        Version: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        FrontendOrigin: FrontendOrigin,
        CorsConfigured: true,
        ApiEndpoints: apiEndpoints,
        FrontendAppEndpoints: frontendAppEndpoints,
        Metrics: app.Environment.IsDevelopment()
            ? app.Services.GetRequiredService<RequestMetrics>().Snapshot()
            : new MetricsSnapshot(false, 0, null, 0, 0, Array.Empty<string>()));

    app.MapGet("/", () => Results.Content(DashboardHtml.Render(BuildDashboardData()), "text/html"));

    object HealthPayload() => new
    {
        status = "ok",
        service = "Stock Research Agent API",
        timestamp = DateTimeOffset.UtcNow,
        environment = app.Environment.EnvironmentName,
        version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
    };

    app.MapGet("/health", () => Results.Json(HealthPayload()));
    app.MapGet("/api/health", () => Results.Json(HealthPayload()));

    app.MapGet("/api/connectivity", (
        SupabaseClient supabase,
        TwelveDataProvider twelveData,
        IOpenAiCompletionService openAi,
        FinnhubProvider finnhub,
        MarketDataOptionsProvider marketData) =>
    {
        return Results.Json(new
        {
            status = "ok",
            timestamp = DateTimeOffset.UtcNow,
            providers = new
            {
                supabase = new { configured = supabase.IsConfigured },
                twelveData = new { configured = twelveData.IsConfigured },
                openAi = new { configured = openAi.IsConfigured },
                finnhub = new { configured = finnhub.IsConfigured },
                marketData = new { configured = marketData.IsConfigured, provider = "MarketData.app" },
            }
        });
    });

    app.MapGet("/api/debug/startup", () =>
    {
        return Results.Json(new
        {
            status = "ok",
            service = "Stock Research Agent API",
            bootTime = BootstrapLogger.BootTime,
            uptime = (DateTimeOffset.UtcNow - BootstrapLogger.BootTime).ToString(),
            timestamp = DateTimeOffset.UtcNow,
            environment = app.Environment.EnvironmentName,
            version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            logFilePath = BootstrapLogger.LogFilePath ?? "(none — file logging unavailable)",
            diagnosticsLog = BootstrapLogger.CapturedLog,
        });
    });

    BootstrapLogger.Log("BOOT 011", "Routes mapping completed");

    // =================================================================
    // START
    // =================================================================
    BootstrapLogger.Log("BOOT 012", "App starting (calling app.Run)...");

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        BootstrapLogger.Log("BOOT 013", "App started successfully — listening for requests");
    });

    app.Run();
}
catch (Exception ex)
{
    BootstrapLogger.LogFatal(ex);

    // Also write to stderr in case the bootstrap logger file isn't reachable
    Console.Error.WriteLine($"[FATAL STARTUP ERROR] {ex}");

    // Exit with non-zero so Azure knows the app crashed
    Environment.Exit(1);
}
