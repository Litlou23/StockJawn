-- Columns the StockJawn Robinhood executor (ClaudePickExecutor) reads and writes.
-- approval_status already allows executing / executed / failed, so the check constraint is untouched.

alter table claude_daily_picks
  add column if not exists order_quantity numeric,
  add column if not exists order_id text,
  add column if not exists order_details jsonb,
  add column if not exists execution_error text,
  add column if not exists execution_started_at timestamptz,
  add column if not exists executed_at timestamptz;

comment on column claude_daily_picks.order_quantity is
  'Shares to buy. Set by whatever creates the pick until the sizing rule is ported into StockJawn.';
comment on column claude_daily_picks.order_details is
  'Order request + raw Robinhood MCP response written by ClaudePickExecutor.';

create index if not exists idx_claude_daily_picks_approval_status_date
  on claude_daily_picks (approval_status, pick_date);
