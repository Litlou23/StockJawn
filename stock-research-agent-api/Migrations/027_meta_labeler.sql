-- Migration 027: Meta-labeler tables (Level 4 scoring improvement — Aug 2026)
--
-- Meta-labeling (López de Prado): a secondary ML model decides whether to
-- ACT on the primary scoring engine's prediction. Trained on historical
-- primary predictions + actual outcomes, using the triple-barrier method:
-- label = 1 if take-profit hit before stop-loss + time barrier, else 0.
--
-- Two tables:
--   meta_labeler_training_data  — one row per historical prediction with
--                                 label + features materialized for training
--   meta_labeler_models         — one row per trained model version with
--                                 metrics + path to the .zip ML.NET artifact
--
-- Idempotent — safe to re-run.

-- ============================================================================
-- 1. meta_labeler_training_data
--    Materialized training rows. Written by TripleBarrierLabeler after joining
--    predictions + prediction_outcomes. features_json is the fixed-length
--    feature vector (order defined by MetaLabelerFeatureExtractor).
--    label = 1 (TP hit first) / 0 (SL or time barrier hit first).
-- ============================================================================

create table if not exists meta_labeler_training_data (
    id              uuid primary key default gen_random_uuid(),
    prediction_id   uuid not null unique,
    profile_id      uuid,
    ticker          text not null,
    prediction_type text not null,          -- bullish | bearish | neutral
    winning_direction text,                 -- from ScoringBreakdown
    label           smallint not null       -- 0 or 1
                      check (label in (0, 1)),
    features_json   jsonb not null,         -- fixed-length feature vector
    outcome_pnl_percent double precision,   -- for regression checks
    time_to_barrier_days integer,           -- how many days to hit whichever barrier
    barrier_hit     text,                   -- take_profit | stop_loss | time
    prediction_created_at timestamptz not null,
    outcome_evaluated_at timestamptz not null,
    labeled_at      timestamptz not null default now()
);

create index if not exists idx_meta_labeler_training_data_prediction
    on meta_labeler_training_data(prediction_id);
create index if not exists idx_meta_labeler_training_data_profile
    on meta_labeler_training_data(profile_id) where profile_id is not null;
create index if not exists idx_meta_labeler_training_data_created
    on meta_labeler_training_data(prediction_created_at desc);
create index if not exists idx_meta_labeler_training_data_label
    on meta_labeler_training_data(label);

-- ============================================================================
-- 2. meta_labeler_models
--    Versioned trained model artifacts. artifact_path points to the ML.NET
--    .zip file on disk (or blob storage). Metrics captured at training time
--    for comparing versions.
--
--    is_active = true on exactly one row (partial unique index enforces this)
--    Loaded at MetaLabelerService startup.
-- ============================================================================

create table if not exists meta_labeler_models (
    id                    uuid primary key default gen_random_uuid(),
    version               integer not null,     -- monotonically increasing
    trained_at            timestamptz not null default now(),
    training_row_count    integer not null,
    positive_label_count  integer not null,     -- how many wins in training
    negative_label_count  integer not null,     -- how many losses in training

    -- Test-set metrics (held-out 20% of training data)
    test_row_count        integer,
    test_accuracy         double precision,     -- 0.0–1.0
    test_auc              double precision,     -- 0.5 = random
    test_f1               double precision,
    test_precision_at_50  double precision,     -- precision at 0.5 threshold
    test_recall_at_50     double precision,

    -- Feature-level info
    feature_count         integer not null,
    feature_names_json    jsonb not null,       -- ordered list of feature names
    top_features_json     jsonb,                -- top 10 by importance

    -- Model artifact
    artifact_path         text not null,        -- e.g. /models/meta_labeler_v3.zip
    artifact_size_bytes   bigint,

    -- Training config
    trainer               text not null default 'FastTree',
    hyperparameters_json  jsonb,

    -- Lifecycle
    is_active             boolean not null default false,
    notes                 text,
    created_at            timestamptz not null default now()
);

create unique index if not exists idx_meta_labeler_models_version
    on meta_labeler_models(version);
create unique index if not exists idx_meta_labeler_models_active
    on meta_labeler_models(is_active) where is_active = true;
create index if not exists idx_meta_labeler_models_trained_at
    on meta_labeler_models(trained_at desc);

-- ============================================================================
-- 3. paper_stock_candidates.meta_probability
--    Advisory column for logging the meta-labeler's probability alongside
--    each candidate. Not enforced yet — just observed until we're confident
--    the model is calibrated. Nullable so existing rows are unaffected.
-- ============================================================================

alter table paper_stock_candidates
    add column if not exists meta_probability double precision,
    add column if not exists meta_model_version integer;

create index if not exists idx_paper_stock_candidates_meta_prob
    on paper_stock_candidates(meta_probability)
    where meta_probability is not null;
