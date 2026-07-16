-- Phase 1: Prediction Profiles foundation
-- Creates profile tables, adds profile_id to predictions, seeds champion.
-- NOTE: Tables were initially created by an earlier migration with column "name".
-- Column was renamed: ALTER TABLE prediction_profiles RENAME COLUMN name TO profile_name;

-- 1. Profile table
CREATE TABLE IF NOT EXISTS prediction_profiles (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_name     TEXT NOT NULL UNIQUE,
    description      TEXT,
    role             TEXT NOT NULL DEFAULT 'challenger'
                     CHECK (role IN ('champion', 'challenger')),
    is_enabled       BOOLEAN NOT NULL DEFAULT true,
    learning_enabled BOOLEAN NOT NULL DEFAULT true,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Enforce exactly one champion at any time
CREATE UNIQUE INDEX IF NOT EXISTS idx_prediction_profiles_champion
    ON prediction_profiles (role) WHERE role = 'champion';

-- 2. Profile config (weight overrides per profile)
CREATE TABLE IF NOT EXISTS prediction_profile_configs (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id      UUID NOT NULL REFERENCES prediction_profiles(id) ON DELETE CASCADE,
    config_key      TEXT NOT NULL,
    config_value    DOUBLE PRECISION NOT NULL,
    description     TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (profile_id, config_key)
);

-- 3. Add profile_id to prediction_candidates (soft reference — survives profile deletion)
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'prediction_candidates' AND column_name = 'profile_id'
    ) THEN
        ALTER TABLE prediction_candidates ADD COLUMN profile_id UUID;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_predictions_profile
    ON prediction_candidates (profile_id, created_at DESC);

-- 4. Seed the champion (Production) profile
INSERT INTO prediction_profiles (profile_name, description, role, is_enabled, learning_enabled)
VALUES (
    'Production',
    'Current production configuration — weights learned from live data',
    'champion',
    true,
    true
)
ON CONFLICT (profile_name) DO NOTHING;

-- 5. Backfill existing predictions with the champion profile_id
UPDATE prediction_candidates
SET profile_id = (SELECT id FROM prediction_profiles WHERE role = 'champion')
WHERE profile_id IS NULL;
