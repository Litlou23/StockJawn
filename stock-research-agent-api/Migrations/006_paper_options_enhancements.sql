-- Migration: Paper Options Enhancements
-- Adds enhanced columns to paper_option_candidates and paper_option_outcomes,
-- creates option_learning_stats table for tracking paper option performance.

-- -----------------------------------------------------------------------
-- paper_option_candidates: add enhanced columns
-- -----------------------------------------------------------------------

ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS provider text default 'marketdata';
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS entry_last double precision default 0;
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS entry_gamma double precision default 0;
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS entry_theta double precision default 0;
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS entry_vega double precision default 0;
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS estimated_contract_cost double precision default 0;
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS spread_percent double precision default 0;
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS duration_bucket text default 'system_recommended';
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS price_bucket text;
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS data_delay_label text;
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS rank integer default 0;
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS warnings_json jsonb;
ALTER TABLE paper_option_candidates ADD COLUMN IF NOT EXISTS raw_provider_data_json jsonb;

-- Update status constraint to allow 'evaluated'
ALTER TABLE paper_option_candidates DROP CONSTRAINT IF EXISTS paper_option_candidates_status_check;
ALTER TABLE paper_option_candidates ADD CONSTRAINT paper_option_candidates_status_check
    CHECK (status IN ('open', 'closed', 'expired', 'evaluated'));

-- -----------------------------------------------------------------------
-- paper_option_outcomes: add enhanced columns
-- -----------------------------------------------------------------------

ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS prediction_id uuid references prediction_candidates(id);
ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS ticker text;
ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS option_symbol text;
ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS current_last double precision default 0;
ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS direction_correct boolean;
ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS contract_profitable boolean;
ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS spread_still_acceptable boolean;
ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS volume_still_acceptable boolean;
ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS outcome_score double precision default 0;
ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS lesson text;
ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS warnings_json jsonb;
ALTER TABLE paper_option_outcomes ADD COLUMN IF NOT EXISTS raw_provider_data_json jsonb;

-- -----------------------------------------------------------------------
-- option_learning_stats: new table
-- -----------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS option_learning_stats (
    id uuid primary key default gen_random_uuid(),
    stat_type text not null,
    stat_key text not null,
    total_candidates integer default 0,
    profitable_candidates integer default 0,
    win_rate double precision default 0,
    average_option_move_percent double precision default 0,
    average_underlying_move_percent double precision default 0,
    average_outcome_score double precision default 0,
    last_updated_at timestamptz default now(),
    UNIQUE(stat_type, stat_key)
);

CREATE INDEX IF NOT EXISTS idx_option_learning_stats_type_key ON option_learning_stats(stat_type, stat_key);
