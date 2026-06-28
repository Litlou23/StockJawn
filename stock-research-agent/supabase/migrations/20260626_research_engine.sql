-- Research Engine tables
-- Run this in the Supabase SQL editor or via `supabase db push`

-- 1. research_runs — each morning scan, EOD review, or learning update
CREATE TABLE IF NOT EXISTS research_runs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  run_type TEXT NOT NULL CHECK (run_type IN ('morning_scan', 'end_of_day_review', 'learning_update')),
  status TEXT NOT NULL DEFAULT 'started' CHECK (status IN ('started', 'completed', 'failed')),
  started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  completed_at TIMESTAMPTZ,
  summary TEXT,
  errors JSONB DEFAULT '[]'::jsonb,
  predictions_generated INT DEFAULT 0,
  predictions_evaluated INT DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_research_runs_type_date ON research_runs (run_type, started_at DESC);

-- 2. market_snapshots — point-in-time data captured during a run
CREATE TABLE IF NOT EXISTS market_snapshots (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  run_id UUID NOT NULL REFERENCES research_runs(id) ON DELETE CASCADE,
  ticker TEXT NOT NULL,
  quote JSONB,
  recent_bars JSONB DEFAULT '[]'::jsonb,
  technical_context JSONB,
  news_context JSONB DEFAULT '[]'::jsonb,
  data_availability JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_market_snapshots_run ON market_snapshots (run_id);
CREATE INDEX IF NOT EXISTS idx_market_snapshots_ticker ON market_snapshots (ticker, created_at DESC);

-- 3. prediction_candidates — structured predictions/watchlist items
CREATE TABLE IF NOT EXISTS prediction_candidates (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  run_id UUID NOT NULL REFERENCES research_runs(id) ON DELETE CASCADE,
  ticker TEXT NOT NULL,
  prediction_type TEXT NOT NULL CHECK (prediction_type IN ('bullish', 'bearish', 'neutral', 'watch_only')),
  asset_type TEXT NOT NULL DEFAULT 'stock' CHECK (asset_type IN ('stock', 'option_watch_candidate')),
  time_window TEXT NOT NULL CHECK (time_window IN ('intraday', '1_day', '3_day', '1_week')),
  confidence_score NUMERIC NOT NULL DEFAULT 0,
  importance_score NUMERIC NOT NULL DEFAULT 0,
  risk_score NUMERIC NOT NULL DEFAULT 0,
  entry_reference_price NUMERIC,
  bullish_case TEXT NOT NULL DEFAULT '',
  bearish_case TEXT NOT NULL DEFAULT '',
  prediction_reason TEXT NOT NULL DEFAULT '',
  invalidation_rule TEXT NOT NULL DEFAULT '',
  data_sources_used JSONB DEFAULT '[]'::jsonb,
  missing_data_warnings JSONB DEFAULT '[]'::jsonb,
  status TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'evaluated', 'expired')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_predictions_run ON prediction_candidates (run_id);
CREATE INDEX IF NOT EXISTS idx_predictions_ticker ON prediction_candidates (ticker, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_predictions_status ON prediction_candidates (status);

-- 4. prediction_inputs — what data backed each prediction
CREATE TABLE IF NOT EXISTS prediction_inputs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  prediction_id UUID NOT NULL REFERENCES prediction_candidates(id) ON DELETE CASCADE,
  input_type TEXT NOT NULL CHECK (input_type IN ('market_data', 'news', 'catalyst', 'sec_filing', 'technical', 'prior_lesson')),
  source_name TEXT NOT NULL,
  source_url TEXT,
  source_record_id TEXT,
  summary TEXT NOT NULL DEFAULT '',
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_prediction_inputs_prediction ON prediction_inputs (prediction_id);

-- 5. prediction_outcomes — how predictions played out
CREATE TABLE IF NOT EXISTS prediction_outcomes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  prediction_id UUID NOT NULL REFERENCES prediction_candidates(id) ON DELETE CASCADE,
  evaluation_time TIMESTAMPTZ NOT NULL DEFAULT now(),
  start_price NUMERIC,
  close_price NUMERIC,
  high_after_prediction NUMERIC,
  low_after_prediction NUMERIC,
  percent_move NUMERIC,
  direction_correct BOOLEAN,
  invalidation_hit BOOLEAN,
  outcome_score NUMERIC,
  outcome_summary TEXT,
  lesson TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_outcomes_prediction ON prediction_outcomes (prediction_id);

-- 6. research_signal_performance — aggregated stats per signal
CREATE TABLE IF NOT EXISTS research_signal_performance (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  signal_name TEXT NOT NULL UNIQUE,
  signal_type TEXT NOT NULL CHECK (signal_type IN ('catalyst', 'technical', 'market_context', 'volume', 'news_sentiment')),
  total_predictions INT NOT NULL DEFAULT 0,
  correct_predictions INT NOT NULL DEFAULT 0,
  accuracy NUMERIC NOT NULL DEFAULT 0,
  average_outcome_score NUMERIC NOT NULL DEFAULT 0,
  last_updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 7. research_scoring_weights — adjustable weights
CREATE TABLE IF NOT EXISTS research_scoring_weights (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  signal_name TEXT NOT NULL UNIQUE,
  weight NUMERIC NOT NULL DEFAULT 1.0,
  reason TEXT NOT NULL DEFAULT 'initial default',
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 8. learning_insights — lessons from outcome analysis
CREATE TABLE IF NOT EXISTS learning_insights (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  insight_type TEXT NOT NULL CHECK (insight_type IN ('ticker', 'signal', 'market_condition', 'risk_rule', 'prompt_rule')),
  summary TEXT NOT NULL,
  evidence TEXT NOT NULL DEFAULT '',
  action_recommendation TEXT NOT NULL DEFAULT '',
  confidence NUMERIC NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_learning_insights_type ON learning_insights (insight_type, created_at DESC);

-- Seed default scoring weights for the 14-ticker watchlist signals
INSERT INTO research_scoring_weights (signal_name, weight, reason) VALUES
  ('catalyst_earnings', 1.5, 'Earnings catalysts historically high-impact'),
  ('catalyst_product_launch', 1.2, 'Product launches drive momentum'),
  ('catalyst_regulatory', 1.3, 'Regulatory events create sharp moves'),
  ('catalyst_partnership', 1.0, 'Partnership news moderate impact'),
  ('catalyst_macro', 0.8, 'Macro catalysts broad, hard to trade'),
  ('technical_trend', 1.0, 'Trend direction baseline signal'),
  ('technical_momentum', 1.1, 'Momentum confirmation useful'),
  ('technical_volume', 1.2, 'Volume confirms conviction'),
  ('technical_ma_position', 0.9, 'MA position lagging indicator'),
  ('news_sentiment_bullish', 1.0, 'Bullish news sentiment baseline'),
  ('news_sentiment_bearish', 1.1, 'Bearish sentiment slightly more predictive'),
  ('news_volume', 1.0, 'Number of news items as attention signal')
ON CONFLICT (signal_name) DO NOTHING;
