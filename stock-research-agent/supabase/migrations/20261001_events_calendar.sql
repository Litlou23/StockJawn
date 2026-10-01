-- Events calendar: earnings, economic data, company events, market holidays/early closes, option expirations.
-- Filled nightly by the API (Finnhub / Alpaca / computed); the nightly job may add company events (source = 'nightly').
create table if not exists market_events (
  id bigserial primary key,
  event_date date not null,
  event_time text,
  kind text not null check (kind in ('earnings', 'economic', 'company', 'holiday', 'early_close', 'opex')),
  ticker text,
  title text not null,
  importance text not null default 'medium' check (importance in ('high', 'medium', 'low')),
  source text not null,
  details jsonb,
  updated_at timestamptz not null default now()
);
create unique index if not exists market_events_uniq on market_events (kind, coalesce(ticker, ''), event_date, title);
create index if not exists market_events_ticker_date on market_events (ticker, event_date);
create index if not exists market_events_date on market_events (event_date);

alter table market_events enable row level security;
revoke insert, update, delete on market_events from anon, authenticated;
drop policy if exists anon_read_market_events on market_events;
create policy anon_read_market_events on market_events for select to anon using (true);
grant select on market_events to anon;

-- Shown on the approval page for positions we hold ("CCL reports before its sell-by date").
alter table claude_daily_picks add column if not exists event_warning text;

insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('calendar_enabled',          1,  0, 1,  1, 0, 'active', '1 = refresh market_events nightly and warn on holdings', now()),
  ('calendar_refresh_time_et',  0,  0, 0,  1, 0, 'active', '18:00', now()),
  ('calendar_alert_enabled',    1,  0, 1,  1, 0, 'active', '1 = send tomorrow''s events to the ntfy topic after each refresh', now()),
  ('ntfy_topic',                0,  0, 0,  1, 0, 'active', 'stockjawn-picks-7428', now()),
  ('premarket_shares_start_et', 0,  0, 0,  1, 0, 'active', '08:00', now()),
  ('stale_buy_minutes',         30, 0, 30, 1, 0, 'active', 'Cancel a share buy that still hasn''t filled after this many minutes (0 = off)', now())
on conflict (signal_name) do nothing;
