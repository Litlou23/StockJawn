-- Migration 020: Signal Analytics Tables
-- Adds contribution_percent and actual_return_percent to observations,
-- and creates four new analytics tables for the layered learning system.

-- 1. Add new columns to prediction_signal_observations
ALTER TABLE prediction_signal_observations
  ADD COLUMN IF NOT EXISTS contribution_percent double precision,
  ADD COLUMN IF NOT EXISTS actual_return_percent double precision;

-- 2. Signal Calibration Buckets
--    Tracks accuracy and avg return by signal strength ranges (0-5, 6-10, etc.)
CREATE TABLE IF NOT EXISTS signal_calibration_buckets (
  id uuid DEFAULT gen_random_uuid() PRIMARY KEY,
  signal_name text NOT NULL,
  direction text NOT NULL DEFAULT 'all',
  score_bucket text NOT NULL,            -- '0-5', '6-10', '11-15', '16-20', '21-25'
  sample_count int NOT NULL DEFAULT 0,
  correct_count int NOT NULL DEFAULT 0,
  accuracy double precision NOT NULL DEFAULT 0,
  avg_return_percent double precision NOT NULL DEFAULT 0,
  avg_outcome_score double precision NOT NULL DEFAULT 0,
  last_updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (signal_name, direction, score_bucket)
);

-- 3. Signal Correlations
--    Stores Pearson r between each signal's net score and actual return
CREATE TABLE IF NOT EXISTS signal_correlations (
  id uuid DEFAULT gen_random_uuid() PRIMARY KEY,
  signal_name text NOT NULL,
  direction text NOT NULL DEFAULT 'all',
  correlation_r double precision NOT NULL DEFAULT 0,
  sample_count int NOT NULL DEFAULT 0,
  p_value double precision,
  avg_net_score double precision NOT NULL DEFAULT 0,
  avg_return_percent double precision NOT NULL DEFAULT 0,
  last_updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (signal_name, direction)
);

-- 4. Signal Influence (Counterfactual)
--    How often was each signal decisive vs redundant?
CREATE TABLE IF NOT EXISTS signal_influence (
  id uuid DEFAULT gen_random_uuid() PRIMARY KEY,
  signal_name text NOT NULL,
  direction text NOT NULL DEFAULT 'all',
  total_predictions int NOT NULL DEFAULT 0,
  decisive_count int NOT NULL DEFAULT 0,       -- removing signal flips prediction
  reinforcing_count int NOT NULL DEFAULT 0,    -- removing signal weakens but doesn't flip
  redundant_count int NOT NULL DEFAULT 0,      -- removing signal barely changes outcome
  avg_margin_impact double precision NOT NULL DEFAULT 0,  -- avg change in bull-bear margin
  decisive_accuracy double precision,          -- accuracy when this signal was decisive
  last_updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (signal_name, direction)
);

-- 5. Signal Interactions
--    Pairwise signal combination performance
CREATE TABLE IF NOT EXISTS signal_interactions (
  id uuid DEFAULT gen_random_uuid() PRIMARY KEY,
  signal_a text NOT NULL,
  signal_b text NOT NULL,
  direction text NOT NULL DEFAULT 'all',
  both_strong_count int NOT NULL DEFAULT 0,     -- both signals > threshold
  both_strong_accuracy double precision NOT NULL DEFAULT 0,
  both_strong_avg_return double precision NOT NULL DEFAULT 0,
  a_strong_b_weak_count int NOT NULL DEFAULT 0,
  a_strong_b_weak_accuracy double precision NOT NULL DEFAULT 0,
  a_weak_b_strong_count int NOT NULL DEFAULT 0,
  a_weak_b_strong_accuracy double precision NOT NULL DEFAULT 0,
  synergy_score double precision NOT NULL DEFAULT 0,  -- how much better both-strong is vs individual
  last_updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (signal_a, signal_b, direction)
);
