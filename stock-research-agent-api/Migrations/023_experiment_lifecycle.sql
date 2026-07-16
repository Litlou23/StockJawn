-- 023: Add experiment lifecycle columns to prediction_profiles
-- experiment_status: draft | testing | completed | archived (champion is always 'active')
-- hypothesis: free-text field describing what this challenger is testing

ALTER TABLE prediction_profiles
  ADD COLUMN IF NOT EXISTS experiment_status text NOT NULL DEFAULT 'active',
  ADD COLUMN IF NOT EXISTS hypothesis text;

-- Backfill: champion stays 'active', existing enabled challengers become 'testing',
-- disabled challengers become 'draft'
UPDATE prediction_profiles
SET experiment_status = CASE
  WHEN role = 'champion' THEN 'active'
  WHEN is_enabled = true THEN 'testing'
  ELSE 'draft'
END
WHERE experiment_status = 'active' AND role = 'challenger';
