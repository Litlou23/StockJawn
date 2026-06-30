-- Migration: paper_option_candidates and paper_option_outcomes
-- These tables track paper (simulated) option picks using REAL contract data
-- from MarketData.app. No invented data — all values come from live API responses.

create table if not exists paper_option_candidates (
    id uuid primary key default gen_random_uuid(),
    prediction_id uuid references prediction_candidates(id),
    ticker text not null,
    option_symbol text not null,
    side text not null check (side in ('call', 'put')),
    strike double precision not null,
    expiration timestamptz not null,
    dte_at_entry integer not null,

    -- Entry snapshot (all from real MarketData.app response)
    entry_underlying_price double precision not null,
    entry_bid double precision not null,
    entry_ask double precision not null,
    entry_mid double precision not null,
    entry_iv double precision not null,
    entry_delta double precision not null,
    entry_open_interest integer not null default 0,
    entry_volume integer not null default 0,

    -- Scoring
    contract_score double precision not null default 0,
    selection_reason text not null default '',

    -- Status
    status text not null default 'open' check (status in ('open', 'closed', 'expired')),
    created_at timestamptz not null default now()
);

create index if not exists idx_paper_option_candidates_ticker on paper_option_candidates(ticker);
create index if not exists idx_paper_option_candidates_status on paper_option_candidates(status);
create index if not exists idx_paper_option_candidates_prediction on paper_option_candidates(prediction_id);

create table if not exists paper_option_outcomes (
    id uuid primary key default gen_random_uuid(),
    paper_candidate_id uuid not null references paper_option_candidates(id),
    evaluation_time timestamptz not null default now(),

    -- Current snapshot
    current_underlying_price double precision not null default 0,
    current_bid double precision not null default 0,
    current_ask double precision not null default 0,
    current_mid double precision not null default 0,
    current_iv double precision not null default 0,
    current_delta double precision not null default 0,
    current_open_interest integer not null default 0,
    current_volume integer not null default 0,

    -- P&L
    paper_pnl_per_contract double precision not null default 0,
    paper_pnl_percent double precision not null default 0,
    underlying_move_percent double precision not null default 0,

    -- IV change
    iv_change double precision not null default 0,

    outcome_summary text not null default '',
    created_at timestamptz not null default now()
);

create index if not exists idx_paper_option_outcomes_candidate on paper_option_outcomes(paper_candidate_id);
create index if not exists idx_paper_option_outcomes_eval_time on paper_option_outcomes(evaluation_time);
