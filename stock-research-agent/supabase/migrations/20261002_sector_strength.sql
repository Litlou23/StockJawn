-- Group leadership (1-month return vs SPY) + affordable leaders, written by the 4:30 PM movers scan.
create table if not exists sector_strength (
  id bigint generated always as identity primary key,
  trade_date date not null,
  etf text not null,
  name text,
  ret_1w numeric,
  ret_1m numeric,
  rs_1m numeric,
  above_sma20 boolean,
  rank int,
  grp text,            -- leading | middle | lagging
  leaders jsonb,       -- affordable stocks in this group beating SPY (top 2 leading groups only)
  created_at timestamptz not null default now()
);
create index if not exists sector_strength_date on sector_strength (trade_date);
alter table sector_strength enable row level security;
revoke all on sector_strength from anon, authenticated;

insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('sector_etfs',                0,   0, 0,   1, 0, 'active', 'XLK,SMH,XLV,XBI,XLF,KRE,XLY,XLE,XLI,XLB,XLRE,XLU,XLC,XLP,ITB,JETS,GLD,SLV,URA,TAN,XME', now()),
  ('affordable_max_price',       60,  0, 60,  1, 0, 'active', 'Leaders list: stocks at or under this price (shares fit the per-trade budget)', now()),
  ('options_min_contract_price', 0.2, 0, 0.2, 1, 0, 'active', 'Refuse option buys whose ask is under this (cheap contracts lose to the spread and decay)', now())
on conflict (signal_name) do nothing;
