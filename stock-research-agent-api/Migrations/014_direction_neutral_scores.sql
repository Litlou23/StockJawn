-- Migration 014: Direction-neutral dual scoring
--
-- Adds independent bullish/bearish scores so the prediction engine
-- evaluates both directions equally instead of treating bearish as
-- negative bullish.

-- prediction_candidates: dual scores
ALTER TABLE prediction_candidates
    ADD COLUMN IF NOT EXISTS bullish_score double precision,
    ADD COLUMN IF NOT EXISTS bearish_score double precision,
    ADD COLUMN IF NOT EXISTS winning_direction text,
    ADD COLUMN IF NOT EXISTS direction_confidence double precision;

-- paper_stock_candidates: carry dual scores from prediction
ALTER TABLE paper_stock_candidates
    ADD COLUMN IF NOT EXISTS bullish_score double precision,
    ADD COLUMN IF NOT EXISTS bearish_score double precision,
    ADD COLUMN IF NOT EXISTS winning_direction text;

-- prediction_outcomes: record what scores were at prediction time for learning
ALTER TABLE prediction_outcomes
    ADD COLUMN IF NOT EXISTS predicted_direction text,
    ADD COLUMN IF NOT EXISTS bullish_score_at_prediction double precision,
    ADD COLUMN IF NOT EXISTS bearish_score_at_prediction double precision;

-- research_signal_performance: per-direction tracking
ALTER TABLE research_signal_performance
    ADD COLUMN IF NOT EXISTS direction text DEFAULT 'all';

-- Update existing rows to 'all' for backward compat
UPDATE research_signal_performance SET direction = 'all' WHERE direction IS NULL OR direction = 'both';

-- Drop old unique constraint on signal_name alone (if exists) and add composite
ALTER TABLE research_signal_performance
    DROP CONSTRAINT IF EXISTS research_signal_performance_signal_name_key;
ALTER TABLE research_signal_performance
    ADD CONSTRAINT research_signal_performance_signal_name_direction_key UNIQUE (signal_name, direction);

CREATE INDEX IF NOT EXISTS idx_prediction_candidates_direction
    ON prediction_candidates(winning_direction);

CREATE INDEX IF NOT EXISTS idx_paper_stock_candidates_direction
    ON paper_stock_candidates(winning_direction);

CREATE INDEX IF NOT EXISTS idx_signal_performance_direction
    ON research_signal_performance(direction);
