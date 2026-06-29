import { NextResponse } from 'next/server';

export const runtime = 'nodejs';
export const maxDuration = 30;

/**
 * GET /api/connectivity
 * Tests connectivity to all external services and returns status for each.
 */

interface ServiceCheck {
  name: string;
  status: 'ok' | 'error' | 'not_configured';
  latencyMs: number | null;
  message: string;
  details?: Record<string, unknown>;
}

async function checkService(
  name: string,
  fn: () => Promise<{ message: string; details?: Record<string, unknown> }>
): Promise<ServiceCheck> {
  const start = Date.now();
  try {
    const result = await fn();
    return {
      name,
      status: 'ok',
      latencyMs: Date.now() - start,
      message: result.message,
      details: result.details,
    };
  } catch (err) {
    return {
      name,
      status: 'error',
      latencyMs: Date.now() - start,
      message: err instanceof Error ? err.message : 'Unknown error',
    };
  }
}

export async function GET() {
  const base = process.env.AGENT_API_BASE_URL;
  const checks: ServiceCheck[] = [];

  // 1. .NET API health
  if (base) {
    checks.push(
      await checkService('.NET API (health)', async () => {
        const res = await fetch(`${base}/health`, { signal: AbortSignal.timeout(10000) });
        const data = await res.json();
        return { message: `${data.status} — ${data.service}`, details: data };
      })
    );
  } else {
    checks.push({ name: '.NET API', status: 'not_configured', latencyMs: null, message: 'AGENT_API_BASE_URL not set' });
  }

  // 2. Supabase (via .NET API debug endpoint)
  if (base) {
    checks.push(
      await checkService('Supabase (via .NET API)', async () => {
        const res = await fetch(`${base}/api/debug/research-engine`, { signal: AbortSignal.timeout(10000) });
        const data = await res.json();
        return {
          message: data.supabaseConfigured ? 'Connected' : 'Not configured',
          details: {
            supabaseConfigured: data.supabaseConfigured,
            recentRunsCount: data.recentRuns?.length ?? 0,
          },
        };
      })
    );
  }

  // 3. Twelve Data (via .NET API debug endpoint)
  if (base) {
    checks.push(
      await checkService('Twelve Data API', async () => {
        const res = await fetch(`${base}/api/debug/market-data?ticker=SPY`, { signal: AbortSignal.timeout(15000) });
        const data = await res.json();
        const health = data.providerHealth;
        return {
          message: health?.status === 'healthy'
            ? `Healthy — SPY quote: $${data.quote?.price ?? 'N/A'}`
            : health?.status === 'not_configured'
              ? 'TWELVE_DATA_API_KEY not set'
              : `Status: ${health?.status ?? 'unknown'}`,
          details: {
            providerStatus: health?.status,
            hasQuote: data.quote !== null,
            price: data.quote?.price,
          },
        };
      })
    );
  }

  // 4. OpenAI (via .NET API — just check if configured)
  if (base) {
    checks.push(
      await checkService('OpenAI API', async () => {
        const res = await fetch(`${base}/api/ai/complete`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            messages: [{ role: 'user', content: 'Say "ok" in one word.' }],
            maxTokens: 5,
          }),
          signal: AbortSignal.timeout(15000),
        });
        if (res.ok) {
          const text = await res.text();
          return { message: `Connected — response: "${text.slice(0, 50)}"` };
        }
        const err = await res.text().catch(() => '');
        return { message: `Error ${res.status}: ${err.slice(0, 100)}` };
      })
    );
  }

  // 5. Finnhub (via .NET API — check if key is configured)
  if (base) {
    checks.push(
      await checkService('Finnhub API', async () => {
        // No dedicated endpoint yet — we'll call health and check logs.
        // For now, just ping the .NET API health to confirm it's up,
        // and report based on whether the key is set.
        const res = await fetch(`${base}/health`, { signal: AbortSignal.timeout(5000) });
        if (!res.ok) throw new Error(`API returned ${res.status}`);
        return { message: 'Check FINNHUB_API_KEY on Azure — no dedicated health endpoint yet' };
      })
    );
  }

  // 6. RSS feeds (direct connectivity test)
  const rssFeeds = [
    { name: 'Yahoo Finance RSS', url: 'https://finance.yahoo.com/news/rssindex' },
    { name: 'CNBC RSS', url: 'https://www.cnbc.com/id/100003114/device/rss/rss.html' },
    { name: 'MarketWatch RSS', url: 'http://feeds.marketwatch.com/marketwatch/topstories/' },
    { name: 'CNBC Technology RSS', url: 'https://www.cnbc.com/id/19854910/device/rss/rss.html' },
    { name: 'Investing.com RSS', url: 'https://www.investing.com/rss/news.rss' },
  ];

  for (const feed of rssFeeds) {
    checks.push(
      await checkService(feed.name, async () => {
        const res = await fetch(feed.url, {
          headers: { 'User-Agent': 'Mozilla/5.0 (compatible; StockResearchAgent/1.0)' },
          signal: AbortSignal.timeout(8000),
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const text = await res.text();
        const itemCount = (text.match(/<item>/gi) || []).length || (text.match(/<entry>/gi) || []).length;
        return { message: `OK — ${itemCount} items`, details: { itemCount } };
      })
    );
  }

  const allOk = checks.every((c) => c.status === 'ok');
  const configured = checks.filter((c) => c.status !== 'not_configured');
  const healthy = configured.filter((c) => c.status === 'ok');

  return NextResponse.json({
    overall: allOk ? 'all_healthy' : 'issues_detected',
    summary: `${healthy.length}/${configured.length} services healthy`,
    checks,
    checkedAt: new Date().toISOString(),
  });
}
