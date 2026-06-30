-- Migration 007: Paper Stock Candidates + stock learning stats
--
-- paper_stock_candidates is the parent record for a short-term stock pick.
-- It wraps an existing prediction_candidates row with paper-trading metadata
-- (timeframe, entry/stop, deterministic score, status). Paper option
-- candidates link to a paper_stock_candidate via paper_stock_candidate_id.
--
-- Existing tables (prediction_candidates, paper_option_candidates,
-- paper_option_outcomes, option_learning_stats) are NOT modified except for
-- the new FK column.

create table if not exists paper_stock_candidates (
    id uuid primary key default gen_random_uuid(),

    -- Linkage
    prediction_id uuid references prediction_candidates(id),
    run_id uuid,

    -- Stock identity
    ticker text not null,
    prediction_type text not null check (prediction_type in ('bullish','bearish','neutral')),
    timeframe text not null check (timeframe in ('1_day','2_day','1_week')),

    -- Entry snapshot from real market data
    entry_price double precision,
    reference_price double precision,
    target_price double precision,
    stop_price double precision,

    -- Deterministic scoring (all 0..100)
    catalyst_score double precision default 0,
    trend_score double precision default 0,
    volume_score double precision default 0,
    market_context_score double precision default 0,
    historical_accuracy_score double precision default 0,
    risk_penalty double precision default 0,
    missing_data_penalty double precision default 0,
    total_score double precision default 0,

    -- Decision metadata
    confidence_score integer default 0,
    risk_score integer default 0,
    catalyst_type text,
    selection_reason text default '',
    warnings_json jsonb,
    data_availability text default 'real',     -- real | partial | unavailable

    -- Status
    status text not null default 'open' check (status in ('open','evaluated','expired','watch_only','unavailable')),
    qualifies_for_options boolean default false,

    -- Timestamps
    created_at timestamptz not null default now()
);

create index if not exists idx_paper_stock_candidates_ticker on paper_stock_candidates(ticker);
create index if not exists idx_paper_stock_candidates_status on paper_stock_candidates(status);
create index if not exists idx_paper_stock_candidates_run on paper_stock_candidates(run_id);
create index if not exists idx_paper_stock_candidates_prediction on paper_stock_candidates(prediction_id);

-- ----------------------------------------------------------------------
-- paper_stock_outcomes: evaluated result for a paper stock candidate
-- ----------------------------------------------------------------------

create table if not exists paper_stock_outcomes (
    id uuid primary key default gen_random_uuid(),
    paper_stock_candidate_id uuid not null references paper_stock_candidates(id),
    prediction_id uuid references prediction_candidates(id),
    ticker text not null,
    evaluation_time timestamptz not null default now(),

    -- Real exit data
    exit_price double precision,
    high_after double precision,
    low_after double precision,
    percent_move double precision,

    -- Verdict (boolean only when we had real data)
    direction_correct boolean,
    target_hit boolean,
    stop_hit boolean,
    invalidation_hit boolean,
    outcome_score double precision default 0,

    outcome_summary text default '',
    lesson text,
    warnings_json jsonb,
    created_at timestamptz not null default now()
);

create index if not exists idx_paper_stock_outcomes_candidate on paper_stock_outcomes(paper_stock_candidate_id);
create index if not exists idx_paper_stock_outcomes_ticker on paper_stock_outcomes(ticker);

-- ----------------------------------------------------------------------
-- stock_learning_stats: parallel of option_learning_stats but for stocks
-- ----------------------------------------------------------------------

create table if not exists stock_learning_stats (
    id uuid primary key default gen_random_uuid(),
    stat_type text not null,
    stat_key text not null,
    total_candidates integer default 0,
    correct_candidates integer default 0,
    accuracy double precision default 0,
    average_percent_move double precision default 0,
    average_outcome_score double precision default 0,
    last_updated_at timestamptz default now(),
    unique(stat_type, stat_key)
);

create index if not exists idx_stock_learning_stats_type_key on stock_learning_stats(stat_type, stat_key);

-- ----------------------------------------------------------------------
-- Link options to their parent stock candidate
-- ----------------------------------------------------------------------

alter table paper_option_candidates
    add column if not exists paper_stock_candidate_id uuid references paper_stock_candidates(id);

create index if not exists idx_paper_option_candidates_stock on paper_option_candidates(paper_stock_candidate_id);
