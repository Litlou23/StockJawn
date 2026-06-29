-- Theoretical Options Lab simulation results
-- All data here is THEORETICAL SIMULATION ONLY — not real option quotes.
-- No real premiums, IV, Greeks, bid/ask, OI, or volume are stored.

create table if not exists theoretical_option_simulations (
  id uuid primary key default gen_random_uuid(),
  prediction_id uuid references prediction_candidates(id) on delete set null,
  ticker text not null,
  strategy_type text not null check (strategy_type in (
    'long_call_proxy', 'long_put_proxy',
    'bull_call_spread_proxy', 'bear_put_spread_proxy',
    'iron_condor_proxy'
  )),
  starting_stock_price double precision not null,
  ending_stock_price double precision not null,
  stock_move_percent double precision not null,
  assumptions_json jsonb not null default '{}'::jsonb,
  estimated_payoff double precision not null,
  estimated_return_percent double precision not null,
  max_profit double precision not null,
  max_loss double precision not null,
  breakevens_json jsonb not null default '[]'::jsonb,
  direction_matched_prediction boolean,
  warnings_json jsonb not null default '[]'::jsonb,
  created_at timestamptz not null default now()
);

-- Index for lookups by prediction
create index if not exists idx_theo_sims_prediction_id
  on theoretical_option_simulations(prediction_id);

-- Index for lookups by ticker
create index if not exists idx_theo_sims_ticker
  on theoretical_option_simulations(ticker);

-- RLS: allow service_role full access (same pattern as other tables)
alter table theoretical_option_simulations enable row level security;

create policy "service_role_full_access" on theoretical_option_simulations
  for all
  using (true)
  with check (true);
