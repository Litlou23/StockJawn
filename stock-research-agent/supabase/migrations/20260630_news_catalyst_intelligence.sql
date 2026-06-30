-- News Catalyst Intelligence layer
-- Adds three tables on top of the existing research_engine + catalyst_items
-- infrastructure. No existing tables are modified.

-- 1. news_catalysts — one row per real catalyst extracted from a real intake item
CREATE TABLE IF NOT EXISTS news_catalysts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  source_item_id TEXT NOT NULL,
  ticker TEXT NOT NULL,
  company_name TEXT,
  headline TEXT NOT NULL DEFAULT '',
  summary TEXT NOT NULL DEFAULT '',
  source_name TEXT NOT NULL DEFAULT '',
  source_url TEXT NOT NULL DEFAULT '',
  published_at TIMESTAMPTZ NOT NULL,
  detected_event_types_json JSONB NOT NULL DEFAULT '[]'::jsonb,
  extracted_keywords_json JSONB NOT NULL DEFAULT '[]'::jsonb,
  sentiment TEXT NOT NULL CHECK (sentiment IN ('positive', 'negative', 'neutral', 'mixed', 'unknown')),
  catalyst_strength_score NUMERIC NOT NULL DEFAULT 0,
  source_reliability_score NUMERIC NOT NULL DEFAULT 0,
  freshness_score NUMERIC NOT NULL DEFAULT 0,
  ticker_relevance_score NUMERIC NOT NULL DEFAULT 0,
  confirmation_count INT NOT NULL DEFAULT 1,
  price_confirmation_status TEXT NOT NULL DEFAULT 'unavailable' CHECK (price_confirmation_status IN ('confirmed', 'not_confirmed', 'unavailable')),
  volume_confirmation_status TEXT NOT NULL DEFAULT 'unavailable' CHECK (volume_confirmation_status IN ('confirmed', 'not_confirmed', 'unavailable')),
  warnings_json JSONB NOT NULL DEFAULT '[]'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- de-dupe key: rerunning extraction on the same intake item + ticker should upsert
  CONSTRAINT news_catalysts_source_ticker_unique UNIQUE (source_item_id, ticker)
);

CREATE INDEX IF NOT EXISTS idx_news_catalysts_ticker_published ON news_catalysts (ticker, published_at DESC);
CREATE INDEX IF NOT EXISTS idx_news_catalysts_strength ON news_catalysts (catalyst_strength_score DESC);
CREATE INDEX IF NOT EXISTS idx_news_catalysts_published ON news_catalysts (published_at DESC);

-- 2. catalyst_prediction_links — connects a catalyst to a paper stock candidate and optional option candidate
CREATE TABLE IF NOT EXISTS catalyst_prediction_links (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  catalyst_id UUID NOT NULL REFERENCES news_catalysts(id) ON DELETE CASCADE,
  paper_stock_candidate_id UUID NOT NULL REFERENCES prediction_candidates(id) ON DELETE CASCADE,
  -- paper_option_candidate_id is intentionally NOT a foreign key:
  -- option candidates live in the .NET backend's own table and are referenced by id only.
  paper_option_candidate_id TEXT,
  ticker TEXT NOT NULL,
  influence_type TEXT NOT NULL CHECK (influence_type IN ('primary', 'supporting', 'risk', 'ignored')),
  influence_score NUMERIC NOT NULL DEFAULT 0,
  reason_linked TEXT NOT NULL DEFAULT '',
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_links_catalyst ON catalyst_prediction_links (catalyst_id);
CREATE INDEX IF NOT EXISTS idx_links_stock_candidate ON catalyst_prediction_links (paper_stock_candidate_id);
CREATE INDEX IF NOT EXISTS idx_links_option_candidate ON catalyst_prediction_links (paper_option_candidate_id);
CREATE INDEX IF NOT EXISTS idx_links_ticker ON catalyst_prediction_links (ticker, created_at DESC);

-- 3. catalyst_outcome_stats — rolled-up performance per (event_type, keyword, ticker)
CREATE TABLE IF NOT EXISTS catalyst_outcome_stats (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  event_type TEXT NOT NULL,
  keyword TEXT,                  -- NULL means "any keyword" (event-type-level row)
  ticker TEXT,                   -- NULL means "any ticker" (cross-ticker row)
  total_linked_predictions INT NOT NULL DEFAULT 0,
  successful_stock_predictions INT NOT NULL DEFAULT 0,
  successful_option_predictions INT NOT NULL DEFAULT 0,
  stock_win_rate NUMERIC NOT NULL DEFAULT 0,
  option_win_rate NUMERIC NOT NULL DEFAULT 0,
  average_stock_move_percent NUMERIC NOT NULL DEFAULT 0,
  average_option_move_percent NUMERIC NOT NULL DEFAULT 0,
  average_outcome_score NUMERIC NOT NULL DEFAULT 0,
  last_updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Unique composite that allows NULL keyword/ticker — Postgres treats NULLs
-- as distinct in UNIQUE by default, so we use a unique INDEX with NULLs NOT DISTINCT.
CREATE UNIQUE INDEX IF NOT EXISTS uq_catalyst_outcome_stats_dims
  ON catalyst_outcome_stats (event_type, keyword, ticker) NULLS NOT DISTINCT;

CREATE INDEX IF NOT EXISTS idx_catalyst_outcome_event ON catalyst_outcome_stats (event_type);
CREATE INDEX IF NOT EXISTS idx_catalyst_outcome_winrate ON catalyst_outcome_stats (stock_win_rate DESC);

-- Seed scoring weights for the new catalyst event types so the learning
-- engine has a baseline to adjust from. Skipped if already present.
INSERT INTO research_scoring_weights (signal_name, weight, reason) VALUES
  ('catalyst_earnings_beat', 1.3, 'Earnings beat — strong directional catalyst'),
  ('catalyst_earnings_miss', 1.3, 'Earnings miss — strong directional catalyst'),
  ('catalyst_guidance_raise', 1.2, 'Guidance raise — multi-day persistence'),
  ('catalyst_guidance_cut', 1.2, 'Guidance cut — multi-day persistence'),
  ('catalyst_analyst_upgrade', 1.0, 'Analyst upgrade — variable persistence'),
  ('catalyst_analyst_downgrade', 1.0, 'Analyst downgrade — variable persistence'),
  ('catalyst_partnership', 1.1, 'Partnership — short-term pop, fades fast'),
  ('catalyst_contract_win', 1.2, 'Contract win — concrete revenue driver'),
  ('catalyst_product_launch', 1.0, 'Product launch — depends on reception'),
  ('catalyst_ai_theme', 1.0, 'AI theme — broad sector tailwind'),
  ('catalyst_merger_acquisition', 1.3, 'M&A — sharp single-day move'),
  ('catalyst_stock_offering', 1.2, 'Stock offering — typically bearish dilution'),
  ('catalyst_debt_offering', 0.9, 'Debt offering — moderate, depends on rate'),
  ('catalyst_insider_buying', 1.0, 'Insider buying — modest positive signal'),
  ('catalyst_insider_selling', 0.7, 'Insider selling — often planned, weak signal'),
  ('catalyst_lawsuit', 1.0, 'Lawsuit — short-term negative'),
  ('catalyst_investigation', 1.2, 'Investigation — extended uncertainty'),
  ('catalyst_regulatory_approval', 1.3, 'Regulatory approval — clears overhang'),
  ('catalyst_regulatory_rejection', 1.3, 'Regulatory rejection — sharp negative'),
  ('catalyst_fda_event', 1.3, 'FDA event — biotech directional driver'),
  ('catalyst_management_change', 1.0, 'Management change — context-dependent'),
  ('catalyst_macro_event', 0.8, 'Macro event — diffuse impact'),
  ('catalyst_sector_rotation', 0.8, 'Sector rotation — flow-driven'),
  ('catalyst_earnings_upcoming', 0.9, 'Earnings upcoming — urgency not direction'),
  ('catalyst_unusual_news_volume', 0.8, 'Unusual news volume — attention proxy'),
  ('catalyst_general_positive_news', 0.6, 'Generic positive sentiment'),
  ('catalyst_general_negative_news', 0.6, 'Generic negative sentiment'),
  ('catalyst_unknown', 0.3, 'Unknown / unclassified — minimal influence')
ON CONFLICT (signal_name) DO NOTHING;
