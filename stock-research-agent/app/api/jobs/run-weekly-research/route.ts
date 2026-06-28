import { NextRequest, NextResponse } from 'next/server';
import { runWeeklyResearch } from '@/services/weeklyResearch/weeklyResearchService';

export const runtime = 'nodejs';

/**
 * Scheduled weekly research job, protected by a shared secret (not Supabase
 * Auth — this is a service-to-service call from the weekly-research Edge
 * Function, invoked by pg_cron). Produces research/watchlist candidates
 * only; never executes, sizes, or recommends a trade.
 *
 * Still callable manually for testing — see README/testing notes — by
 * sending the same x-job-secret header yourself (curl/Postman), no need to
 * wait for the Monday schedule.
 */
export async function POST(req: NextRequest) {
  const expectedSecret = process.env.JOB_RUN_SECRET;
  if (!expectedSecret) {
    console.error('run-weekly-research: JOB_RUN_SECRET is not set in this environment — refusing to run unprotected.');
    return NextResponse.json({ error: 'Server misconfigured: JOB_RUN_SECRET not set' }, { status: 500 });
  }

  const providedSecret = req.headers.get('x-job-secret');
  if (!providedSecret || providedSecret !== expectedSecret) {
    return NextResponse.json({ error: 'Unauthorized' }, { status: 401 });
  }

  let trigger = 'manual';
  try {
    const body = await req.json();
    if (body?.trigger) trigger = String(body.trigger);
  } catch {
    // No/invalid body is fine — this route requires no input beyond the secret header.
  }

  try {
    const result = await runWeeklyResearch(trigger);
    return NextResponse.json(result);
  } catch (err) {
    console.error('jobs/run-weekly-research failed', err);
    return NextResponse.json(
      { error: err instanceof Error ? err.message : 'Unknown error' },
      { status: 500 },
    );
  }
}
