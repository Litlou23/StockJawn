-- Dynamic Watchlist System
-- 3 tables: watchlist_items, watchlist_change_log, watchlist_candidates
-- Non-destructive: no DROP TABLE, no data deletion
-- RLS enabled with policies based on auth.uid() = user_id

-- ============================================================
-- 1. watchlist_items — the active/review/archived watchlist
-- ============================================================

create table if not exists watchlist_items (
  id uuid primary key default gen_random_uuid(),
  user_id uuid references auth.users(id),
  ticker text not null,
  company_name text,
  status text not null default 'active',
  category text not null default 'general',
  watch_reason text,
  thesis_summary text,
  bullish_case text,
  bearish_case text,
  data_confidence text,
  total_score numeric,
  catalyst_score numeric,
  risk_score numeric,
  options_readiness_score numeric,
  added_at timestamptz default now(),
  last_reviewed_at timestamptz,
  review_by_date date,
  invalidation_point text,
  exit_or_removal_conditions jsonb,
  swap_reason text,
  sources_used jsonb,
  missing_data_warnings jsonb,
  raw_context jsonb,
  archived_at timestamptz,
  created_at timestamptz default now(),
  updated_at timestamptz default now()
);

-- Indexes
create index if not exists idx_watchlist_items_user_id on watchlist_items(user_id);
create index if not exists idx_watchlist_items_status on watchlist_items(status);
create index if not exists idx_watchlist_items_ticker on watchlist_items(ticker);
create index if not exists idx_watchlist_items_user_status on watchlist_items(user_id, status);
create index if not exists idx_watchlist_items_created_at on watchlist_items(created_at);

-- RLS
alter table watchlist_items enable row level security;

create policy "Users can view their own watchlist items"
  on watchlist_items for select
  using (auth.uid() = user_id);

create policy "Users can insert their own watchlist items"
  on watchlist_items for insert
  with check (auth.uid() = user_id);

create policy "Users can update their own watchlist items"
  on watchlist_items for update
  using (auth.uid() = user_id);

-- Service role bypass (for server-side jobs that run without a user session)
create policy "Service role full access on watchlist_items"
  on watchlist_items for all
  using (auth.role() = 'service_role');

-- updated_at trigger
create or replace function update_watchlist_items_updated_at()
returns trigger as $$
begin
  new.updated_at = now();
  return new;
end;
$$ language plpgsql;

drop trigger if exists trg_watchlist_items_updated_at on watchlist_items;
create trigger trg_watchlist_items_updated_at
  before update on watchlist_items
  for each row execute function update_watchlist_items_updated_at();

-- ============================================================
-- 2. watchlist_change_log — audit trail of every change
-- ============================================================

create table if not exists watchlist_change_log (
  id uuid primary key default gen_random_uuid(),
  user_id uuid references auth.users(id),
  watchlist_item_id uuid references watchlist_items(id) on delete cascade,
  ticker text not null,
  change_type text not null,
  previous_status text,
  new_status text,
  previous_score numeric,
  new_score numeric,
  reason text,
  metadata jsonb,
  created_at timestamptz default now()
);

create index if not exists idx_watchlist_change_log_user_id on watchlist_change_log(user_id);
create index if not exists idx_watchlist_change_log_item_id on watchlist_change_log(watchlist_item_id);
create index if not exists idx_watchlist_change_log_ticker on watchlist_change_log(ticker);
create index if not exists idx_watchlist_change_log_created_at on watchlist_change_log(created_at);

alter table watchlist_change_log enable row level security;

create policy "Users can view their own watchlist change log"
  on watchlist_change_log for select
  using (auth.uid() = user_id);

create policy "Users can insert their own watchlist change log"
  on watchlist_change_log for insert
  with check (auth.uid() = user_id);

create policy "Service role full access on watchlist_change_log"
  on watchlist_change_log for all
  using (auth.role() = 'service_role');

-- ============================================================
-- 3. watchlist_candidates — scored candidates from each run
-- ============================================================

create table if not exists watchlist_candidates (
  id uuid primary key default gen_random_uuid(),
  user_id uuid references auth.users(id),
  ticker text not null,
  company_name text,
  source text not null,
  category text,
  candidate_score numeric,
  catalyst_score numeric,
  risk_score numeric,
  options_readiness_score numeric,
  data_confidence text,
  reason text,
  selected_for_watchlist boolean default false,
  raw_context jsonb,
  created_at timestamptz default now()
);

create index if not exists idx_watchlist_candidates_user_id on watchlist_candidates(user_id);
create index if not exists idx_watchlist_candidates_ticker on watchlist_candidates(ticker);
create index if not exists idx_watchlist_candidates_created_at on watchlist_candidates(created_at);

alter table watchlist_candidates enable row level security;

create policy "Users can view their own watchlist candidates"
  on watchlist_candidates for select
  using (auth.uid() = user_id);

create policy "Users can insert their own watchlist candidates"
  on watchlist_candidates for insert
  with check (auth.uid() = user_id);

create policy "Service role full access on watchlist_candidates"
  on watchlist_candidates for all
  using (auth.role() = 'service_role');
