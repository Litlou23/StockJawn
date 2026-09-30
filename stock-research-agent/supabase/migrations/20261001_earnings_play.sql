-- A pick flagged earnings_play may hold through its earnings report (max 1 open at a time); others are blocked.
alter table claude_daily_picks add column if not exists earnings_play boolean not null default false;
