-- Migration 026: Parameter sweeps for the backtest engine (Phase 5)
--
-- A parameter sweep runs the same date range through the Cartesian product
-- of user-supplied parameter arrays, ranks the resulting backtest_runs by
-- expectancy / profit factor / max drawdown, and stores the best combination
-- so the operator can see "which R:R × trail % × confidence-floor produced
-- the highest expectancy over Q2 2026." Each child run still lives in
-- backtest_runs — the sweep row links them together and stores the ranking.

-- ============================================================================
-- 1. backtest_sweeps — parent record for each sweep invocation.
--    parameter_space is the full spec ({ min_confidence: [30,35,40], ... })
--    best_parameters snapshots the winning row's parameters JSON.
--    ranking JSONB is an ordered array of { run_id, params, metrics } after
--    all child runs complete.
-- ============================================================================

create table if not exists backtest_sweeps (
    id                    uuid primary key default gen_random_uuid(),
    start_date            date not null,
    end_date              date not null,
    parameter_space       jsonb not null,     -- { key: [values...] }
    combination_count     integer,
    status                text not null default 'running'
                            check (status in ('running','completed','failed','cancelled')),
    runs_completed        integer default 0,
    runs_failed           integer default 0,
    best_run_id           uuid,               -- FK not enforced to allow deletion of child runs
    best_expectancy       double precision,
    best_profit_factor    double precision,
    best_parameters       jsonb,
    ranking               jsonb,              -- ordered array of ranked runs
    summary               text,
    error_message         text,
    created_at            timestamptz not null default now(),
    completed_at          timestamptz
);

create index if not exists idx_backtest_sweeps_status
    on backtest_sweeps(status);
create index if not exists idx_backtest_sweeps_created_at
    on backtest_sweeps(created_at desc);

-- ============================================================================
-- 2. backtest_runs.sweep_id — links each run to its parent sweep (nullable
--    because standalone runs are still allowed).
-- ============================================================================

alter table backtest_runs
    add column if not exists sweep_id uuid
        references backtest_sweeps(id) on delete set null;

create index if not exists idx_backtest_runs_sweep_id
    on backtest_runs(sweep_id) where sweep_id is not null;
