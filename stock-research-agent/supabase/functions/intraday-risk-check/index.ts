// Supabase Edge Function: intraday-risk-check
//
// Lightweight risk management check that runs every 30 minutes during market hours.
// Checks all open portfolio positions and predictions against stop-loss, take-profit,
// and trailing stop levels. Closes positions that hit their limits.
//
// Unlike portfolio-refresh, this does NOT rebuild the dashboard cache — it only
// evaluates and enforces exit rules. Fast and cheap.
//
// Suggested pg_cron schedule (ET trading hours in UTC, every 30 min):
//   '0,30 14,15,16,17,18,19 * * 1-5'  →  10:00 AM through 3:30 PM ET every 30 min
//
// The portfolio-refresh cron already covers 9:30 AM, 11 AM, 1 PM, 3 PM ET,
// so this fills the gaps between those checks.
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
      JSON.stringify({ ok: false, jobName: 'intraday-risk-check', error: 'APP_BASE_URL not set.' }),
      { status: 500, headers: { 'Content-Type': 'application/json' } },
    );
  }

  const targetUrl = `${appBaseUrl.replace(/\/$/, '')}/api/portfolio/intraday-risk-check`;

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
  return new Response(JSON.stringify({ ok, jobName: 'intraday-risk-check', downstreamStatus, result: downstreamJson, error: ok ? null : downstreamError }), {
    status: ok ? 200 : 502, headers: { 'Content-Type': 'application/json' },
  });
});
