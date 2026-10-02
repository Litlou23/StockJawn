-- Results of POST /api/jobs/backtest-triggers (the live trigger strategy replayed on daily bars).
create table if not exists trigger_backtest_runs (
  id uuid primary key,
  created_at timestamptz not null default now(),
  from_date date,
  to_date date,
  tickers int,
  summary jsonb,
  notes text
);
create table if not exists trigger_backtest_trades (
  id bigint generated always as identity primary key,
  run_id uuid not null references trigger_backtest_runs(id) on delete cascade,
  signal_date date,
  ticker text,
  direction text,
  is_theme boolean,
  tags text,
  spy_change_pct numeric,
  trigger_price numeric,
  stop_price numeric,
  target_price numeric,
  entry_date date,
  entry_price numeric,
  exit_date date,
  exit_price numeric,
  exit_reason text,
  return_pct numeric,
  r_multiple numeric
);
create index if not exists trigger_backtest_trades_run on trigger_backtest_trades (run_id);
alter table trigger_backtest_runs enable row level security;
alter table trigger_backtest_trades enable row level security;
revoke all on trigger_backtest_runs from anon, authenticated;
revoke all on trigger_backtest_trades from anon, authenticated;

insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('trigger_backtest_request', 0, 0, 0, 1, 0, 'active', '365', now())
on conflict (signal_name) do nothing;
