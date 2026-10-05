-- Server pick checker results (PickChecker): problems, R:R/distance summary, last check time.
alter table claude_daily_picks
  add column if not exists check_flags text,
  add column if not exists check_summary text,
  add column if not exists checked_at timestamptz;
comment on column claude_daily_picks.check_flags is 'Problems found by the server pick checker (empty = none)';
