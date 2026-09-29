-- Risk limits, fill tracking, exits, and approval PIN lockout for the StockJawn executor.

-- Fill tracking + exits on each pick.
alter table claude_daily_picks
  add column if not exists fill_status text,
  add column if not exists filled_quantity numeric,
  add column if not exists filled_avg_price numeric,
  add column if not exists filled_at timestamptz,
  add column if not exists fill_checked_at timestamptz,
  add column if not exists exit_status text,
  add column if not exists exit_order_id text,
  add column if not exists exit_reason text,
  add column if not exists exit_price numeric,
  add column if not exists exited_at timestamptz;

-- First account value of each day (ET); the weekly loss breaker compares against the week's first one.
create table if not exists account_value_snapshots (
  snapshot_date date primary key,
  total_value numeric not null,
  captured_at timestamptz not null default now()
);
alter table account_value_snapshots enable row level security;
revoke all on account_value_snapshots from anon, authenticated;

-- Wrong-PIN attempts on /api/approve (server-side only).
create table if not exists approval_pin_failures (
  id bigserial primary key,
  attempted_at timestamptz not null default now()
);
alter table approval_pin_failures enable row level security;
revoke all on approval_pin_failures from anon, authenticated;

-- Risk + exit switches. max_position_pct and circuit_breaker_weekly_loss_pct already exist.
insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('risk_max_trade_dollars', 80, 0, 80, 1, 0, 'active', 'Max dollars per new buy (old Claude job used $80)', now()),
  ('risk_min_stock_price',   4,  0, 4,  1, 0, 'active', 'Skip stocks under this price (old Claude job used $4)', now()),
  ('exits_enabled',          1,  0, 1,  1, 0, 'active', '1 = place stop-loss after fill and sell at target_price; 0 = off', now()),
  ('approval_pin_max_failures', 5, 0, 5, 1, 0, 'active', 'Wrong PINs allowed per 15 minutes before approvals lock', now())
on conflict (signal_name) do nothing;
