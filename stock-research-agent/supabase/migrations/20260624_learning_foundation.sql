-- Learning foundation: thesis tracking, outcome tracking, signal performance
-- summaries, agent feedback, and learning reports. Additive only — does not
-- touch picks, watchlist_items, catalyst_items, daily_reports, agent_reports,
-- notifications, agent_snapshots, option_watchlist_candidates, or
-- signal_weights. No weights are written or changed by anything here;
-- signal_weights stays manual/untouched.
--
-- Safe to run multiple times (IF NOT EXISTS / ADD COLUMN IF NOT EXISTS).

-- 1. Thesis tracker — one row per pick/watchlist idea, capturing what the
-- agent believed at the time so it can later be compared to what happened.
create table if not exists agent_theses (
  id uuid primary key default gen_random_uuid(),
  ticker text not null,
  pick_id uuid references picks(id),
  setup_type text,
  thesis_summary text not null,
  bullish_case text,
  bearish_case text,
  invalidation_point text,
  expected_timeframe text check (expected_timeframe in ('1d', '5d', '20d', '60d')),
  confidence_at_creation text check (confidence_at_creation in ('low', 'medium', 'high')),
  data_confidence_at_creation text check (data_confidence_at_creation in ('low', 'medium', 'high')),
  sources_used jsonb default '[]',
  missing_data_warnings jsonb default '[]',
  chat_message_id uuid references chat_messages(id),
  created_at timestamptz default now()
);

create index if not exists idx_agent_theses_ticker on agent_theses (ticker);
create index if not exists idx_agent_theses_pick_id on agent_theses (pick_id);

-- 2. Outcome tracker — reuses result_placeholders (already used for
-- PickResult) rather than creating a parallel table. Adds the columns
-- needed for manual outcome entry (ticker, evaluation window, prices,
-- catalyst/options-specific correctness, notes, evaluation timestamp)
-- alongside the existing return_*/spy_*/qqq_*/max_*/thesis_correct columns.
alter table result_placeholders add column if not exists ticker text;
alter table result_placeholders add column if not exists evaluation_window text check (evaluation_window in ('1d', '5d', '20d', '60d'));
alter table result_placeholders add column if not exists start_price numeric;
alter table result_placeholders add column if not exists end_price numeric;
alter table result_placeholders add column if not exists return_percent numeric;
alter table result_placeholders add column if not exists spy_return_percent numeric;
alter table result_placeholders add column if not exists qqq_return_percent numeric;
alter table result_placeholders add column if not exists catalyst_played_out boolean;
alter table result_placeholders add column if not exists options_setup_worked boolean;
alter table result_placeholders add column if not exists notes text;
alter table result_placeholders add column if not exists evaluated_at timestamptz default now();
alter table result_placeholders add column if not exists thesis_id uuid references agent_theses(id);

-- 3. Signal performance tracker — a persisted *summary* per signal name,
-- recomputed each time /api/jobs/analyze-learning runs. This is a cache of
-- the analysis, not raw per-use logs (those are derived from picks +
-- result_placeholders at analysis time).
create table if not exists signal_performance (
  id uuid primary key default gen_random_uuid(),
  signal_name text not null unique,
  times_used integer not null default 0,
  average_outcome numeric,
  win_rate numeric,
  false_positive_count integer not null default 0,
  false_negative_count integer not null default 0,
  notes text,
  confidence_in_signal text check (confidence_in_signal in ('insufficient_data', 'low', 'medium', 'high')),
  updated_at timestamptz default now()
);

-- 4. Agent feedback tracker — user feedback on a specific agent chat reply.
create table if not exists agent_feedback (
  id uuid primary key default gen_random_uuid(),
  chat_message_id uuid references chat_messages(id),
  rating text not null check (rating in ('useful', 'not_useful', 'too_confident', 'missed_risk', 'good_risk_call', 'wrong', 'unclear')),
  notes text,
  created_at timestamptz default now()
);

create index if not exists idx_agent_feedback_chat_message_id on agent_feedback (chat_message_id);

-- 5. Learning report — output of /api/jobs/analyze-learning. Manual route
-- only; nothing auto-applies suggested_weight_changes.
create table if not exists learning_reports (
  id uuid primary key default gen_random_uuid(),
  report_date date not null,
  sample_size integer not null default 0,
  summary text,
  best_signals jsonb default '[]',
  worst_signals jsonb default '[]',
  overconfidence_warnings jsonb default '[]',
  missing_data_patterns jsonb default '[]',
  suggested_weight_changes jsonb default '[]',
  raw_metadata jsonb,
  created_at timestamptz default now()
);

create index if not exists idx_learning_reports_report_date on learning_reports (report_date desc);
