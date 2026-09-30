-- StockedUp-style entries: an approved pick waits until the stock breaks its trigger level, then buys.
-- Levels are the STOCK's price (e.g. "ORCL above 140"), even for option picks.

alter table claude_daily_picks
  add column if not exists trigger_price numeric,
  add column if not exists trigger_direction text,
  add column if not exists trigger_hit_at timestamptz,
  add column if not exists trigger_hit_price numeric,
  add column if not exists level_target numeric,
  add column if not exists level_stop numeric;

alter table claude_daily_picks drop constraint if exists claude_daily_picks_trigger_direction_check;
alter table claude_daily_picks add constraint claude_daily_picks_trigger_direction_check
  check (trigger_direction is null or trigger_direction in ('above', 'below'));

insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('trigger_entries_enabled', 1, 0, 1, 1, 0, 'active', '1 = picks with trigger_price wait for the break before buying; 0 = buy right after approval', now()),
  ('trigger_max_chase_pct',   3, 0, 3, 1, 0, 'active', 'Don''t buy if the stock already ran more than this % past the trigger (old executor used 3%)', now()),
  ('trigger_cutoff_et',       0, 0, 0, 1, 0, 'active', '15:30', now())
on conflict (signal_name) do nothing;

-- PDT guard: hold overnight; a same-day sell only on a big loss, max 2 per 5 trading days.
alter table claude_daily_picks
  add column if not exists same_day_exit boolean not null default false,
  add column if not exists exit_placed_at timestamptz;

insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('max_day_trades',           2,  0, 2,  1, 0, 'active', 'Same-day sells allowed per 5 trading days (PDT limit is 3; keep 1 spare)', now()),
  ('same_day_stop_stock_pct',  5,  0, 5,  1, 0, 'active', 'Bought-today shares are only sold today if down this % or more', now()),
  ('same_day_stop_option_pct', 40, 0, 40, 1, 0, 'active', 'Bought-today options are only sold today if the premium is down this % or more', now())
on conflict (signal_name) do nothing;
