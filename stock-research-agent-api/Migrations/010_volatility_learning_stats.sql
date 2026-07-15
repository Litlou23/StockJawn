-- Phase 4: Volatility learning stats
-- One row per evaluated prediction, bridging VOE assessment → outcome → learning.

CREATE TABLE IF NOT EXISTS volatility_learning_stats (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    prediction_id           text NOT NULL,
    run_id                  text NOT NULL,
    ticker                  text NOT NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),

    -- Opportunity context (snapshot from VOE assessment at prediction time)
    opportunity_type        text,
    opportunity_score       double precision,
    stock_volatility_regime text,
    atr_percentile          double precision,
    atr_acceleration        double precision,
    bandwidth_percentile    double precision,
    gap_type                text,
    gap_percent             double precision,
    catalyst_age_hours      double precision,

    -- Prediction context
    confidence              integer,
    risk                    integer,
    prediction_type         text,
    time_window             text,

    -- Movement outcome
    direction_correct       boolean,
    outcome_score           double precision,
    holding_period_hours    double precision,
    max_favorable_excursion double precision,
    max_adverse_excursion   double precision,

    -- Time-to-move (in trading days / bars)
    time_to_3pct            integer,
    time_to_5pct            integer,
    time_to_target          integer,

    -- Recovery metrics
    recovery_speed          double precision,
    bounce_quality_realized text,

    -- Opportunity success
    opportunity_success     boolean,
    opportunity_success_reason text
);

CREATE INDEX IF NOT EXISTS idx_vol_learning_prediction ON volatility_learning_stats (prediction_id);
CREATE INDEX IF NOT EXISTS idx_vol_learning_ticker ON volatility_learning_stats (ticker);
CREATE INDEX IF NOT EXISTS idx_vol_learning_opportunity ON volatility_learning_stats (opportunity_type);
CREATE INDEX IF NOT EXISTS idx_vol_learning_created ON volatility_learning_stats (created_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS idx_vol_learning_prediction_unique ON volatility_learning_stats (prediction_id);
