-- =====================================================================
-- Combined migration: 005 + 006 + 007
--
-- Run this ONCE against your Supabase database (SQL editor or psql).
-- All statements are idempotent — safe to re-run.
--
-- Creates / extends:
--   paper_option_candidates       (005, extended in 006, FK added in 007)
--   paper_option_outcomes         (005, extended in 006)
--   option_learning_stats         (006)
--   paper_stock_candidates        (007 — parent record for stock picks)
--   paper_stock_outcomes          (007)
--   stock_learning_stats          (007)
--
-- The order matters: 005 creates the tables that 006 alters and that 007
-- references via FK. Do not rearrange.
-- =====================================================================


-- =====================================================================
-- 005: paper_option_candidates + paper_option_outcomes
-- =====================================================================

create table if not exists paper_option_candidates (
    id uuid primary key default gen_random_uuid(),
    prediction_id uuid references prediction_candidates(id),
    ticker text not null,
    option_symbol text not null,
    side text not null check (side in ('call', 'put')),
    strike double precision not null,
    expiration timestamptz not null,
    dte_at_entry integer not null,

    entry_underlying_price double precision not null,
    entry_bid double precision not null,
    entry_ask double precision not null,
    entry_mid double precision not null,
    entry_iv double precision not null,
    entry_delta double precision not null,
    entry_open_interest integer not null default 0,
    entry_volume integer not null default 0,

    contract_score double precision not null default 0,
    selection_reason text not null default '',

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

    current_underlying_price double precision not null default 0,
    current_bid double precision not null default 0,
    current_ask double precision not null default 0,
    current_mid double precision not null default 0,
    current_iv double precision not null default 0,
    current_delta double precision not null default 0,
    current_open_interest integer not null default 0,
    current_volume integer not null default 0,

    paper_pnl_per_contract double precision not null default 0,
    paper_pnl_percent double precision not null default 0,
    underlying_move_percent double precision not null default 0,

    iv_change double precision not null default 0,

    outcome_summary text not null default '',
    created_at timestamptz not null default now()
);

create index if not exists idx_paper_option_outcomes_candidate on paper_option_outcomes(paper_candidate_id);
create index if not exists idx_paper_option_outcomes_eval_time on paper_option_outcomes(evaluation_time);


-- =====================================================================
-- 006: enhanced columns + option_learning_stats
-- =====================================================================

alter table paper_option_candidates add column if not exists provider text default 'marketdata';
alter table paper_option_candidates add column if not exists entry_last double precision default 0;
alter table paper_option_candidates add column if not exists entry_gamma double precision default 0;
alter table paper_option_candidates add column if not exists entry_theta double precision default 0;
alter table paper_option_candidates add column if not exists entry_vega double precision default 0;
alter table paper_option_candidates add column if not exists estimated_contract_cost double precision default 0;
alter table paper_option_candidates add column if not exists spread_percent double precision default 0;
alter table paper_option_candidates add column if not exists duration_bucket text default 'system_recommended';
alter table paper_option_candidates add column if not exists price_bucket text;
alter table paper_option_candidates add column if not exists data_delay_label text;
alter table paper_option_candidates add column if not exists rank integer default 0;
alter table paper_option_candidates add column if not exists warnings_json jsonb;
alter table paper_option_candidates add column if not exists raw_provider_data_json jsonb;

alter table paper_option_candidates drop constraint if exists paper_option_candidates_status_check;
alter table paper_option_candidates add constraint paper_option_candidates_status_check
    check (status in ('open', 'closed', 'expired', 'evaluated'));

alter table paper_option_outcomes add column if not exists prediction_id uuid references prediction_candidates(id);
alter table paper_option_outcomes add column if not exists ticker text;
alter table paper_option_outcomes add column if not exists option_symbol text;
alter table paper_option_outcomes add column if not exists current_last double precision default 0;
alter table paper_option_outcomes add column if not exists direction_correct boolean;
alter table paper_option_outcomes add column if not exists contract_profitable boolean;
alter table paper_option_outcomes add column if not exists spread_still_acceptable boolean;
alter table paper_option_outcomes add column if not exists volume_still_acceptable boolean;
alter table paper_option_outcomes add column if not exists outcome_score double precision default 0;
alter table paper_option_outcomes add column if not exists lesson text;
alter table paper_option_outcomes add column if not exists warnings_json jsonb;
alter table paper_option_outcomes add column if not exists raw_provider_data_json jsonb;

create table if not exists option_learning_stats (
    id uuid primary key default gen_random_uuid(),
    stat_type text not null,
    stat_key text not null,
    total_candidates integer default 0,
    profitable_candidates integer default 0,
    win_rate double precision default 0,
    average_option_move_percent double precision default 0,
    average_underlying_move_percent double precision default 0,
    average_outcome_score double precision default 0,
    last_updated_at timestamptz default now(),
    unique(stat_type, stat_key)
);

create index if not exists idx_option_learning_stats_type_key on option_learning_stats(stat_type, stat_key);


-- =====================================================================
-- 007: paper_stock_candidates + paper_stock_outcomes + stock_learning_stats
--       + FK from paper_option_candidates to paper_stock_candidates
-- =====================================================================

create table if not exists paper_stock_candidates (
    id uuid primary key default gen_random_uuid(),

    prediction_id uuid references prediction_candidates(id),
    run_id uuid,

    ticker text not null,
    prediction_type text not null check (prediction_type in ('bullish','bearish','neutral')),
    timeframe text not null check (timeframe in ('1_day','2_day','1_week')),

    entry_price double precision,
    reference_price double precision,
    target_price double precision,
    stop_price double precision,

    catalyst_score double precision default 0,
    trend_score double precision default 0,
    volume_score double precision default 0,
    market_context_score double precision default 0,
    historical_accuracy_score double precision default 0,
    risk_penalty double precision default 0,
    missing_data_penalty double precision default 0,
    total_score double precision default 0,

    confidence_score integer default 0,
    risk_score integer default 0,
    catalyst_type text,
    selection_reason text default '',
    warnings_json jsonb,
    data_availability text default 'real',

    status text not null default 'open' check (status in ('open','evaluated','expired','watch_only','unavailable')),
    qualifies_for_options boolean default false,

    created_at timestamptz not null default now()
);

create index if not exists idx_paper_stock_candidates_ticker on paper_stock_candidates(ticker);
create index if not exists idx_paper_stock_candidates_status on paper_stock_candidates(status);
create index if not exists idx_paper_stock_candidates_run on paper_stock_candidates(run_id);
create index if not exists idx_paper_stock_candidates_prediction on paper_stock_candidates(prediction_id);

create table if not exists paper_stock_outcomes (
    id uuid primary key default gen_random_uuid(),
    paper_stock_candidate_id uuid not null references paper_stock_candidates(id),
    prediction_id uuid references prediction_candidates(id),
    ticker text not null,
    evaluation_time timestamptz not null default now(),

    exit_price double precision,
    high_after double precision,
    low_after double precision,
    percent_move double precision,

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

alter table paper_option_candidates
    add column if not exists paper_stock_candidate_id uuid references paper_stock_candidates(id);

create index if not exists idx_paper_option_candidates_stock on paper_option_candidates(paper_stock_candidate_id);
