-- 019: Clean up bad data from false paper option losses and contaminated learning stats.
--
-- Problems:
--   1. paper_option_outcomes recorded LOSS with 0% P&L when chain data was unavailable
--   2. option_learning_stats trained on those false losses
--   3. prediction_signal_observations and scoring_weight_overrides may reflect bad data
--   4. research_signal_performance aggregates need recalculation
--
-- Strategy: archive bad outcomes, delete them, reset affected candidates to "open",
-- truncate derived learning tables so the learning engine rebuilds from clean data.
-- All derived tables use UPSERT/INSERT (cold-start safe, verified).

BEGIN;

-- Step 1: Truncate derived tables first (child→parent order to avoid FK issues)
TRUNCATE TABLE learning_insights;
TRUNCATE TABLE scoring_weight_overrides;
TRUNCATE TABLE research_signal_performance;
TRUNCATE TABLE prediction_signal_observations;
TRUNCATE TABLE option_learning_stats;

-- Step 2: Archive bad outcomes before deleting (forensic history + rollback safety)
CREATE TABLE IF NOT EXISTS bad_option_outcomes_archive (LIKE paper_option_outcomes INCLUDING ALL);

INSERT INTO bad_option_outcomes_archive
SELECT * FROM paper_option_outcomes
WHERE paper_pnl_percent = 0
  AND underlying_move_percent = 0
  AND (current_bid IS NULL OR current_bid = 0);

-- Step 3: Delete false paper option outcomes
-- Note: parens around the OR are critical — without them current_bid=0 matches everything
DELETE FROM paper_option_outcomes
WHERE paper_pnl_percent = 0
  AND underlying_move_percent = 0
  AND (current_bid IS NULL OR current_bid = 0);

-- Step 4: Reset paper option candidates that were falsely evaluated back to "open"
UPDATE paper_option_candidates
SET status = 'open'
WHERE status = 'evaluated'
  AND id NOT IN (
    SELECT DISTINCT paper_candidate_id FROM paper_option_outcomes
  );

COMMIT;
