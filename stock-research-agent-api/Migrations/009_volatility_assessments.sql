-- Phase 3: Volatility Opportunity Engine persistence
-- Stores one assessment per ticker per Morning Scan run.

CREATE TABLE IF NOT EXISTS volatility_assessments (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id          text NOT NULL,
    ticker          text NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),

    -- Volatility context
    atr_percentile          double precision,
    atr_acceleration        double precision,
    bandwidth_percentile    double precision,
    bandwidth_direction     double precision,
    stock_volatility_regime text,

    -- Gap context
    gap_percent     double precision,
    gap_direction   text,
    gap_type        text,
    gap_with_volume boolean NOT NULL DEFAULT false,

    -- Support / Resistance
    distance_from_support    double precision,
    distance_from_resistance double precision,

    -- Volume
    volume_ratio_persistence double precision,

    -- Catalyst
    catalyst_age_hours double precision,

    -- Classification
    opportunity_type        text NOT NULL DEFAULT 'None',
    opportunity_score       double precision NOT NULL DEFAULT 0,
    volatility_risk_modifier double precision NOT NULL DEFAULT 0,

    -- Metadata
    features_skipped text[] DEFAULT '{}',
    bars_used_for_history integer NOT NULL DEFAULT 0
);

-- Indexes for learning queries
CREATE INDEX IF NOT EXISTS idx_vol_assessments_ticker ON volatility_assessments (ticker);
CREATE INDEX IF NOT EXISTS idx_vol_assessments_run_id ON volatility_assessments (run_id);
CREATE INDEX IF NOT EXISTS idx_vol_assessments_created_at ON volatility_assessments (created_at DESC);
CREATE INDEX IF NOT EXISTS idx_vol_assessments_opportunity ON volatility_assessments (opportunity_type);
CREATE UNIQUE INDEX IF NOT EXISTS idx_vol_assessments_ticker_run ON volatility_assessments (ticker, run_id);
