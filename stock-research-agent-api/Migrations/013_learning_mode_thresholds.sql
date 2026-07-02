-- Migration 013: learning-mode thresholds, candidate classification, and audit trail
--
-- Adds classification fields to paper_stock_candidates and paper_option_candidates
-- so the system can separate paper-learning throughput from stricter
-- actionable-shadow / future live-eligible gates.
--
-- Also adds candidate_generation_audit so every prediction candidate leaves an
-- audit trail whether or not an option candidate was created.

alter table paper_stock_candidates
    add column if not exists candidate_mode text
        default 'learning'
        check (candidate_mode in ('learning','actionable_shadow','live_eligible'));

alter table paper_stock_candidates
    add column if not exists quality_tier text
        default 'very_weak'
        check (quality_tier in ('very_weak','weak','medium','strong_paper','production_candidate'));

alter table paper_stock_candidates
    add column if not exists is_actionable boolean default false;

alter table paper_stock_candidates
    add column if not exists threshold_policy_version text default 'learning_options_v1';

alter table paper_stock_candidates
    add column if not exists inclusion_reason text default '';

alter table paper_stock_candidates
    add column if not exists exclusion_reason text;

alter table paper_stock_candidates
    add column if not exists score_percentile_in_run double precision default 0;

alter table paper_option_candidates
    add column if not exists candidate_mode text
        default 'learning'
        check (candidate_mode in ('learning','actionable_shadow','live_eligible'));

alter table paper_option_candidates
    add column if not exists quality_tier text
        default 'very_weak'
        check (quality_tier in ('very_weak','weak','medium','strong_paper','production_candidate'));

alter table paper_option_candidates
    add column if not exists is_actionable boolean default false;

alter table paper_option_candidates
    add column if not exists threshold_policy_version text default 'learning_options_v1';

alter table paper_option_candidates
    add column if not exists inclusion_reason text default '';

alter table paper_option_candidates
    add column if not exists exclusion_reason text;

alter table paper_option_candidates
    add column if not exists score_percentile_in_run double precision default 0;

create table if not exists candidate_generation_audit (
    id uuid primary key default gen_random_uuid(),
    run_id uuid,
    ticker text not null,
    prediction_candidate_id uuid references prediction_candidates(id),
    paper_stock_candidate_id uuid references paper_stock_candidates(id),
    paper_option_candidate_id uuid references paper_option_candidates(id),
    prediction_type text not null,
    confidence_score integer not null default 0,
    risk_score integer not null default 0,
    score_percentile_in_run double precision default 0,
    stock_candidate_created boolean not null default false,
    option_candidate_created boolean not null default false,
    candidate_mode text not null
        check (candidate_mode in ('learning','actionable_shadow','live_eligible')),
    quality_tier text not null
        check (quality_tier in ('very_weak','weak','medium','strong_paper','production_candidate')),
    option_block_reason text,
    market_data_available boolean not null default false,
    option_chain_available boolean not null default false,
    threshold_policy_version text not null default 'learning_options_v1',
    created_at timestamptz not null default now()
);

create index if not exists idx_candidate_generation_audit_run
    on candidate_generation_audit(run_id, created_at desc);

create index if not exists idx_candidate_generation_audit_ticker
    on candidate_generation_audit(ticker);

create index if not exists idx_candidate_generation_audit_block_reason
    on candidate_generation_audit(option_block_reason);
