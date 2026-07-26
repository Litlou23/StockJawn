-- Schedules intraday portfolio risk checks every 30 minutes during market hours.
--
-- Architecture: pg_cron -> pg_net -> Edge Function -> .NET API
--
-- This is a lightweight job that ONLY checks stop-loss, take-profit, and trailing
-- stops on open positions. Unlike portfolio-refresh, it does not rebuild the
-- dashboard cache, making it fast and cheap to run frequently.
--
-- Schedule: Every 30 minutes from 9:30 AM to 3:30 PM ET (weekdays)
--   ET trading hours in UTC: 13:30 - 19:30
--   Cron: '0,30 14,15,16,17,18,19 * * 1-5' covers 10:00 AM - 3:30 PM ET
--   The portfolio-refresh cron covers 9:30 AM, 11 AM, 1 PM, 3 PM ET,
--   so between the two jobs, risk is checked roughly every 30 min all day.
--
-- PREREQUISITES:
--   1. pg_cron and pg_net extensions enabled
--   2. Vault secrets: project_url, function_auth_token
--   3. Deploy Edge Function: intraday-risk-check

-- Remove previous schedule if re-running
select cron.unschedule('portfolio-intraday-risk-check')
where exists (select 1 from cron.job where jobname = 'portfolio-intraday-risk-check');

-- Intraday Risk Check: weekdays, every 30 min during trading hours (UTC)
select cron.schedule(
  'portfolio-intraday-risk-check',
  '0,30 14,15,16,17,18,19 * * 1-5',
  $$
  select net.http_post(
    url := (select decrypted_secret from vault.decrypted_secrets where name = 'project_url') || '/functions/v1/intraday-risk-check',
    headers := jsonb_build_object(
      'Content-Type', 'application/json',
      'apikey', (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token')
    ),
    body := jsonb_build_object('trigger', 'scheduled', 'jobName', 'intraday-risk-check'),
    timeout_milliseconds := 30000
  ) as request_id;
  $$
);

-- Verify:
--   select jobid, jobname, schedule, active from cron.job
--   where jobname = 'portfolio-intraday-risk-check';
