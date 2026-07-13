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
`id`, `run_id`, `ticker`, `prediction_type` (bullish/bearish/neutral_no_edge/neutral_range_bound/neutral_high_volatility/watch_only/rejected/unavailable), `asset_type`, `time_window`, `confidence_score`, `importance_score`, `risk_score`, `entry_reference_price`, `atr14`, `atr_percent`, `timeframe_multiplier`, `signal_modifier`, `expected_move_dollar`, `expected_move_percent`, `predicted_price`, `predicted_move_percent`, `projected_price_low`, `projected_price_high`, `target_price`, `stop_price`, `invalidation_price`, `support_level`, `resistance_level`, `risk_reward_ratio`, `price_prediction_method`, `price_prediction_warnings`, `bullish_case`, `bearish_case`, `prediction_reason`, `invalidation_rule`, `data_sources_used`, `missing_data_warnings`, `status` (open/evaluated/expired), `score_debug_json`, `bullish_score`, `bearish_score`, `winning_direction`, `direction_confidence`, `created_at`

**score_debug_json structure** (JSON text, envelope `{"Breakdown": {...}}`):
- `BullishScore`, `BearishScore`, `WinningDirection`, `DirectionConfidence`
- `RawConfidence`, `CalibratedConfidence`, `DataQualityFactor`, `CalibrationFactor`
- `RiskScore`, `DecisionMargin` (normalized: `(W-L)/(W+L)`, 0=conflicted, 1=clear), `ClearDirection` (bool)
- `OppositionPenalty` (1.0 = no penalty, floor 0.6)
- `ConfidenceCap` (reason string if capped, e.g. `"Risk 75 ≥ 75 (dir clear, boost 3)"`)
- Legacy net scores: `TrendScore`, `MomentumScore`, `VolumeScore`, `VolatilitySetupScore`, `MarketContextScore`, `CatalystScore`, `LearningScore`
- Per-bucket bull/bear splits: `TrendBullish`/`TrendBearish`, `MomentumBullish`/`MomentumBearish`, `VolumeBullish`/`VolumeBearish`, etc.
- `AlignedBuckets`, `ConflictingBuckets`, `ConfirmationMultiplier`
- Research Universe integration: `ResearchUniverseInterestScore`, `ResearchUniverseEvidenceCount`, `ResearchUniverseState`, `HasResearchAsset`, `HistoricalVolatility`, `HistoricalAtrPercent`

### prediction_inputs
`id`, `prediction_id`, `input_type`, `input_data`, `created_at`

### prediction_outcomes
`id`, `prediction_id`, `evaluation_time`, `start_price`, `close_price`, `high_after_prediction`, `low_after_prediction`, `percent_move`, `direction_correct`, `predicted_price`, `predicted_move_percent`, `projected_price_low`, `projected_price_high`, `price_accuracy_percent`, `price_prediction_error_percent`, `was_in_projected_zone`, `target_hit`, `stop_hit`, `invalidation_hit`, `max_favorable_percent`, `max_adverse_percent`, `outcome_score`, `outcome_summary`, `lesson`, `created_at`

### research_signal_performance
`id`, `signal_name`, `total_predictions`, `correct_predictions`, `accuracy`, `avg_confidence_when_correct`, `avg_confidence_when_wrong`, `last_updated`

### research_scoring_weights
`id`, `signal_name`, `weight`, `updated_at`, `reason`

**Special weight overrides:**
- `risk_cap_boost` — auto-managed by Stage 3c self-tuning. Integer 0-15, added to risk-confidence caps when direction is clear. Max movement ±2 pts/day. Loosens caps when calibration error shows underconfidence, tightens when overconfident.

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

### cap_tuning_stats
`id`, `cap_reason` (UNIQUE — e.g. "Risk 75 ≥ 75 (dir clear, boost 0)"), `sample_size`, `accuracy`, `avg_confidence`, `avg_risk`, `avg_opposition_ratio`, `recommended_cap`, `current_cap`, `cap_delta`, `is_effective`, `analysis_notes`, `computed_at`, `applied_at`

Used by the self-tuning confidence cap system (LearningEngine Stage 3c). Nightly learning job groups resolved predictions by their `ConfidenceCap` reason from `score_debug_json`, measures direct calibration error (`accuracy - predictedProb`), and persists results here. Drives the `risk_cap_boost` weight override.

### research_timeline_events
`id`, `ticker`, `timestamp`, `event_type` (EvidenceAdded/Discovered/StatePromotion/ThesisUpdated/PredictionGenerated/PredictionOutcome/ScoreChange/Archived/VolumeSpike/CatalystEvent), `description`, `source`, `related_entity_id`, `related_entity_type`, `interest_score_snapshot`, `research_state_snapshot`, `thesis_snapshot`, `created_at`

Immutable "Git history" for each stock's research journey. Append-only — never updated or deleted. Used by Learning Engine to reconstruct thesis evolution.

### historical_research_profiles
`id`, `ticker`, `research_asset_id`, `built_at`, `historical_volatility`, `atr_percent`, `high_52_week`, `low_52_week`, `price_position_in_52_week_range`, `avg_earnings_move_percent`, `avg_analyst_upgrade_move_percent`, `avg_sec_filing_move_percent`, `avg_daily_volume_30d`, `avg_daily_volume_90d`, `sector`, `industry`, `relative_strength_30d`, `previous_prediction_count`, `previous_prediction_accuracy`, `avg_previous_confidence`, `pattern_summary`, `last_updated`, `refresh_count`, `last_refresh_reason`

Historical profile built when a stock first enters the Research Universe. Refreshable on a configurable schedule (default 90 days) or after significant corporate events (earnings, filings, regulatory events, insider activity). Unique on `ticker`.

### discovery_checkpoints
`id`, `checkpoint_name` (UNIQUE), `checkpoint_value`, `updated_at`

Persistent key-value store for discovery cycle checkpoints. The continuous discovery engine stores its last-processed timestamp here so it survives app restarts. Simple upsert on `checkpoint_name`.

### pg_cron jobs
Column is `jobname` (not `name`). Query: `SELECT jobname, schedule, command FROM cron.job`

## Research Universe → Prediction Pipeline Integration

The Morning Scan pipeline now consumes Research Universe data during prediction generation:

**Data flow:** `DailyResearchRunService.GetResearchCandidatesAsync()` returns full `ResearchAsset` objects (not just tickers) → `PredictionGenerator.GeneratePredictionsForWatchlistAsync` receives an asset lookup dictionary → `GeneratePredictionForTickerAsync` receives the `ResearchAsset` → builds `ResearchUniverseContext` (includes `HistoricalResearchProfile` data) → passes to `ScoringEngine.Evaluate` → `EvaluationContext.ResearchUniverse` is available to all evaluators.

**What's consumed during scoring:**
- `InterestScore`, `EvidenceCount`, `ResearchState` → boost `DataQualityFactor` in `ConfidenceEngine` (configurable via `research_universe_weight` scoring weight, default 1.0)
- `HistoricalVolatility` → caps confidence on highly volatile stocks (>40% annualized)
- `HistoricalAtrPercent` → sanity-checks live ATR in `ComputeAtrPriceForecast` (warns if live/historical ratio > 2x or < 0.5x)
- `PreviousPredictionAccuracy`, `PreviousPredictionCount` → available on `ResearchUniverseContext` for future use

**What's NOT consumed yet (stored for future use):**
- `CurrentThesis` — reserved for Learning and explanation improvements
- `ResearchTimeline` — reserved for Learning and explanation improvements

**Backward compatibility:** All new parameters are optional with null defaults. Watchlist-fallback tickers (when Research Universe is empty) pass through with `HasResearchAsset = false` and receive no research universe scoring adjustments.

## Deployment

- Azure .NET API: `https://stock-research-agent-api-lsmart-ghhwebetfycxgrf8.centralus-01.azurewebsites.net`
- Edge Functions use `DOTNET_API_BASE_URL` Supabase secret to reach the Azure API
- Edge Functions: `supabase/functions/` — morning-scan, end-of-day-review, learning-update, weekly-research
