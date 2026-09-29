-- Options support for the StockJawn executor (single-leg buy-to-open calls/puts).

alter table claude_daily_picks
  add column if not exists order_type text not null default 'stock',
  add column if not exists option_expiration date,
  add column if not exists option_strike numeric,
  add column if not exists option_contract_symbol text,
  add column if not exists option_contract_id text;

alter table claude_daily_picks drop constraint if exists claude_daily_picks_order_type_check;
alter table claude_daily_picks add constraint claude_daily_picks_order_type_check
  check (order_type in ('stock', 'call', 'put'));

comment on column claude_daily_picks.option_contract_id is
  'Robinhood option instrument UUID (from get_option_instruments). Required for call/put picks.';
comment on column claude_daily_picks.order_quantity is
  'Shares for stock picks; whole contracts for call/put picks.';

-- Options configs from the scope doc. The executor enforces options_enabled and options_max_contract_price;
-- the rest are for pick generation (contract selection). options_strike_preference lives in reason.
insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('options_enabled',            0,   0, 0,   1, 0, 'active', 'Kill switch: 1 = executor may place option orders', now()),
  ('options_max_contract_price', 50,  0, 50,  1, 0, 'active', 'Max premium per contract in dollars (limit x 100)', now()),
  ('options_min_open_interest',  100, 0, 100, 1, 0, 'active', 'Minimum open interest when choosing a contract', now()),
  ('options_preferred_dte',      5,   0, 5,   1, 0, 'active', 'Target days to expiration when choosing a contract', now()),
  ('options_strike_preference',  0,   0, 0,   1, 0, 'active', 'atm', now())
on conflict (signal_name) do nothing;

-- Executor switches (replace the ROBINHOOD_EXECUTOR_ENABLED / _DRY_RUN Azure settings).
-- Seeded to match the current Azure values (enabled, live). Flip with:
--   update scoring_weight_overrides set effective_weight = 0 where signal_name = 'executor_enabled';
insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('executor_enabled', 1, 0, 1, 1, 0, 'active', '1 = executor places orders for approved picks, 0 = off', now()),
  ('executor_dry_run', 0, 0, 0, 1, 0, 'active', '1 = dry run (review only, no orders), 0 = live', now())
on conflict (signal_name) do nothing;
