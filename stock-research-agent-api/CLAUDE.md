## Project Notes

- **No mock data**: This is a live system. Do not use mock fallbacks. If a data source fails, return empty results with honest status — never inject fake data.
- **Check config before asking**: Before asking Lou about env vars, API URLs, ports, or settings, read the relevant files first (`appsettings.json`, `appsettings.Development.json`, `launchSettings.json`). Only ask if the information isn't in any config file.
- **Verify before suggesting**: NEVER suggest SQL queries, API calls, column names, table names, or code snippets from memory or assumption. ALWAYS read the actual source code first — migration files, `Services/Supabase/ResearchRepository.cs` (MapPrediction, MapOutcome, MapSignalPerformance, etc.), model definitions in `Models/ResearchEngineModels.cs`, controller routes — to get the real names. If you don't know a table or column name, LOOK IT UP before giving Lou a query.
- **Security**: Do not hardcode API keys. Do not expose API keys in frontend code. Do not log API keys. Protect job routes with JOB_SECRET header.
- **StockFit is fundamentals only, never technicals.** `StockFitProvider` handles company news, SEC filings (8-K/10-Q/10-K/13D/13F/Form 4), earnings calendar, key metrics, insider trades, institutional ownership. Do NOT reroute quotes, intraday bars, technical indicators, or option chains through StockFit — those stay with Twelve Data (`TwelveDataProvider`) and MarketData.app (`MarketDataOptionsProvider`). If `STOCKFIT_API_KEY` is not set the provider is marked unavailable and every call returns `{ status: 0, warnings: ['stockfit_not_configured'] }` — never fake filings, earnings dates, insider trades, or metrics. Endpoint paths are configurable via `STOCKFIT_PATH_NEWS`, `_FILINGS`, `_EARNINGS`, `_METRICS`, `_INSIDER`, `_INST` env vars (defaults follow a `/{endpoint}/{ticker}` convention). Base URL is `STOCKFIT_BASE_URL` (defaults `https://api.stockfit.io/v1`). Auth mode is `STOCKFIT_AUTH_MODE` (`header` X-API-Key default, `bearer`, or `query` ?apikey=). If the real docs require different paths, override those env vars instead of editing the client. Never log the raw key; debug endpoints use `MaskKey()` before echoing URLs.
- **Long-running endpoints MUST be fire-and-forget + JobStatusTracker, not synchronous waits.** Anything that loops over the watchlist, hits MarketData.app per ticker, runs the morning scan, evaluates open candidates, or executes the dynamic orchestrator will routinely exceed Azure App Service's 230s HTTP idle timeout and Netlify's function limit. Even when the work succeeds, callers get 502 because the proxy chain dropped the connection. Required pattern (already used for `RunWeeklyResearchAsync`): (1) the controller validates `x-job-secret`, records job start via `JobStatusTracker`, kicks off the actual work as a `_ = Task.Run(async () => { ... })`, and returns `Accepted(202)` with `{ status: "started", jobId }` immediately. (2) The background task updates `JobStatusTracker` on every milestone (`InProgress` → `Completed` / `Failed`, with `Summary` text and `DurationSeconds`). (3) A separate `GET /api/jobs/status` returns the in-memory state for the UI to poll. **All three dynamic orchestrator endpoints** — `run-dynamic-morning-picks`, `run-dynamic-eod-review`, `run-dynamic-learning-update` — and any future endpoint that touches more than a single ticker or row MUST be retrofitted to this pattern. If you write a controller method that awaits the orchestrator inline and returns the result, expect 502s in production even when the job succeeded. The user sees an error, the work is still running, and the UI never shows the outcome.

## Supabase Database Tables (actual column names from ResearchRepository.cs)

### research_runs
`id`, `run_type` (morning_scan, end_of_day_review, learning_update, weekly_research), `status`, `started_at`, `completed_at`, `summary`, `error_message`, `metadata`

### prediction_candidates
`id`, `run_id`, `ticker`, `prediction_type` (bullish/bearish/neutral_no_edge/neutral_range_bound/neutral_high_volatility/watch_only/rejected/unavailable), `asset_type`, `time_window`, `confidence_score`, `importance_score`, `risk_score`, `entry_reference_price`, `atr14`, `atr_percent`, `timeframe_multiplier`, `signal_modifier`, `expected_move_dollar`, `expected_move_percent`, `predicted_price`, `predicted_move_percent`, `projected_price_low`, `projected_price_high`, `target_price`, `stop_price`, `invalidation_price`, `support_level`, `resistance_level`, `risk_reward_ratio`, `price_prediction_method`, `price_prediction_warnings`, `bullish_case`, `bearish_case`, `prediction_reason`, `invalidation_rule`, `data_sources_used`, `missing_data_warnings`, `status` (open/evaluated/expired), `created_at`

### prediction_inputs
`id`, `prediction_id`, `input_type`, `input_data`, `created_at`

### prediction_outcomes
`id`, `prediction_id`, `evaluation_time`, `start_price`, `close_price`, `high_after_prediction`, `low_after_prediction`, `percent_move`, `direction_correct`, `predicted_price`, `predicted_move_percent`, `projected_price_low`, `projected_price_high`, `price_accuracy_percent`, `price_prediction_error_percent`, `was_in_projected_zone`, `target_hit`, `stop_hit`, `invalidation_hit`, `max_favorable_percent`, `max_adverse_percent`, `outcome_score`, `outcome_summary`, `lesson`, `created_at`

### research_signal_performance
`id`, `signal_name`, `total_predictions`, `correct_predictions`, `accuracy`, `avg_confidence_when_correct`, `avg_confidence_when_wrong`, `last_updated`

### research_scoring_weights
`id`, `signal_name`, `weight`, `updated_at`, `reason`

### learning_insights
`id`, `run_id`, `insight_type`, `insight_text`, `action_suggested`, `created_at`

### paper_option_candidates
`id`, `prediction_id`, `ticker`, `option_symbol`, `side` (call/put), `strike`, `expiration`, `dte_at_entry`, `entry_underlying_price`, `entry_bid`, `entry_ask`, `entry_mid`, `entry_iv`, `entry_delta`, `entry_open_interest`, `entry_volume`, `contract_score`, `selection_reason`, `status` (open/closed/expired), `created_at`

### paper_option_outcomes
`id`, `paper_candidate_id`, `evaluation_time`, `current_underlying_price`, `current_bid`, `current_ask`, `current_mid`, `current_iv`, `current_delta`, `current_open_interest`, `current_volume`, `paper_pnl_per_contract`, `paper_pnl_percent`, `underlying_move_percent`, `iv_change`, `outcome_summary`, `created_at`

### portfolio_challenges
`id`, `name`, `starting_balance`, `current_balance`, `target_balance`, `current_cash`, `buying_power`, `realized_profit`, `unrealized_profit`, `total_return`, `percent_return`, `number_of_trades`, `winning_trades`, `losing_trades`, `win_rate`, `status` (active/completed/paused/abandoned), `portfolio_mode` (swing_trading/day_trading/options_only/stock_only/mixed), `risk_profile` (conservative/moderate/aggressive), `notes`, `created_at`, `updated_at`

### portfolio_positions
`id`, `portfolio_id` (FK → portfolio_challenges.id), `prediction_id`, `ticker`, `asset_type` (stock/option), `entry_date`, `exit_date`, `entry_price`, `exit_price`, `quantity`, `dollars_invested`, `dollars_returned`, `profit_loss`, `percent_gain`, `reason_entered`, `reason_exited`, `status` (open/closed/cancelled), `created_at`, `updated_at`

### pg_cron jobs
Column is `jobname` (not `name`). Query: `SELECT jobname, schedule, command FROM cron.job`

## Deployment

- Azure .NET API: `https://stock-research-agent-api-lsmart-ghhwebetfycxgrf8.centralus-01.azurewebsites.net`
- Edge Functions use `DOTNET_API_BASE_URL` Supabase secret to reach the Azure API
- Edge Functions: `supabase/functions/` — morning-scan, end-of-day-review, learning-update, weekly-research
