-- Migration 029: Regime metadata on backtest tables.
--
-- Two additions:
--   1. backtest_runs.skipped_days   — how many days the trend-quality gate
--                                     told the engine to sit out. Lets the
--                                     UI show "traded 45 of 62 days".
--   2. backtest_trades.regime_*     — snapshot of SPY's trend signals on the
--                                     entry day of each trade. Used to answer
--                                     "which regime were the winners taken in?"
--
-- Idempotent — safe to re-run.

alter table backtest_runs
    add column if not exists skipped_days integer default 0,
    add column if not exists regime_gate_active boolean default true;

alter table backtest_trades
    add column if not exists regime_adx double precision,
    add column if not exists regime_rv_ratio double precision,
    add column if not exists regime_hh_count integer;

create index if not exists idx_backtest_trades_regime_adx
    on backtest_trades(regime_adx)
    where regime_adx is not null;
