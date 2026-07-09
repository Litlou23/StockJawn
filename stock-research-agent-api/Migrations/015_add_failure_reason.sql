-- Migration 015: Add failure_reason column to paper_stock_outcomes
-- Stores which signal buckets were culprits when a prediction is wrong

ALTER TABLE paper_stock_outcomes
ADD COLUMN IF NOT EXISTS failure_reason TEXT;
