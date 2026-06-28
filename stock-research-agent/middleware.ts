import type { NextRequest } from 'next/server';
import { NextResponse } from 'next/server';

/**
 * Site-wide HTTP Basic Auth gate. Runs in front of every page and API
 * route except the ones explicitly excluded below — see `config.matcher`.
 * Intended for "don't let strangers browse this once it's on a real
 * domain", not as a real multi-user auth system.
 *
 * Deliberately named/filed as `middleware` (Next 16's deprecated
 * convention), not `proxy` — Next 16's `proxy.ts` can ONLY run on the
 * Node.js runtime (no edge option, see the version-16 upgrade guide), but
 * Netlify's plugin bundles it as a Deno-based Edge Function regardless,
 * which fails to load ("Could not load edge function ...
 * node-middleware"). This file has no Node-only dependencies, so the
 * legacy `middleware` convention (which still defaults to the Edge
 * runtime) avoids that mismatch entirely. Revisit once Netlify's
 * @netlify/plugin-nextjs supports Next 16's Node-only Proxy.
 *
 * Excluded:
 * - /api/jobs/run-weekly-research: called machine-to-machine by the
 *   weekly-research Edge Function (via pg_cron), which can't supply a
 *   Basic Auth header. It already has its own protection (x-job-secret —
 *   see app/api/jobs/run-weekly-research/route.ts).
 * - Static assets / well-known metadata files.
 *
 * Everything else — including the other manual job routes
 * (intake-catalysts, score-watchlist, generate-daily-report,
 * analyze-learning) and /api/agent-chat — now requires Basic Auth too, so
 * manual curl/Postman testing needs `-u <user>:<password>` after this is
 * deployed.
 */
export function middleware(request: NextRequest) {
  const expectedUser = process.env.SITE_AUTH_USER || 'admin';
  const expectedPassword = process.env.SITE_AUTH_PASSWORD;

  if (!expectedPassword) {
    console.error('proxy: SITE_AUTH_PASSWORD is not set — blocking all access until it is configured.');
    return new Response('Site is not configured correctly (SITE_AUTH_PASSWORD missing).', { status: 500 });
  }

  const authHeader = request.headers.get('authorization');
  if (authHeader?.startsWith('Basic ')) {
    try {
      const decoded = atob(authHeader.slice('Basic '.length));
      const separatorIndex = decoded.indexOf(':');
      const user = decoded.slice(0, separatorIndex);
      const password = decoded.slice(separatorIndex + 1);
      if (user === expectedUser && password === expectedPassword) {
        const response = NextResponse.next();
        // Never let a CDN cache an authenticated response and replay it to
        // someone who hasn't entered credentials.
        response.headers.set('Cache-Control', 'no-store');
        return response;
      }
    } catch {
      // Malformed header — fall through to the 401 below.
    }
  }

  return new Response('Authentication required.', {
    status: 401,
    headers: {
      'WWW-Authenticate': 'Basic realm="Stock Research Agent", charset="UTF-8"',
      'Cache-Control': 'no-store',
    },
  });
}

export const config = {
  matcher: [
    '/((?!api/jobs/run-weekly-research|_next/static|_next/image|favicon\\.ico|robots\\.txt|sitemap\\.xml|.*\\.(?:png|jpg|jpeg|gif|webp|svg|ico|css|js)$).*)',
  ],
};
