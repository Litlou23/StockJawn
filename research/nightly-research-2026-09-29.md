# Nightly Research — for trading day Tue 2026-09-29

Run time: 2026-09-29 ~01:35 ET (Monday-night run fired late, so target session is Tue 9/29, not 9/30).

**DB insert FAILED** — `claude_daily_picks_approval_status_check` only allows
pending / approved / executing / executed / expired / failed / skipped / ready. `'research'` is rejected.
Rows were NOT written under another status to avoid them being treated as real picks.
Fix: add `'research'` to the check constraint, then these rows can be inserted.

## Account
- Agentic buying power: $200 → max per trade $80, min stock price $4

## Candidates (preliminary, needs morning validation)

| Ticker | Score | Signals | Catalyst (dated) | Notes |
|---|---|---|---|---|
| AIR | 62 (medium) | PEAD beat+raise, M&A (acquirer) | 9/28 AMC: adj EPS $1.49 vs $1.29, sales +24% to $918M; FY27 sales growth guide raised to "low teens" (from low double-digits–low teens); deal to acquire controlling interest in MRO Holdings | 6B/2H/0S, mean PT $149 vs $115.09 close; AH ~$118 (+2.6%). Price > $80 budget → fractional only. Call 9/29 7am CT. |
| QTTB | 48 (low) | Analyst initiation | Jefferies initiated Buy, PT $29, on 9/28 | 7B/0H/0S, mean PT $39.33 vs $9.23 close; AH ~$9.60 (+4%). Day-2 of initiation; small-cap biotech. |

Reviewed and skipped (<40 or no qualifying catalyst): CBRL (9/23 beat but new FY27 outlook not a raise; 2B/4H/4S; already above mean PT), MLKN (beat but modest guide), JEF (beat, AH flat/down), KBH/WOR/BB (beats, no raise found), 9/28 small-cap initiations (MBAI, ROC, TRT — minor banks; MBGL — JPM Neutral).

## Pending events Tue 9/29 (premarket, not yet reported)
- CCL, KMX, UEC report before the open — morning task should check results. CCL has a fuel-cost headwind with oil rising.

## Other scans
- Congress: no trades found for AIR, CBRL, QTTB. Pelosi's latest disclosed buys (BE, INTC — July) are stale.
- Index: last S&P 500 changes (BE, ILMN, Everpure) took effect 9/21; no pending additions found.
- FDA / M&A: nothing specific dated 9/28–9/29 found beyond AIR/MRO Holdings. KOD +155% on 9/28 (Phase 3) — already moved.

## Macro Tue 9/29
- No FOMC. JOLTS (est 7.225M) + Conference Board Consumer Confidence (est 90.0) at 10:00 ET; Case-Shiller/FHFA.
- Market fell ~0.8% on 9/28 on US–Iran standoff pushing oil higher, with rate-hike worries → headwind for airlines/cruise lines, tailwind for energy.
