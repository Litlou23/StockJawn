-- Schedules afternoon opportunity scan at 2 PM ET (18:00 UTC) on weekdays.
--
-- Architecture: pg_cron -> pg_net -> Edge Function -> .NET API
--
-- This is a second pass at today's open candidates. The morning scan runs at
-- 9:00 AM ET and generates candidates, but the time-of-day gate blocks entries
-- during the chaotic 9:30-10:00 AM open. This afternoon scan catches those
-- deferred candidates plus any slots freed up by risk management closures.
--
-- Schedule: 2:00 PM ET = 18:00 UTC, weekdays only
--
-- PREREQUISITES:
--   1. pg_cron and pg_net extensions enabled
--   2. Vault secrets: project_url, function_auth_token
--   3. Deploy Edge Function: afternoon-scan

-- Remove previous schedule if re-running
select cron.unschedule('portfolio-afternoon-scan')
where exists (select 1 from cron.job where jobname = 'portfolio-afternoon-scan');

-- Afternoon Opportunity Scan: weekdays at 2 PM ET (18:00 UTC)
select cron.schedule(
  'portfolio-afternoon-scan',
  '0 18 * * 1-5',
  $$
  select net.http_post(
    url := (select decrypted_secret from vault.decrypted_secrets where name = 'project_url') || '/functions/v1/afternoon-scan',
    headers := jsonb_build_object(
      'Content-Type', 'application/json',
      'apikey', (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token')
    ),
    body := jsonb_build_object('trigger', 'scheduled', 'jobName', 'afternoon-scan'),
    timeout_milliseconds := 30000
  ) as request_id;
  $$
);

-- Verify:
--   select jobid, jobname, schedule, active from cron.job
--   where jobname = 'portfolio-afternoon-scan';
