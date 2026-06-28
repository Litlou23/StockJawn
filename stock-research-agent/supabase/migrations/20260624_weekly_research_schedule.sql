-- Schedules the weekly research job: every Monday at 13:00 UTC (approx.
-- 8:00 AM Central — 7:00 AM during standard time, 8:00 AM during daylight
-- saving time; adjust the cron expression below if you want it pinned to
-- exactly one offset year-round instead of following UTC).
--
-- Architecture: pg_cron fires -> pg_net does an HTTP POST to the
-- `weekly-research` Edge Function -> the Edge Function calls
-- POST /api/jobs/run-weekly-research on the Next.js app with the
-- x-job-secret header -> that route runs the weekly research workflow and
-- saves results to Supabase. This file only sets up the first hop
-- (pg_cron -> Edge Function); the second hop's secret (JOB_RUN_SECRET) is
-- a separate Edge Function secret, set with `supabase secrets set`, not
-- stored here.
--
-- IMPORTANT — run the two `vault.create_secret` blocks below ONCE with
-- your real values before running the `cron.schedule` block. Do not commit
-- real secret values into this file or any other tracked file — paste them
-- directly into the Supabase SQL Editor when you run this.

-- 1. Enable the required extensions (safe to run repeatedly).
create extension if not exists pg_cron;
create extension if not exists pg_net;

-- 2. Store secrets in Vault (run once, with your real values — DO NOT commit
-- real values). `vault.create_secret(secret_value, unique_name)`.
--
--   select vault.create_secret('https://<your-project-ref>.supabase.co', 'project_url');
--   select vault.create_secret('<your-anon-or-service-role-key>', 'function_auth_token');
--   select vault.create_secret('<the-same-value-as-JOB_RUN_SECRET-in-.env.local-and-edge-function-secrets>', 'job_run_secret');
--
-- If you ever need to rotate one of these, use:
--   select vault.update_secret(id, new_secret) where id = (select id from vault.secrets where name = '...');
-- (look up the row's id via `select id, name from vault.secrets;`).

-- 3. Remove any previous schedule with this name before re-creating it, so
-- this file is safe to re-run if you change the cron expression later.
select cron.unschedule('weekly-research-monday')
where exists (select 1 from cron.job where jobname = 'weekly-research-monday');

select cron.schedule(
  'weekly-research-monday',
  '0 13 * * 1', -- Monday 13:00 UTC ≈ Monday 8:00 AM Central (7:00 AM CST / 8:00 AM CDT)
  $$
  select net.http_post(
    url := (select decrypted_secret from vault.decrypted_secrets where name = 'project_url') || '/functions/v1/weekly-research',
    headers := jsonb_build_object(
      'Content-Type', 'application/json',
      'apikey', (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'x-job-secret', (select decrypted_secret from vault.decrypted_secrets where name = 'job_run_secret')
    ),
    body := jsonb_build_object(
      'trigger', 'scheduled',
      'jobName', 'weekly-research',
      'runType', 'weekly',
      'scheduledAt', now()
    ),
    timeout_milliseconds := 25000
  ) as request_id;
  $$
);

-- Verify it's scheduled:
--   select jobid, jobname, schedule, active from cron.job where jobname = 'weekly-research-monday';

-- To disable without deleting the row:
--   select cron.alter_job((select jobid from cron.job where jobname = 'weekly-research-monday'), active := false);

-- To remove entirely:
--   select cron.unschedule('weekly-research-monday');
