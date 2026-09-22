-- StockJawn Nightly Picks - Generated 2026-09-21 for Monday 2026-09-22 trading
-- BLOCKED: Supabase execute_sql requires approval for scheduled runs.
-- Run these manually or approve the Supabase tool for future scheduled runs.

-- ============================================================
-- STEP 1: Clean up old claude_research picks (3+ days old)
-- ============================================================
UPDATE research_universe
SET interest_score = 30, last_updated = now()
WHERE discovery_source = 'claude_research'
AND last_updated < now() - interval '3 days'
AND interest_score > 30;

-- ============================================================
-- STEP 2: Upsert picks into research_universe
-- ============================================================

-- For tickers already in research_universe, UPDATE:
-- COST (already in BaseUniverse)
UPDATE research_universe SET
  interest_score = 90,
  discovery_source = 'claude_research',
  discovery_reason = 'Claude research: Q4 FY2026 earnings Sept 24 after close. Consensus EPS $6.55 (+12% YoY), revenue $94.85B (+10%). Membership renewal rate key metric. BofA trimmed PT to $1,095 but maintains Buy.',
  last_updated = now()
WHERE ticker = 'COST';

-- NFLX (already in BaseUniverse)
UPDATE research_universe SET
  interest_score = 85,
  discovery_source = 'claude_research',
  discovery_reason = 'Claude research: Wells Fargo downgrade to Underweight, PT cut to $57. 8% decline in viewing time per subscriber. H2 2026 content slate materially lighter. Bearish setup.',
  last_updated = now()
WHERE ticker = 'NFLX';

-- MRVL (already in BaseUniverse)
UPDATE research_universe SET
  interest_score = 85,
  discovery_source = 'claude_research',
  discovery_reason = 'Claude research: BofA upgrade to Buy, PT $365. Custom AI chip market could reach $300B by 2030. Stock up 180% YTD. Ships to all 4 major cloud providers. Oct 6 analyst day upcoming.',
  last_updated = now()
WHERE ticker = 'MRVL';

-- For tickers NOT in research_universe, INSERT:
-- CVX (Chevron) - Energy leader
INSERT INTO research_universe (id, ticker, date_discovered, discovery_source, discovery_reason, current_state, interest_score, status, created_at, last_updated)
VALUES (gen_random_uuid(), 'CVX', now(), 'claude_research', 'Claude research: Oil near $100/bbl on Iran/Strait of Hormuz tensions. Venezuela deal for 65B barrels. Trading at $208, up 36% YTD. 5% dividend yield. Energy sector leading rotation.', 'Discovered', 85, 'Active', now(), now())
ON CONFLICT (ticker) DO UPDATE SET
  interest_score = 85,
  discovery_source = 'claude_research',
  discovery_reason = EXCLUDED.discovery_reason,
  last_updated = now();

-- XOM (Exxon Mobil) - Energy leader
INSERT INTO research_universe (id, ticker, date_discovered, discovery_source, discovery_reason, current_state, interest_score, status, created_at, last_updated)
VALUES (gen_random_uuid(), 'XOM', now(), 'claude_research', 'Claude research: Top US oil giant. Iran war driving crude to $100+. Energy sector up 43% YTD. Strait of Hormuz disruption is largest supply disruption in history per IEA.', 'Discovered', 80, 'Active', now(), now())
ON CONFLICT (ticker) DO UPDATE SET
  interest_score = 80,
  discovery_source = 'claude_research',
  discovery_reason = EXCLUDED.discovery_reason,
  last_updated = now();

-- COP (ConocoPhillips) - Energy
INSERT INTO research_universe (id, ticker, date_discovered, discovery_source, discovery_reason, current_state, interest_score, status, created_at, last_updated)
VALUES (gen_random_uuid(), 'COP', now(), 'claude_research', 'Claude research: Major E&P beneficiary of $100 oil. Top analyst watchlist in Sept 2026. Structural sector rotation into energy underway.', 'Discovered', 80, 'Active', now(), now())
ON CONFLICT (ticker) DO UPDATE SET
  interest_score = 80,
  discovery_source = 'claude_research',
  discovery_reason = EXCLUDED.discovery_reason,
  last_updated = now();

-- DRI (Darden Restaurants)
INSERT INTO research_universe (id, ticker, date_discovered, discovery_source, discovery_reason, current_state, interest_score, status, created_at, last_updated)
VALUES (gen_random_uuid(), 'DRI', now(), 'claude_research', 'Claude research: Q1 FY2027 earnings Sept 24 before open. Consensus EPS $2.06, revenue $3.21B. LongHorn same-store sales grew 9.5% last Q beating estimates. Brand divergence key watch.', 'Discovered', 85, 'Active', now(), now())
ON CONFLICT (ticker) DO UPDATE SET
  interest_score = 85,
  discovery_source = 'claude_research',
  discovery_reason = EXCLUDED.discovery_reason,
  last_updated = now();

-- CRH (CRH plc) - Infrastructure/M&A
INSERT INTO research_universe (id, ticker, date_discovered, discovery_source, discovery_reason, current_state, interest_score, status, created_at, last_updated)
VALUES (gen_random_uuid(), 'CRH', now(), 'claude_research', 'Claude research: $8.5B Arcosa acquisition approved by shareholders Sept 4. Closing Q1 2027. Major infrastructure buildout play. Aggregates and engineered structures expansion.', 'Discovered', 80, 'Active', now(), now())
ON CONFLICT (ticker) DO UPDATE SET
  interest_score = 80,
  discovery_source = 'claude_research',
  discovery_reason = EXCLUDED.discovery_reason,
  last_updated = now();

-- OXY (Occidental Petroleum)
INSERT INTO research_universe (id, ticker, date_discovered, discovery_source, discovery_reason, current_state, interest_score, status, created_at, last_updated)
VALUES (gen_random_uuid(), 'OXY', now(), 'claude_research', 'Claude research: Beneficiary of $100 oil and US-Venezuela deal. Part of energy rally. Strong domestic production. Buffett-backed.', 'Discovered', 78, 'Active', now(), now())
ON CONFLICT (ticker) DO UPDATE SET
  interest_score = 78,
  discovery_source = 'claude_research',
  discovery_reason = EXCLUDED.discovery_reason,
  last_updated = now();

-- JBHT (JB Hunt Transport)
INSERT INTO research_universe (id, ticker, date_discovered, discovery_source, discovery_reason, current_state, interest_score, status, created_at, last_updated)
VALUES (gen_random_uuid(), 'JBHT', now(), 'claude_research', 'Claude research: Citizens analyst upgrade to Market Outperform, PT $300. Transport/logistics recovery play.', 'Discovered', 78, 'Active', now(), now())
ON CONFLICT (ticker) DO UPDATE SET
  interest_score = 78,
  discovery_source = 'claude_research',
  discovery_reason = EXCLUDED.discovery_reason,
  last_updated = now();

-- CPRT (Copart)
INSERT INTO research_universe (id, ticker, date_discovered, discovery_source, discovery_reason, current_state, interest_score, status, created_at, last_updated)
VALUES (gen_random_uuid(), 'CPRT', now(), 'claude_research', 'Claude research: Acquiring ACV Auctions for $1.9B (Sept 10). Expanding digital vehicle remarketing platform. Closing expected by year-end 2026.', 'Discovered', 77, 'Active', now(), now())
ON CONFLICT (ticker) DO UPDATE SET
  interest_score = 77,
  discovery_source = 'claude_research',
  discovery_reason = EXCLUDED.discovery_reason,
  last_updated = now();

-- LEN (Lennar) - Bearish
INSERT INTO research_universe (id, ticker, date_discovered, discovery_source, discovery_reason, current_state, interest_score, status, created_at, last_updated)
VALUES (gen_random_uuid(), 'LEN', now(), 'claude_research', 'Claude research: Q3 earnings miss Sept 16. EPS $1.23 vs $1.29 est, revenue $8.05B vs $8.32B. Orders down 9% YoY. Mortgage rates at 6.8%. Housing affordability squeeze. Bearish.', 'Discovered', 75, 'Active', now(), now())
ON CONFLICT (ticker) DO UPDATE SET
  interest_score = 75,
  discovery_source = 'claude_research',
  discovery_reason = EXCLUDED.discovery_reason,
  last_updated = now();

-- PAYX (Paychex)
INSERT INTO research_universe (id, ticker, date_discovered, discovery_source, discovery_reason, current_state, interest_score, status, created_at, last_updated)
VALUES (gen_random_uuid(), 'PAYX', now(), 'claude_research', 'Claude research: Q1 FY2027 earnings Sept 23 before open. Consensus EPS $1.32 (+8% YoY), revenue $1.6B. Consistent earnings beater.', 'Discovered', 75, 'Active', now(), now())
ON CONFLICT (ticker) DO UPDATE SET
  interest_score = 75,
  discovery_source = 'claude_research',
  discovery_reason = EXCLUDED.discovery_reason,
  last_updated = now();

-- ============================================================
-- STEP 3: Log picks in claude_daily_picks
-- ============================================================
INSERT INTO claude_daily_picks (id, pick_date, ticker, direction, conviction, reason, catalyst, timeframe, sector, created_at)
VALUES
  (gen_random_uuid(), CURRENT_DATE + 1, 'COST', 'bullish', 'high', 'Q4 FY2026 earnings Sept 24. Revenue expected +10% YoY, EPS +12%. Membership renewal rate key.', 'Earnings report Sept 24 after close', '1-day', 'Consumer Staples', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'NFLX', 'bearish', 'high', 'Wells Fargo downgrade to Underweight. 8% viewing time decline. H2 content slate weak.', 'Wells Fargo downgrade, engagement deterioration', '1-day', 'Communication Services', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'MRVL', 'bullish', 'high', 'BofA upgrade to Buy, PT $365. Custom AI chip TAM could reach $300B by 2030.', 'BofA upgrade, AI custom chip expansion', '1-day', 'Technology', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'CVX', 'bullish', 'high', 'Oil at $100/bbl. Iran/Strait of Hormuz. Venezuela 65B barrel deal. 5% dividend.', 'Geopolitical oil supply disruption, Venezuela deal', '1-day', 'Energy', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'DRI', 'bullish', 'high', 'Q1 FY2027 earnings Sept 24 pre-market. LongHorn same-store sales +9.5% last Q.', 'Earnings report Sept 24 before open', '1-day', 'Consumer Discretionary', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'XOM', 'bullish', 'medium', 'Top US oil giant benefiting from $100 crude. Iran war driving largest supply disruption in history.', 'Iran conflict, elevated crude prices', '1-day', 'Energy', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'COP', 'bullish', 'medium', 'Major E&P beneficiary of elevated oil. Structural sector rotation into energy.', 'Sector rotation into energy, $100 oil', '1-day', 'Energy', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'CRH', 'bullish', 'medium', '$8.5B Arcosa acquisition approved. Infrastructure buildout and aggregates expansion.', 'Arcosa acquisition closing Q1 2027', '1-day', 'Industrials', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'OXY', 'bullish', 'medium', 'Oil rally beneficiary. US-Venezuela deal. Buffett-backed. Strong domestic production.', 'Venezuela deal, elevated crude', '1-day', 'Energy', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'JBHT', 'bullish', 'medium', 'Citizens analyst upgrade to Market Outperform, PT $300.', 'Analyst upgrade', '1-day', 'Industrials', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'CPRT', 'bullish', 'medium', 'Acquiring ACV Auctions for $1.9B. Expanding digital vehicle remarketing.', 'ACV Auctions acquisition', '1-day', 'Industrials', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'LEN', 'bearish', 'medium', 'Q3 earnings miss. Orders -9% YoY. Mortgage rates 6.8%. Housing affordability squeeze.', 'Post-earnings weakness, housing downturn', '1-day', 'Consumer Discretionary', now()),
  (gen_random_uuid(), CURRENT_DATE + 1, 'PAYX', 'bullish', 'medium', 'Q1 FY2027 earnings Sept 23 pre-market. Consensus EPS $1.32 (+8% YoY).', 'Earnings report Sept 23 before open', '1-day', 'Financials', now());
