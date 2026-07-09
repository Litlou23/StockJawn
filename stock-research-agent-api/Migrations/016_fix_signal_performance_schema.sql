-- Migration 016: Fix research_signal_performance schema
-- The table was missing a direction column and had an overly restrictive
-- signal_type CHECK constraint, preventing the learning engine from
-- writing per-direction stats and non-technical signal types.

-- 1. Add direction column with default 'all'
ALTER TABLE research_signal_performance
ADD COLUMN IF NOT EXISTS direction TEXT NOT NULL DEFAULT 'all';

-- 2. Drop the old unique constraint on signal_name only
ALTER TABLE research_signal_performance
DROP CONSTRAINT IF EXISTS research_signal_performance_signal_name_key;

-- 3. Add new unique constraint on (signal_name, direction)
ALTER TABLE research_signal_performance
ADD CONSTRAINT research_signal_performance_signal_name_direction_key
UNIQUE (signal_name, direction);

-- 4. Drop the overly restrictive signal_type CHECK constraint
ALTER TABLE research_signal_performance
DROP CONSTRAINT IF EXISTS research_signal_performance_signal_type_check;

-- 5. Add a broader CHECK that matches what the code actually writes
ALTER TABLE research_signal_performance
ADD CONSTRAINT research_signal_performance_signal_type_check
CHECK (signal_type IN ('technical', 'catalyst', 'market_context', 'volume',
                       'news_sentiment', 'scoring_bucket', 'research'));
