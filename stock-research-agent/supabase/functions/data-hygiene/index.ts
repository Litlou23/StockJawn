// Supabase Edge Function: data-hygiene
//
// Invoked nightly by pg_cron + pg_net. Forwards to the .NET API job route.
// Detects and corrects bad data: false option losses, stale predictions,
// impossible values, orphaned records, low-sample learning stats.
//
// Required env vars (set with `supabase secrets set`):
//   DOTNET_API_BASE_URL or APP_BASE_URL - base URL of the .NET API
//   JOB_RUN_SECRET - shared secret for x-job-secret header

Deno.serve(async (req: Request) => {
  if (req.method !== 'POST') {
    return new Response(JSON.stringify({ ok: false, error: 'Method not allowed' }), {
      status: 405, headers: { 'Content-Type': 'application/json' },
    });
  }

  const appBaseUrl = Deno.env.get('DOTNET_API_BASE_URL') ?? Deno.env.get('APP_BASE_URL');
  const jobRunSecret = Deno.env.get('JOB_RUN_SECRET');

  if (!appBaseUrl || !jobRunSecret) {
    return new Response(
      JSON.stringify({ ok: false, jobName: 'data-hygiene', error: 'APP_BASE_URL or JOB_RUN_SECRET not set.' }),
      { status: 500, headers: { 'Content-Type': 'application/json' } },
    );
  }

  const targetUrl = `${appBaseUrl.replace(/\/$/, '')}/api/jobs/run-data-hygiene`;

  let downstreamStatus = 0;
  let downstreamJson: Record<string, unknown> | null = null;
  let downstreamError: string | null = null;

  try {
    const response = await fetch(targetUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'x-job-secret': jobRunSecret },
      body: JSON.stringify({ trigger: 'scheduled', jobName: 'data-hygiene', scheduledAt: new Date().toISOString() }),
    });
    downstreamStatus = response.status;
    try { downstreamJson = await response.json(); } catch { downstreamError = 'Response not JSON'; }
    if (!response.ok) downstreamError = downstreamError ?? (downstreamJson?.error as string) ?? `HTTP ${response.status}`;
  } catch (err) {
    downstreamError = err instanceof Error ? err.message : String(err);
  }

  const ok = downstreamStatus >= 200 && downstreamStatus < 300 && !downstreamError;
  return new Response(JSON.stringify({ ok, jobName: 'data-hygiene', downstreamStatus, result: downstreamJson, error: ok ? null : downstreamError }), {
    status: ok ? 200 : 502, headers: { 'Content-Type': 'application/json' },
  });
});
