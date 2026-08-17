-- Migration 028: Meta-labeler backtest integration + enforcement config.
-- Adds the columns backtest_trades needs to record meta-probability, and
-- documents (via a hint row) the enforcement threshold key.
--
-- Idempotent — safe to re-run.

-- ============================================================================
-- 1. backtest_trades.meta_probability + meta_model_version
--    Records what the meta-labeler said about each simulated trade so we can
--    later compare "with meta filter" vs "without" by re-slicing the same run.
-- ============================================================================

alter table backtest_trades
    add column if not exists meta_probability double precision,
    add column if not exists meta_model_version integer;

create index if not exists idx_backtest_trades_meta_prob
    on backtest_trades(meta_probability)
    where meta_probability is not null;

-- ============================================================================
-- 2. Enforcement threshold override — how to enable meta-labeler gating.
--
--    The live pipeline reads scoring_weight_overrides where
--    signal_name = 'meta_labeler_enforce_threshold' AND status = 'active',
--    then uses effective_weight as the probability floor. When no such row
--    exists (or status != 'active'), the pipeline runs in advisory mode.
--
--    NO seed row is inserted here — the status check constraint on
--    scoring_weight_overrides rejects our documentation-placeholder statuses.
--    Admins enable enforcement by running:
--
--      INSERT INTO scoring_weight_overrides
--        (signal_name, base_weight, adjustment_percent, effective_weight,
--         confidence, sample_size, status, reason, last_updated)
--      VALUES ('meta_labeler_enforce_threshold', 0.55, 0, 0.55, 1, 0,
--              'active', 'meta-labeler enforcement enabled', now())
--      ON CONFLICT (signal_name) DO UPDATE
--        SET effective_weight = EXCLUDED.effective_weight,
--            status = 'active', last_updated = now();
--
--    Disable it by setting status to any non-'active' value.
-- ============================================================================
