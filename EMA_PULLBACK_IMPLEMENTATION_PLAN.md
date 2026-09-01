# EMA Pullback Strategy — Implementation Plan

**Date:** August 31, 2026  
**Status:** Ready to implement  

---

## The Problem

All 4 prediction profiles make identical calls on the same tickers — no diversification. Prediction-based trading (predict direction + time window) has a 25% accuracy rate. Profiles are just opinion variants, not strategy variants.

## The Solution: Setup-Based Trading

Shift from "predict direction" to "detect mechanical setups." AI grades setup quality instead of guessing direction.

### EMA Pullback Strategy (First Pattern)

**Rules:**
1. Stock in uptrend: price > 21 EMA, 21 EMA > 50 EMA
2. Price pulls back TO the 21 EMA (within 0.5% of touching it)
3. Reversal candle confirms bounce (close > open, close > prior close)
4. Entry on bounce confirmation
5. Stop below pullback low
6. Target: previous swing high (or 2:1 R:R minimum)

**AI Role:** Grade setup quality (0-100) based on trend strength, volume on pullback vs bounce, sector momentum, how cleanly price respected the EMA historically.

---

## Architecture (Integration, NOT Redesign)

### What changes:
- Add `GetEma21Async()` to TwelveDataProvider (new EMA period call)
- Add `EmaPullbackScanner` — a new method inside `TradeSetupEngine.cs` (extend, don't create new service)
- Add `setup_type` column to `prediction_candidates` to distinguish pattern-based entries
- Wire scanner into morning scan alongside existing prediction generation

### What stays the same:
- Morning scan pipeline (DailyResearchRunService)
- PortfolioLifecycleService entry/exit logic
- Portfolio challenges, positions, P&L tracking
- All existing scoring, evaluators, learning engine

---

## Implementation Steps

### Step 1: Add EMA 21 to Market Data

**File:** `TwelveDataProvider.cs`  
- Add `GetEma21Async(string ticker)` — single API call to `/ema?time_period=21`
- Or extend `GetEmaAsync` to return 4 values: (Ema12, Ema21, Ema26, Ema50)

**File:** `MarketDataService.cs`  
- Add cached wrapper for EMA 21

### Step 2: Add EMA Pullback Detection to TradeSetupEngine

**File:** `TradeSetupEngine.cs` (extend existing)

New method: `ScanForEmaPullbackAsync(string ticker, MarketSnapshot snapshot)`

Logic:
```
1. Get EMA 21 and EMA 50 for ticker
2. Get current price from quote
3. Get last 10 daily bars

CHECK UPTREND:
- price > ema21 (or within 0.5% above after bounce)
- ema21 > ema50

CHECK PULLBACK:
- One of the recent bars touched or crossed below ema21
- The most recent bar closed above ema21 (bounce confirmation)
- Most recent bar: close > open (green candle)

COMPUTE TRADE PARAMS:
- entry = current price
- stop = lowest low of pullback bars (below ema21)
- target = highest high in last 20 bars (swing high)
- R:R = (target - entry) / (entry - stop)
- Only pass if R:R >= 2.0

GRADE SETUP (AI role):
- Trend strength: distance between ema21 and ema50 (wider = stronger)
- Pullback depth: how far below ema21 did it go (shallow = better)
- Volume pattern: declining volume on pullback, rising on bounce
- Clean EMA respect: how many times has price bounced off ema21 in last 30 bars
```

Returns: `EmaPullbackSetup` record with ticker, entry, stop, target, quality_score, setup_details

### Step 3: Wire Into Morning Scan

**File:** `DailyResearchRunService.cs`

After existing prediction generation (line ~225), add a new block:
```csharp
// 4. Scan for EMA Pullback setups
var emaPullbackSetups = new List<PredictionCandidate>();
foreach (var snapshot in snapshots)
{
    var setup = await _setupEngine.ScanForEmaPullbackAsync(snapshot.Ticker, snapshot);
    if (setup != null)
        emaPullbackSetups.Add(setup.ToPredictionCandidate(runId));
}
allPredictions.AddRange(emaPullbackSetups);
```

The setup produces a `PredictionCandidate` with:
- `prediction_type = "bullish"` (EMA pullback is inherently bullish)
- `setup_type = "ema_pullback"` (new field to distinguish from prediction-based)
- `confidence_score` = quality grade from setup detection
- `time_window = "swing"` (no time prediction — hold until target or stop)
- Pre-computed stop/target from mechanical rules

### Step 4: Position Entry Integration

**File:** `PortfolioLifecycleService.cs`

No changes needed — EMA pullback setups flow through as PredictionCandidates with pre-set stops and targets. The existing entry logic (confidence gates, daily limits, cooldowns) all apply.

The `setup_type` field lets us track performance separately: pattern-based vs prediction-based.

### Step 5: Reduce Non-Champion Profile Volume

**Method:** Use existing `prediction_profile_configs` ticker_pool mechanism.

For Balanced Aggressor, Risk-Adjusted, Data-Driven Scalper:
- Set `ticker_pool` to a small set of ~10 high-quality tickers (include mode)
- This naturally limits their search volume without code changes
- They still run, still compete, but on a focused universe

Champion (Catalyst Momentum) keeps full universe access.

Alternative: Add a `max_prediction_count` config per profile that caps how many predictions each profile generates per run. Read from `prediction_profile_configs`, default to unlimited if not set.

### Step 6: DB Schema

```sql
-- Add setup_type to prediction_candidates
ALTER TABLE prediction_candidates 
ADD COLUMN IF NOT EXISTS setup_type TEXT DEFAULT 'prediction';
-- Values: 'prediction' (existing), 'ema_pullback', 'breakout' (future), 'rsi_reversion' (future)

-- Add setup_details JSONB for pattern-specific data
ALTER TABLE prediction_candidates 
ADD COLUMN IF NOT EXISTS setup_details JSONB;
-- Stores: { ema21, ema50, pullback_low, swing_high, rr_ratio, trend_strength, ... }
```

### Step 7: Performance Tracking

Track setup-based vs prediction-based P&L separately in `daily_profile_performance` or a new view. This lets us compare:
- Champion profile (prediction-based) P&L
- EMA Pullback (setup-based) P&L
- Which approach makes more money

---

## Profile Strategy After Implementation

| Profile | Role | Strategy | Volume |
|---------|------|----------|--------|
| Catalyst Momentum | Champion | Prediction-based (full scan) | Full universe |
| EMA Pullback | New pattern | Setup detection | Full universe (only fires on valid setups) |
| Balanced Aggressor | Challenger | Prediction-based (reduced) | ~10 tickers |
| Risk-Adjusted | Challenger | Prediction-based (reduced) | ~10 tickers |
| Data-Driven Scalper | Challenger | Prediction-based (reduced) | ~10 tickers |

---

## Future Patterns (after EMA Pullback is proven)

1. **Breakout Strategy** — Price consolidates in tight range, breaks above resistance on volume
2. **RSI Reversion** — RSI < 30 oversold bounce in uptrending stock
3. **VWAP Reclaim** — Price drops below VWAP, reclaims it with volume

Each becomes another setup type flowing through the same pipeline. They compete against each other and against the champion's prediction-based approach on P&L.

---

## Files to Modify

1. `TwelveDataProvider.cs` — Add EMA 21 fetch
2. `MarketDataService.cs` — Add EMA 21 cache wrapper
3. `TradeSetupEngine.cs` — Add `ScanForEmaPullbackAsync` method
4. `DailyResearchRunService.cs` — Wire scanner into morning scan
5. `Models/` — Add EmaPullbackSetup record, extend PredictionCandidate with setup_type
6. DB migration — Add setup_type and setup_details columns

## Files NOT Modified

- PortfolioLifecycleService.cs (entry/exit logic unchanged)
- ScoringEngine.cs (scoring unchanged)
- LearningEngine.cs (learning unchanged)
- PredictionGenerator.cs (existing prediction flow unchanged)
- All evaluators (unchanged)
