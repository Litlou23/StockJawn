-- Stores the Robinhood MCP login for the StockJawn executor (one row, id = 'default').
-- refresh_token_enc is AES-GCM encrypted by the .NET API with a key derived from JOB_RUN_SECRET; the key never lives here.
-- RLS on with no policies: only the service role (the .NET API) can read or write it.

create table if not exists robinhood_oauth_credentials (
  id text primary key,
  client_id text,
  redirect_uri text,
  refresh_token_enc text,
  registered_at timestamptz,
  last_refresh_at timestamptz,
  last_error text,
  updated_at timestamptz not null default now()
);

alter table robinhood_oauth_credentials enable row level security;
revoke all on robinhood_oauth_credentials from anon, authenticated;
