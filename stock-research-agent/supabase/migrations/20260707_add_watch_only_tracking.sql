-- Track the raw model's directional lean even when the final recommendation
-- is watch_only. Also track WHY it was downgraded so the learning engine
-- can evaluate each guardrail independently.

-- 1. Add columns to prediction_candidates
ALTER TABLE prediction_candidates
  ADD COLUMN IF NOT EXISTS winning_direction text,
  ADD COLUMN IF NOT EXISTS bullish_score double precision,
  ADD COLUMN IF NOT EXISTS bearish_score double precision,
  ADD COLUMN IF NOT EXISTS downgrade_reasons text[];

-- 2. Add abstention evaluation columns to prediction_outcomes
ALTER TABLE prediction_outcomes
  ADD COLUMN IF NOT EXISTS abstention_correct boolean,
  ADD COLUMN IF NOT EXISTS missed_alpha_percent double precision,
  ADD COLUMN IF NOT EXISTS guardrail_justified boolean,
  ADD COLUMN IF NOT EXISTS original_direction text,
  ADD COLUMN IF NOT EXISTS downgrade_reasons_evaluated text[];

-- 3. Update the insert_prediction_candidates RPC to include new columns
CREATE OR REPLACE FUNCTION public.insert_prediction_candidates(payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
  result jsonb := '[]'::jsonb;
  row_result jsonb;
  item jsonb;
  new_id uuid;
BEGIN
  FOR item IN SELECT * FROM jsonb_array_elements(payload)
  LOOP
    INSERT INTO prediction_candidates (
      run_id, ticker, prediction_type, asset_type, time_window,
      confidence_score, importance_score, risk_score, entry_reference_price,
      atr14, atr_percent, timeframe_multiplier, signal_modifier,
      expected_move_dollar, expected_move_percent, predicted_price, predicted_move_percent,
      projected_price_low, projected_price_high, target_price, stop_price,
      invalidation_price, support_level, resistance_level, risk_reward_ratio,
      price_prediction_method, price_prediction_warnings,
      score_debug_json, actionability_score, actionability_tier,
      bullish_case, bearish_case, prediction_reason, invalidation_rule,
      data_sources_used, missing_data_warnings, status,
      winning_direction, bullish_score, bearish_score, downgrade_reasons
    ) VALUES (
      (item->>'run_id')::uuid,
      item->>'ticker',
      item->>'prediction_type',
      COALESCE(item->>'asset_type', 'stock'),
      item->>'time_window',
      COALESCE((item->>'confidence_score')::int, 0),
      COALESCE((item->>'importance_score')::int, 0),
      COALESCE((item->>'risk_score')::int, 0),
      (item->>'entry_reference_price')::numeric,
      (item->>'atr14')::double precision,
      (item->>'atr_percent')::double precision,
      (item->>'timeframe_multiplier')::double precision,
      (item->>'signal_modifier')::double precision,
      (item->>'expected_move_dollar')::double precision,
      (item->>'expected_move_percent')::double precision,
      (item->>'predicted_price')::double precision,
      (item->>'predicted_move_percent')::double precision,
      (item->>'projected_price_low')::double precision,
      (item->>'projected_price_high')::double precision,
      (item->>'target_price')::double precision,
      (item->>'stop_price')::double precision,
      (item->>'invalidation_price')::double precision,
      (item->>'support_level')::double precision,
      (item->>'resistance_level')::double precision,
      (item->>'risk_reward_ratio')::double precision,
      item->>'price_prediction_method',
      COALESCE(ARRAY(SELECT jsonb_array_elements_text(item->'price_prediction_warnings')), '{}'::text[]),
      item->>'score_debug_json',
      (item->>'actionability_score')::int,
      item->>'actionability_tier',
      COALESCE(item->>'bullish_case', ''),
      COALESCE(item->>'bearish_case', ''),
      COALESCE(item->>'prediction_reason', ''),
      COALESCE(item->>'invalidation_rule', ''),
      COALESCE(ARRAY(SELECT jsonb_array_elements_text(item->'data_sources_used')), '{}'::text[]),
      COALESCE(ARRAY(SELECT jsonb_array_elements_text(item->'missing_data_warnings')), '{}'::text[]),
      COALESCE(item->>'status', 'open'),
      item->>'winning_direction',
      (item->>'bullish_score')::double precision,
      (item->>'bearish_score')::double precision,
      COALESCE(ARRAY(SELECT jsonb_array_elements_text(item->'downgrade_reasons')), '{}'::text[])
    ) RETURNING id INTO new_id;

    result := result || jsonb_build_array(jsonb_build_object('id', new_id));
  END LOOP;

  RETURN result;
END;
$$;
