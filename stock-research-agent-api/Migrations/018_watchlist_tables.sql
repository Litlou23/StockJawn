-- 018: Create watchlist tables for weekly research pipeline
-- These tables are used by WatchlistRepository / DynamicWatchlistService.
-- Without them, run-weekly-research silently fails on first INSERT.

-- Dynamic watchlist with status tracking
create table if not exists watchlist_items (
    id uuid primary key default gen_random_uuid(),
    user_id text,
    ticker text not null,
    company_name text,
    status text not null default 'active',
    category text not null default 'general',
    watch_reason text,
    thesis_summary text,
    bullish_case text,
    bearish_case text,
    data_confidence text,
    total_score float8,
    catalyst_score float8,
    risk_score float8,
    options_readiness_score float8,
    added_at timestamptz,
    last_reviewed_at timestamptz,
    review_by_date text,
    invalidation_point text,
    exit_or_removal_conditions jsonb,
    swap_reason text,
    sources_used jsonb,
    missing_data_warnings jsonb,
    raw_context jsonb,
    archived_at timestamptz,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create index if not exists idx_watchlist_items_status on watchlist_items (status);
create index if not exists idx_watchlist_items_ticker on watchlist_items (ticker);

-- Audit trail of watchlist additions/removals
create table if not exists watchlist_change_log (
    id uuid primary key default gen_random_uuid(),
    user_id text,
    watchlist_item_id uuid references watchlist_items(id),
    ticker text not null,
    change_type text not null,
    previous_status text,
    new_status text,
    previous_score float8,
    new_score float8,
    reason text,
    metadata jsonb,
    created_at timestamptz not null default now()
);

create index if not exists idx_watchlist_change_log_created on watchlist_change_log (created_at desc);

-- Scored candidates from universe discovery
create table if not exists watchlist_candidates (
    id uuid primary key default gen_random_uuid(),
    user_id text,
    ticker text not null,
    company_name text,
    source text not null default '',
    category text,
    candidate_score float8,
    catalyst_score float8,
    risk_score float8,
    options_readiness_score float8,
    data_confidence text,
    reason text,
    selected_for_watchlist boolean not null default false,
    raw_context jsonb,
    created_at timestamptz not null default now()
);

create index if not exists idx_watchlist_candidates_created on watchlist_candidates (created_at desc);

-- RLS: allow service role full access (tables use service key, not anon)
alter table watchlist_items enable row level security;
alter table watchlist_change_log enable row level security;
alter table watchlist_candidates enable row level security;

create policy "Service role full access" on watchlist_items
    for all using (true) with check (true);
create policy "Service role full access" on watchlist_change_log
    for all using (true) with check (true);
create policy "Service role full access" on watchlist_candidates
    for all using (true) with check (true);
