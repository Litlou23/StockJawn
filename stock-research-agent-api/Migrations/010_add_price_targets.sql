-- Migration 010: ATR-based price prediction engine
--
-- Adds volatility-grounded price forecasting to predictions.
-- ATR measures how much a stock typically moves; direction still comes
-- from the signal engine. The result is a projected price zone, not a
-- point prediction.
--
-- prediction_candidates: ATR metrics, projected price zone, target/stop/invalidation, R:R
-- prediction_outcomes: price accuracy tracking against the projected zone

-- prediction_candidates
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS atr14 double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS atr_percent double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS timeframe_multiplier double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS signal_modifier double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS expected_move_dollar double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS expected_move_percent double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS predicted_price double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS projected_price_low double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS projected_price_high double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS target_price double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS stop_price double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS invalidation_price double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS support_level double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS resistance_level double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS risk_reward_ratio double precision;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS price_prediction_method text;
ALTER TABLE prediction_candidates ADD COLUMN IF NOT EXISTS price_prediction_warnings text[];

-- prediction_outcomes
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS predicted_price double precision;
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS projected_price_low double precision;
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS projected_price_high double precision;
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS price_prediction_error_percent double precision;
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS was_in_projected_zone boolean;
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS target_hit boolean;
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS stop_hit boolean;
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS invalidation_hit boolean;
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS max_favorable_percent double precision;
ALTER TABLE prediction_outcomes ADD COLUMN IF NOT EXISTS max_adverse_percent double precision;
