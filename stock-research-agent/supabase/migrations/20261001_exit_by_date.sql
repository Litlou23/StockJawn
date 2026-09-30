-- Sell on/after this day (ET) whatever the price — used for short holds like inverse ETFs (SPXS/SQQQ/UVXY decay).
alter table claude_daily_picks add column if not exists exit_by_date date;
