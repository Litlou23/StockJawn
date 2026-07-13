-- =================================================================
-- Sync: apply migration objects missing from the live database
-- Adds: theoretical_option_simulations table (from 20260629 migration, never applied)
-- Adds: portfolio_decision_log view (created via dashboard, now tracked)
-- =================================================================

-- ── Theoretical Option Simulations ───────────────────────────────
-- All data here is THEORETICAL SIMULATION ONLY — not real option quotes.
CREATE TABLE IF NOT EXISTS theoretical_option_simulations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prediction_id UUID REFERENCES prediction_candidates(id) ON DELETE SET NULL,
    ticker TEXT NOT NULL,
    strategy_type TEXT NOT NULL CHECK (strategy_type IN (
        'long_call_proxy', 'long_put_proxy',
        'bull_call_spread_proxy', 'bear_put_spread_proxy',
        'iron_condor_proxy'
    )),
    starting_stock_price DOUBLE PRECISION NOT NULL,
    ending_stock_price DOUBLE PRECISION NOT NULL,
    stock_move_percent DOUBLE PRECISION NOT NULL,
    assumptions_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    estimated_payoff DOUBLE PRECISION NOT NULL,
    estimated_return_percent DOUBLE PRECISION NOT NULL,
    max_profit DOUBLE PRECISION NOT NULL,
    max_loss DOUBLE PRECISION NOT NULL,
    breakevens_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    direction_matched_prediction BOOLEAN,
    warnings_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_theo_sims_prediction_id
    ON theoretical_option_simulations(prediction_id);

CREATE INDEX IF NOT EXISTS idx_theo_sims_ticker
    ON theoretical_option_simulations(ticker);

ALTER TABLE theoretical_option_simulations ENABLE ROW LEVEL SECURITY;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_policies
        WHERE tablename = 'theoretical_option_simulations'
        AND policyname = 'service_role_theo_sims'
    ) THEN
        CREATE POLICY "service_role_theo_sims" ON theoretical_option_simulations
            FOR ALL USING (auth.role() = 'service_role');
    END IF;
END $$;

-- ── Portfolio Decision Log (view) ────────────────────────────────
-- Joins portfolio_positions with paper_stock_candidates and
-- prediction_outcomes for a unified decision audit trail.
-- Was previously created via the Supabase dashboard — now tracked in migrations.
CREATE OR REPLACE VIEW portfolio_decision_log AS
SELECT
    pp.id AS position_id,
    pp.portfolio_id,
    pp.ticker,
    pp.asset_type,
    pp.status AS position_status,
    pp.entry_date,
    pp.exit_date,
    pp.entry_price,
    pp.exit_price,
    pp.quantity,
    pp.dollars_invested,
    pp.dollars_returned,
    pp.profit_loss,
    pp.percent_gain,
    pp.reason_entered,
    pp.reason_exited,
    pp.prediction_id,
    psc.prediction_type,
    psc.timeframe,
    psc.confidence_score,
    psc.risk_score,
    psc.candidate_mode,
    psc.quality_tier,
    psc.is_actionable,
    psc.total_score,
    psc.catalyst_type,
    psc.selection_reason,
    psc.inclusion_reason,
    psc.bullish_score,
    psc.bearish_score,
    psc.winning_direction,
    psc.target_price,
    psc.stop_price,
    psc.status AS candidate_status,
    po.direction_correct,
    po.percent_move,
    po.outcome_score,
    po.outcome_summary,
    po.lesson,
    po.target_hit,
    po.stop_hit,
    po.invalidation_hit,
    po.max_favorable_percent,
    po.max_adverse_percent,
    pp.created_at
FROM portfolio_positions pp
LEFT JOIN paper_stock_candidates psc ON psc.prediction_id = pp.prediction_id::uuid
LEFT JOIN prediction_outcomes po ON po.prediction_id = pp.prediction_id::uuid
ORDER BY pp.created_at DESC;
