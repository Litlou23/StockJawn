-- Adds a 30-minute retry for the EOD review job.
--
-- If the primary EOD cron (21:30 UTC) fails because the .NET API was
-- temporarily unreachable, this retry fires at 22:00 UTC and checks
-- whether an end_of_day_review research_run was created today.
-- If not, it re-fires the edge function. If EOD already ran, it's a no-op.
--
-- Also shifts learning-update from 22:00 to 22:05 to avoid overlap.

-- 1. Create the retry function
create or replace function public.retry_eod_if_missed()
returns void
language plpgsql
security definer
as $$
declare
  eod_ran boolean;
begin
  -- Check if an end_of_day_review ran today (UTC)
  select exists(
    select 1 from research_runs
    where run_type = 'end_of_day_review'
      and started_at >= date_trunc('day', now())
      and status in ('completed', 'running')
  ) into eod_ran;

  if eod_ran then
    raise log '[eod-retry] EOD already ran today — skipping retry';
    return;
  end if;

  raise log '[eod-retry] EOD did not run today — firing retry';

  perform net.http_post(
    url := (select decrypted_secret from vault.decrypted_secrets where name = 'project_url')
           || '/functions/v1/end-of-day-review',
    headers := jsonb_build_object(
      'Content-Type', 'application/json',
      'apikey', (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'function_auth_token'),
      'x-job-secret', (select decrypted_secret from vault.decrypted_secrets where name = 'job_run_secret')
    ),
    body := jsonb_build_object('trigger', 'scheduled-retry', 'jobName', 'end-of-day-review'),
    timeout_milliseconds := 55000
  );
end;
$$;

-- 2. Schedule the retry: weekdays at 22:00 UTC (5:00 PM CT), 30 min after primary
select cron.unschedule('research-eod-retry')
where exists (select 1 from cron.job where jobname = 'research-eod-retry');

select cron.schedule(
  'research-eod-retry',
  '0 22 * * 1-5',
  $$ select public.retry_eod_if_missed(); $$
);

-- 3. Shift learning-update from 22:00 to 22:05 so EOD retry has time to land
select cron.unschedule('research-learning-update')
where exists (select 1 from cron.job where jobname = 'research-learning-update');

select cron.schedule(
  'research-learning-update',
  '5 22 * * 1-5',
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
