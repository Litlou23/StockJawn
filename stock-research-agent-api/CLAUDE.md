## Project Notes

- **No mock data**: This is a live system. Do not use mock fallbacks. If a data source fails, return empty results with honest status — never inject fake data.
- **Check config before asking**: Before asking Lou about env vars, API URLs, ports, or settings, read the relevant files first (`appsettings.json`, `appsettings.Development.json`, `launchSettings.json`). Only ask if the information isn't in any config file.
- **Verify before suggesting**: NEVER suggest SQL queries, API calls, column names, table names, or code snippets from memory or assumption. ALWAYS read the actual source code first — migration files, `Services/Supabase/ResearchRepository.cs` (MapPrediction, MapOutcome, MapSignalPerformance, etc.), model definitions in `Models/ResearchEngineModels.cs`, controller routes — to get the real names. If you don't know a table or column name, LOOK IT UP before giving Lou a query.
- **Security**: Do not hardcode API keys. Do not expose API keys in frontend code. Do not log API keys. Protect job routes with JOB_SECRET header.

## Supabase Database Tables (actual column names from ResearchRepository.cs)

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

## Deployment

- Azure .NET API: `https://stock-research-agent-api-lsmart-ghhwebetfycxgrf8.centralus-01.azurewebsites.net`
- Edge Functions use `DOTNET_API_BASE_URL` Supabase secret to reach the Azure API
- Edge Functions: `supabase/functions/` — morning-scan, end-of-day-review, learning-update, weekly-research
