-- Migration 018: Fix prediction_candidates time_window CHECK constraint
-- The code's DetermineTimeWindow can return '1_month', '3_month', '6_month', '1_year'
-- but the CHECK only allowed 'intraday', '1_day', '3_day', '1_week'.
-- This caused ALL predictions to silently fail to save when the time window
-- was anything longer than 1 week.

ALTER TABLE prediction_candidates
DROP CONSTRAINT IF EXISTS prediction_candidates_time_window_check;

ALTER TABLE prediction_candidates
ADD CONSTRAINT prediction_candidates_time_window_check
CHECK (time_window IN ('intraday', '1_day', '2_day', '3_day', '1_week',
                       '1_month', '3_month', '6_month', '1_year'));
