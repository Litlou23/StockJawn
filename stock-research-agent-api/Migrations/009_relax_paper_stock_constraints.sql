-- Relax check constraints on paper_stock_candidates to support new prediction types and timeframes.

-- 1. Drop old prediction_type constraint
ALTER TABLE paper_stock_candidates
DROP CONSTRAINT paper_stock_candidates_prediction_type_check;

-- 2. Add updated prediction_type constraint with all types
ALTER TABLE paper_stock_candidates
ADD CONSTRAINT paper_stock_candidates_prediction_type_check
CHECK (prediction_type IN (
    'bullish', 'bearish',
    'neutral', 'neutral_no_edge', 'neutral_range_bound', 'neutral_high_volatility',
    'watch_only', 'rejected', 'unavailable'
));

-- 3. Drop old timeframe constraint
ALTER TABLE paper_stock_candidates
DROP CONSTRAINT paper_stock_candidates_timeframe_check;

-- 4. Add updated timeframe constraint with long-term windows
ALTER TABLE paper_stock_candidates
ADD CONSTRAINT paper_stock_candidates_timeframe_check
CHECK (timeframe IN (
    'intraday', '1_day', '2_day', '1_week',
    'one_day', 'two_day', 'one_week',
    'one_month', 'three_month', 'six_month', 'one_year'
));
