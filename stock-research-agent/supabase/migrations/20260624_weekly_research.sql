-- Weekly research job tables. Additive/non-destructive only — no DROP, no
-- changes to any existing table. These are scored RESEARCH/WATCHLIST
-- candidates only; nothing here executes or represents a trade.
--
-- Note on user_id / RLS: this app currently runs as a single private user
-- with no real Supabase Auth session wired up yet (see app/login/page.tsx —
-- placeholder). All reads/writes from the Next.js app go through the
-- service-role client (lib/supabase/serverClient.ts), which bypasses RLS
-- entirely, same as every other table in this app. user_id + RLS policies
-- below are included as requested and are forward-compatible scaffolding
-- for when real per-user auth exists — they do not currently gate the
-- service-role write path used by /api/jobs/run-weekly-research.
--
-- Safe to run multiple times (IF NOT EXISTS).

create table if not exists weekly_research_runs (
  id uuid primary key default gen_random_uuid(),
  user_id uuid references auth.users(id),
  run_date date not null,
  run_type text default 'weekly',
  trigger_source text default 'scheduled',
  universe text[] not null,
  summary text,
  market_context jsonb,
  data_quality jsonb,
  status text default 'completed',
  error_message text,
  created_at timestamptz default now()
);

create index if not exists idx_weekly_research_runs_run_date on weekly_research_runs (run_date desc);

alter table weekly_research_runs enable row level security;

drop policy if exists "weekly_research_runs_select_own" on weekly_research_runs;
create policy "weekly_research_runs_select_own" on weekly_research_runs
  for select using (auth.uid() = user_id or user_id is null);

drop policy if exists "weekly_research_runs_insert_own" on weekly_research_runs;
create policy "weekly_research_runs_insert_own" on weekly_research_runs
  for insert with check (auth.uid() = user_id or user_id is null);

drop policy if exists "weekly_research_runs_update_own" on weekly_research_runs;
create policy "weekly_research_runs_update_own" on weekly_research_runs
  for update using (auth.uid() = user_id or user_id is null);


create table if not exists weekly_stock_reviews (
  id uuid primary key default gen_random_uuid(),
  user_id uuid references auth.users(id),
  run_id uuid references weekly_research_runs(id) on delete cascade,
  ticker text not null,
  company_name text,
  long_term_score numeric,
  short_term_score numeric,
  options_readiness_score numeric,
  risk_score numeric,
  total_score numeric,
  data_confidence text,
  catalyst_summary text,
  risk_summary text,
  missing_data_warnings jsonb,
  raw_context jsonb,
  created_at timestamptz default now()
);

create index if not exists idx_weekly_stock_reviews_run_id on weekly_stock_reviews (run_id);
create index if not exists idx_weekly_stock_reviews_ticker on weekly_stock_reviews (ticker);

alter table weekly_stock_reviews enable row level security;

drop policy if exists "weekly_stock_reviews_select_own" on weekly_stock_reviews;
create policy "weekly_stock_reviews_select_own" on weekly_stock_reviews
  for select using (auth.uid() = user_id or user_id is null);

drop policy if exists "weekly_stock_reviews_insert_own" on weekly_stock_reviews;
create policy "weekly_stock_reviews_insert_own" on weekly_stock_reviews
  for insert with check (auth.uid() = user_id or user_id is null);


create table if not exists weekly_candidates (
  id uuid primary key default gen_random_uuid(),
  user_id uuid references auth.users(id),
  run_id uuid references weekly_research_runs(id) on delete cascade,
  ticker text not null,
  company_name text,
  category text not null check (category in ('long_term', 'short_term', 'options_watch')),
  rank int not null,
  total_score numeric,
  thesis text,
  bullish_case text,
  bearish_case text,
  suggested_duration text,
  review_date date,
  invalidation_point text,
  exit_rules jsonb,
  profit_taking_rules jsonb,
  data_confidence text,
  sources_used jsonb,
  created_at timestamptz default now()
);

create index if not exists idx_weekly_candidates_run_id on weekly_candidates (run_id);
create index if not exists idx_weekly_candidates_ticker on weekly_candidates (ticker);

alter table weekly_candidates enable row level security;

drop policy if exists "weekly_candidates_select_own" on weekly_candidates;
create policy "weekly_candidates_select_own" on weekly_candidates
  for select using (auth.uid() = user_id or user_id is null);

drop policy if exists "weekly_candidates_insert_own" on weekly_candidates;
create policy "weekly_candidates_insert_own" on weekly_candidates
  for insert with check (auth.uid() = user_id or user_id is null);
