using StockResearchAgent.Api.Dashboard;
using StockResearchAgent.Api.Services;
using StockResearchAgent.Api.Services.Supabase;
using StockResearchAgent.Api.Services.MarketData;
using StockResearchAgent.Api.Services.ResearchEngine;
using StockResearchAgent.Api.Services.Watchlist;
using StockResearchAgent.Api.Services.UniverseDiscovery;

// Kept as a literal in sync with the CORS policy below — the dashboard
// displays this same value, it does not change CORS behavior.
const string FrontendOrigin = "http://localhost:3000";

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
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

// Dev-only in-memory request counter for the "/" dashboard — see
// Dashboard/RequestMetrics.cs for why this is never trusted in production.
builder.Services.AddSingleton<RequestMetrics>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("NextJsDev", policy =>
        policy.WithOrigins(FrontendOrigin).AllowAnyHeader().AllowAnyMethod());
});

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

app.Run();
