-- Migration 011: Add score_debug_json column for scoring breakdown storage
-- Stores the full ScoringBreakdown object as JSON for debugging and calibration

ALTER TABLE prediction_candidates
ADD COLUMN IF NOT EXISTS score_debug_json TEXT;

COMMENT ON COLUMN prediction_candidates.score_debug_json IS 'JSON-serialized ScoringBreakdown with all bucket scores, factors, caps, and indicator lists';
