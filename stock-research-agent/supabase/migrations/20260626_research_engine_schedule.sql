-- Schedules the daily research engine jobs via pg_cron.
--
-- Architecture (same as weekly-research):
--   pg_cron -> pg_net HTTP POST -> Edge Function -> Next.js API route
--
-- PREREQUISITES:
--   1. pg_cron and pg_net extensions enabled (done in weekly_research_schedule)
--   2. Vault secrets already set: project_url, function_auth_token, job_run_secret
--      (done in weekly_research_schedule -- reused here)
--   3. Deploy three Edge Functions: morning-scan, end-of-day-review, learning-update
--      Each must forward the request to your Next.js app with x-job-secret header.
--
-- Schedule (Central Time -> UTC):
--   Morning scan:    8:00 AM CT  = 13:00 UTC  (weekdays)
--   EOD review:      4:30 PM CT  = 21:30 UTC  (weekdays)
--   Learning update: 5:00 PM CT  = 22:00 UTC  (weekdays)

-- Remove previous schedules if re-running
select cron.unschedule('research-morning-scan')
where exists (select 1 from cron.job where jobname = 'research-morning-scan');

select cron.unschedule('research-eod-review')
where exists (select 1 from cron.job where jobname = 'research-eod-review');

select cron.unschedule('research-learning-update')
where exists (select 1 from cron.job where jobname = 'research-learning-update');

-- 1. Morning Scan: weekdays at 13:00 UTC (8:00 AM CT)
select cron.schedule(
  'research-morning-scan',
  '0 13 * * 1-5',
  $$
  select net.http_post(
    url := (select decrypted_secret from vault.decrypted_secrets where name = 'project_url') || '/functions/v1/morning-scan',
    headers := jsonb_build_object(
      'Content-Type', 'application/json',
      'apikey', (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'x-job-secret', (select decrypted_secret from vault.decrypted_secrets where name = 'job_run_secret')
    ),
    body := jsonb_build_object('trigger', 'scheduled', 'jobName', 'morning-scan'),
    timeout_milliseconds := 55000
  ) as request_id;
  $$
);

-- 2. EOD Review: weekdays at 21:30 UTC (4:30 PM CT)
select cron.schedule(
  'research-eod-review',
  '30 21 * * 1-5',
  $$
  select net.http_post(
    url := (select decrypted_secret from vault.decrypted_secrets where name = 'project_url') || '/functions/v1/end-of-day-review',
    headers := jsonb_build_object(
      'Content-Type', 'application/json',
      'apikey', (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'x-job-secret', (select decrypted_secret from vault.decrypted_secrets where name = 'job_run_secret')
    ),
    body := jsonb_build_object('trigger', 'scheduled', 'jobName', 'end-of-day-review'),
    timeout_milliseconds := 55000
  ) as request_id;
  $$
);

-- 3. Learning Update: weekdays at 22:00 UTC (5:00 PM CT)
select cron.schedule(
  'research-learning-update',
  '0 22 * * 1-5',
  $$
  select net.http_post(
    url := (select decrypted_secret from vault.decrypted_secrets where name = 'project_url') || '/functions/v1/learning-update',
    headers := jsonb_build_object(
      'Content-Type', 'application/json',
      'apikey', (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'x-job-secret', (select decrypted_secret from vault.decrypted_secrets where name = 'job_run_secret')
    ),
    body := jsonb_build_object('trigger', 'scheduled', 'jobName', 'learning-update'),
    timeout_milliseconds := 55000
  ) as request_id;
  $$
);

-- Verify:
--   select jobid, jobname, schedule, active from cron.job
--   where jobname like 'research-%';
