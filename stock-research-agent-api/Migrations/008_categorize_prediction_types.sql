-- Step 1: Drop the old check constraint that only allows (bullish, bearish, neutral)
ALTER TABLE prediction_candidates
DROP CONSTRAINT prediction_candidates_prediction_type_check;

-- Step 2: Add updated check constraint with all new prediction types
ALTER TABLE prediction_candidates
ADD CONSTRAINT prediction_candidates_prediction_type_check
CHECK (prediction_type IN (
    'bullish', 'bearish',
    'neutral', 'neutral_no_edge', 'neutral_range_bound', 'neutral_high_volatility',
    'watch_only', 'rejected', 'unavailable'
));

-- Step 3: Backfill legacy 'neutral' to 'neutral_no_edge'
-- No data is deleted. Existing outcomes stay as-is.
UPDATE prediction_candidates
SET prediction_type = 'neutral_no_edge'
WHERE prediction_type = 'neutral';
