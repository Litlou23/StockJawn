-- Migration 024: research_signals + research_scoring_weights
--
-- These two tables were created by hand in Supabase before migration tracking
-- existed, so they were only ever documented as comment lines in
-- 001_base_schema_reference_NOT_a_migration.sql. Backend code has been reading
-- and writing both for weeks (ResearchSignalRepository, ResearchSignalService,
-- PredictionGenerator, DynamicWatchlistService, MarketFactService).
--
-- This migration makes the schema reproducible from source control. It is
-- written to be a safe no-op if the tables already exist, and to backfill the
-- unique constraints and indexes if they were created without them.
--
-- Columns are taken from Services/Supabase/ResearchSignalRepository.cs
-- (UpsertSignalsAsync payload + MapSignal reader), not from assumption.

-- -----------------------------------------------------------------------
-- 1. research_signals — external signals (congress trades, insider, etc.)
-- -----------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS research_signals (
    id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),

    ticker           text NOT NULL,

    -- Drives the scoring bucket weight key and the learning key.
    -- Deliberately unconstrained text: adding a new IResearchSignalProvider
    -- must never require a schema change (see ADR-004).
    signal_type      text NOT NULL,

    -- Coarse grouping for scoring caps: institutional, flow, sentiment, catalyst.
    signal_category  text NOT NULL DEFAULT '',

    -- Which provider emitted this signal.
    provider         text NOT NULL DEFAULT '',

    -- Directional: positive = bullish, negative = bearish.
    strength         double precision NOT NULL DEFAULT 0,

    -- Reliability of this individual instance.
    confidence       double precision NOT NULL DEFAULT 0,

    -- When the underlying real-world event occurred.
    event_timestamp  timestamptz NOT NULL,

    -- When this system first observed it.
    detected_at      timestamptz NOT NULL DEFAULT now(),

    -- Null means the signal never expires.
    expires_at       timestamptz,

    active           boolean NOT NULL DEFAULT true,
    summary          text NOT NULL DEFAULT '',
    metadata         jsonb
);

-- UpsertSignalsAsync dedupes on (ticker, signal_type, event_timestamp).
-- PostgREST on_conflict requires a matching unique index, so without this the
-- upsert fails for every signal. Added separately from CREATE TABLE so it also
-- repairs a hand-made table that never had it.
-- Wrapped so pre-existing duplicate rows raise a clear notice instead of
-- aborting the migration and rolling back the tables and indexes above.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'research_signals_ticker_type_event_key'
    ) THEN
        BEGIN
            ALTER TABLE research_signals
                ADD CONSTRAINT research_signals_ticker_type_event_key
                UNIQUE (ticker, signal_type, event_timestamp);
        EXCEPTION WHEN unique_violation THEN
            RAISE NOTICE 'research_signals has duplicate (ticker, signal_type, event_timestamp) rows. '
                'Upserts will keep failing until they are removed. Inspect with: '
                'SELECT ticker, signal_type, event_timestamp, count(*) FROM research_signals '
                'GROUP BY 1,2,3 HAVING count(*) > 1;';
        END;
    END IF;
END $$;

-- GetActiveSignalsByTickersAsync / GetActiveSignalsForTickerAsync:
-- active=eq.true & ticker=in.(...) ordered by detected_at desc
CREATE INDEX IF NOT EXISTS idx_research_signals_active_ticker
    ON research_signals (ticker, active, detected_at DESC);

-- ExpireStaleSignalsAsync: active=eq.true & expires_at=lt.now()
CREATE INDEX IF NOT EXISTS idx_research_signals_expiry
    ON research_signals (active, expires_at)
    WHERE expires_at IS NOT NULL;

-- GetSignalsActiveAtTimeAsync: point-in-time reconstruction for backtesting
CREATE INDEX IF NOT EXISTS idx_research_signals_detected
    ON research_signals (ticker, detected_at);

-- -----------------------------------------------------------------------
-- 2. research_scoring_weights — base weight per signal type
-- -----------------------------------------------------------------------
-- Auto-seeded by ResearchSignalService.SeedNewWeightsAsync when a provider
-- emits a signal_type that has no weight row yet.

CREATE TABLE IF NOT EXISTS research_scoring_weights (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    signal_name  text NOT NULL,
    weight       double precision NOT NULL DEFAULT 1.0,
    reason       text,
    updated_at   timestamptz NOT NULL DEFAULT now()
);

-- InsertScoringWeightAsync upserts on signal_name.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'research_scoring_weights_signal_name_key'
    ) THEN
        BEGIN
            ALTER TABLE research_scoring_weights
                ADD CONSTRAINT research_scoring_weights_signal_name_key
                UNIQUE (signal_name);
        EXCEPTION WHEN unique_violation THEN
            RAISE NOTICE 'research_scoring_weights has duplicate signal_name rows. '
                'Weight seeding will keep failing until they are removed.';
        END;
    END IF;
END $$;
