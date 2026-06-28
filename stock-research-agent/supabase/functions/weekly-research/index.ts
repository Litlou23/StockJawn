// Supabase Edge Function: weekly-research
//
// Invoked weekly by a pg_cron + pg_net schedule (see the migration SQL in
// supabase/migrations for the cron.schedule() call). Its only job is to
// call the .NET API job route, forward the shared secret, and return a
// clear, useful JSON result. It does not score anything itself, does not
// touch Supabase directly, does not execute trades, and does not connect
// to a brokerage.
//
// Required environment variables (set with `supabase secrets set`):
//   DOTNET_API_BASE_URL - base URL of the .NET API, e.g. https://stock-research-agent-api-....azurewebsites.net
//                         Falls back to APP_BASE_URL if not set.
//   JOB_RUN_SECRET      - shared secret also set as JOB_RUN_SECRET in the .NET API's env

Deno.serve(async (req: Request) => {
  if (req.method !== 'POST') {
    return new Response(JSON.stringify({ ok: false, error: 'Method not allowed' }), {
      status: 405,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  const apiBaseUrl = Deno.env.get('DOTNET_API_BASE_URL') ?? Deno.env.get('APP_BASE_URL');
  const jobRunSecret = Deno.env.get('JOB_RUN_SECRET');

  if (!apiBaseUrl || !jobRunSecret) {
    console.error('weekly-research: missing DOTNET_API_BASE_URL/APP_BASE_URL or JOB_RUN_SECRET environment variable.');
    return new Response(
      JSON.stringify({ ok: false, jobName: 'weekly-research', error: 'Edge Function misconfigured: DOTNET_API_BASE_URL or JOB_RUN_SECRET not set.' }),
      { status: 500, headers: { 'Content-Type': 'application/json' } },
    );
  }

  let incomingTrigger = 'scheduled';
  try {
    const incoming = await req.json();
    if (incoming?.trigger) incomingTrigger = String(incoming.trigger);
  } catch {
    // No body, or not JSON — fine, fall back to the default.
  }

  const targetUrl = `${apiBaseUrl.replace(/\/$/, '')}/api/jobs/run-weekly-research`;
  const requestBody = {
    trigger: incomingTrigger,
    jobName: 'weekly-research',
    scheduledAt: new Date().toISOString(),
  };

  let downstreamStatus = 0;
  let downstreamJson: Record<string, unknown> | null = null;
  let downstreamError: string | null = null;

  try {
    const response = await fetch(targetUrl, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'x-job-secret': jobRunSecret,
      },
      body: JSON.stringify(requestBody),
    });
    downstreamStatus = response.status;

    try {
      downstreamJson = await response.json();
    } catch (parseErr) {
      downstreamError = `Downstream response was not valid JSON: ${parseErr instanceof Error ? parseErr.message : String(parseErr)}`;
    }

    if (!response.ok) {
      downstreamError = downstreamError ?? (downstreamJson?.error as string | undefined) ?? `Downstream route returned HTTP ${response.status}`;
      console.error('weekly-research: downstream /api/jobs/run-weekly-research failed', {
        status: downstreamStatus,
        body: downstreamJson,
      });
    }
  } catch (err) {
    downstreamError = err instanceof Error ? err.message : String(err);
    console.error('weekly-research: failed to reach .NET API', { targetUrl, error: downstreamError });
  }

  const ok = downstreamStatus >= 200 && downstreamStatus < 300 && !downstreamError;

  const result = {
    ok,
    jobName: 'weekly-research',
    downstreamStatus,
    runId: downstreamJson?.runId ?? null,
    activeWatchlistCount: downstreamJson?.activeWatchlistCount ?? null,
    error: ok ? null : downstreamError ?? 'Unknown error calling the weekly research route.',
  };

  return new Response(JSON.stringify(result), {
    status: ok ? 200 : 502,
    headers: { 'Content-Type': 'application/json' },
  });
});
