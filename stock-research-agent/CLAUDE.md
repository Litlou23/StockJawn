@AGENTS.md

## Project Notes

- **Restart reminder**: When making changes to API routes, server services, or anything under `services/` or `app/api/`, remind Lou to restart the dev server (`npm run dev`) so changes go live.
- **No mock data**: This is a live system. Do not use mock fallbacks. If a data source fails, return empty results with honest status — never inject fake data.
- **RSS/intake is the live data source**: The information intake pipeline (`services/informationIntake/`) fetches real RSS feeds. The learning analysis pulls from this live data, not just manually-entered outcomes.
- **Check config before asking**: Before asking Lou about env vars, API URLs, ports, or settings, read the relevant files first (`.env.local`, `.env`, `appsettings.json`, `appsettings.Development.json`, `launchSettings.json`, `next.config.ts`, `package.json`). Only ask if the information isn't in any config file.
- **Verify before suggesting**: NEVER suggest SQL queries, API calls, column names, table names, or code snippets from memory or assumption. ALWAYS read the actual source code first — migration files, repository classes, model definitions, controller routes — to get the real names. If you don't know a table or column name, look it up in `Services/Supabase/ResearchRepository.cs` (MapPrediction, MapOutcome, etc.) or the migration SQL files before giving Lou a query.
- **Every new page must have proper UI states**: Every new page/screen MUST handle all of these states on the page itself: (1) Loading state — use `FullScreenLoader` from `@/components/FullScreenLoader` with a relevant message and steps, (2) Error state — show a clear error message, (3) Empty state — show a helpful message explaining what's missing and how to populate it, (4) Data state — the actual content. Look at existing pages (e.g. `app/results/page.tsx`, `app/dashboard/page.tsx`) for patterns. Never create a page that just renders nothing while data loads.
- **Long-running jobs MUST use the fire-and-forget pattern, not synchronous waits.** Anything that touches multiple Supabase rows, multiple MarketData.app / Twelve Data ticker fetches, the morning scan, EOD evaluation, or the dynamic orchestrator can easily exceed 30s. Netlify functions, Azure App Service, and intermediate proxies will return 502 long before the .NET API actually finishes — the job is still running, the user just sees an error. **Never** write a UI button that posts to a `/api/jobs/*` route with `AbortSignal.timeout(290_000)` and waits for the body. The correct pattern, already implemented for `run-weekly-research`: (1) the Next.js proxy fires the request with a short 10s timeout just to confirm the .NET side accepted it, returns `{ status: 'started' }` on `TimeoutError`; (2) the .NET handler records job state via `JobStatusTracker` (see `Services/JobStatusTracker.cs`) before doing the work in a background task; (3) the UI polls `/api/jobs/status` every 5s and shows progress, then renders the final summary when state flips to `completed` or `failed`. When adding new long-running job routes — including `run-dynamic-morning-picks`, `run-dynamic-eod-review`, `run-dynamic-learning-update`, and anything that scans an option chain per ticker — add them to `FIRE_AND_FORGET_JOBS` in `app/api/jobs/trigger/route.ts` and wire JobStatusTracker on the .NET side. If you skip this, expect 502s in production even when the job actually succeeded.

## Supabase Database Tables (actual column names)

### research_runs
`id`, `run_type` (morning_scan, end_of_day_review, learning_update, weekly_research), `status`, `started_at`, `completed_at`, `summary`, `error_message`, `metadata`

### prediction_candidates
`id`, `run_id`, `ticker`, `prediction_type` (bullish/bearish/neutral), `asset_type`, `time_window`, `confidence_score`, `importance_score`, `risk_score`, `entry_reference_price`, `bullish_case`, `bearish_case`, `prediction_reason`, `invalidation_rule`, `data_sources_used`, `missing_data_warnings`, `status` (open/evaluated/expired), `created_at`

### prediction_inputs
`id`, `prediction_id`, `input_type`, `input_data`, `created_at`

### prediction_outcomes
`id`, `prediction_id`, `evaluation_time`, `start_price`, `close_price`, `high_after_prediction`, `low_after_prediction`, `actual_move_percent`, `outcome_direction`, `was_correct`, `score`, `notes`

### signal_performance
`id`, `signal_name`, `total_predictions`, `correct_predictions`, `accuracy`, `avg_confidence_when_correct`, `avg_confidence_when_wrong`, `last_updated`

### signal_weights
`id`, `signal_name`, `weight`, `updated_at`, `reason`

### learning_insights
`id`, `run_id`, `insight_type`, `insight_text`, `action_suggested`, `created_at`

### paper_option_candidates
`id`, `prediction_id`, `ticker`, `option_symbol`, `side` (call/put), `strike`, `expiration`, `dte_at_entry`, `entry_underlying_price`, `entry_bid`, `entry_ask`, `entry_mid`, `entry_iv`, `entry_delta`, `entry_open_interest`, `entry_volume`, `contract_score`, `selection_reason`, `status` (open/closed/expired), `created_at`

### paper_option_outcomes
`id`, `paper_candidate_id`, `evaluation_time`, `current_underlying_price`, `current_bid`, `current_ask`, `current_mid`, `current_iv`, `current_delta`, `current_open_interest`, `current_volume`, `paper_pnl_per_contract`, `paper_pnl_percent`, `underlying_move_percent`, `iv_change`, `outcome_summary`, `created_at`

### pg_cron jobs
Column is `jobname` (not `name`). Query: `SELECT jobname, schedule, command FROM cron.job`

### Azure .NET API
Deployed at: `https://stock-research-agent-api-lsmart-ghhwebetfycxgrf8.centralus-01.azurewebsites.net`
Edge Functions use `DOTNET_API_BASE_URL` secret to reach it.
