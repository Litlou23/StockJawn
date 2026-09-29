# Nightly Research — for trading day Mon 2026-09-28

Run: Sun 2026-09-27 ~9pm ET. RESEARCH ONLY — not picks.

## ⚠️ DB insert blocked
`claude_daily_picks.approval_status` has a CHECK constraint allowing only
pending/approved/executing/executed/expired/failed/skipped/ready — **'research' is not allowed**.
No rows were inserted (logging as 'pending' would make them look like real picks). Schema was not changed.

Also note: the DB clock is UTC, so at 9pm ET `CURRENT_DATE + INTERVAL '1 day'` = 9/29, not 9/28. Use an explicit date or `(now() AT TIME ZONE 'America/New_York')::date + 1`.

Fix (run once, then re-run the inserts below):
```sql
ALTER TABLE claude_daily_picks DROP CONSTRAINT claude_daily_picks_approval_status_check;
ALTER TABLE claude_daily_picks ADD CONSTRAINT claude_daily_picks_approval_status_check
  CHECK (approval_status = ANY (ARRAY['pending','approved','executing','executed','expired','failed','skipped','ready','research']));
```

## Account
Buying power $200 (plus $100 pending deposit) → max per trade $80, min price $4.

## Scan results
| Signal | Found | Notes |
|---|---|---|
| PEAD | 0 logged | CBRL (EPS 0.99 vs 0.26, 9/23) already gapped ~8%, weak analyst sentiment (2B/4H/4S). MLKN beat 9/22 but guidance only maintained. KBH beat 9/22, 3B/10H/4S. SNX too expensive. |
| Analyst initiations | 5 | All dated 9/25 (see below). Monday's initiations not out yet. |
| Congress | 0 | No trades on GENI/PDFS; Pelosi's latest (BE, INTC) are July trades — stale, no bipartisan cluster. |
| Index inclusions | 0 | S&P Sept rebalance (BE, ILMN, Everpure) already effective 9/21. |
| Breaking catalysts | 0 affordable | MIRM FDA approval (Atebrioz, FOP) ~9/27 but ~$92 > $80 budget. |

## Candidates (preliminary)
| Ticker | Score | Catalyst | Close 9/25 | Consensus |
|---|---|---|---|---|
| GENI | 58 | JPMorgan initiated Overweight, $8 PT, 9/25 | $6.44 (Sun overnight ~$6.91) | 19B/3H/0S, mean PT $10.84 |
| LMRI | 52 | KeyBanc initiated Overweight, $18 PT, 9/25 | $10.21 | 9B/0H/0S, mean PT $17.63 (wide spread) |
| SPTX | 50 | Raymond James initiated Strong Buy, $46 PT, 9/25 | $21.52 | 7B/1H/0S, mean PT $38.29 |
| PDFS | 46 | Needham initiated Buy, $60 PT, 9/25 | $49.55 | 7B/1H/0S, mean PT $61.50 |
| PGEN | 44 | B. Riley initiated Buy, $12 PT, 9/25 | $7.75 | no consensus data |

GENI already jumped ~10% on initiation day and is showing another +7% overnight — check premarket before chasing.

## Macro
Mon 9/28: light (Dallas Fed mfg). Week: JOLTS + Consumer Confidence Tue, PCE + ADP Wed, ISM Thu, jobs Fri. No FOMC. PCE matters for rate-sensitive growth tech (PDFS).

## Ready-to-run inserts (after constraint fix)
```sql
DELETE FROM claude_daily_picks WHERE approval_status='research' AND pick_date < DATE '2026-09-28';
INSERT INTO claude_daily_picks (pick_date,ticker,direction,conviction,reason,catalyst,entry_price,target_price,stop_price,timeframe,sector,total_score,notes,approval_status) VALUES
(DATE '2026-09-28','GENI','bullish','medium','JPM initiation OW $8 PT; 19/22 buy','JPMorgan initiated coverage Overweight, $8 PT on 9/25/2026',0,0,0,'1-3 days','Communication Services',58,'NIGHTLY RESEARCH | Signals: Analyst initiation (JPM) | Macro: light Mon, PCE Wed | Account budget: $80 | Preliminary — needs morning validation','research'),
(DATE '2026-09-28','LMRI','bullish','medium','KeyBanc initiation OW $18 PT; 9/9 buy','KeyBanc initiated coverage Overweight, $18 PT on 9/25/2026',0,0,0,'1-3 days','Healthcare',52,'NIGHTLY RESEARCH | Signals: Analyst initiation | Macro: light Mon | Account budget: $80 | Preliminary — needs morning validation','research'),
(DATE '2026-09-28','SPTX','bullish','medium','Raymond James Strong Buy $46 PT; 7/8 buy','Raymond James initiated coverage Strong Buy, $46 PT on 9/25/2026',0,0,0,'1-3 days','Healthcare',50,'NIGHTLY RESEARCH | Signals: Analyst initiation | Macro: light Mon | Account budget: $80 | Preliminary — needs morning validation','research'),
(DATE '2026-09-28','PDFS','bullish','low','Needham Buy $60 PT; 7/8 buy','Needham initiated coverage Buy, $60 PT on 9/25/2026',0,0,0,'1-3 days','Technology',46,'NIGHTLY RESEARCH | Signals: Analyst initiation | Macro: PCE Wed hits growth tech | Account budget: $80 | Preliminary — needs morning validation','research'),
(DATE '2026-09-28','PGEN','bullish','low','B. Riley Buy $12 PT','B. Riley initiated coverage Buy, $12 PT on 9/25/2026',0,0,0,'1-3 days','Healthcare',44,'NIGHTLY RESEARCH | Signals: Analyst initiation | Macro: light Mon | Account budget: $80 | Preliminary — needs morning validation','research');
```
