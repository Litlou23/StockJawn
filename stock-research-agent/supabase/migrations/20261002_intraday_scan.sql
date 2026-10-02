-- Market-hours scan + "what did we miss" report.
create table if not exists missed_movers (
  id bigint generated always as identity primary key,
  trade_date date not null,
  ticker text not null,
  kind text not null default 'stock',   -- 'stock' | 'theme'
  change_pct numeric,
  price numeric,
  dollar_volume_m numeric,
  gap_pct numeric,
  spy_change_pct numeric,
  our_status text,                      -- null = we never had it
  reasons text,
  created_at timestamptz not null default now()
);
create index if not exists missed_movers_date on missed_movers (trade_date);
alter table missed_movers enable row level security;
revoke all on missed_movers from anon, authenticated;

insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('intraday_scan_enabled',              1,   0, 1,   1, 0, 'active', '1 = scan for market-hours leaders at intraday_scan_times_et and ping Lou', now()),
  ('intraday_scan_times_et',             0,   0, 0,   1, 0, 'active', '10:00,11:30', now()),
  ('intraday_min_move_pct',              2,   0, 2,   1, 0, 'active', 'Leader must be up/down at least this % today', now()),
  ('intraday_max_move_pct',              15,  0, 15,  1, 0, 'active', 'Skip moves bigger than this % (too late to chase)', now()),
  ('intraday_min_rs_pct',                1.5, 0, 1.5, 1, 0, 'active', 'Must beat SPY by at least this many % points today', now()),
  ('intraday_min_rel_volume',            1.5, 0, 1.5, 1, 0, 'active', 'Volume so far vs a normal day at this time', now()),
  ('intraday_max_picks',                 2,   0, 2,   1, 0, 'active', 'Most picks one market-hours scan stages', now()),
  ('approval_page_url',                  0,   0, 0,   1, 0, 'active', 'https://yvyofficial.com/approve', now()),
  ('missed_movers_enabled',              1,   0, 1,   1, 0, 'active', '1 = after the close, log the day''s big movers we missed to missed_movers', now()),
  ('missed_movers_time_et',              0,   0, 0,   1, 0, 'active', '16:15', now()),
  ('missed_movers_min_move_pct',         4,   0, 4,   1, 0, 'active', 'Mover must be up/down at least this % to count', now()),
  ('missed_movers_min_dollar_volume_m',  50,  0, 50,  1, 0, 'active', 'Mover must trade at least this many $ millions today', now()),
  ('missed_movers_max_rows',             15,  0, 15,  1, 0, 'active', 'Most stock rows per day', now())
on conflict (signal_name) do nothing;
