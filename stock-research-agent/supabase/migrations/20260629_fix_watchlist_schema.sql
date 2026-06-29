-- Fix watchlist schema: add missing columns and tables
-- Safe to run multiple times (all statements use IF NOT EXISTS / IF EXISTS checks)

-- ============================================================
-- 1. Add missing columns to watchlist_items
-- ============================================================

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'updated_at') THEN
    ALTER TABLE watchlist_items ADD COLUMN updated_at timestamptz DEFAULT now();
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'total_score') THEN
    ALTER TABLE watchlist_items ADD COLUMN total_score numeric;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'added_at') THEN
    ALTER TABLE watchlist_items ADD COLUMN added_at timestamptz DEFAULT now();
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'catalyst_score') THEN
    ALTER TABLE watchlist_items ADD COLUMN catalyst_score numeric;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'risk_score') THEN
    ALTER TABLE watchlist_items ADD COLUMN risk_score numeric;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'options_readiness_score') THEN
    ALTER TABLE watchlist_items ADD COLUMN options_readiness_score numeric;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'company_name') THEN
    ALTER TABLE watchlist_items ADD COLUMN company_name text;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'category') THEN
    ALTER TABLE watchlist_items ADD COLUMN category text NOT NULL DEFAULT 'general';
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'watch_reason') THEN
    ALTER TABLE watchlist_items ADD COLUMN watch_reason text;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'thesis_summary') THEN
    ALTER TABLE watchlist_items ADD COLUMN thesis_summary text;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'bullish_case') THEN
    ALTER TABLE watchlist_items ADD COLUMN bullish_case text;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'bearish_case') THEN
    ALTER TABLE watchlist_items ADD COLUMN bearish_case text;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'data_confidence') THEN
    ALTER TABLE watchlist_items ADD COLUMN data_confidence text;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'last_reviewed_at') THEN
    ALTER TABLE watchlist_items ADD COLUMN last_reviewed_at timestamptz;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'review_by_date') THEN
    ALTER TABLE watchlist_items ADD COLUMN review_by_date date;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'invalidation_point') THEN
    ALTER TABLE watchlist_items ADD COLUMN invalidation_point text;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'exit_or_removal_conditions') THEN
    ALTER TABLE watchlist_items ADD COLUMN exit_or_removal_conditions jsonb;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'swap_reason') THEN
    ALTER TABLE watchlist_items ADD COLUMN swap_reason text;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'sources_used') THEN
    ALTER TABLE watchlist_items ADD COLUMN sources_used jsonb;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'missing_data_warnings') THEN
    ALTER TABLE watchlist_items ADD COLUMN missing_data_warnings jsonb;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'raw_context') THEN
    ALTER TABLE watchlist_items ADD COLUMN raw_context jsonb;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'watchlist_items' AND column_name = 'archived_at') THEN
    ALTER TABLE watchlist_items ADD COLUMN archived_at timestamptz;
  END IF;
END $$;

-- Indexes (safe with IF NOT EXISTS)
CREATE INDEX IF NOT EXISTS idx_watchlist_items_status ON watchlist_items(status);
CREATE INDEX IF NOT EXISTS idx_watchlist_items_ticker ON watchlist_items(ticker);
CREATE INDEX IF NOT EXISTS idx_watchlist_items_created_at ON watchlist_items(created_at);

-- updated_at trigger
CREATE OR REPLACE FUNCTION update_watchlist_items_updated_at()
RETURNS trigger AS $$
BEGIN
  new.updated_at = now();
  RETURN new;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_watchlist_items_updated_at ON watchlist_items;
CREATE TRIGGER trg_watchlist_items_updated_at
  BEFORE UPDATE ON watchlist_items
  FOR EACH ROW EXECUTE FUNCTION update_watchlist_items_updated_at();

-- ============================================================
-- 2. watchlist_change_log
-- ============================================================

CREATE TABLE IF NOT EXISTS watchlist_change_log (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid,
  watchlist_item_id uuid,
  ticker text NOT NULL,
  change_type text NOT NULL,
  previous_status text,
  new_status text,
  previous_score numeric,
  new_score numeric,
  reason text,
  metadata jsonb,
  created_at timestamptz DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_watchlist_change_log_ticker ON watchlist_change_log(ticker);
CREATE INDEX IF NOT EXISTS idx_watchlist_change_log_created_at ON watchlist_change_log(created_at);

ALTER TABLE watchlist_change_log ENABLE ROW LEVEL SECURITY;

-- Service role bypass
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'watchlist_change_log' AND policyname = 'Service role full access on watchlist_change_log') THEN
    CREATE POLICY "Service role full access on watchlist_change_log"
      ON watchlist_change_log FOR ALL
      USING (auth.role() = 'service_role');
  END IF;
END $$;

-- ============================================================
-- 3. watchlist_candidates
-- ============================================================

CREATE TABLE IF NOT EXISTS watchlist_candidates (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid,
  ticker text NOT NULL,
  company_name text,
  source text NOT NULL,
  category text,
  candidate_score numeric,
  catalyst_score numeric,
  risk_score numeric,
  options_readiness_score numeric,
  data_confidence text,
  reason text,
  selected_for_watchlist boolean DEFAULT false,
  raw_context jsonb,
  created_at timestamptz DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_watchlist_candidates_ticker ON watchlist_candidates(ticker);
CREATE INDEX IF NOT EXISTS idx_watchlist_candidates_created_at ON watchlist_candidates(created_at);

ALTER TABLE watchlist_candidates ENABLE ROW LEVEL SECURITY;

-- Service role bypass
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'watchlist_candidates' AND policyname = 'Service role full access on watchlist_candidates') THEN
    CREATE POLICY "Service role full access on watchlist_candidates"
      ON watchlist_candidates FOR ALL
      USING (auth.role() = 'service_role');
  END IF;
END $$;
