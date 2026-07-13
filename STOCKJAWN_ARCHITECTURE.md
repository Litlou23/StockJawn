STOCKJAWN — Current Architecture Documentation

Purpose of this document: describe how the STOCKJAWN system works today, as built. This is a descriptive record for a technical reviewer, not a design proposal. No critique, redesign, or feature suggestions are included.

Scope note on repos: the codebase is two applications in one workspace.
- `stock-research-agent` — a Next.js 15 (App Router) frontend/BFF, deployed as the web app. Holds all pages and most `/api/*` proxy routes.
- `stock-research-agent-api` — a .NET 9 Web API (`StockResearchAgent.Api`), deployed separately to Azure App Service. Holds the research engine, Supabase access, and the only OpenAI API key in the system.
The Next.js app never talks to Supabase or OpenAI directly for the "dynamic" pipeline — it proxies to the .NET API over HTTP using `AGENT_API_BASE_URL`. A smaller set of legacy features in the Next.js app talk to Supabase directly with their own service-role client (`lib/supabase/serverClient.ts`).

---

## 1. High-Level Product Purpose

STOCKJAWN is a personal, single-user stock and options research assistant. The root layout's own metadata describes it plainly: "Private personal stock research dashboard — not financial advice." There is no login system, no multi-tenant data model, and no brokerage connection anywhere in the code. It is built for one user (referred to in code comments as "Lou").

The problem it solves: rather than the user manually tracking a watchlist, reading news, and guessing whether a setup is worth acting on, the system automatically (1) discovers candidate tickers from live financial news and earnings calendars, (2) maintains a small active watchlist by scoring and swapping candidates over time, (3) generates directional (bullish/bearish/neutral) predictions for each watchlist ticker using deterministic technical/catalyst scoring plus an AI-written explanation, (4) turns qualifying predictions into simulated ("paper") stock and options trades using real market prices, (5) checks those predictions and paper trades against what actually happened, and (6) feeds the results back into its own scoring weights so the system recalibrates itself over time. A separate, unrelated module surfaces recently disclosed U.S. congressional stock trades as a reference feed.

The primary user is a single individual doing personal research and paper-trading experimentation — the system explicitly never executes real trades, never gives buy/sell instructions, and repeatedly labels itself as "Learning Mode / Paper Only / Not Actionable" in both UI copy and the AI system prompt.

Typical user workflow:
1. The user opens the app, which redirects `/` to `/chat` — a conversational assistant is the default landing experience.
2. On the Dashboard, the user can manually trigger (or wait for the scheduled) "Morning Scan," "End of Day Check," "Learning Update," and "Weekly Research" jobs.
3. Weekly Research discovers new candidate tickers from news/earnings and updates the active watchlist (add/keep/flag/swap/archive).
4. Morning Scan generates predictions for every active watchlist ticker, wraps eligible ones as paper stock candidates, and where a real option chain and a qualifying score exist, creates a matching paper option candidate.
5. Throughout the day (or next run), End of Day Check fetches live prices and scores outcomes for every open prediction and paper candidate.
6. Learning Update aggregates those outcomes into per-signal accuracy stats and nudges the scoring weights used by the next Morning Scan.
7. The user reviews outcomes on Predictions, Watchlist, Stock Lab, Results, and History pages, asks the AI chat assistant follow-up questions (which it answers only from live tool-call data), and separately can browse Congress Trades as an unrelated reference feed.

---

## 2. Application Architecture

All pages are Next.js App Router routes under `stock-research-agent/app/`. All pages that show "system data" are wrapped in the shared `AppShell` component, which renders a left/side navigation (`Sidebar`/`MobileNav`, built from `components/navItems.tsx`) and the page content.

Navigation is organized into 5 top-level groups (from `navItems.tsx`): Chat, Dashboard, Research (Stock Lab / Watchlist / Predictions / Congress Trades / Results / History), Options (Options Lookup / Options Simulator / Practice Options), and System (Learning / Connection Status / Settings). Two more pages (`/picks/[id]` and `/catalysts/[id]`) exist as detail views reached by links rather than nav entries, and `/demo` exists with no nav entry at all.

### `/` (root)
- Purpose: entry redirect only.
- Route: `app/page.tsx`.
- Behavior: immediately calls `redirect('/chat')`. No content of its own.

### `/chat` — Chat (default landing page)
- Purpose: conversational interface to the AI research assistant.
- Route: `app/chat/page.tsx`.
- Inputs: user's typed message; short client-side conversation history (last 8 turns) sent with each request.
- Outputs: AI-generated JSON response rendered as a chat bubble (message text, data-confidence badge, suggested follow-up prompts, risk warnings, optional captured "thesis"); a right-hand panel shows `TopPicksPanel` and `MarketSummaryCard`, built server-side by `services/contextBuilder.ts` (`buildTodayMarketContext`).
- User actions: send a message, click a suggested prompt.
- Connections: posts to `POST /api/agent-chat`, which runs a tool-calling loop against the .NET `/api/chat-tools/*` endpoints (see §6). Every user and assistant message is saved to the `chat_messages` table for audit/learning purposes, but is never reloaded — each page load starts a fresh conversation by design (see comment in `app/chat/page.tsx`). Client-side, `services/agentChatService.ts`'s `sendAgentMessage()` calls that route and, only if the request throws or returns a non-OK status, falls back to a fully local, deterministic mock responder (`sendAgentMessageMock`) so the chat UI never hard-fails — the response in that fallback case is tagged `diagnostics.provider = 'client-fallback'` / `usedFallback: true` rather than being a live AI answer.

### `/dashboard` — Dashboard
- Purpose: single-screen overview of the whole system's current state.
- Route: `app/dashboard/page.tsx` (server component, `dynamic = 'force-dynamic'`).
- Inputs: none from the user; fetches `GET {AGENT_API_BASE_URL}/api/dashboard/summary` server-side at request time.
- Outputs: watchlist counts (active / needs review / might replace), directional and long-term prediction accuracy stats, paper-option stats, "stocks passed on" (scan) stats, a live "Today's Picks" panel (`DynamicSummaryCards`, client-fetched from `/api/dashboard/dynamic-summary`), a news/catalyst intelligence section, a sortable watchlist table, recent watchlist change log, job status cards for Morning Scan / EOD / Learning, data-quality warnings, and a "what the system has learned" signal-performance table with recent insights.
- User actions: click through to Predictions/Watchlist, trigger any of the four background jobs via `JobTriggerButtons`, click a canned chat prompt (routes to `/chat?q=...`).
- Connections: reads from the .NET `/api/dashboard/summary` endpoint (legacy/combined stats) and `/api/dashboard/dynamic-summary` (the newer dynamic-orchestrator funnel stats from `DynamicPickOrchestrator.GetDashboardSummaryAsync`). Job triggers call `POST /api/jobs/trigger`, which proxies to the .NET job endpoints.

### `/stock-lab` — Practice Stocks
- Purpose: the primary working surface for the "dynamic" (current-generation) paper-stock pipeline — lets the user directly fire the three dynamic orchestrator jobs and see their output.
- Route: `app/stock-lab/page.tsx` (client component).
- Inputs: button clicks only (Generate / Evaluate / Learning Update).
- Outputs: a table of `paper_stock_candidates`, a table of `paper_stock_outcomes`, and grouped `stock_learning_stats` (accuracy by ticker/timeframe/prediction type/etc.).
- User actions: "Run Dynamic Morning Picks," "Run Dynamic EOD Review," "Run Dynamic Learning Update" — each fires a job then polls `GET /api/jobs/status` until it completes (long-running jobs return 202 immediately; the UI polls rather than waiting on the HTTP request).
- Connections: talks to the .NET API through `services/researchOrchestrator/dynamicPickOrchestrator.ts` (`dynamicPickOrchestrator.*` client + `pollJobUntilDone`), which hits `DynamicPicksController` endpoints.

### `/watchlist` — Watchlist
- Purpose: shows every ticker the system is currently tracking, why, and what it thinks about each one; provides a full drill-down per ticker.
- Route: `app/watchlist/page.tsx` (client component).
- Inputs: sort selector; click a card to open a full-screen ticker detail modal.
- Outputs: four sections — Active, Needs a Second Look (`review_needed`), Might Replace (`swap_candidate`), Removed (`archived`) — each card showing a derived plain-English verdict, score, risk, and info-availability labels. The detail modal shows the watchlist thesis/score breakdown, the latest prediction, the paper stock candidate status, any matched option contract, and the outcome, all translated into plain English by client-side helper functions (`friendlySignalName`, `friendlyTechnicalCase`, etc.).
- User actions: sort by score/risk/ticker; open/close ticker detail.
- Connections: `GET /api/watchlist` and `GET /api/watchlist/changes` (both proxy to the .NET `WatchlistController`); ticker detail modal calls `GET /api/ticker-detail?ticker=...`, which proxies to `ChatToolsController`'s `get_ticker_detail` tool endpoint — the same endpoint the AI chat assistant uses.

### `/predictions` — Predictions
- Purpose: compare every prediction the system has made against what actually happened, across four categories.
- Route: `app/predictions/page.tsx` (client component, `dynamic = 'force-dynamic'`).
- Inputs: category tabs (Predictions / Long-Term / Options / Passed On), date-range preset or custom range, correct/wrong/pending filter, sort order.
- Outputs: stat cards (total/evaluated/correct/incorrect/accuracy), and a card per prediction showing signal strength, risk, significance, entry/target/stop/invalidation prices, bull/bear case, data sources used, missing-data warnings, and (once evaluated) the actual outcome, price-accuracy percentage, and a plain-English "lesson."
- User actions: switch category/tab/date range/sort; expand a card for full detail.
- Connections: `GET /api/research/predictions-with-outcomes` (proxies to .NET `ResearchController`) for stock categories; `GET /api/paper-options/all-with-outcomes` for the Options tab.

### `/congress-trades` — Congress Trades
- Purpose: shows recently disclosed U.S. House and Senate stock trades. See §8 for full detail — it is architecturally independent of everything else in the app.
- Route: `app/congress-trades/page.tsx` (client component).
- Inputs: refresh button; ticker/politician text filter.
- Outputs: an AI-generated insight paragraph, a "most-traded tickers" chip list, a list of individual disclosed trades (ticker, buy/sell, amount range, politician, dates, link to the source filing), and a disclosure of any filings that could not be parsed.
- User actions: refresh (bypasses the 6-hour cache), filter by ticker/politician, click through to a source PDF/filing.
- Connections: `GET /api/congressional-trades?chamber=house|senate` and `POST /api/congressional-trades/insight`, both self-contained Next.js routes with no Supabase or .NET involvement other than the shared AI gateway for the insight text.

### `/results` — Results
- Purpose: an older/simpler results view than `/predictions` — raw predictions joined to outcomes with per-ticker summary stats.
- Route: `app/results/page.tsx` (client component).
- Inputs: All/Open/Evaluated tab; sort order.
- Outputs: hit-rate/average-move/accuracy-score stat cards, a per-ticker win/loss breakdown table, and an expandable list of predictions with bull/bear case and outcome summary.
- User actions: switch tab, change sort, expand a card.
- Connections: `GET /api/research/predictions?limit=500` and `GET /api/research/outcomes`, both proxying to the .NET `ResearchController`.

### `/history` — History
- Purpose: shows the legacy "picks" model's history (an older, pre-dynamic-pipeline concept of a stock pick) alongside its result snapshot.
- Route: `app/history/page.tsx` (server component).
- Inputs: none.
- Outputs: a `PickCard` + `ResultSnapshot` pair per historical pick.
- Connections: `services/picksService.ts` (`getPickHistory`) and `services/resultsService.ts` (`getResultByPickId`), which read directly from Supabase's legacy `picks` / `result_placeholders` tables via the Next.js server-side Supabase client — this page does not go through the .NET API.

### `/picks/[id]` — Pick Detail
- Purpose: detail view for one legacy "pick" (score breakdown, signals, options signals, bearish counterpoint, invalidation point, outcome snapshot).
- Route: `app/picks/[id]/page.tsx` (server component, dynamic `id` param).
- Inputs: pick ID from the URL.
- Outputs: full breakdown of a single legacy pick and its linked options signals/result.
- Connections: `services/picksService.ts`, `services/resultsService.ts`, `services/signalsService.ts` — all direct-to-Supabase legacy reads (not the .NET API). Reached from `/history`.

### `/catalysts/[id]` — News Event (Catalyst) Detail
- Purpose: detail view for one classified news catalyst, showing why it was scored the way it was, an AI-written plain-English explanation, which predictions it was linked to and their outcomes, and historical performance for that event type.
- Route: `app/catalysts/[id]/page.tsx` (server component, `dynamic = 'force-dynamic'`).
- Inputs: catalyst ID from the URL.
- Outputs: classification stats (importance, source trust, freshness, ticker relevance, sentiment, confirmation counts), event types/keywords found, an AI explanation built strictly from those deterministic fields, linked predictions with outcomes, and historical win-rate stats for that event type.
- Connections: `services/persistence/newsIntelligenceRepository.ts` and `services/persistence/researchRepository.ts` (direct Supabase reads from the Next.js app), plus one direct call to `requestAiCompletion` (the shared AI gateway) for the explanation text. Reached from the Dashboard's "News Analysis" section.

### `/options-research` — Options Lookup
- Purpose: ad hoc real-time options-chain lookup/scoring tool for any ticker, independent of the prediction pipeline.
- Route: `app/options-research/page.tsx` (client component).
- Outputs: a scored table of option contracts (liquidity/spread/IV/DTE component scores and an overall score) plus underlying stock quote.
- Connections: `app/api/options-data/*` routes, proxying to .NET `OptionsDataController` (backed by `MarketDataOptionsProvider`, i.e., MarketData.app).

### `/options-lab` — Options Simulator
- Purpose: theoretical options-strategy simulator against a chosen prediction — generates strike/strategy scenarios and their payoff profile.
- Route: `app/options-lab/page.tsx` (client component).
- Outputs: scenario cards (strategy type, strikes, premium, breakevens, max profit/loss, estimated return, a confidence-fit score, and a plain-English "why this scenario was generated").
- Connections: `app/api/options-lab/*` routes, proxying to .NET `OptionsLabController` (`AutomaticScenarioGenerator`, `TheoreticalOptionsSimulator`, `StrategyPayoffCalculator`, `ScenarioRankingService`).

### `/paper-options` — Practice Options
- Purpose: view/manage the paper option-contract candidates generated from qualifying stock predictions (an older, single-purpose page compared to the combined Options tab now on `/predictions`).
- Route: `app/paper-options/page.tsx` (client component).
- Outputs: prediction list, paper option candidates with full contract detail, evaluate/generate actions.
- Connections: `app/api/paper-options/*` routes, proxying to .NET `PaperOptionsController` / `OptionsDataController`.

### `/learning` — System Learning
- Purpose: shows the output of the learning feedback loop in plain language.
- Route: `app/learning/page.tsx` (server component).
- Outputs: `LearningReportCard` (latest learning report), `SignalPerformancePanel` (per-signal accuracy), and a "Run Fresh Analysis" button.
- Connections: `services/persistence/learningRepository.ts` — direct Supabase reads (legacy `learning_reports` / `signal_performance` tables) from the Next.js app, not the .NET dynamic pipeline's `research_signal_performance`/`learning_insights` tables. The "Run Fresh Analysis" button calls `POST /api/jobs/analyze-learning`, a self-contained Next.js-only analysis job (`services/learning/learningAnalysisService.ts`) that is distinct from every other job described in this document: it reads real `prediction_candidates`/`prediction_outcomes` plus live RSS news intake, computes signal performance and suggested weight changes, generates a small set of unsaved "auto picks" from today's headlines (`rssPickGenerator.ts`), asks the shared AI gateway for a short market briefing, and persists the resulting signal-performance and report rows back to the legacy `signal_performance`/`learning_reports` tables this page reads from.

### `/connectivity` — Connection Status
- Purpose: a live health check across every external dependency.
- Route: `app/connectivity/page.tsx` (client component).
- Outputs: status dot (ok/error/not_configured), latency, and message per service: .NET API health, Supabase (via .NET), Twelve Data, OpenAI, Finnhub, and each configured RSS feed.
- Connections: `GET /api/connectivity`, a Next.js route that pings each dependency (some directly, some by asking the .NET API to check its own configured services).

### `/settings` — Settings
- Purpose: read-only preview of the current signal weighting used by the (legacy) scoring model.
- Route: `app/settings/page.tsx` (server component).
- Outputs: a list of signal names, their current weight multiplier, active/inactive status, and notes. The page explicitly states in its own copy that these will become user-adjustable "in a future update" — today it is view-only.
- Connections: `services/persistence/picksRepository.ts` (`getSignalWeightsFromDb`) — direct Supabase read of the legacy `signal_weights` table.

### `/demo` — Demo (unlisted)
- Purpose: appears to be a leftover development/test page, not linked from navigation. Renders four `<iframe>`s pointing at external news sites (NYT, The Dispatch, Fox News, Al Jazeera) with the label "News source pipeline test."
- Route: `app/demo/page.tsx`.
- Connections: none — purely static, no data fetching.

---

## 3. Navigation Flow

Primary flow (matches the persistent sidebar groups):

Chat (default landing, `/`) → Dashboard → Research group: Stock Lab → Watchlist → Predictions → Congress Trades → Results → History → Options group: Options Lookup → Options Simulator → Practice Options → System group: Learning → Connectivity → Settings.

Branches out of the flow above:

- Dashboard → "View all predictions" → Predictions.
- Dashboard → "Full watchlist" → Watchlist.
- Dashboard → News Analysis section → Catalyst Detail (`/catalysts/[id]`) → (back to) Dashboard.
- Dashboard → canned chat prompt → Chat (`/chat?q=...`).
- Watchlist → click any ticker card → in-page Ticker Detail modal (not a separate route; fetches `/api/ticker-detail`) → "Back to Watchlist" closes it.
- History → each `PickCard` → Pick Detail (`/picks/[id]`).
- Congress Trades → "View filing" links → external PDF/filing pages (leaves the app).
- Predictions (Options tab) and Practice Options and Options Lookup/Simulator are cross-referenced conceptually (all draw from the same `paper_option_candidates` data) but are not linked to one another by in-app navigation — each is reached only via the sidebar.
- `/demo` is reachable only by direct URL; it has no inbound or outbound in-app links.

There is no ticker-detail *page* route — the equivalent of "Ticker Detail" in a conceptual sense is the Watchlist page's modal, and separately the Predictions/Results pages' own expandable cards, and the Pick Detail page for legacy picks. These are three different implementations of "show me everything about one item," not one shared route.

---

## 4. Backend Data Flow

### Ticker discovery (source: live web)
`UniverseDiscoveryService` (.NET) combines: RSS financial news feeds (`RssFeedService`, ticker mentions scored higher for cashtags than bare tickers), Finnhub's upcoming-earnings calendar (7-day window) and Finnhub market news (both direct-ticker fields and text-extracted mentions via `TickerExtractor`), and a boost for tickers already on the active watchlist (pulled from Supabase). Results are deduplicated, scored, and capped to the top 30 tickers ("the universe"). Consumer: `DynamicWatchlistService`, invoked by the Weekly Research job.

### Watchlist maintenance (processing + storage)
`DynamicWatchlistService` loads the current active/review watchlist from Supabase (`watchlist_items`), loads prior scoring weights (`research_scoring_weights`), recent insights (`learning_insights`), and recent prediction/outcome history to compute each ticker's historical accuracy. It scores every discovered candidate, compares to the existing list (capped at 10 active items, minimum score threshold, staleness threshold, swap-delta threshold), and writes add/keep/review/swap/archive decisions back to `watchlist_items`, logging every change to `watchlist_change_log`. Consumers: Dashboard, Watchlist page, and the next Morning Scan (which only researches *active* watchlist tickers).

### Morning Scan — feature generation (processing)
`DailyResearchRunService.RunMorningScanAsync` reads the active watchlist, then for each ticker calls `PredictionGenerator.BuildMarketSnapshotAsync`, which pulls: a live quote + recent bars + computed technical indicators from Twelve Data (`MarketDataService`/`TwelveDataProvider`, feeding `IndicatorEngine`), fundamentals-only news/SEC filings/earnings-calendar context from StockFit (`StockFitProvider` — explicitly never used for price/technical data), and recent company news from Finnhub. Every one of these sources degrades to an honest warning (never fabricated data) if not configured or if the call fails. Storage: one `market_snapshots` row per ticker per run.

### Morning Scan — scoring and prediction creation (processing + AI + storage)
`ScoringEngine` (static, deterministic) combines weighted buckets — trend, momentum, volume, volatility, SPY-relative market context, catalyst importance, a "learning edge" term from past lessons, and a risk penalty — using the current `research_scoring_weights`, applies a confirmation multiplier when multiple buckets agree, a data-quality factor based on how many indicators could actually be computed, and a calibration factor from learning, to produce a directional score, confidence, risk, and prediction type. `PredictionGenerator` then asks OpenAI (model `OPENAI_PREDICTION_MODEL`, default `gpt-4.1-nano`) to write the narrative explanation (thesis, bull case, bear case, invalidation) strictly from those already-computed numbers and signals — OpenAI never determines direction, confidence, or risk. If OpenAI is unavailable, a rule-based explanation is generated from the signal list instead; the run never blocks on AI. Storage: `prediction_candidates` (the prediction itself) and `prediction_inputs` (the raw signal inputs, kept for audit).

### Morning Scan — paper candidate + option generation (processing + storage)
`DynamicPickOrchestrator.RunDynamicMorningPicksAsync` runs immediately after the above (within the same "Morning Scan" job), and for every prediction: computes a second, independent deterministic composite score (catalyst/trend/volume/market-context/historical-accuracy/confidence minus risk and missing-data penalties), assigns a quality tier (`very_weak` → `weak` → `medium` → `strong_paper` → `production_candidate`, based on confidence) and a candidate mode (`learning` / `actionable_shadow` / `live_eligible`, based on confidence+risk thresholds), and saves a `paper_stock_candidates` row. For candidates that qualify (directional, has market data, an option-data provider is configured, risk/confidence within learning thresholds) and fall within per-run (25) and per-ticker (1) caps, it calls `PaperOptionsService.GenerateCandidatesAsync`, which scans a real option chain via `MarketDataOptionsProvider` (MarketData.app) and saves a matching `paper_option_candidates` row. Every attempt — created or blocked, and the specific reason if blocked — is written to `candidate_generation_audit` for full funnel visibility. After saving each stock candidate, `DynamicPickOrchestrator` also classifies it as a trade setup via `TradeSetupEngine` (non-blocking) — generating a setup fingerprint, determining setup type, computing entry/target/stop/invalidation levels, and saving a `trade_setups` row with historical favorability from `setup_learning_stats`. Consumers: Stock Lab, Predictions (Options tab), Practice Options, Dashboard funnel stats.

### End of Day review (processing + storage)
Three evaluators run, all against live Twelve Data / MarketData.app quotes, all refusing to fabricate a result when live data is unavailable (the item simply stays open for the next run):
- `OutcomeEvaluator` scores every open `prediction_candidates` row into `prediction_outcomes` (direction correct, target/stop/invalidation hit, price-accuracy percent, max favorable/adverse move, and SPY-relative performance for short-term predictions).
- `DynamicPickOrchestrator`'s own stock evaluator scores every open `paper_stock_candidates` row into `paper_stock_outcomes`, and immediately upserts `stock_learning_stats` bucketed by ticker / timeframe / prediction type / confidence bucket / catalyst type / trend signal / volume signal.
- `PaperOptionsService.EvaluateAllOpenAsync` scores every open `paper_option_candidates` row into `paper_option_outcomes` (simulated P&L, IV change) and updates `option_learning_stats`.

### Learning Update (processing + storage)
`LearningEngine` re-tallies signal-level accuracy across recent predictions/outcomes into `research_signal_performance`, nudges `research_scoring_weights` up or down (bounded per-adjustment, minimum sample size required) based on measured accuracy, and writes plain-English summaries to `learning_insights`. It also runs several additional stages: (1) **Confidence calibration** — `ComputeConfidenceCalibrationAsync` measures predicted vs. actual accuracy across confidence bands, and `ApplyCalibrationFactorAsync` computes a dampening multiplier (clamped 0.85–1.15, moved max 1%/day toward the target) that is persisted as a weight override (`calibration_factor`) and applied by `ScoringEngine` on the next Morning Scan to correct systematic overconfidence or underconfidence. (1b) **Self-tuning confidence caps** — `ComputeCapEffectivenessAsync` groups resolved predictions by the `ConfidenceCap` reason stored in their `score_debug_json`, measures accuracy per cap reason, persists results to `cap_tuning_stats`, and computes a `risk_cap_boost` weight override (0–15 pts, max 2 pts/day movement) when risk-capped predictions are more accurate than their confidence band implies — see §10 "Self-Tuning Confidence Caps". (2) **Setup performance analytics** — `ComputeSetupPerformanceAsync` groups resolved predictions by their setup fingerprint (canonical pipe-delimited string of active signal components), computes per-fingerprint win rate, average win/loss, expected value, confidence, risk rating, regime breakdown, and degradation detection, and upserts results to `setup_learning_stats`. These weights, calibration factor, cap boost, and insights are read back in on the *next* Morning Scan by both `ScoringEngine` (weights, calibration factor, risk cap boost, setup history) and `DynamicWatchlistService` (insights, ticker accuracy).

### Scheduled jobs (external trigger)
Per `stock-research-agent-api/CLAUDE.md`, scheduling is external to both repositories: Supabase Edge Functions (`supabase/functions/morning-scan`, `end-of-day-review`, `learning-update`, `weekly-research`, in the separate Supabase project, not in this workspace) run on a `pg_cron` schedule and call the .NET API's job endpoints (`POST /api/jobs/run-*`) using a `DOTNET_API_BASE_URL` secret. Every job route requires an `x-job-secret` header matching `JOB_RUN_SECRET`. The Dashboard's manual "Run" buttons call the exact same .NET endpoints, via the Next.js proxy route `POST /api/jobs/trigger` (which adds the same secret server-side). Long-running jobs (weekly research, all three dynamic-orchestrator jobs) use a fire-and-forget pattern: the controller validates the secret, starts a background `Task.Run`, and returns HTTP 202 immediately; a `JobStatusTracker` singleton holds in-memory job state that `GET /api/jobs/status` exposes for polling.

### AI processing (all of it, system-wide)
There is exactly one place in the entire system that holds the OpenAI API key and calls OpenAI: `OpenAiCompletionService` inside the .NET API, exposed as `POST /api/ai/complete`. Every Next.js server route that needs AI text (`lib/ai/aiClient.ts`'s `requestAiCompletion`) calls that single endpoint over HTTP — the Next.js app never talks to OpenAI directly. Inside the .NET process, `PredictionGenerator` additionally instantiates its own `ChatClient` directly (not through `OpenAiCompletionService`) because it already runs server-side in the same process; it uses a separately configurable model (`OPENAI_PREDICTION_MODEL`, default `gpt-4.1-nano`) versus the general-purpose gateway's default (`OPENAI_MODEL`, default `gpt-4.1-mini`). See §6 for a full inventory of every AI call site.

### An earlier TypeScript implementation that is present but not currently reachable
The Next.js app also contains a full second implementation of the research pipeline, written entirely in TypeScript and talking to Supabase directly rather than through the .NET API: `services/researchEngine/*` (its own `dailyResearchRunService.ts`, `predictionGenerator.ts`, `outcomeEvaluator.ts`, `learningEngine.ts`, `dailyReportService.ts`) and `services/weeklyResearch/weeklyResearchService.ts`. It is built against a **hardcoded** 13-ticker watchlist (`SPY, QQQ, AAPL, MSFT, NVDA, AMD, TSLA, AMZN, META, GOOGL, PLTR, AVGO, NFLX, COIN`) rather than a discovered universe. Tracing every caller of these files from the app's routes shows none of them are currently imported or invoked anywhere reachable: `app/api/jobs/trigger` and the dedicated `app/api/jobs/run-weekly-research` shim both always forward to the .NET API over HTTP regardless of which job name is requested, and the job names that sound like they'd match this TS code (`run-morning-scan`, `run-end-of-day-review`, `run-learning-update`, `run-weekly-research`) are in fact handled by *.NET* controllers of the same names (`ResearchJobsController`, `WatchlistController`) that call the .NET `DailyResearchRunService` / `UniverseDiscoveryService` / `DynamicWatchlistService` described above — the same underlying service the "dynamic" endpoints wrap, not a separate system. In other words, this TS folder appears to be an earlier version of the pipeline that has since been fully reimplemented in .NET, left in place but not wired to anything live. Its would-be output tables (`picks`, `signal_weights`, `result_placeholders`, `weekly_research_runs`, `weekly_stock_reviews`, `weekly_candidates`) are consequently not being written by anything currently reachable in the app — see §9.

Separately, and still genuinely live, `services/newsIntelligence/*` implements a news-catalyst classification subsystem (deterministic keyword/event extraction and strength scoring, no AI) that is invoked on demand via `POST /api/news-intelligence/reprocess` (re-processes the latest news intake items) and read via `GET /api/news-intelligence/catalysts` and `catalyst-stats` — all direct-to-Supabase Next.js routes, independent of the .NET pipeline. This is what actually populates `news_catalysts`, `catalyst_prediction_links`, and `catalyst_outcome_stats`, and is what powers the Dashboard's "News Analysis" section and the `/catalysts/[id]` page. No button in the pages reviewed was found to call the reprocess endpoint, so it is presumably triggered externally (manually or by an undiscovered scheduled call) rather than automatically as part of the .NET Morning Scan.

---

## 5. Database Schema

All storage is Supabase Postgres, accessed via PostgREST. The .NET API uses a small hand-written REST client (`Services/Supabase/SupabaseClient.cs`) rather than a Supabase SDK; the Next.js app uses a direct `@supabase/supabase-js` server client (`lib/supabase/serverClient.ts`) for the tables it reads and writes directly. Tables below are grouped by which subsystem currently owns them: the .NET dynamic pipeline (actively read and written), an earlier TS pipeline whose write path is now orphaned (still read by a few pages, but effectively frozen — see §9), and a handful of Next.js-only tables that are still actively written today outside the .NET pipeline.

### Current ("dynamic" pipeline) tables — used by Dashboard, Stock Lab, Watchlist, Predictions, Practice Options

**`research_runs`** — one row per pipeline execution. Columns include `run_type` (morning_scan / end_of_day_review / learning_update / weekly_research), `status`, `started_at`, `completed_at`, `summary`, `error_message`, `metadata`. Every prediction/snapshot/outcome batch is tied back to a run via `run_id`. Used by: Dashboard job-status cards, Stock Lab.

**`market_snapshots`** — one row per ticker per run: the raw quote, recent price bars, computed technical context, and news context captured at scan time. Relationship: many-to-one with `research_runs`. Used internally by the scoring engine; not directly rendered on any page.

**`prediction_candidates`** (referred to in the UI simply as "predictions") — the core output of the scoring engine: `ticker`, `prediction_type` (bullish/bearish/neutral_no_edge/neutral_range_bound/neutral_high_volatility/watch_only/rejected/unavailable), `confidence_score`, `importance_score`, `risk_score`, `entry_reference_price`, ATR-based volatility fields, `target_price`/`stop_price`/`invalidation_price`/`support_level`/`resistance_level`, `risk_reward_ratio`, `bullish_case`/`bearish_case`/`prediction_reason` (the AI-written narrative), `data_sources_used`, `missing_data_warnings`, `status` (open/evaluated/expired). Relationship: many-to-one with `research_runs`; one-to-one (eventually) with `prediction_outcomes`; one-to-one with a `paper_stock_candidates` row once wrapped. Used by: Dashboard, Predictions, Results, Watchlist ticker-detail modal, Chat (via `get_predictions` tool).

**`prediction_inputs`** — the raw signal/indicator values that fed one prediction, kept as an audit trail. Relationship: many-to-one with `prediction_candidates`. Not directly rendered; exists for traceability.

**`prediction_outcomes`** — the result of checking a prediction against reality: `start_price`/`close_price`/`percent_move`, `direction_correct`, `target_hit`/`stop_hit`/`invalidation_hit`, `price_accuracy_percent`, `max_favorable_percent`/`max_adverse_percent`, `outcome_score`, `outcome_summary`, `lesson`. Relationship: many-to-one with `prediction_candidates` (a prediction can be re-evaluated, producing more than one outcome row over time; the UI takes the latest). Used by: Dashboard, Predictions, Results, Watchlist ticker detail.

**`research_signal_performance`** — rolling accuracy per named signal (e.g., a specific technical or catalyst signal), used to judge which signals are actually working. Used by: Dashboard "What the System Has Learned," Chat.

**`research_scoring_weights`** — the current multiplier applied to each named signal inside `ScoringEngine`, adjusted by `LearningEngine`. Read at the start of every Morning Scan.

**`learning_insights`** — plain-English insight + recommended action generated after a Learning Update run. Used by: Dashboard "Recent Insights."

**`watchlist_items`** — the actively tracked ticker list: `ticker`, `status` (active/review_needed/swap_candidate/archived), `total_score`, `catalyst_score`, `risk_score`, `thesis_summary`, `bullish_case`/`bearish_case`, `data_confidence`, `invalidation_point`, `swap_reason`, `sources_used`, `missing_data_warnings`, a raw JSON `score_breakdown`. Used by: Dashboard, Watchlist page (all four sections read from this one table filtered by status).

**`watchlist_change_log`** — an append-only history of every add/keep/flag/swap/archive/score-change event against `watchlist_items`. Used by: Dashboard "Recent Watchlist Changes," Watchlist page "Recent Changes."

**`watchlist_candidates`** — raw discovery candidates considered by `DynamicWatchlistService` before (or instead of) promotion into `watchlist_items`; effectively a staging table for the discovery step.

**`paper_stock_candidates`** — a prediction wrapped with its own composite score: `entry_price`/`target_price`/`stop_price`, `catalyst_score`/`trend_score`/`volume_score`/`market_context_score`/`historical_accuracy_score`, `risk_penalty`/`missing_data_penalty`, `total_score`, `candidate_mode` (learning/actionable_shadow/live_eligible), `quality_tier` (very_weak/weak/medium/strong_paper/production_candidate), `is_actionable`, `qualifies_for_options`, `inclusion_reason`/`exclusion_reason`, `status` (open/watch_only/unavailable/evaluated). Relationship: one-to-one with `prediction_candidates` via `prediction_id`; one-to-many with `paper_option_candidates`. Used by: Stock Lab, Watchlist ticker detail, Dashboard funnel stats.

**`paper_stock_outcomes`** — EOD evaluation of one `paper_stock_candidates` row: `percent_move`, `direction_correct`, `target_hit`/`stop_hit`/`invalidation_hit`, `max_favorable`/`max_adverse`, `outcome_score`, `outcome_summary`, `lesson`. Used by: Stock Lab.

**`stock_learning_stats`** — aggregated win-rate/accuracy stats keyed by a `(stat_type, stat_key)` pair (e.g., type=`ticker` key=`AAPL`, or type=`confidence_bucket` key=`high`), tracking `total_candidates`, `accuracy`, `average_outcome_score`. Read back into scoring (`ScoreHistoricalAccuracyAsync`) and shown on Stock Lab and Dashboard.

**`paper_option_candidates`** — one specific real option contract matched to a qualifying stock candidate: `option_symbol`, `side` (call/put), `strike`, `expiration`, `dte_at_entry`, `entry_underlying_price`, entry `bid`/`ask`/`mid`/`iv`/`delta`/`open_interest`/`volume`, `contract_score`, `selection_reason`, `status`. Relationship: many-to-one with `paper_stock_candidates` (and, transitively, `prediction_candidates`). Used by: Predictions (Options tab), Practice Options, Options Lookup/Simulator context, Watchlist ticker detail.

**`paper_option_outcomes`** — simulated P&L evaluation of one `paper_option_candidates` row: current Greeks/IV/underlying price, `paper_pnl_per_contract`/`paper_pnl_percent`, `underlying_move_percent`, `iv_change`, `outcome_summary`. Used by: Predictions (Options tab), Practice Options.

**`option_learning_stats`** — aggregated option win-rate stats, structurally parallel to `stock_learning_stats`.

**`trade_setups`** — a detected trade setup associated with a prediction candidate: `setup_fingerprint` (canonical pipe-delimited string of active signal components like `momentum_strong|trend_bullish|volume_confirming`), `setup_type` (momentum_breakout/trend_continuation/mean_reversion/volatility_squeeze/catalyst_driven/multi_signal_convergence/mixed), `direction`, entry/target/stop/invalidation prices, expected holding period, `risk_reward_ratio`, `expected_value_percent`, `is_historically_favorable`, `setup_confidence`, `status` (active/target_hit/stop_hit/invalidated/expired/closed), and `resolution_*` fields tracking the outcome. Relationship: many-to-one with `prediction_candidates` via `prediction_id`; linked to `setup_learning_stats` via `setup_fingerprint`. Used by: EOD review (setup resolution), Chat (`get_setup_performance` tool).

**`setup_learning_stats`** — aggregated performance statistics per unique setup fingerprint: `sample_size`, `win_rate`, `avg_win_percent`/`avg_loss_percent`, `expected_value_percent`, `confidence_mean`/`risk_mean`, `regime_breakdown` (JSON of performance by market regime), `is_trusted` (sample ≥ 10 and EV > 0), `is_degraded` (recent performance declining), `last_seen_at`. Unique constraint on `setup_fingerprint`. Read by: `ScoringEngine.AdjustForSetupHistory` (boosts/penalizes confidence based on historical setup EV), `PredictionGenerator` (pipeline integration), Chat (`get_setup_performance`/`explain_scoring` tools).

**`candidate_generation_audit`** — one row per prediction per run recording exactly what happened during candidate generation: whether a stock candidate and/or option candidate was created, the candidate mode/quality tier, and — if an option candidate was *not* created — the specific block reason (e.g., `risk_too_high`, `confidence_below_learning_threshold`, `max_candidates_reached`, `missing_market_data`). Used by: Dashboard funnel/"block reason breakdown" stats.

### Older tables — still read by some pages, but written by the earlier TS pipeline described above (which is not currently reachable)

These tables are still queried directly from Supabase by a few Next.js pages, but their only known write path is the orphaned `services/researchEngine`/`services/weeklyResearch` code from §4 — meaning, as best this review could confirm by tracing callers, nothing currently reachable in the app is adding new rows to most of them. They should be read as historical/frozen data rather than an actively updating dataset:

**`picks`** — an older, simpler model of a single stock pick (score, risk level, conviction level, main reason, bearish counterpoint, invalidation point, suggested research action). Used by: History, Pick Detail. Write path (`savePicks`) exists only inside the orphaned `weeklyResearchService.ts`.

**`signal_weights`** — the legacy equivalent of `research_scoring_weights` (signal name, weight, active flag, notes). Used by: Settings (explicitly read-only in the current UI).

**`result_placeholders`** — legacy outcome tracking tied to `picks`. Used by: History, Pick Detail (`ResultSnapshot`). Same orphaned write path as `picks`.

**`signal_performance`** — legacy equivalent of `research_signal_performance` (distinct table, distinct columns: includes `avg_confidence_when_correct`/`avg_confidence_when_wrong`). Used by: Learning page.

**`learning_reports`** — legacy learning-analysis report text. Used by: Learning page (`LearningReportCard`).

**`weekly_research_runs`**, **`weekly_stock_reviews`**, **`weekly_candidates`** — the orphaned TS weekly-research service's own run/candidate tracking, parallel to (but separate from) `research_runs`/`watchlist_candidates`.

**`option_watchlist_candidates`** — a legacy options-candidate scoring table (`services/persistence/scoringRepository.ts`), predating `paper_option_candidates`.

**`agent_reports`, `daily_reports`, `notifications`, `agent_snapshots`** — legacy daily-report generation and notification tables (`services/persistence/reportsRepository.ts`); no current page reads these directly in the pages reviewed.

**`catalyst_items`** — a separate, simpler news-item store from an even earlier intake pipeline (`services/informationIntake/*`), distinct from `news_catalysts`.

### Tables that are still actively written today, outside the .NET pipeline

**`chat_messages`** — every user and assistant chat turn from `/api/agent-chat`, saved for audit/learning even though the Chat page itself never reloads history from it. Actively written on every chat turn.

**`agent_theses`** — trade theses captured from AI chat conversations (`saveThesis`, called from `/api/agent-chat` whenever the AI's JSON response includes a `thesis` object). Actively written; not surfaced as a dedicated page in this review, so it currently functions as a capture-only audit table.

**`agent_feedback`** — thumbs-up/down or similar feedback tied to chat/theses (schema present in `learningRepository.ts`); no page in the nav was found writing or reading it through a visible UI control in this review.

**`news_catalysts`** — the current news-catalyst-intelligence subsystem's classified news events (headline, source, detected event types, extracted keywords, sentiment, catalyst-strength/source-reliability/freshness/ticker-relevance scores, price/volume confirmation status, warnings). Used by: Dashboard "News Analysis," `/catalysts/[id]`.

**`catalyst_prediction_links`** — join table linking a `news_catalysts` row to the `prediction_candidates`/`paper_option_candidates` row(s) it influenced, with an `influence_type`/`influence_score`/`reason_linked`. Used by: `/catalysts/[id]` "Predictions That Used This News."

**`catalyst_outcome_stats`** — historical win-rate and average-move statistics aggregated per detected event type. Used by: `/catalysts/[id]` "Historical performance."

### Congress trading module — no tables

The congressional-trades feature (§8) stores nothing in Supabase. Its only "storage" is an in-process `Map` cache in the Next.js server process, keyed by chamber, expiring after 6 hours.

---

## 6. AI Responsibilities

Every AI call in the system ultimately reaches OpenAI's Chat Completions API through `OpenAiCompletionService` in the .NET API (default model `gpt-4.1-mini`, overridable via `OPENAI_MODEL`), except one internal .NET caller (`PredictionGenerator`) that instantiates its own client directly with a separately configurable model (`OPENAI_PREDICTION_MODEL`, default `gpt-4.1-nano`). All AI output is narrative/explanatory text layered on top of numbers that are computed deterministically beforehand — no AI call in the system determines a prediction's direction, confidence, risk score, or any price level.

**1. Prediction narrative generation**
- Where: `PredictionGenerator.cs`, called once per ticker during Morning Scan.
- Inputs: the already-computed direction/confidence/risk/importance scores, the list of contributing signals, and the raw market/news context for that ticker.
- Prompt purpose: write the thesis, bullish case, bearish case, invalidation rule, and key price-level narrative in plain language, strictly from the supplied facts.
- Output: free text stored in `prediction_candidates.bullish_case` / `bearish_case` / `prediction_reason` / `invalidation_rule`.
- Displayed: Dashboard, Predictions, Results, Watchlist ticker detail — anywhere a prediction card is shown; the UI shows an "AI" badge when `data_sources_used` includes `openai-analysis`.
- Deterministic or AI-generated: the *decision* (direction/confidence/risk) is 100% deterministic (`ScoringEngine`); only the *explanation text* is AI-generated. If OpenAI is unavailable, a rule-based fallback explanation is generated instead and the prediction still ships.

**2. Chat assistant**
- Where: `POST /api/agent-chat` (Next.js) → tool-calling loop → .NET `ChatToolsController` endpoints.
- Inputs: the user's message, up to 8 prior turns of conversation, and the results of up to 3 rounds of tool calls executed against live Supabase-backed data.
- Tools available (defined in `lib/ai/chatToolDefinitions.ts`): **Read-only** — `get_dashboard_summary`, `get_predictions`, `get_stock_candidates`, `get_option_candidates`, `get_ticker_detail`, `get_setup_performance`, `get_learning_stats`, `explain_scoring`, `get_config`. **Action triggers** (POST) — `run_learning_update`, `run_morning_scan`, `run_eod_review`, `update_config`. Action tools use POST; the frontend's `executeToolCall` selects HTTP method dynamically based on a `POST_TOOLS` set.
- Prompt purpose (`SLIM_SYSTEM_PROMPT`): act as a skeptical, factual research assistant that must base every answer only on tool-returned data, never invent numbers, always separate evidence-for/evidence-against/missing-data/confidence, and never give trade instructions or position sizing. The assistant can trigger system actions (scans, reviews, learning updates, config changes) and explain scoring breakdowns and setup performance.
- Output: a strict JSON envelope — `message`, `dataConfidence` (high/medium/low), `suggestedPrompts`, `riskWarnings`, and an optional `thesis` object (which, if present, is persisted to `agent_theses`).
- Displayed: `/chat` page.
- Deterministic or AI-generated: fully AI-generated conversational text, but constrained to only reference facts returned by deterministic tool calls; the assistant is explicitly instructed to say "no good setups today" when data doesn't support an idea rather than force an answer.

**3. Congressional trades insight**
- Where: `POST /api/congressional-trades/insight` → `generateInsight()` in `congressionalTradesService.ts`.
- Inputs: up to 100 already-parsed trade records (politician, ticker, action, amount range, dates) — no market data, no scoring.
- Prompt purpose: summarize clusters of activity in the same ticker/sector, notably large positions, and net buy/sell direction, in 3-5 factual sentences, explicitly instructed not to speculate about motive or give investment advice.
- Output: a single paragraph of free text.
- Displayed: top of `/congress-trades` as an "AI Insight" banner.
- Deterministic or AI-generated: fully AI-generated summary over deterministically parsed data; the trades themselves are never AI-touched.

**4. Catalyst explanation**
- Where: `/catalysts/[id]` page, `buildExplanation()`, calling `requestAiCompletion` directly from a Next.js server component.
- Inputs: the catalyst's deterministic classification fields (headline, detected event types, extracted keywords, sentiment, catalyst-strength score) and a text summary of linked-prediction outcomes.
- Prompt purpose: explain in 2-3 sentences why the signals would matter for a prediction and what the outcome data shows, explicitly instructed never to invent facts not present in the inputs.
- Output: free text, not persisted (recomputed on every page load).
- Displayed: `/catalysts/[id]`, "Why This News Mattered" section.
- Deterministic or AI-generated: AI-generated explanation strictly over deterministic classification/outcome data.

**5. Learning-page market briefing**
- Where: `POST /api/jobs/analyze-learning` → `services/learning/learningAnalysisService.ts`'s `tryAiSummary()`, triggered by the "Run Fresh Analysis" button on `/learning`.
- Inputs: a computed sentiment/trending-ticker/catalyst summary of the latest RSS news intake, plus the top auto-generated picks derived from that news.
- Prompt purpose: "You are a concise stock research analyst. No disclaimers, no filler." — write a short plain-text market briefing from the supplied intake summary.
- Output: a short paragraph of free text (`aiBriefing`), plus a rule-based `summary` string used whenever the AI call is unavailable.
- Displayed: `/learning` page, "AI Market Summary" panel (only shown when the call succeeds).
- Deterministic or AI-generated: AI-generated text over a deterministic RSS-sentiment computation; falls back to a fully rule-based summary sentence if the AI gateway is unreachable.

**6. Legacy prompt templates (present but unused)**
- Where: `lib/ai/prompts.ts` — `AGENT_CHAT_SYSTEM_PROMPT`, `buildDailyReportPrompt`, `buildTickerExplanationPrompt`, `buildBearishCounterpointPrompt`.
- Status: not imported anywhere in the current codebase (confirmed by search). This was an earlier, "context-stuffing" design for the chat assistant (feeding it entire watchlist/pick/news bundles) that has been superseded by the slim tool-calling design described in item 2. It is documented here only because it is present in the code, not because it currently runs.

---

## 7. Prediction Pipeline

End-to-end, in execution order:

**Candidate discovery.** `UniverseDiscoveryService.DiscoverUniverseAsync()` scans RSS financial news feeds for ticker mentions (weighted by whether the mention was a cashtag, a company name, or a bare ticker), pulls Finnhub's 7-day upcoming-earnings calendar, pulls Finnhub general market news (using both its structured related-tickers field and text extraction over headlines/summaries), and adds a small boost for tickers already on the active watchlist. All sources are merged into one scored, deduplicated list capped at the top 30 tickers. This runs as part of the Weekly Research job, feeding `DynamicWatchlistService`.

**Watchlist selection.** `DynamicWatchlistService.BuildDynamicWatchlistAsync()` takes that universe plus the current watchlist state, scores every candidate (data-backed score plus a catalyst boost for news-discovered tickers, discounted by staleness and recent inaccuracy), and decides, per ticker, to add / keep / flag for review / mark as a swap candidate / archive — capped at 10 active items with a minimum-score floor. Every decision is logged to `watchlist_change_log`.

**Feature generation.** For every *active* watchlist ticker, `PredictionGenerator.BuildMarketSnapshotAsync()` assembles: Twelve Data quote, recent daily bars, and computed technical indicators (`IndicatorEngine` — trend, momentum, RSI, volume, volatility; each indicator explicitly tracked as computed-or-skipped so downstream scoring knows exactly how much real signal it has), StockFit fundamentals context (news, SEC filings, earnings-calendar proximity — fundamentals only, never technicals, per the project's own engineering rule), and Finnhub recent company news. Anything unavailable is recorded as a warning, never invented.

**Scoring.** `ScoringEngine.Score()` computes independent bucket scores for trend, momentum, volume, a volatility "setup" score, SPY-relative market context, catalyst importance, a "learning edge" score derived from `research_scoring_weights`/prior lessons, and a risk penalty. These sum to a directional score. A confirmation multiplier (1.00–1.30) rewards agreement across buckets and penalizes conflicting ones; a data-quality factor discounts confidence when few indicators were computable; a calibration factor (itself tuned by the learning engine) applies a final correction. Hard caps prevent high confidence when only one signal bucket is available or when trend and momentum directly conflict. The output is a prediction type (bullish/bearish/one of several neutral or rejected variants/watch_only), a confidence score, and a risk score — entirely rule-based, no AI involved in this step.

**Setup history adjustment.** After scoring, `PredictionGenerator` reconstructs the signal evidence from the scoring breakdown, generates a setup fingerprint, and looks up historical performance in `setup_learning_stats`. `ScoringEngine.AdjustForSetupHistory` then adjusts the confidence score: proven setups (positive EV, trusted) receive a +5 to +15 boost proportional to EV strength; degraded setups (sample ≥ 8, not trusted) receive a −10 penalty; negative EV setups (EV < −0.5%, sample ≥ 8) receive a −15 penalty. Actionability tier is recomputed after adjustment. This step is wrapped in a try/catch so failures never block prediction creation.

**Prediction creation.** The scored result, plus entry price, ATR-derived expected move, target/stop/invalidation levels, and support/resistance, is saved as a `prediction_candidates` row. OpenAI (`gpt-4.1-nano` by default) is then given only the already-computed numbers and is asked to write the thesis and bull/bear case narrative — never to change or re-derive the numbers themselves. If OpenAI fails or is unconfigured, an automatically assembled explanation from the raw signal list is used instead so the prediction is never blocked on AI availability.

**Candidate wrapping and option generation.** `DynamicPickOrchestrator` wraps each freshly created prediction in a `paper_stock_candidates` row with its own composite score (25% catalyst, 20% trend, 15% volume, 10% market context, 15% historical accuracy, 15% confidence, minus a risk penalty and a missing-data penalty), assigns a `quality_tier` from the confidence score and a `candidate_mode` from confidence+risk thresholds (`learning` → `actionable_shadow` → `live_eligible`), and computes deterministic target/stop bands (±2–3% depending on direction) directly from the entry price. Candidates that qualify for options (directional, has a live entry price, an options data provider is configured, risk ≤ 90, confidence ≥ 15 or in the run's top quartile) are ranked and — subject to a 25-per-run and 1-per-ticker cap — passed to `PaperOptionsService`, which scans a real option chain from MarketData.app and saves the best-fit contract as `paper_option_candidates`. Every prediction's outcome in this step (candidate created, option created, or specifically why not) is written to `candidate_generation_audit`.

**Outcome tracking.** On End of Day review, four separate evaluators re-fetch live quotes and score every open item: `OutcomeEvaluator` for raw predictions (`prediction_outcomes`), `DynamicPickOrchestrator`'s stock evaluator for paper stock candidates (`paper_stock_outcomes`), `DynamicPickOrchestrator`'s trade setup evaluator for active `trade_setups` (resolving as target_hit/stop_hit/invalidated/expired based on current prices), and `PaperOptionsService` for paper option candidates (`paper_option_outcomes`). Each computes direction-correctness, target/stop/invalidation hits, maximum favorable/adverse excursion, and a 0–100 outcome score; each writes a short plain-English "lesson." Any item whose live quote can't be fetched is left open rather than scored with placeholder data. Paper option candidates are only closed when the contract has actually expired, hit a ±50% profit target or stop loss, or is on its last day before expiry — otherwise the P&L is logged as a snapshot and the candidate remains open for re-evaluation on the next EOD run.

**Learning updates.** `LearningEngine` re-tallies which named signals were present in predictions that turned out correct vs. incorrect (`research_signal_performance`), and nudges `research_scoring_weights` toward signals that have been performing well and away from ones that haven't (bounded to a maximum ±0.3 adjustment per cycle, requiring at least 5 qualifying predictions before adjusting a given signal), and writes summary `learning_insights`. It then runs confidence calibration — measuring predicted vs. actual accuracy across confidence bands, computing a weighted-average calibration error, and persisting a `calibration_factor` (0.85–1.15, moved max 1%/day) as a weight override that `ScoringEngine` reads on the next Morning Scan. Finally, it runs setup performance analytics — grouping resolved predictions by setup fingerprint and computing per-fingerprint win rate, average win/loss, expected value, regime breakdown, and degradation flags into `setup_learning_stats`. In parallel, the stock/option evaluators upsert `stock_learning_stats`/`option_learning_stats`, bucketed by ticker, timeframe, prediction type, confidence bucket, catalyst type, and trend/volume-signal strength. All of these — weights, calibration factor, setup history, and per-ticker stats — are read back in on the next Morning Scan by `ScoringEngine` (weights, calibration factor, setup history adjustment), `PredictionGenerator` (setup fingerprint lookup and confidence adjustment), and `DynamicPickOrchestrator` (per-ticker historical accuracy, trade setup classification) — closing the loop.

---

## 8. Congress Trading Module

**Data source.** Two independent public government sources, fetched live on every (non-cached) request — no third-party API and no scraping of any private data:
- U.S. House: the House Clerk's public financial-disclosure site (`disclosures-clerk.house.gov`). A yearly bulk ZIP index (`{year}FD.zip`) lists every filing; the code filters to `FilingType = "P"` (Periodic Transaction Reports — the actual stock-trade disclosures) and downloads the individual PTR PDF for each of the most recent 8 filings.
- U.S. Senate: the Senate's electronic financial disclosure system (`efdsearch.senate.gov`). This system requires first accepting a "prohibition agreement" to obtain a session cookie, then POSTing a DataTables-style search request filtered to report type 11 (PTRs), then fetching each individual electronic filing's HTML transactions table.

**Parsing pipeline.** House PDFs are downloaded and their text layer extracted (`unpdf`); a regex tuned to the Clerk's fixed row format (`(TICKER) [ST] P|S|E (partial)? date date $min - $max`) extracts individual trade rows, with the preceding asset-name text recovered by walking backward from each match. Senate filings are plain HTML tables scraped with a `<tr>`/`<td>` regex, decoding HTML entities and mapping `Purchase`/`Sale`/`Exchange` text to `buy`/`sell`/`exchange`. In both pipelines, any filing that has no usable text layer (a scanned/paper filing) or that yields zero parseable stock rows is explicitly recorded as "skipped" with a human-readable reason — it is never silently dropped or guessed at.

**Database tables.** None. There is no persistence layer for this feature at all; results live only in an in-memory `Map` inside the Next.js server process (`congressionalTradesService.ts`), keyed by chamber selector, with a 6-hour expiry. A server restart or redeploy clears the cache entirely, and there is no historical record kept beyond what the source sites themselves currently expose (which, per the UI's own disclosure, can lag up to 45 days behind the actual trade date due to the statutory disclosure window).

**Current UI.** `/congress-trades` (in the "Research" nav group): a refresh button (bypasses cache), a most-traded-tickers chip filter, a ticker/politician text filter, an AI insight banner, the trade list itself (ticker, buy/sell badge, amount range, politician, state/district, transaction/disclosure dates, link to the original filing), and a collapsible disclosure of filings that couldn't be parsed. The House and Senate chambers are fetched as two separate requests from the client specifically so that each stays within a serverless function's execution time limit.

**Current AI usage.** One call: `POST /api/congressional-trades/insight` sends up to 100 already-normalized trades to the shared AI gateway (see §6, item 3) and returns a short factual summary paragraph, generated only after the trades themselves have already rendered (so a slow or failed AI call never blocks the page from showing real data).

**Connection to the rest of STOCKJAWN.** None, functionally. This module does not read or write `watchlist_items`, `prediction_candidates`, any `paper_*` table, or any learning table; it is not one of the sources considered by `UniverseDiscoveryService`; it is not one of the tools available to the chat assistant (`chatToolDefinitions.ts` has no congress-related tool); and it does not appear in `candidate_generation_audit` or any scoring bucket in `ScoringEngine`. Its only shared infrastructure with the rest of the app is the `AGENT_API_BASE_URL`-routed AI completion gateway and the general Next.js hosting environment.

---

## 9. Current Limitations

The codebase contains a full, hardcoded-13-ticker TypeScript reimplementation of the research pipeline (`services/researchEngine/*`, `services/weeklyResearch/weeklyResearchService.ts`) that, by tracing every import in the current routes, is not called from anywhere reachable in the running app — the actual pipeline behind every job name (legacy-sounding or "dynamic") is implemented once, in .NET, and the "dynamic" endpoints are a wrapper around the same underlying service the "legacy-named" endpoints call directly (see §4). This orphaned TS code and its output tables (`picks`, `signal_weights`, `result_placeholders`, `weekly_research_runs`, `weekly_stock_reviews`, `weekly_candidates`) still exist in the repository and are still read by a few pages (History, Pick Detail, Settings, Learning), which means those pages are showing historical/frozen data with no currently-active write path feeding them, rather than being wired to whichever pipeline generation is actually running today.

The Learning page (`/learning`) and Settings page (`/settings`) read from the *legacy* tables (`learning_reports`, `signal_performance`, `signal_weights`) rather than the tables the current dynamic pipeline actually updates (`learning_insights`, `research_signal_performance`, `research_scoring_weights`), while the Dashboard reads from the newer tables. This means the Learning/Settings pages and the Dashboard's own "What the System Has Learned" section can be describing two different scoring systems at the same time.

`lib/ai/prompts.ts`, including the more elaborate `AGENT_CHAT_SYSTEM_PROMPT` context-stuffing design, is present in the codebase but not imported or executed anywhere — it has been fully superseded by the slim tool-calling chat design in `lib/ai/chatToolDefinitions.ts` / `/api/agent-chat`.

The Congress Trades module keeps no persistent history (in-memory cache only, cleared on every redeploy) and is fully disconnected from the prediction/scoring/learning system, as described in §8 — it functions purely as a read-only reference feed today.

The Settings page is explicitly read-only; its own copy states that adjustable weights are planned "in a future update" but are not implemented.

The `/demo` route is an orphaned page (four iframes of external news sites) with no navigation entry pointing to it and no data fetching — it appears to be a leftover from an earlier news-source connectivity test rather than a current feature.

Every real-time data dependency — Twelve Data (quotes/bars/technicals), MarketData.app (option chains), Finnhub (news/earnings), StockFit (fundamentals/filings), the RSS feed list, and OpenAI itself — is treated as optional at every call site: each one degrades to an explicit warning rather than fabricated data when unavailable, which is a deliberate "no mock data" engineering rule (`stock-research-agent-api/CLAUDE.md`), but it also means the depth and confidence of any given day's predictions and candidates is directly gated by which of those API keys happen to be configured and healthy at run time.

The actual cron schedule that triggers Morning Scan / EOD Review / Learning Update / Weekly Research lives outside both repositories in this workspace — in Supabase Edge Functions and a `pg_cron` job definition inside the linked Supabase project — so the exact cadence is not discoverable from this codebase alone; only the fact that it calls the .NET job endpoints with a shared secret is documented here (`stock-research-agent-api/CLAUDE.md`).

No authentication, authorization, or multi-user data model exists anywhere in either application — this is architecturally a single-user system.

---

## 10. Future Vision

There is no dedicated roadmap document in either repository (no `ROADMAP.md`, no phase plan, no explicit "next steps" file was found). What follows is inferred strictly from naming and structure already present in the code, not from any stated plan — it should be read as "what the current design implies it is heading toward," not as a confirmed plan.

The `candidate_mode` enum on `paper_stock_candidates` (`learning` → `actionable_shadow` → `live_eligible`) and the `quality_tier` enum (`very_weak` → `weak` → `medium` → `strong_paper` → `production_candidate`), together with a versioned `ThresholdPolicyVersion` field (currently `learning_options_v1`) attached to every candidate, describe a graduation path: candidates start in a pure-learning bucket where nothing is actionable, and — as confidence/risk thresholds are met and a track record accumulates in `stock_learning_stats`/`option_learning_stats` — individual setups could eventually be distinguished as "live eligible" or "production" candidates. Today, the UI does not treat `live_eligible` items any differently from `learning` items beyond a text label, and there is no code path anywhere that places a real order or connects to a brokerage — the system currently stops at fully simulated, paper-only tracking for every candidate mode.

The Settings page's own copy ("These will be adjustable in a future update — for now, this is a preview") indicates an intended move from the current read-only, algorithm-managed scoring weights toward direct user control over signal weighting.

The chat-tool architecture (`chatToolDefinitions.ts`) has grown to 13 tools covering read-only queries, action triggers (morning scan, EOD review, learning update), scoring explanation, setup performance analysis, and configuration management. The tool surface is explicitly designed to be extended further — it serves as the single integration point between the AI assistant and all system capabilities, and the same `get_ticker_detail` tool is shared between the chat assistant and the Watchlist page's detail modal.

### Self-Tuning Confidence Caps (Phase 1 — Active)

The system's confidence formula includes hard caps that limit confidence based on risk score, trend/momentum conflict, market context conflict, and earnings proximity. Historically these caps were static, which created a problem: the risk caps in particular crushed confidence on predictions that had clear directional signals in volatile environments. Calibration data showed the 0–30 confidence band (all risk-capped) was 57% accurate — the best of any band — while the 50–65 band (uncapped but directionally conflicted) was only 19% accurate. The system was saying "I'm not confident" about its best calls and "I'm confident" about its worst.

**Phase 1** adds a self-tuning feedback loop:

1. **Cap Effectiveness Analysis** (`LearningEngine.ComputeCapEffectivenessAsync`): During the nightly learning update, the system groups all resolved predictions by their `ConfidenceCap` reason (stored in `score_debug_json`), measures accuracy per cap reason, and persists results to `cap_tuning_stats`. A cap is classified as *ineffective* if the accuracy of capped predictions exceeds the expected accuracy for their confidence band by more than 10%.

2. **Automatic Cap Adjustment**: When risk-capped predictions are collectively more accurate than their confidence band implies, the learning engine computes a `risk_cap_boost` (0–15 points) and persists it as a weight override. Movement is gradual (max 2 points per day) to prevent whiplash.

3. **ScoringEngine Consumption**: `ScoringEngine.Score()` reads `risk_cap_boost` from the weights dictionary (same mechanism as `calibration_factor`) and adds it to the risk-confidence caps, with absolute ceilings to prevent runaway boosting (70 for risk ≥ 75, 75 for risk ≥ 60, 80 for risk ≥ 50).

4. **Direction-Aware Caps**: Risk caps are now conditioned on the normalized decision margin `(W-L)/(W+L)` — when the margin exceeds 0.54 (clear directional signal), caps are loosened before the self-tuning boost is applied. The opposition penalty also uses decision margin instead of a simple ratio, which is more stable across different score magnitudes. This addresses the independence of risk (environmental volatility) vs. directional uncertainty. Risk model quality is separately evaluated using MAE (max adverse excursion) per risk bucket, not directional accuracy.

**Future phases** (not yet built):
- Per-cap-reason individual tuning (separate boosts for earnings caps, conflict caps, etc.)
- Meta-analysis correlating prediction accuracy with each confidence formula component
- Automatic cap threshold discovery (instead of fixed risk ≥ 60/75 breakpoints)
- Separation of "risk score" into distinct dimensions: environmental risk (volatility, liquidity) vs. directional uncertainty

---

## 11. Research Pipeline v2 — Discovery → Evidence → Research Universe

The original pipeline was: watchlist (manual/weekly refresh) → predictions → outcomes → learning. The new architecture adds four systems between data ingestion and prediction generation, creating a more autonomous and evidence-driven flow.

### New Pipeline Flow

```
Data Sources (Finnhub, TwelveData, MarketData.app, Congress data)
        ↓
Market Intelligence (MarketRegimeEngine, AdaptiveLearningEngine, KnowledgeBase)
        ↓
Discovery Engine (multiple providers scan for interesting tickers)
        ↓
Evidence Engine (every signal becomes an evidence record with weight and decay)
        ↓
Research Universe (scored, lifecycle-managed collection of research targets)
        ↓
Morning Scan (generates predictions ONLY for Research Universe members)
        ↓
Scoring Engine (confidence, risk, EV, trade grade)
        ↓
Trade Decision Engine (filters, risk-reward, portfolio context)
        ↓
Portfolio (paper stock + option candidates)
        ↓
Outcome Evaluation (EOD price check → evidence feedback)
        ↓
Learning (signal performance, self-tuning, opportunity learning)
```

### 11.1 Discovery Engine

**Purpose**: Automatically find new tickers worth researching — replaces the manual "weekly research" as the primary ticker intake.

**Entry point**: `IDiscoveryEngine.RunDiscoveryAsync()` via `POST /api/jobs/run-discovery`.

**Provider pattern**: Each discovery source implements `IDiscoveryProvider`:
- `NewsDiscoveryProvider` — scans Finnhub news for tickers with high mention counts or significant catalysts.
- `MoverDiscoveryProvider` — uses TwelveData top movers to find stocks with unusual price action.
- `CongressDiscoveryProvider` — flags tickers with recent congressional trading disclosures.

**Flow**: The engine runs all registered providers in sequence, deduplicates by ticker, persists `DiscoveryEvent` records to the `discovery_events` table, and calls `IResearchUniverseService.DiscoverAsync()` for each new ticker (idempotent — creates a `ResearchAsset` only if one doesn't already exist).

**Model**: `DiscoveryEvent` — ticker, category (`DiscoveryCategory` enum: TopMover, HighVolume, NewsCatalyst, EarningsUpcoming, CongressTrade, TechnicalBreakout, SectorRotation, OptionsUnusualActivity, InsiderActivity, AnalystUpgrade, IPO, Other), source, confidence, metadata JSON, summary.

**Table**: `discovery_events` — indexed on ticker, category, and discovered_at.

### 11.2 Evidence Engine

**Purpose**: Every observation about a ticker — from discovery, predictions, outcomes, regime changes, or learning insights — is recorded as a weighted evidence record. Evidence accumulates over time and decays, providing a quantitative measure of how interesting a ticker is right now.

**Entry point**: `IEvidenceService` — the main orchestrator.

**Core concepts**:
- `EvidenceRecord` — a single observation: ticker, type (`EvidenceType` enum: News, Technical, Congress, SEC, Learning, MarketRegime, Options, Volume, Momentum, Research, Catalyst), source string, weight (-1.0 to 1.0, positive = bullish), importance (1-100), optional expiration, optional link to a DiscoveryEvent.
- `EvidenceSnapshot` — aggregated view: interest score (0-100), evidence count, weight breakdown by type, auto-generated thesis, timeline.
- **Decay**: Each evidence type has configurable half-life and TTL. News evidence decays faster than SEC filings. Weight diminishes over time using the configured half-life.

**Recording sources**:
- Discovery Engine → `RecordFromDiscoveryAsync()` converts discovery events into evidence.
- Morning Scan → `DynamicPickOrchestrator` records evidence for each prediction generated.
- Outcome Evaluation → `OutcomeEvaluator` records evidence from evaluated outcomes (correct predictions strengthen, wrong predictions weaken).
- Future: market regime changes, learning insights, manual analyst input.

**Research Asset sync**: `SyncToResearchAssetAsync()` recomputes the evidence snapshot for a ticker and pushes updated interest score, evidence count, thesis, and last activity timestamp to the corresponding `ResearchAsset`.

**Components**: `IEvidenceRepository` (Supabase persistence), `IEvidenceAggregator` (snapshot computation), `IEvidenceDecayStrategy` (time-based weight decay).

**Table**: `evidence_records` — indexed on ticker, evidence_type, timestamp.

### 11.3 Research Universe

**Purpose**: A scored, lifecycle-managed collection of every ticker the system is actively researching. Replaces the old static watchlist as the source of morning scan candidates.

**Entry point**: `IResearchUniverseEngine.RunMaintenanceAsync()` via `POST /api/jobs/run-universe-maintenance`.

**Lifecycle states** (`ResearchState` enum): `New` → `Active` → `Monitoring` → `Archived`. Each state has rules for promotion/demotion:
- `New` → `Active`: sufficient evidence accumulates (configurable threshold in `ResearchUniverseConfig`).
- `Active` → `Monitoring`: interest score decays below threshold, or no new evidence for a configurable period.
- `Monitoring` → `Archived`: remains low-interest for too long, or staleness threshold exceeded.
- Any state can be promoted back if new high-weight evidence arrives.

**Status tracking** (`ResearchAssetStatus` enum): NotStarted, Discovered, EvidenceGathering, ReadyForAnalysis, ActiveResearch, Monitoring, Archived.

**Model**: `ResearchAsset` — ticker, state, status, interest score, evidence count, thesis, first/last discovery, holding window start/end, tags, metadata.

**Configurable rules** (`ResearchUniverseConfig`):
- `ActiveMinInterestScore` / `MonitoringMinInterestScore` — promotion/demotion thresholds.
- `StalenessThresholdDays` — how long without new evidence before demotion.
- `DecayRatePerDay` — daily interest score decay for assets with no new evidence.
- `MinEvidenceForActive` — minimum evidence records to promote from New.
- `HoldingWindowDays` — minimum days an asset stays Active before it can be demoted.
- `MaxActiveAssets` — hard cap on how many tickers can be Active simultaneously.

**Morning Scan integration**: `DailyResearchRunService.GetResearchCandidatesAsync()` now pulls from `IResearchUniverseService.GetActiveAssetsAsync()` instead of the watchlist. Falls back to watchlist if the Research Universe is empty (bootstrap period).

**Table**: `research_universe` — indexed on ticker (unique), state, interest_score.

### 11.4 Opportunity Learning

**Purpose**: Learn from opportunities the system missed. Instead of only evaluating predictions the system chose to make, it retroactively analyzes every significant stock movement to determine whether the pipeline should have caught it.

**Entry point**: `IOpportunityLearningService.ScanForMissedOpportunitiesAsync()` via `POST /api/jobs/run-opportunity-scan`. Also runs automatically as part of the learning update pipeline in `DynamicPickOrchestrator.RunDynamicLearningUpdateAsync()`.

**Movement thresholds** (configurable in `OpportunityLearningConfig`): 10%, 20%, 30%, 50%. Each movement is classified into a `MovementTier`: Tier1 (10%+), Tier2 (20%+), Tier3 (30%+), Tier4 (50%+).

**Measurement periods**: 1-day, 1-week (5 days), 1-month (21 days) — configurable.

**For each significant mover, the system evaluates**:
1. **Discovery awareness**: Was it in our Discovery Events? How many days before the move? From what source?
2. **Research Universe state**: Was it in the Research Universe? What state? What was its interest score and evidence count at the time?
3. **Prediction coverage**: Did we generate a prediction? Was it the correct direction? What was the confidence and risk?
4. **Capture status** (`OpportunityCaptureStatus`): Captured (prediction correct direction), PartiallyCaptured (discovered/in universe but no prediction), WrongDirection (prediction opposite), CompletelyMissed (never discovered).
5. **Miss reasons** (`MissedOpportunityReason` enum): NeverDiscovered, NotInResearchUniverse, NoPredictionGenerated, LowConfidence, HighRisk, MissingCatalyst, MissingNews, MissingTechnicalConfirmation, MissingVolume, MissingWatchlistEntry, WrongDirection, TooLate, ArchivedTooEarly.

**Analytics** (`OpportunityAnalytics`): Total opportunities, capture rate, awareness rate, breakdown by tier and period, top miss reasons, average discovery lead days.

**Design constraint**: Observation only — no automatic weight updates based on opportunity analysis. All records are persisted for manual review and future integration.

**Table**: `opportunity_learning_records` — indexed on ticker, scan_date, capture_status, highest_tier, percent_move.

### 11.5 Scheduled Jobs

The pipeline adds three new scheduled job endpoints (all require `x-job-secret` header):

| Endpoint | Service | Purpose |
|---|---|---|
| `POST /api/jobs/run-discovery` | `IDiscoveryEngine.RunDiscoveryAsync()` | Run all discovery providers, persist events, create research assets |
| `POST /api/jobs/run-universe-maintenance` | `IResearchUniverseEngine.RunMaintenanceAsync()` | Evaluate all assets: decay scores, promote/demote states, sync evidence |
| `POST /api/jobs/run-opportunity-scan` | `IOpportunityLearningService.ScanForMissedOpportunitiesAsync()` | Scan for significant movers, evaluate pipeline coverage |

**Recommended cron schedule** (not yet configured):
1. Discovery → runs before morning scan (e.g. 7:00 AM ET)
2. Universe Maintenance → runs after discovery, before morning scan (e.g. 7:15 AM ET)
3. Morning Scan → existing schedule (e.g. 7:30 AM ET)
4. Opportunity Scan → runs after EOD/learning update (e.g. 8:00 PM ET) or as part of learning update

### 11.6 New Supabase Tables

| Table | Purpose |
|---|---|
| `discovery_events` | Raw discovery observations from providers |
| `evidence_records` | Weighted, timestamped evidence attached to tickers |
| `research_universe` | Lifecycle-managed research targets with scores |
| `opportunity_learning_records` | Retroactive analysis of missed opportunities |

### 11.7 Future Extension Points

1. **Additional Discovery Providers**: Implement `IDiscoveryProvider` to add new data sources (social sentiment, insider trading feeds, sector ETF flows, earnings whisper data).
2. **Evidence from External Signals**: Any system that observes something about a ticker can call `IEvidenceService.RecordAsync()` — the interface is intentionally generic.
3. **Automatic Weight Tuning from Opportunity Learning**: Currently observation-only. Future work: use miss-reason patterns to automatically adjust discovery provider weights, scoring thresholds, or confidence caps.
4. **Research Universe Capacity Planning**: Dynamic `MaxActiveAssets` based on system load and prediction accuracy.
5. **Evidence-Driven Prediction Confidence**: Use the evidence snapshot's interest score and thesis quality to modulate prediction confidence before scoring.
6. **Cross-Ticker Evidence Correlation**: Detect when evidence for one ticker implies something about related tickers (sector peers, supply chain).

---

## 12. Market Intelligence Layer

Built in a prior phase, the Market Intelligence layer provides context-aware analysis that feeds into the Research and Discovery pipelines.

### 12.1 MarketRegimeEngine

**Purpose**: Classifies the current market environment into regimes (Bull, Bear, Volatile, Transitioning, Sideways, CrisisSelling, MeltUp, SectorRotation). Uses SPY price data, VIX levels, breadth indicators, and moving average analysis.

**Service**: `IMarketRegimeEngine` → `MarketRegimeEngine`.
**Output**: `MarketRegimeAssessment` — current regime, confidence, regime history, transition probability.
**Persistence**: `SupabaseMarketRegimeRepository` → `market_regime_assessments` table.

### 12.2 AdaptiveLearningEngine

**Purpose**: Tracks what prediction strategies work under which market conditions. Records strategy-regime performance pairs and adjusts recommendations.

**Service**: `IAdaptiveLearningEngine` → `AdaptiveLearningEngine`.
**Output**: `AdaptiveLearningInsight` — strategy effectiveness by regime, suggested adjustments.
**Persistence**: `SupabaseAdaptiveLearningRepository` → `adaptive_learning_insights` table.

### 12.3 StrategyDiscoveryEngine

**Purpose**: Identifies new trading patterns by analyzing which combinations of signals, time windows, and market conditions produce the best outcomes.

**Service**: `IStrategyDiscoveryEngine` → `StrategyDiscoveryEngine`.
**Output**: `DiscoveredStrategy` — signal combination, performance stats, regime affinity.
**Persistence**: `SupabaseStrategyDiscoveryEngine` → `discovered_strategies` table.

### 12.4 KnowledgeBase

**Purpose**: Centralized repository of system insights — facts the system has learned from its own experience. Queryable by topic, ticker, or regime.

**Service**: `IKnowledgeBase` → `SupabaseKnowledgeBase`.
**Output**: `KnowledgeEntry` — topic, content, confidence, source, tags.
**Persistence**: `knowledge_entries` table.

### 12.5 HistoricalCaseRepository

**Purpose**: Stores complete decision records (prediction + context + outcome) for historical similarity matching. Used by `HistoricalSimilarityEngine` in the Trade Decision pipeline.

**Service**: `IHistoricalCaseRepository` → `SupabaseHistoricalCaseRepository`.
**Output**: `HistoricalCase` — full decision snapshot including signals, regime, outcome.
**Persistence**: `historical_cases` table.

---

## 13. Trade Decision Pipeline

The Trade Decision pipeline transforms raw predictions into fully-qualified trade decisions with grades, explanations, and portfolio context.

### Components (all in `Services/TradeDecision/`):

1. **TradeDecisionEngine** — orchestrator. Takes a prediction + market data, runs it through EV calculation, risk-reward analysis, trade filters, grading, and explanation generation. Returns a `TradeDecisionResult`.

2. **EVService** — calculates expected value using prediction confidence, historical accuracy by setup type, and risk-reward ratio. Returns `EVCalculation` with raw EV, risk-adjusted EV, and Kelly fraction.

3. **RiskRewardService** — computes target price, stop price, risk-reward ratio, max position size, and drawdown estimates. Returns `RiskRewardAnalysis`.

4. **Trade Filters** (`ITradeFilter` pattern):
   - `VolatilityFilter` — blocks trades in extreme volatility.
   - `LiquidityFilter` — blocks illiquid tickers.
   - `CorrelationFilter` — blocks trades too correlated with existing portfolio.

5. **TradeGradeService** — assigns letter grades (A+ through F) based on EV, confidence, risk, and filter results. Returns `TradeGrade` with component scores.

6. **DecisionExplanationService** — generates human-readable explanations of why a trade was graded the way it was, what factors contributed, and what risks exist.

7. **PortfolioDecisionEngine** — adds portfolio-level context: existing exposure, sector concentration, correlation with current holdings. Returns `PortfolioDecisionResult`.

8. **HistoricalSimilarityEngine** — finds past predictions with similar signal profiles and compares outcomes. Returns `SimilarityResult` with matched cases and statistical summary.

---

## 14. Complete DI Registration Summary

All services are registered as `AddSingleton` in `Program.cs`. Key registrations for the new architecture:

```
// Discovery
IDiscoveryEventRepository → SupabaseDiscoveryEventRepository
IDiscoveryEngine → DiscoveryEngine
NewsDiscoveryProvider, MoverDiscoveryProvider, CongressDiscoveryProvider (as IDiscoveryProvider)

// Evidence
IEvidenceRepository → SupabaseEvidenceRepository
IEvidenceAggregator → EvidenceAggregator
IEvidenceService → EvidenceService

// Research Universe
IResearchUniverseRepository → SupabaseResearchUniverseRepository
IResearchUniverseService → ResearchUniverseService
ResearchUniverseConfig (default thresholds)
IResearchUniverseEngine → ResearchUniverseEngine

// Opportunity Learning
OpportunityLearningConfig
IOpportunityLearningRepository → SupabaseOpportunityLearningRepository
IOpportunityLearningService → OpportunityLearningService

// Market Intelligence
IMarketRegimeEngine → MarketRegimeEngine
IAdaptiveLearningEngine → AdaptiveLearningEngine
IStrategyDiscoveryEngine → StrategyDiscoveryEngine
IKnowledgeBase → SupabaseKnowledgeBase
IHistoricalCaseRepository → SupabaseHistoricalCaseRepository

// Trade Decision
EVService, RiskRewardService, TradeGradeService, DecisionExplanationService
ITradeFilter → VolatilityFilter, LiquidityFilter, CorrelationFilter
TradeDecisionEngine, PortfolioDecisionEngine, HistoricalSimilarityEngine
```

---

## 15. Migration Notes

### What changed for existing functionality:

1. **Morning Scan candidate source**: `DailyResearchRunService.GetResearchCandidatesAsync()` now pulls from Research Universe instead of watchlist. Falls back to watchlist if Research Universe is empty — zero regression risk during bootstrap.

2. **DynamicPickOrchestrator**: Added `IEvidenceService` and `IOpportunityLearningService` dependencies. After predictions are generated, evidence records are created (wrapped in try/catch — non-blocking). Opportunity scan runs as part of learning update.

3. **OutcomeEvaluator**: Added `IEvidenceService` dependency. After evaluating outcomes, evidence records are created for each evaluation (wrapped in try/catch — non-blocking).

4. **No existing job behavior changed**: All existing jobs (morning scan, EOD, learning update) continue to work identically. The new evidence recording and opportunity scanning are additive and non-blocking.

5. **No existing tables modified**: All new functionality uses new tables only.

6. **Watchlist still works**: The watchlist system is unchanged. It continues to function for display, manual tracking, and as a fallback for morning scan candidates.

Beyond these code-level signals, no further intended phases, integrations, or feature plans could be confirmed from the source.
