// Supabase Edge Function: afternoon-scan
//
// Afternoon opportunity scan — second pass at today's open candidates.
// Catches positions that were deferred by the morning open gate (9:30-10:00 AM ET),
// or that couldn't be opened because slots were full (positions may have closed since).
// Also runs risk management checks while it's at it.
//
// Suggested pg_cron schedule: once daily at 2 PM ET (18:00 UTC)
//   '0 18 * * 1-5'  →  2:00 PM ET weekdays
//
// Required env vars (set with `supabase secrets set`):
//   DOTNET_API_BASE_URL or APP_BASE_URL - base URL of the .NET API

Deno.serve(async (req: Request) => {
  if (req.method !== 'POST') {
    return new Response(JSON.stringify({ ok: false, error: 'Method not allowed' }), {
      status: 405, headers: { 'Content-Type': 'application/json' },
    });
  }

  const appBaseUrl = Deno.env.get('DOTNET_API_BASE_URL') ?? Deno.env.get('APP_BASE_URL');

  if (!appBaseUrl) {
    return new Response(
      JSON.stringify({ ok: false, jobName: 'afternoon-scan', error: 'APP_BASE_URL not set.' }),
      { status: 500, headers: { 'Content-Type': 'application/json' } },
    );
  }

  const targetUrl = `${appBaseUrl.replace(/\/$/, '')}/api/portfolio/afternoon-scan`;

  let downstreamStatus = 0;
  let downstreamJson: Record<string, unknown> | null = null;
  let downstreamError: string | null = null;

  try {
    const response = await fetch(targetUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
    });
    downstreamStatus = response.status;
    try { downstreamJson = await response.json(); } catch { downstreamError = 'Response not JSON'; }
    if (!response.ok) downstreamError = downstreamError ?? (downstreamJson?.error as string) ?? `HTTP ${response.status}`;
  } catch (err) {
    downstreamError = err instanceof Error ? err.message : String(err);
  }

  const ok = downstreamStatus >= 200 && downstreamStatus < 300 && !downstreamError;
  return new Response(JSON.stringify({ ok, jobName: 'afternoon-scan', downstreamStatus, result: downstreamJson, error: ok ? null : downstreamError }), {
    status: ok ? 200 : 502, headers: { 'Content-Type': 'application/json' },
  });
});
