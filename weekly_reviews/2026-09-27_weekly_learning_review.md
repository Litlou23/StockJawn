# StockJawn Weekly Learning Review — 2026-09-27

## This Week (9/21–9/25)
- Evaluated picks: **1** (NVDA bullish 9/22, +0.13%, scratch). Record: **0W-0L-1S**.
- 3 CASH days (9/21 no scan ran, 9/23 sat out, 9/25 account had $0 equity / $0 buying power).
- 9/24: NVDA (approved) and PLTR (expired) picks, both still unevaluated, and both have **wrong prices**:
  - NVDA entry $131.50 / target $138 / stop $128. Actual close 9/24: **$224.58**.
  - PLTR entry $73.50 / target $77 / stop $71.50. Actual close 9/24: **$192.59**.
- No trades executed. P&L: $0.

## All-Time (claude_daily_picks, evaluated, non-CASH)
- 12 picks: **3W-2L-7S**. Win rate 60% of decided picks, but only 5 of 12 were decided.
- 6 of the 7 scratches are "auto-closed 8+ days without evaluation" with null entry price, so the data is missing, not flat.
- By week: 9/14 week 3W-2L-6S. 9/21 week 0W-0L-1S. Nothing to call a trend yet.
- Only 1 of 12 picks has a total_score. Target/stop set on only 2 of 12. No usable score or calibration analysis.
- Old system (prediction_candidates, 8/12–9/18): ~1,700 rows. It has no `actual_outcome` column, so the template query fails. Outcome data lives elsewhere; see scoring_weight_overrides reasons (bearish 53.5% vs bullish ~48%, n≈481).

## Top 3 Findings
1. **Data integrity is the main problem, not strategy.**
   - The 9/24 NVDA/PLTR entry prices are off by 40–60% from real prices. These look stale or made up.
   - ABNB and AXP rows disagree with their own notes. ABNB is stored as -1.45% but the note says -4.2%. AXP is stored as -0.69% but the note says -2.7%.
   - DHI is stored as -1.63% but the note says +0.15%.
   - FTNT is marked a loss with direction_correct = true (+1.31%).
2. **The pipeline isn't running reliably.**
   - No scan ran on 9/21.
   - The 9/24 run ran late (~3:35 PM ET).
   - The PLTR approval expired.
   - 6 picks were never evaluated.
   - The account shows $0 buying power (9/25).
3. **Early signal (very low confidence, n=12):**
   - Bearish Fed-hike picks on 9/17 went 2W-1L. Bearish shows 66.7% vs bullish 50%, consistent with the older 53.5% vs 48% data.
   - The only big win was catalyst-driven: INTC +16.3% on M&A news.
   - The 5 real results were 3W-2L: high conviction 2W-0L, medium 1W-2L.

## Changes Made
- Upserted the `weekly_review_insights` marker row into claude_factor_performance (2026-09-27).
- **No weight changes.** 12 picks (5 decided), with factor_performance at n=1 per factor, is far too small to justify retuning. Changing weights now would only fit noise.

## Recommendations (need human judgment)
1. **Price-sanity gate in premarket-picks:** reject any pick whose entry_price differs from the live quote by more than 2%. The existing `max_entry_slippage_pct=1.5` should have caught this, so check whether the pick path actually applies it. Fix or void the 9/24 NVDA/PLTR rows before EOD eval scores them.
2. **Fix the EOD evaluator:**
   - Write price_change_pct from the same prices used in notes.
   - Don't mark a trade a loss when direction_correct = true, unless the stop was hit (and then log that).
   - Require entry_price when a pick is saved so there are no more null-entry auto-closes.
3. **Fund the account or pause picks.** 697458438 showed $0 equity on 9/25. No option trade is possible until it's funded.
4. **Always save total_score, target and stop with every pick.** Without them, score-bracket and calibration analysis can't be done.
5. **Security:** `approval_pin` is stored in plain text in scoring_weight_overrides. Move it to a secret or hashed store.
6. **Behavioral (tentative):**
   - Keep favoring high-conviction, catalyst-driven picks (M&A, earnings, congress buys).
   - Limit picks to 2–3 per day. On 9/17 there were 10 picks, 6 of which were never tracked.

## Next Week Focus
- Log clean, complete, trackable data: correct entry, score, target and stop on every pick, and an evaluation within 1–3 days.
- Make sure the premarket scan runs on time every weekday.
- Re-run this review once there are at least 30 decided picks before changing any weights.
