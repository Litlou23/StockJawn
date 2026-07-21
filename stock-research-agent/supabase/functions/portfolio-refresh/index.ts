// Supabase Edge Function: portfolio-refresh
//
// Invoked ~4× during trading hours by pg_cron + pg_net.
// Refreshes the cached portfolio dashboard data so page loads are instant.
//
// Suggested pg_cron schedule (ET trading hours in UTC):
//   '30 13,15,17,19 * * 1-5'  →  9:30 AM, 11 AM, 1 PM, 3 PM ET
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
      JSON.stringify({ ok: false, jobName: 'portfolio-refresh', error: 'APP_BASE_URL not set.' }),
      { status: 500, headers: { 'Content-Type': 'application/json' } },
    );
  }

  const targetUrl = `${appBaseUrl.replace(/\/$/, '')}/api/portfolio/dashboard/refresh`;

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
  return new Response(JSON.stringify({ ok, jobName: 'portfolio-refresh', downstreamStatus, result: downstreamJson, error: ok ? null : downstreamError }), {
    status: ok ? 200 : 502, headers: { 'Content-Type': 'application/json' },
  });
});
