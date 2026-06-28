/**
 * Shared result shape for persistence functions. Saving is always
 * best-effort and additive — if Supabase isn't configured, functions
 * return `{ persisted: false, reason: '...' }` instead of throwing, so
 * job routes can report clearly without failing the whole run.
 */
export interface PersistenceResult {
  persisted: boolean;
  reason?: string;
  count?: number;
}

export const NOT_CONFIGURED: PersistenceResult = {
  persisted: false,
  reason: 'Supabase is not configured (SUPABASE_URL / SUPABASE_SERVICE_ROLE_KEY not set) — skipped.',
};
