import { NextResponse } from 'next/server';

export const runtime = 'nodejs';

/**
 * GET /api/debug/agent-status
 *
 * Returns safe, non-secret diagnostic info about the agent's AI provider
 * configuration. Use this to quickly check whether the .NET API is configured
 * and reachable without sending a real chat message.
 */
export async function GET() {
  const agentApiBaseUrl = process.env.AGENT_API_BASE_URL;
  const agentApiConfigured = Boolean(agentApiBaseUrl);
  const hasSupabaseUrl = Boolean(process.env.NEXT_PUBLIC_SUPABASE_URL);
  const hasSupabaseServiceKey = Boolean(process.env.SUPABASE_SERVICE_ROLE_KEY);

  let dotnetApiReachable: boolean | null = null;
  let dotnetApiError: string | null = null;
  let dotnetApiResponseTime: number | null = null;

  if (agentApiConfigured) {
    const start = Date.now();
    try {
      // Attempt a lightweight request to the .NET API base URL.
      // Most .NET APIs respond to a GET at the root or return 404 — either
      // proves the server is up. We use a short timeout.
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 5000);
      const res = await fetch(`${agentApiBaseUrl}/api/ai/complete`, {
        method: 'OPTIONS',
        signal: controller.signal,
      }).catch((err) => {
        // Also try a simple GET to the base URL as fallback
        clearTimeout(timeout);
        throw err;
      });
      clearTimeout(timeout);
      dotnetApiResponseTime = Date.now() - start;
      // Any response (even 4xx/5xx) means the server is reachable
      dotnetApiReachable = true;
    } catch (err) {
      dotnetApiResponseTime = Date.now() - start;
      dotnetApiReachable = false;
      dotnetApiError = err instanceof Error ? err.message : 'Unknown error';
    }
  }

  return NextResponse.json({
    timestamp: new Date().toISOString(),
    environment: process.env.NODE_ENV,
    agentApiConfigured,
    // Show the host portion only (no path, no credentials)
    agentApiHost: agentApiConfigured
      ? (() => {
          try {
            return new URL(agentApiBaseUrl!).host;
          } catch {
            return 'invalid-url';
          }
        })()
      : null,
    dotnetApiReachable,
    dotnetApiError,
    dotnetApiResponseTime,
    supabase: {
      urlConfigured: hasSupabaseUrl,
      serviceKeyConfigured: hasSupabaseServiceKey,
    },
    explanation:
      'This app does NOT call OpenAI directly. It forwards chat messages to a .NET API ' +
      'at AGENT_API_BASE_URL/api/ai/complete, which then calls OpenAI. If dotnetApiReachable ' +
      'is false, the server-side route returns 502 and the client falls back to rule-based ' +
      'mock responses — which is why the agent "responds" but OpenAI shows no usage.',
  });
}
