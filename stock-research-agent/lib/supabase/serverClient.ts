import 'server-only';

/**
 * Server-only Supabase client using the service role key — this bypasses
 * RLS by design, which is fine because nothing in this app exposes this
 * client (or the key) to the browser. Never import this from a client
 * component or from agentChatService.ts's client-facing path.
 *
 * Uses NEXT_PUBLIC_SUPABASE_URL (safe to be public — it's just an endpoint)
 * but SUPABASE_SERVICE_ROLE_KEY (deliberately NOT prefixed NEXT_PUBLIC_,
 * server-only). If either is unset, `isSupabaseConfigured()` returns false
 * and every repository function in /services/persistence no-ops
 * gracefully instead of throwing — persistence is additive, never a hard
 * dependency for the app to function.
 */

import { createClient, SupabaseClient } from '@supabase/supabase-js';

let client: SupabaseClient | null = null;

export function isSupabaseConfigured(): boolean {
  return Boolean(process.env.NEXT_PUBLIC_SUPABASE_URL && process.env.SUPABASE_SERVICE_ROLE_KEY);
}

export function getSupabaseClient(): SupabaseClient {
  if (!isSupabaseConfigured()) {
    throw new Error(
      'Supabase is not configured. Set NEXT_PUBLIC_SUPABASE_URL and SUPABASE_SERVICE_ROLE_KEY in .env.local.',
    );
  }
  if (!client) {
    client = createClient(process.env.NEXT_PUBLIC_SUPABASE_URL!, process.env.SUPABASE_SERVICE_ROLE_KEY!, {
      auth: { persistSession: false },
    });
  }
  return client;
}
