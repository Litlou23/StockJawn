-- Approval lockdown: approvals only go through the server-side /api/approve route (PIN checked there),
-- the PIN is no longer readable with the public anon key, and the database enforces status transitions.

-- 1. The browser (anon key) can no longer write claude_daily_picks at all.
drop policy if exists anon_approve_picks on claude_daily_picks;

-- 2. Anon can still read configs, except the approval PIN.
drop policy if exists anon_read_configs on scoring_weight_overrides;
create policy anon_read_configs on scoring_weight_overrides
  for select to anon
  using (signal_name <> 'approval_pin');

-- 3. Status transitions, enforced for every writer (service role included).
create or replace function enforce_claude_pick_status_transition()
returns trigger
language plpgsql
as $$
declare
  allowed text[];
begin
  if new.approval_status is not distinct from old.approval_status then
    return new;
  end if;

  if new.approval_status = 'approved' then
    if old.approval_status is distinct from 'pending' then
      raise exception 'claude_daily_picks %: cannot move % -> approved (only pending can be approved)', old.id, old.approval_status;
    end if;
    if new.approved_at is null then
      raise exception 'claude_daily_picks %: approved_at must be set when approving', old.id;
    end if;
    return new;
  end if;

  allowed := case old.approval_status
    when 'pending'   then array['expired', 'skipped']
    when 'approved'  then array['executing', 'executed', 'ready', 'failed', 'expired', 'skipped']
    when 'executing' then array['executed', 'ready', 'failed']
    when 'ready'     then array['executed', 'failed', 'skipped']
    when 'failed'    then array['executed', 'ready']
    when 'executed'  then array[]::text[]
    when 'expired'   then array[]::text[]
    when 'skipped'   then array[]::text[]
    else null
  end;

  -- Statuses not listed above (e.g. future research states) stay unrestricted, apart from the approved rule.
  if allowed is not null and not (new.approval_status = any(allowed)) then
    raise exception 'claude_daily_picks %: status change % -> % is not allowed', old.id, old.approval_status, new.approval_status;
  end if;

  return new;
end;
$$;

drop trigger if exists trg_claude_pick_status_transition on claude_daily_picks;
create trigger trg_claude_pick_status_transition
  before update of approval_status on claude_daily_picks
  for each row execute function enforce_claude_pick_status_transition();
