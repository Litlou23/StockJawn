-- Migration 012: Actionability tier + score on prediction_candidates
--
-- Adds a fields to record what tier of action a prediction warrants,
-- separate from confidence and prediction_type. Confidence bands map to a
-- base tier; guardrails (poor R/R, market conflict, low data quality) can
-- downgrade further. See ScoringEngine.ComputeActionability.
--
-- Tiers: scan | watch_only | actionable | strong | strongest

alter table prediction_candidates
    add column if not exists actionability_score integer;

alter table prediction_candidates
    add column if not exists actionability_tier text
        check (actionability_tier is null or actionability_tier in
               ('scan','watch_only','actionable','strong','strongest'));

create index if not exists idx_prediction_candidates_actionability_tier
    on prediction_candidates(actionability_tier);
