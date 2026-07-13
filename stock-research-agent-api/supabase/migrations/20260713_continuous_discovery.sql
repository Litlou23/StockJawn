-- =================================================================
-- Continuous Discovery Enhancement
-- Adds: research_timeline_events, historical_research_profiles
-- Adds: historical_profile_id column to research_universe
-- =================================================================

-- ── Research Timeline Events ───────────────────────────────────
-- Immutable "Git history" for each stock's research journey.
-- Append-only — never updated or deleted.
CREATE TABLE IF NOT EXISTS research_timeline_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticker TEXT NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL DEFAULT now(),
    event_type TEXT NOT NULL DEFAULT 'EvidenceAdded',
    description TEXT NOT NULL DEFAULT '',
    source TEXT NOT NULL DEFAULT '',
    related_entity_id TEXT,
    related_entity_type TEXT,
    interest_score_snapshot INTEGER,
    research_state_snapshot TEXT,
    thesis_snapshot TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Indexes for common query patterns
CREATE INDEX IF NOT EXISTS idx_timeline_ticker_ts
    ON research_timeline_events (ticker, timestamp DESC);

CREATE INDEX IF NOT EXISTS idx_timeline_recent
    ON research_timeline_events (timestamp DESC);

CREATE INDEX IF NOT EXISTS idx_timeline_ticker_type
    ON research_timeline_events (ticker, event_type, timestamp DESC);

-- ── Historical Research Profiles ───────────────────────────────
-- One-time profile built when a stock first enters the Research Universe.
-- Never rebuilt hourly — provides persistent context for scoring.
CREATE TABLE IF NOT EXISTS historical_research_profiles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ticker TEXT NOT NULL,
    research_asset_id TEXT NOT NULL,
    built_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    -- Volatility & Price History
    historical_volatility DOUBLE PRECISION,
    atr_percent DOUBLE PRECISION,
    high_52_week DECIMAL,
    low_52_week DECIMAL,
    price_position_in_52_week_range DOUBLE PRECISION,
    -- Catalyst Reaction History
    avg_earnings_move_percent DOUBLE PRECISION,
    avg_analyst_upgrade_move_percent DOUBLE PRECISION,
    avg_sec_filing_move_percent DOUBLE PRECISION,
    -- Volume Profile
    avg_daily_volume_30d BIGINT,
    avg_daily_volume_90d BIGINT,
    -- Sector & Relative Strength
    sector TEXT,
    industry TEXT,
    relative_strength_30d DOUBLE PRECISION,
    -- Learning History
    previous_prediction_count INTEGER NOT NULL DEFAULT 0,
    previous_prediction_accuracy DOUBLE PRECISION,
    avg_previous_confidence DOUBLE PRECISION,
    -- Pattern Summary
    pattern_summary TEXT,
    last_updated TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Unique constraint: one profile per ticker
CREATE UNIQUE INDEX IF NOT EXISTS idx_historical_profile_ticker
    ON historical_research_profiles (ticker);

CREATE INDEX IF NOT EXISTS idx_historical_profile_asset
    ON historical_research_profiles (research_asset_id);

-- ── Add historical_profile_id to research_universe ───────────────
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'research_universe'
        AND column_name = 'historical_profile_id'
    ) THEN
        ALTER TABLE research_universe
            ADD COLUMN historical_profile_id UUID;
    END IF;
END $$;

-- ── RLS policies (match existing pattern) ──────────────────────
ALTER TABLE research_timeline_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE historical_research_profiles ENABLE ROW LEVEL SECURITY;

-- Service role has full access (matches other tables)
CREATE POLICY "service_role_timeline" ON research_timeline_events
    FOR ALL USING (auth.role() = 'service_role');

CREATE POLICY "service_role_profiles" ON historical_research_profiles
    FOR ALL USING (auth.role() = 'service_role');

-- ── Schedule: continuous discovery every 60 minutes during market hours ──
-- Uses pg_cron → Edge Function pattern (same as other jobs).
-- Uncomment and adjust after creating the Edge Function:
--
-- SELECT cron.schedule(
--     'continuous-discovery-hourly',
--     '0 14,15,16,17,18,19,20 * * 1-5',  -- Every hour 9AM-4PM ET (UTC offset)
--     $$SELECT net.http_post(
--         url := current_setting('app.settings.edge_function_url') || '/continuous-discovery',
--         headers := jsonb_build_object(
--             'Content-Type', 'application/json',
--             'Authorization', 'Bearer ' || current_setting('app.settings.service_key')
--         ),
--         body := '{"trigger":"pg_cron"}'
--     )$$
-- );
