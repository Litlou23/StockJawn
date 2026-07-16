-- Phase 2: Profile-aware learning isolation
-- Adds profile_id to learning data tables so each profile's learning is isolated.

-- 1. Add profile_id to signal observations
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'prediction_signal_observations' AND column_name = 'profile_id'
    ) THEN
        ALTER TABLE prediction_signal_observations ADD COLUMN profile_id UUID;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_signal_obs_profile
    ON prediction_signal_observations (profile_id, created_at DESC);

-- 2. Add profile_id to volatility learning stats
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'volatility_learning_stats' AND column_name = 'profile_id'
    ) THEN
        ALTER TABLE volatility_learning_stats ADD COLUMN profile_id UUID;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_vol_learning_profile
    ON volatility_learning_stats (profile_id, created_at DESC);

-- 3. Backfill profile_id from prediction_candidates
UPDATE prediction_signal_observations o
SET profile_id = p.profile_id
FROM prediction_candidates p
WHERE o.prediction_id = p.id
  AND o.profile_id IS NULL
  AND p.profile_id IS NOT NULL;

-- volatility_learning_stats.prediction_id is text; prediction_candidates.id is uuid
UPDATE volatility_learning_stats v
SET profile_id = p.profile_id
FROM prediction_candidates p
WHERE v.prediction_id = p.id::text
  AND v.profile_id IS NULL
  AND p.profile_id IS NOT NULL;
