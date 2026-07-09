-- Schedules the nightly data hygiene job via pg_cron.
--
-- Architecture: pg_cron -> pg_net HTTP POST -> Edge Function -> .NET API
--
-- Schedule: Every night at 11:00 PM CT (04:00 UTC next day), 7 days a week.
-- Runs after markets close and after all other jobs finish.

-- Remove previous schedule if re-running
select cron.unschedule('data-hygiene')
where exists (select 1 from cron.job where jobname = 'data-hygiene');

-- Data Hygiene: nightly at 04:00 UTC (11:00 PM CT)
select cron.schedule(
  'data-hygiene',
  '0 4 * * *',
  $$
  select net.http_post(
    url := (select decrypted_secret from vault.decrypted_secrets where name = 'project_url') || '/functions/v1/data-hygiene',
    headers := jsonb_build_object(
      'Content-Type', 'application/json',
      'apikey', (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'x-job-secret', (select decrypted_secret from vault.decrypted_secrets where name = 'job_run_secret')
    ),
    body := jsonb_build_object('trigger', 'scheduled', 'jobName', 'data-hygiene'),
    timeout_milliseconds := 55000
  ) as request_id;
  $$
);

-- Verify:
--   select jobid, jobname, schedule, active from cron.job
--   where jobname = 'data-hygiene';
