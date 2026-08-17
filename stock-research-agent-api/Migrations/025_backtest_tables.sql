-- Migration 025: Backtest engine tables + RPCs
--
-- Backs the Phase 1–4 implementation in Services/Backtesting/.
-- Column names match what BacktestEngine.cs, HistoricalDataLoader.cs, and
-- SimulatedPortfolio.cs already write. Any deviation between this file and
-- the code paths is a bug in this migration, not in the code — fix here.
--
-- Idempotent: uses IF NOT EXISTS / CREATE OR REPLACE throughout so re-running
-- is safe.

-- ============================================================================
-- 1. historical_candles — daily OHLCV cache for the backtest engine.
--    Written by HistoricalDataLoader.LoadHistoryAsync (chunks of 50 upserts).
--    Read by HistoricalMarketSnapshotBuilder.BuildAsync, BacktestEngine.
--    GetTradingDaysAsync, and FetchDayCandlesAsync.
-- ============================================================================

create table if not exists historical_candles (
    ticker       text   not null,
    candle_date  date   not null,
    open         double precision not null default 0,
    high         double precision not null default 0,
    low          double precision not null default 0,
    close        double precision not null default 0,
    volume       double precision not null default 0,
    created_at   timestamptz not null default now(),
    constraint historical_candles_pkey primary key (ticker, candle_date)
);

create index if not exists idx_historical_candles_date
    on historical_candles(candle_date);
create index if not exists idx_historical_candles_ticker_date
    on historical_candles(ticker, candle_date desc);

-- ============================================================================
-- 2. backtest_runs — one row per RunAsync invocation.
--    Written by BacktestEngine.RunAsync (INSERT running → UPDATE completed).
--    Failure path calls MarkRunFailed which writes error_message.
-- ============================================================================

create table if not exists backtest_runs (
    id                     uuid primary key,
    start_date             date not null,
    end_date               date not null,
    parameters             jsonb,
    status                 text not null default 'running'
                             check (status in ('running','completed','failed')),
    tickers_tested         integer,
    trading_days           integer,
    predictions_generated  integer,
    trades_taken           integer,
    total_pnl              double precision,
    win_rate               double precision,
    max_drawdown           double precision,
    sharpe_ratio           double precision,
    profit_factor          double precision,
    avg_win                double precision,
    avg_loss               double precision,
    best_trade             double precision,
    worst_trade            double precision,
    summary                text,
    error_message          text,
    created_at             timestamptz not null default now(),
    completed_at           timestamptz
);

create index if not exists idx_backtest_runs_status
    on backtest_runs(status);
create index if not exists idx_backtest_runs_created_at
    on backtest_runs(created_at desc);

-- ============================================================================
-- 3. backtest_trades — one row per simulated trade produced by
--    SimulatedPortfolio during a run.
-- ============================================================================

create table if not exists backtest_trades (
    id                      uuid primary key default gen_random_uuid(),
    run_id                  uuid not null
                              references backtest_runs(id) on delete cascade,
    ticker                  text not null,
    direction               text not null,   -- bullish | bearish
    timeframe               text,
    entry_date              date not null,
    entry_price             double precision,
    exit_date               date,
    exit_price              double precision,
    exit_reason             text,            -- stop_loss | take_profit | trailing | time_stop | eod | ...
    pnl_dollars             double precision,
    pnl_percent             double precision,
    max_favorable_percent   double precision,
    max_adverse_percent     double precision,
    confidence              double precision,
    expected_value          double precision,
    risk_reward_ratio       double precision,
    score_debug             text,
    created_at              timestamptz not null default now()
);

create index if not exists idx_backtest_trades_run
    on backtest_trades(run_id);
create index if not exists idx_backtest_trades_ticker
    on backtest_trades(ticker);
create index if not exists idx_backtest_trades_entry_date
    on backtest_trades(entry_date);

-- ============================================================================
-- 4. backtest_equity_curve — one row per day per run showing portfolio value.
--    Written by BacktestEngine.RunAsync after CloseAllOpen. Unique per
--    (run_id, snapshot_date) so re-runs of the same run are impossible without
--    a delete first.
-- ============================================================================

create table if not exists backtest_equity_curve (
    id                    uuid primary key default gen_random_uuid(),
    run_id                uuid not null
                            references backtest_runs(id) on delete cascade,
    snapshot_date         date not null,
    cash                  double precision not null default 0,
    invested_value        double precision not null default 0,
    total_equity          double precision not null default 0,
    open_position_count   integer not null default 0,
    created_at            timestamptz not null default now(),
    unique(run_id, snapshot_date)
);

create index if not exists idx_backtest_equity_curve_run
    on backtest_equity_curve(run_id);

-- ============================================================================
-- 5. RPC: get_candle_summary — returns ticker + candle_count for every
--    ticker that has data. Called by HistoricalDataLoader.GetStoredTickerCountsAsync
--    (reads r["candle_count"] as long).
-- ============================================================================

drop function if exists get_candle_summary();
create or replace function get_candle_summary()
returns table (ticker text, candle_count bigint)
language sql
stable
as $$
    select ticker, count(*)::bigint as candle_count
    from historical_candles
    group by ticker
    order by ticker
$$;

-- ============================================================================
-- 6. RPC: get_latest_candle_dates(tickers text[]) — returns ticker + latest
--    stored candle_date for each ticker in the input array. Called by
--    HistoricalDataLoader.GetLatestStoredDatesAsync (in chunks of 100 tickers)
--    to power incremental loads. Reads r["ticker"] and r["latest_date"].
-- ============================================================================

drop function if exists get_latest_candle_dates(text[]);
create or replace function get_latest_candle_dates(tickers text[])
returns table (ticker text, latest_date date)
language sql
stable
as $$
    select hc.ticker, max(hc.candle_date) as latest_date
    from historical_candles hc
    where hc.ticker = any(tickers)
    group by hc.ticker
$$;

-- ============================================================================
-- Done. After running this on a clean DB, verify with:
--   select count(*) from historical_candles;         -- 0
--   select count(*) from backtest_runs;              -- 0
--   select count(*) from backtest_trades;            -- 0
--   select count(*) from backtest_equity_curve;      -- 0
--   select * from get_candle_summary();              -- returns 0 rows
--   select * from get_latest_candle_dates(array['SPY']);  -- returns 0 rows
-- ============================================================================
