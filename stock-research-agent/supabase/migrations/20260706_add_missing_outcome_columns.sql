-- Add columns that OutcomeEvaluator writes but were never migrated.
-- These were added to the C# insert object without a corresponding DDL change,
-- causing Supabase PostgREST to reject the insert silently.

ALTER TABLE prediction_outcomes
  ADD COLUMN IF NOT EXISTS predicted_direction text,
  ADD COLUMN IF NOT EXISTS bullish_score_at_prediction double precision,
  ADD COLUMN IF NOT EXISTS bearish_score_at_prediction double precision,
  ADD COLUMN IF NOT EXISTS predicted_move_percent double precision,
  ADD COLUMN IF NOT EXISTS price_accuracy_percent double precision;
