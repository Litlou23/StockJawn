using StockResearchAgent.Api.Dashboard;
using StockResearchAgent.Api.Diagnostics;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.MarketIntelligence;
using StockResearchAgent.Api.Services.Knowledge;
using StockResearchAgent.Api.Services.ResearchEngine;
using StockResearchAgent.Api.Services.ResearchEngine.Evaluation;
using StockResearchAgent.Api.Services.Watchlist;
using StockResearchAgent.Api.Services.UniverseDiscovery;
using StockResearchAgent.Api.Services.OptionsLab;
using StockResearchAgent.Api.Services.OptionsData;
using StockResearchAgent.Api.Services.Providers.StockFit;
using StockResearchAgent.Api.Services.ResearchSignals;
using StockResearchAgent.Api.Services.ResearchSignals.Providers;
using StockResearchAgent.Api.Services.TradeDecision;
using StockResearchAgent.Api.Services.TradeDecision.Filters;
using StockResearchAgent.Api.Services.Portfolio;
using StockResearchAgent.Api.Services.MarketRegime;
using StockResearchAgent.Api.Services.AdaptiveLearning;
using StockResearchAgent.Api.Services.StrategyDiscovery;
using StockResearchAgent.Api.Services.CaseRepository;
using StockResearchAgent.Api.Services.KnowledgeBase;
using StockResearchAgent.Api.Services.ResearchUniverse;
using StockResearchAgent.Api.Services.Discovery;
using StockResearchAgent.Api.Services.Discovery.Providers;
using StockResearchAgent.Api.Services.Evidence;
using StockResearchAgent.Api.Services.OpportunityLearning;
using StockResearchAgent.Api.Models;

// =====================================================================
// TOP-LEVEL TRY/CATCH — catches fatal startup exceptions and writes
// them to the bootstrap log before the process exits.
// =====================================================================
try
{
    BootstrapLogger.Init(); // BOOT 001 + 002 inside

    BootstrapLogger.Log("BOOT 003", $"Creating builder...");
    var builder = WebApplication.CreateBuilder(args);

    // CORS-allowed frontend origins. Reads FRONTEND_ORIGINS (comma-separated)
    // from configuration so dev and Azure App Service can differ. Falls back
    // to localhost:3000 for local dev. The dashboard displays the joined list.
    var frontendOriginsRaw =
        builder.Configuration["FRONTEND_ORIGINS"]
        ?? builder.Configuration["FRONTEND_ORIGIN"];

    var frontendOriginDefaulted = string.IsNullOrWhiteSpace(frontendOriginsRaw);
    frontendOriginsRaw ??= "http://localhost:3000";

    var frontendOrigins = frontendOriginsRaw
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToArray();

    string FrontendOrigin = string.Join(", ", frontendOrigins);

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
    BootstrapLogger.Log("BOOT 006", $"  StockFitConfigured: {!string.IsNullOrWhiteSpace(tempConfig["STOCKFIT_API_KEY"])}");

    BootstrapLogger.Log("BOOT 007", "Builder created successfully");

    // =================================================================
    // SERVICE REGISTRATION — no external calls happen here, only DI wiring
    // =================================================================
    BootstrapLogger.Log("BOOT 008", "Services registration started...");

    builder.Services.AddControllers()
        .AddJsonOptions(opts =>
        {
            opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            opts.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        });
    builder.Services.AddOpenApi();

    builder.Services.AddSingleton<IOpenAiCompletionService, OpenAiCompletionService>();

    // Research engine services — Supabase, Twelve Data, prediction loop.
    builder.Services.AddSingleton<SupabaseClient>();
    builder.Services.AddSingleton<ResearchRepository>();
    builder.Services.AddSingleton<TwelveDataProvider>();
    builder.Services.AddSingleton<MarketDataService>();
    builder.Services.AddSingleton<IMarketFactService, MarketFactService>();
    builder.Services.AddSingleton<IMarketFeatureService, MarketFeatureService>();
    builder.Services.AddSingleton<IMarketEvidenceService, MarketEvidenceService>();
    builder.Services.AddSingleton<IMarketThesisService, MarketThesisService>();
    builder.Services.AddSingleton<IMarketIntelligencePipeline, MarketIntelligencePipeline>();
    builder.Services.AddSingleton<ITrendEvaluator, TrendEvaluator>();
    builder.Services.AddSingleton<IMomentumEvaluator, MomentumEvaluator>();
    builder.Services.AddSingleton<IVolumeEvaluator, VolumeEvaluator>();
    builder.Services.AddSingleton<IVolatilityEvaluator, VolatilityEvaluator>();
    builder.Services.AddSingleton<IMarketContextEvaluator, MarketContextEvaluator>();
    builder.Services.AddSingleton<ICatalystEvaluator, CatalystEvaluator>();
    builder.Services.AddSingleton<ILearningAdjustmentEvaluator, LearningAdjustmentEvaluator>();
    builder.Services.AddSingleton<IResearchSignalEvaluator, ResearchSignalEvaluator>();
    builder.Services.AddSingleton<IScoreAggregator, ScoreAggregator>();
    builder.Services.AddSingleton<IConfidenceEngine, ConfidenceEngine>();
    builder.Services.AddSingleton<IRiskEngine, RiskEngine>();
    builder.Services.AddSingleton<IScoringEngine, ScoringEngine>();
    builder.Services.AddSingleton<IKnowledgeRepository, InMemoryKnowledgeRepository>();
    builder.Services.AddSingleton<IConceptLearningService, ConceptLearningService>();
    builder.Services.AddSingleton<ICaseLibraryBuilder, CaseLibraryBuilder>();
    builder.Services.AddSingleton<IKnowledgePatternDetectionService, KnowledgePatternDetectionService>();
    builder.Services.AddSingleton<IKnowledgeRuleGenerator, KnowledgeRuleGenerator>();
    builder.Services.AddSingleton<IKnowledgeRetrievalService, KnowledgeRetrievalService>();
    builder.Services.AddSingleton<IKnowledgeEngine, KnowledgeEngine>();
    builder.Services.AddSingleton<IExpectedValueCalculator, ExpectedValueCalculator>();
    builder.Services.AddSingleton<IRiskRewardAnalyzer, RiskRewardAnalyzer>();
    builder.Services.AddSingleton<ITradeFilter, ConfidenceTradeFilter>();
    builder.Services.AddSingleton<ITradeFilter, LiquidityTradeFilter>();
    builder.Services.AddSingleton<ITradeFilter, VolatilityTradeFilter>();
    builder.Services.AddSingleton<ITradeGradeService, TradeGradeService>();
    builder.Services.AddSingleton<IDecisionExplanationService, DecisionExplanationService>();
    builder.Services.AddSingleton<ITradeDecisionEngine, TradeDecisionEngine>();
    builder.Services.AddSingleton<IHistoricalSimilarityEngine, HistoricalSimilarityEngine>();
    builder.Services.AddSingleton<IPortfolioDecisionEngine, PortfolioDecisionEngine>();
    // ── Market Intelligence layer ────────────────────────────────
    builder.Services.AddSingleton<IMarketRegimeEngine, MarketRegimeEngine>();
    builder.Services.AddSingleton<IAdaptiveLearningRepository, SupabaseAdaptiveLearningRepository>();
    builder.Services.AddSingleton<IAdaptiveLearningEngine, AdaptiveLearningEngine>();
    builder.Services.AddSingleton<IStrategyDiscoveryRepository, SupabaseStrategyDiscoveryRepository>();
    builder.Services.AddSingleton<IStrategyDiscoveryEngine, StrategyDiscoveryEngine>();
    builder.Services.AddSingleton<IHistoricalCaseRepository, SupabaseHistoricalCaseRepository>();
    builder.Services.AddSingleton<IKnowledgeBase, SupabaseKnowledgeBase>();
    // ── Research Universe layer ─────────────────────────────────
    builder.Services.AddSingleton<IResearchUniverseRepository, SupabaseResearchUniverseRepository>();
    builder.Services.AddSingleton<IResearchUniverseService, ResearchUniverseService>();
    builder.Services.AddSingleton<ResearchUniverseConfig>();
    builder.Services.AddSingleton<IResearchUniverseEngine, ResearchUniverseEngine>();
    // ── Discovery Engine ────────────────────────────────────────
    builder.Services.AddSingleton<IDiscoveryProvider, FinnhubDiscoveryProvider>();
    builder.Services.AddSingleton<IDiscoveryProvider, TwelveDataDiscoveryProvider>();
    builder.Services.AddSingleton<IDiscoveryProvider, CongressDiscoveryProvider>();
    builder.Services.AddSingleton<IDiscoveryProvider, MarketIntelligenceDiscoveryProvider>();
    builder.Services.AddSingleton<IDiscoveryEventRepository, SupabaseDiscoveryEventRepository>();
    builder.Services.AddSingleton<IDiscoveryEngine, DiscoveryEngine>();
    // ── Continuous Discovery Engine ─────────────────────────────
    builder.Services.AddSingleton<ContinuousDiscoveryConfig>();
    builder.Services.AddSingleton<IResearchTimelineRepository, SupabaseResearchTimelineRepository>();
    builder.Services.AddSingleton<IDiscoveryCheckpointRepository, SupabaseDiscoveryCheckpointRepository>();
    builder.Services.AddSingleton<IHistoricalProfileBuilder, HistoricalProfileBuilder>();
    builder.Services.AddSingleton<IContinuousDiscoveryEngine, ContinuousDiscoveryEngine>();
    // ── Evidence Engine ─────────────────────────────────────────
    builder.Services.AddSingleton<IEvidenceRepository, SupabaseEvidenceRepository>();
    builder.Services.AddSingleton<IEvidenceDecayStrategy, PassthroughDecayStrategy>();
    builder.Services.AddSingleton<IEvidenceAggregator, EvidenceAggregator>();
    builder.Services.AddSingleton<IEvidenceService, EvidenceService>();
    // ── Opportunity Learning ───────────────────────────────────────
    builder.Services.AddSingleton<OpportunityLearningConfig>();
    builder.Services.AddSingleton<IOpportunityLearningRepository, SupabaseOpportunityLearningRepository>();
    builder.Services.AddSingleton<IOpportunityLearningService, OpportunityLearningService>();
    builder.Services.AddSingleton<MarketSnapshotBuilder>();
    builder.Services.AddSingleton<PredictionGenerator>();
    builder.Services.AddSingleton<OutcomeEvaluator>();
    builder.Services.AddSingleton<TradeSetupEngine>();
    builder.Services.Configure<LearningGuardrailOptions>(
        builder.Configuration.GetSection(LearningGuardrailOptions.SectionName));
    builder.Services.AddSingleton<WeightUpdateValidator>();
    builder.Services.AddSingleton<LearningEngine>();
    builder.Services.AddSingleton<DailyReportService>();
    builder.Services.AddSingleton<PatternDetectionService>();
    builder.Services.AddSingleton<IntakeAnalysisService>();
    builder.Services.AddSingleton<EnsembleScoringService>();
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
builder.Services.AddSingleton<CandidateGenerationAuditRepository>();
builder.Services.AddSingleton<OptionsDataService>();

    // Paper Options — enhanced flow for /paper-options page
    builder.Services.AddSingleton<PaperOptionsService>();

    // Data hygiene — scheduled cleanup of bad/stale data
    builder.Services.AddSingleton<DataHygieneService>();

    // StockFit — fundamentals, filings, insider, institutional. Never used
    // for live quotes / bars / technicals / options chains (those stay with
    // Twelve Data + MarketData.app). Marked unavailable if key missing.
    builder.Services.AddSingleton<StockFitClient>();
    builder.Services.AddSingleton<StockFitProvider>();

    // Research signal infrastructure
    builder.Services.AddSingleton<ResearchSignalRepository>();
    builder.Services.AddSingleton<ResearchSignalService>();
    builder.Services.AddSingleton<IResearchSignalProvider, CongressSignalProvider>();

    // Dynamic pick orchestrator — wraps research engine + paper options
    // services to auto-generate stock + linked option candidates daily.
    builder.Services.AddSingleton<PaperStockCandidateRepository>();
    builder.Services.AddSingleton<StockCandidateService>();
    builder.Services.AddSingleton<OptionCandidateService>();
    builder.Services.AddSingleton<PortfolioLifecycleService>();
    builder.Services.AddSingleton<DynamicPickOrchestrator>();

    // Portfolio Challenge — simulated portfolio growth tracking.
    // Portfolio AI is separate from the Prediction Engine: predictions
    // find opportunities, Portfolio AI decides whether/how much to invest.
    builder.Services.AddSingleton<PortfolioChallengeRepository>();
    builder.Services.AddSingleton<PortfolioBalanceEngine>();

    // In-memory request counter — recorded on every request, displayed
    // on the dashboard with a "per-instance, resets on restart" caveat.
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
    }

    // In-memory request metrics are useful in production too as long as we
    // label them clearly ("per-instance, resets on restart"). Without this,
    // the dashboard never shows what calls are happening on the deployed
    // server — exactly the visibility gap Lou flagged.
    app.UseMiddleware<RequestMetricsMiddleware>();

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
        new("POST", "/api/jobs/run-discovery", "Discovery engine: scans all providers (Finnhub, TwelveData, Congress, Market Intelligence) for new research assets.", true, "Scheduled (pg_cron -> Edge Function), x-job-secret required", "This server"),
        new("POST", "/api/jobs/run-continuous-discovery", "Continuous discovery: lightweight incremental scan for new evidence since last checkpoint. Updates Research Universe without generating predictions.", true, "Scheduled (pg_cron -> Edge Function, hourly), x-job-secret required", "This server"),
        new("POST", "/api/jobs/run-universe-maintenance", "Research Universe maintenance: decay scores, promote assets, archive stale research.", true, "Scheduled (pg_cron -> Edge Function), x-job-secret required", "This server"),
        new("POST", "/api/jobs/run-opportunity-scan", "Opportunity learning: scan for significant movers and evaluate pipeline coverage.", true, "Scheduled (pg_cron -> Edge Function), x-job-secret required", "This server"),
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
        new("GET", "/api/dashboard/summary", "Aggregated dashboard data: watchlist overview, job statuses, predictions, learning, data quality.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/paper-options/predictions", "Eligible saved predictions for the Paper Options page.", false, "Next.js app, browser", "This server"),
        new("POST", "/api/paper-options/generate-candidates", "Score real option contracts for a saved prediction. Body: { predictionId, durationPreference, autoSave }.", false, "Next.js app, browser", "This server"),
        new("POST", "/api/paper-options/save-candidate", "Persist a chosen paper candidate. Body: { predictionId, candidate }.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/paper-options/open-candidates", "Currently open paper candidates.", false, "Next.js app, browser", "This server"),
        new("POST", "/api/paper-options/evaluate-candidate", "Evaluate one paper candidate against current market data. Body: { paperCandidateId }.", false, "Next.js app, browser", "This server"),
        new("POST", "/api/paper-options/evaluate-open-candidates", "Evaluate every open paper candidate.", false, "Next.js app, browser or scheduled job", "This server"),
        new("GET", "/api/paper-options/outcomes", "Recent paper option outcomes.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/paper-options/debug", "Counts, learning stats, and provider config for paper options.", false, "Dev only", "This server"),
        new("GET", "/api/portfolio/summary", "Portfolio challenge dashboard: balance, progress, positions, return, win rate.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/portfolio/summary/{id}", "Dashboard summary for a specific challenge.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/portfolio/challenges", "List all portfolio challenges.", false, "Next.js app, browser", "This server"),
        new("POST", "/api/portfolio/challenges", "Create a new portfolio challenge.", false, "Next.js app, browser", "This server"),
        new("PATCH", "/api/portfolio/challenges/{id}/status", "Update challenge status (active/paused/abandoned).", false, "Next.js app, browser", "This server"),
        new("GET", "/api/portfolio/positions/open", "Open positions for the active (or specified) challenge.", false, "Next.js app, browser", "This server"),
        new("GET", "/api/portfolio/positions/closed", "Closed positions for the active (or specified) challenge.", false, "Next.js app, browser", "This server"),
        new("POST", "/api/portfolio/positions/open", "Open a new portfolio position (deducts from cash).", false, "Next.js app, browser", "This server"),
        new("POST", "/api/portfolio/positions/close", "Close a position (records P&L, updates balance).", false, "Next.js app, browser", "This server"),
    };

    var frontendAppEndpoints = new List<EndpointInfo>
    {
        new("POST", "/api/agent-chat", "Live chat agent — builds context, calls this API's /api/ai/complete, saves to Supabase.", false, "Browser (chat UI)", "Next.js app"),
        new("POST", "/api/jobs/analyze-learning", "Summarizes signal performance and learning patterns.", false, "Manual trigger", "Next.js app"),
    };

    DashboardData BuildDashboardData() => new(
        ServiceName: "Stock Research Agent API",
        Status: "Online",
        ServerTimeUtc: DateTimeOffset.UtcNow,
        Environment: app.Environment.EnvironmentName,
        Version: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        FrontendOrigin: FrontendOrigin,
        CorsConfigured: true,
        FrontendOriginDefaulted: frontendOriginDefaulted,
        ApiEndpoints: apiEndpoints,
        FrontendAppEndpoints: frontendAppEndpoints,
        Metrics: app.Services.GetRequiredService<RequestMetrics>().Snapshot());

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
        MarketDataOptionsProvider marketData,
        StockFitProvider stockFit) =>
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
                stockFit = new { configured = stockFit.IsConfigured, baseUrl = stockFit.BaseUrl, provider = "StockFit" },
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

    app.Lifetime.ApplicationStarted.Register(async () =>
    {
        BootstrapLogger.Log("BOOT 013", "App started successfully — listening for requests");

        // ── Startup schema validation ─────────────────────────────────
        // Probe critical tables for expected columns. PostgREST returns
        // 400 when a column doesn't exist, so an empty result = OK,
        // an exception = missing column(s).
        try
        {
            var db = app.Services.GetRequiredService<SupabaseClient>();
            var logger = app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("StartupSchemaCheck");

            var expectedColumns = new Dictionary<string, string[]>
            {
                ["paper_stock_candidates"] = [
                    "id", "prediction_id", "run_id", "ticker", "prediction_type", "timeframe",
                    "entry_price", "confidence_score", "risk_score", "status", "candidate_mode",
                    "quality_tier", "is_actionable", "bullish_score", "bearish_score", "winning_direction"
                ],
                ["portfolio_challenges"] = [
                    "id", "name", "starting_balance", "current_balance", "target_balance",
                    "current_cash", "status", "risk_profile", "portfolio_mode"
                ],
                ["portfolio_positions"] = [
                    "id", "portfolio_id", "ticker", "entry_price", "quantity",
                    "dollars_invested", "status", "prediction_id"
                ],
            };

            foreach (var (table, columns) in expectedColumns)
            {
                try
                {
                    await db.SelectAsync(table,
                        filter: "id=eq.00000000-0000-0000-0000-000000000000",
                        select: string.Join(",", columns),
                        limit: 1);
                }
                catch
                {
                    logger.LogError(
                        "SCHEMA DRIFT DETECTED: Table '{Table}' is missing expected columns. " +
                        "Expected: {Columns}. Pipeline inserts WILL fail silently until this is fixed.",
                        table, string.Join(", ", columns));
                }
            }

            BootstrapLogger.Log("BOOT 014", "Startup schema validation completed");
        }
        catch (Exception ex)
        {
            BootstrapLogger.Log("BOOT 014", $"Startup schema validation failed: {ex.Message}");
        }
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
