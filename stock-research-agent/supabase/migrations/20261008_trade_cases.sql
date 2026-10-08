-- Multi-day trade cases (Delento's evening job opens them; Lenny's nightly turns ready ones into CASE research rows).
create table if not exists public.trade_cases (
  id uuid primary key default gen_random_uuid(),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  ticker text not null,
  direction text not null check (direction in ('bullish','bearish')),
  source text not null,
  thesis text not null,
  sector_etf text,
  trigger_price numeric not null,
  stop_price numeric not null,
  target_price numeric not null,
  invalidate_below numeric,
  opened_on date not null default (now() at time zone 'America/New_York')::date,
  expires_on date not null,
  status text not null default 'open' check (status in ('open','staged','triggered','won','lost','expired','killed')),
  staged_pick_id uuid,
  times_staged int not null default 0,
  closed_at timestamptz,
  close_reason text,
  result_pct numeric,
  notes text
);
alter table public.trade_cases enable row level security;
create index if not exists idx_trade_cases_open on public.trade_cases (status, expires_on);
create unique index if not exists idx_trade_cases_live_ticker on public.trade_cases (ticker) where status in ('open','staged','triggered');
alter table public.claude_daily_picks add column if not exists case_id uuid;

insert into public.scoring_weight_overrides (signal_name, effective_weight, base_weight, reason)
select v.n, v.w, v.w, v.r from (values
 ('cases_enabled',1,'trade cases on'),
 ('cases_max_per_day',2,'max case picks staged per morning'),
 ('cases_max_trigger_dist_pct',4,'stage only within this % of trigger'),
 ('cases_default_days',5,'trading days a case stays open'),
 ('gap_wait_enabled',1,'no buys in first minutes of gap days'),
 ('gap_wait_min_gap_pct',3,'gap size that triggers the wait'),
 ('gap_wait_minutes',30,'minutes to wait after open on gap days'),
 ('unusual_volume_enabled',1,'scan quiet volume spikes'),
 ('unusual_volume_min_rel',2,'min relative volume'),
 ('unusual_volume_min_move_pct',2,'min % move'),
 ('unusual_volume_max_move_pct',6,'max % move (bigger = headline)'),
 ('unusual_volume_max_rows',5,'max rows per scan')
) as v(n,w,r)
where not exists (select 1 from public.scoring_weight_overrides s where s.signal_name=v.n);
