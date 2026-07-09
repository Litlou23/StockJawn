-- Migration 017: Fix learning_insights insight_type CHECK constraint
-- The code writes 'pattern_detection', 'direction_asymmetry', 'setup',
-- and 'setup_degradation' but the CHECK only allowed 'ticker', 'signal',
-- 'market_condition', 'risk_rule', 'prompt_rule'. This caused silent
-- insert failures for those insight types.

ALTER TABLE learning_insights
DROP CONSTRAINT IF EXISTS learning_insights_insight_type_check;

ALTER TABLE learning_insights
ADD CONSTRAINT learning_insights_insight_type_check
CHECK (insight_type IN ('ticker', 'signal', 'market_condition', 'risk_rule',
                        'prompt_rule', 'pattern_detection', 'direction_asymmetry',
                        'setup', 'setup_degradation'));
