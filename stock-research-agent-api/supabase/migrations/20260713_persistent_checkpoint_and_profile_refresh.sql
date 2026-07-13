-- =================================================================
-- Persistent Discovery Checkpoint + Profile Refresh
-- Adds: discovery_checkpoints table
-- Adds: refresh_count, last_refresh_reason to historical_research_profiles
-- =================================================================

-- ── Discovery Checkpoints ─────────────────────────────────────────
-- Simple key-value store for discovery cycle checkpoints.
-- Survives app restarts so the continuous discovery engine doesn't
-- re-process events it already handled.
CREATE TABLE IF NOT EXISTS discovery_checkpoints (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    checkpoint_name TEXT NOT NULL,
    checkpoint_value TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- One row per named checkpoint
CREATE UNIQUE INDEX IF NOT EXISTS idx_checkpoint_name
    ON discovery_checkpoints (checkpoint_name);

-- ── Add refresh columns to historical_research_profiles ───────────
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'historical_research_profiles'
        AND column_name = 'refresh_count'
    ) THEN
        ALTER TABLE historical_research_profiles
            ADD COLUMN refresh_count INTEGER NOT NULL DEFAULT 0;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'historical_research_profiles'
        AND column_name = 'last_refresh_reason'
    ) THEN
        ALTER TABLE historical_research_profiles
            ADD COLUMN last_refresh_reason TEXT;
    END IF;
END $$;

-- ── RLS for discovery_checkpoints ─────────────────────────────────
ALTER TABLE discovery_checkpoints ENABLE ROW LEVEL SECURITY;

CREATE POLICY "service_role_checkpoints" ON discovery_checkpoints
    FOR ALL USING (auth.role() = 'service_role');
