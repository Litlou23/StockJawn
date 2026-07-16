# Prediction Profiles — Design Review

> **Reviewer**: Claude (acting as senior software architect)  
> **Date**: 2026-07-15  
> **Document Reviewed**: `prediction-profiles-design.md`  
> **Verdict**: The core concept is sound, but the integration plan has several flaws that would cause bugs or unnecessary cost if implemented as written. Key changes needed before coding begins.

---

## 1. Architectural Flaws

### FLAW 1: The "Shared Pipeline" Claim is Wrong

The design states: "Market data is fetched once per ticker, then scoring runs once per enabled profile with different weights." This is not how the code actually works.

`GeneratePredictionForTickerAsync` does far more than just score. For every ticker, it:

1. Computes technical indicators (`IndicatorEngine.Compute`)
2. Fetches SPY/QQQ quotes for benchmark context (API calls)
3. Fetches active research signals for the ticker (DB query)
4. Runs the full market intelligence pipeline (`BuildContextAsync`) — evidence, features, thesis
5. Looks up historical research profiles (DB query)
6. Builds `ResearchUniverseContext`
7. Runs the Volatility Opportunity Engine assessment
8. Persists the VOE assessment
9. Runs `ScoringEngine.Evaluate` (the part that actually uses weights)
10. Calls OpenAI for explanation (API call)
11. Computes ATR price forecast, setup history lookup, per-ticker reliability

Steps 1-8 and 10-11 are **identical across profiles** — only step 9 changes. The design proposes calling `GeneratePredictionsForWatchlistAsync` N times, which re-does steps 1-11 for every profile.

**Impact**: At 150 tickers × 3 profiles, you'd make 450 SPY/QQQ quote fetches instead of 150, 450 intelligence pipeline runs, 450 historical profile lookups, etc. This isn't about cost — it's about correctness. The design claims to share these calls but doesn't.

**Fix**: The integration point cannot be `GeneratePredictionsForWatchlistAsync`. It must be *inside* `GeneratePredictionForTickerAsync`, after steps 1-8 produce the shared intermediate data, but before step 9 (scoring). For each ticker: build the shared context once, then loop over profiles for scoring only. This is more invasive than the design acknowledges — it requires refactoring the prediction generator's internals, which the design explicitly said it wouldn't do.

The practical compromise: extract the pre-scoring work (steps 1-8) into a method that returns the intermediate context (indicators, benchmark, intelligence, research signals, research universe, VOE assessment), then call `ScoringEngine.Evaluate` once per profile with different weights, then assemble predictions per profile. This is a refactor of `GeneratePredictionForTickerAsync` but not of the engines themselves.

### FLAW 2: Dedup Will Block Cross-Profile Predictions

The dedup system in `GeneratePredictionsForWatchlistAsync` (lines 509-517) fetches ALL today's predictions and builds a `ticker→time_window` lookup. When Profile A generates a bullish AAPL/1_day prediction and Profile B runs next, the dedup sees AAPL/1_day already exists and skips it. Profile B never generates a prediction for AAPL.

The design mentions this in Risk 4 but calls it a "mitigation" to add `WHERE profile_id = @profileId`. That's correct, but the design buries it as a risk note rather than a core design requirement. This is not a risk — it's a guaranteed bug if not handled. Every dedup query, supersession check, and batch tracker needs profile scoping.

### FLAW 3: DynamicPickOrchestrator Will Consume Challenger Predictions

After morning scan, `DynamicPickOrchestrator.RunDynamicMorningPicksAsync()` calls `GetPredictionsByRunAsync(scan.RunId)` to load ALL predictions from the run. It then:

- Records evidence into the Evidence Engine for every prediction
- Builds paper stock candidates from every prediction
- Opens portfolio positions from every prediction

With profiles sharing a `run_id`, the orchestrator would record evidence from challengers (polluting the Evidence Engine with experimental data), create paper stock candidates from challengers (inflating the paper trading pool), and potentially open portfolio positions from challenger predictions.

**Fix**: Either (a) give each profile its own `run_id`, or (b) filter by `profile_id` in the orchestrator. Option (a) is cleaner because `run_id` is the natural batch boundary and many downstream queries use it. Option (b) is a patch that requires auditing every `GetPredictionsByRunAsync` caller.

### FLAW 4: Learning Engine Isolation Is Underspecified

The design says "add `RunProfileLearningCycleAsync(profileId)`" but doesn't address that `RunFullLearningCycleAsync` calls `GetRecentPredictionsAsync(500)` in **six different places** (lines 182, 731, 1217, 1387, 2511, 2707) plus several other queries that pull predictions globally. Each of these needs a profile filter, plus the signal observations, calibration, correlation, influence, interaction, setup performance, supersession analytics, VOE learning, and threshold optimization stages.

That's not "add a method" — it's threading a `profileId` parameter through the entire 2,983-line learning engine. The design's estimate of this complexity is far too low.

---

## 2. Unnecessary Complexity

### COMPLEXITY 1: `is_production` AND `role` Are Redundant

The `prediction_profiles` table has both `is_production BOOLEAN` and `role TEXT CHECK ('champion', 'challenger')`. These encode the same information. A champion is the production profile. Having both means they can get out of sync (e.g., `is_production = true` but `role = 'challenger'`).

**Fix**: Drop `is_production`. Use `role = 'champion'` as the single source of truth. The unique partial index already enforces one champion.

### COMPLEXITY 2: `profile_name` on `prediction_candidates` Is Denormalized

Storing `profile_name` alongside `profile_id` on every prediction row means renaming a profile requires updating potentially thousands of prediction rows. This is a maintenance trap.

**Fix**: Store only `profile_id`. Join to `prediction_profiles` when you need the name. The index on `profile_id` makes this a cheap join.

### COMPLEXITY 3: `profile_learning_snapshots` Duplicates Computable Data

Every metric in `profile_learning_snapshots` (bullish accuracy, bear accuracy, avg confidence, avg return, calibration error) can be computed on-demand from `prediction_candidates` + `prediction_outcomes` filtered by `profile_id`. Materializing it into a separate table means:

- The snapshot can disagree with the live data if predictions are re-evaluated
- You need code to compute it AND code to read the pre-computed version
- The table grows unboundedly (one row per profile per day forever)

**Recommendation**: Keep this table, but acknowledge it's a cache/materialized view, not a source of truth. The frontend should use it for historical charts (how did accuracy look 30 days ago?) but the comparison table should compute live metrics from the actual predictions. This distinction matters and the design doesn't make it.

### COMPLEXITY 4: Promotion Flow Copies Weights to `scoring_weight_overrides`

The promotion step says "Copy challenger's `prediction_profile_configs` → `scoring_weight_overrides`." This creates two sources of truth for the champion's weights. After promotion, are the champion's weights read from `prediction_profile_configs` or `scoring_weight_overrides`? What happens when the learning engine updates weights — does it update both?

**Fix**: The champion profile should read its weights from `prediction_profile_configs` just like challengers. Don't use `scoring_weight_overrides` for profile-managed weights at all. Instead, the champion profile's `prediction_profile_configs` rows become the effective weights. This eliminates the dual-write problem.

However, this conflicts with the backward compatibility goal (existing code reads `scoring_weight_overrides`). The cleaner approach: during the transition, have `PreloadProfileContextsAsync` read from `prediction_profile_configs` for all profiles, and have the champion profile's configs initialized from `scoring_weight_overrides` during migration. Then `scoring_weight_overrides` becomes a legacy table that the learning engine stops writing to once profiles are active.

---

## 3. Simplifications

### SIMPLIFY 1: Start With a Single Challenger, Not N

The design allows unlimited profiles from day one. This multiplies every integration concern. For MVP, support exactly two profiles: the champion (Production) and one challenger. The morning scan runs Production, then runs the challenger. Dedup is trivial (two separate runs). Learning is trivial (champion uses existing code, challenger gets the new filtered path).

Expanding to N challengers is a later optimization once the single-challenger path is proven.

### SIMPLIFY 2: Don't Change `GeneratePredictionsForWatchlistAsync` Signature

Instead of looping over profiles at the `DailyResearchRunService` level, run the entire morning scan twice — once for production (existing path, zero changes), once for the challenger. The challenger run uses its own `run_id`, its own dedup scope, and produces its own predictions.

This is less elegant (two full scan calls) but dramatically reduces integration risk because the existing production path is completely untouched. The "shared snapshot" optimization can come later once the basic profile system is proven.

### SIMPLIFY 3: Skip Weight History Table in MVP

`profile_weight_history` is a nice-to-have audit trail. For MVP, the learning snapshots plus git history of weight changes is sufficient. The weight history table can be added in Phase 2 when you actually have a frontend to display it.

---

## 4. Constraint Violations

### VIOLATION 1: "Do NOT modify the Prediction Engine"

The design's integration point (`PreloadProfileContextsAsync`, modifying `SharedPredictionContext`, changing `GeneratePredictionsForWatchlistAsync` to accept different contexts) modifies the Prediction Engine. The design claims otherwise, but `PredictionGenerator` IS the prediction engine.

**Assessment**: This violation is unavoidable. You cannot run multiple configurations through the prediction pipeline without touching the prediction pipeline. The constraint should be updated to "Do not modify the scoring/evaluation logic within the Prediction Engine. Profile integration is permitted in the orchestration layer."

### VIOLATION 2: "Do NOT create new services"

`PredictionProfileRepository` is listed as a new file. It's a repository, not a service, but the line is blurry. The constraint should clarify: repositories and controllers for new domain entities are acceptable; new domain services that duplicate existing logic are not.

---

## 5. Missing Integration Points

### MISSING 1: Frontend Predictions Page

The existing `/predictions` page shows all predictions. With profiles, it would show 3× the predictions (one per profile per ticker). Users need a way to filter by profile or default to showing only champion predictions. The design's Phase 3 frontend section doesn't mention modifying the existing predictions page — only creating new profile-specific pages.

### MISSING 2: Dashboard and Reports

The `/dashboard` page likely shows summary stats (today's predictions, accuracy, etc.). These need profile scoping or they'll double/triple count predictions.

### MISSING 3: Chat Tools

The existing chat tool system can run morning scans, check predictions, etc. It would return predictions from all profiles mixed together unless filtered.

### MISSING 4: Options Lab Integration

`TheoreticalOptionsSimulator` and `AutomaticScenarioGenerator` both call `GetRecentPredictionsAsync` to find predictions to generate options scenarios for. They'd generate scenarios for challenger predictions, wasting OpenAI cost on experimental configurations.

### MISSING 5: Paper Stock Candidates

`StockCandidateService.BuildDirectionalRankings` is called by the orchestrator to rank predictions. With profiles, it would mix champion and challenger predictions in the same ranking.

### MISSING 6: `PaperOptionsService` and `OptionsDataService`

Both call `GetRecentPredictionsAsync` to find predictions for options analysis. Need profile filtering.

### MISSING 7: `DynamicWatchlistService`

Calls `GetRecentPredictionsAsync(100)` to inform watchlist decisions. Challenger predictions would influence the dynamic watchlist.

### MISSING 8: `PatternDetectionService`

Calls `GetRecentPredictionsAsync(500)` to detect patterns. Challenger predictions would corrupt pattern detection for the production pipeline.

---

## 6. Database Design Concerns

### CONCERN 1: CASCADE DELETE on `prediction_profile_configs`

`ON DELETE CASCADE` from `prediction_profiles` to `prediction_profile_configs` is correct. But the design says "Predictions remain (orphaned `profile_id`)." This means `prediction_candidates.profile_id` has a FK to `prediction_profiles` but no CASCADE. If you delete a profile, the FK constraint would block the delete unless you either (a) SET NULL the profile_id on predictions first, or (b) remove the FK constraint.

**Fix**: Make `profile_id` a soft reference (no FK constraint) since predictions are historical data that should survive profile deletion. Or use `ON DELETE SET NULL`.

### CONCERN 2: Backfill Migration

The backfill `UPDATE prediction_candidates SET profile_name = 'Production' WHERE profile_id IS NULL` doesn't set `profile_id` because the Production profile's UUID isn't known at migration time. You'd need a two-step migration: (1) create the profile, (2) backfill the UUID. On a table with thousands of rows, the UPDATE could lock the table.

**Fix**: Use a sub-select: `UPDATE prediction_candidates SET profile_id = (SELECT id FROM prediction_profiles WHERE role = 'champion') WHERE profile_id IS NULL`. Run as a background job, not a blocking migration.

### CONCERN 3: No Index for Champion Profile Lookup

Every downstream consumer needs to filter by champion profile. The query `SELECT id FROM prediction_profiles WHERE role = 'champion'` should be instant (unique partial index exists), but the more common pattern will be `SELECT * FROM prediction_candidates WHERE profile_id = @championId AND ...`. The proposed index `(profile_id, created_at DESC)` covers this.

---

## 7. Performance Concerns

### PERF 1: Morning Scan Duration (Realistic Assessment)

The design dismisses scoring as "sub-millisecond per ticker." True for `ScoringEngine.Evaluate` alone, but the full `GeneratePredictionForTickerAsync` includes DB queries, API calls, and OpenAI calls per ticker. If you call it N times per profile (as the design proposes), the morning scan duration multiplies.

With the fix from Flaw 1 (share pre-scoring work, only re-run scoring per profile), the extra cost per profile is genuinely minimal — scoring + prediction assembly + DB insert, probably 5-10ms per ticker. At 150 tickers × 2 extra profiles = ~1.5 seconds added. Acceptable.

### PERF 2: Learning Engine Duration

The learning engine runs `GetRecentPredictionsAsync(500)` six times, plus `GetSignalObservationsAsync(5000)` four times, plus various other queries. Running per profile means N× these queries. At 3 profiles, this triples the nightly learning job from ~30s to ~90s. Not a crisis, but worth noting.

---

## 8. Edge Cases

### EDGE 1: Profile Created Mid-Day

If a new profile is created after the morning scan has already run, it has zero predictions for the day. The next morning scan will generate predictions for it. But the EOD review will try to evaluate predictions for it and find none. This is fine — no crash, just no-ops. But the frontend should handle "no data yet" gracefully.

### EDGE 2: Profile Disabled During Active Predictions

If a profile is disabled while it has open predictions, the EOD evaluator (which evaluates ALL open predictions) would still evaluate them. This is actually correct — you want outcomes recorded even for disabled profiles so you can compare historical performance.

### EDGE 3: Two Profiles Produce Different Directions for Same Ticker

Production says AAPL is bullish. Challenger says AAPL is bearish. Both are correct behavior — the whole point is different configurations disagree. But downstream consumers (portfolio, stock candidates) must only act on the champion's prediction.

### EDGE 4: Champion Profile Deleted

The unique partial index prevents deleting the champion via the `role` check, but the API should also block this. The design mentions "Delete (blocked if champion)" but doesn't specify enforcement — needs a check in the controller, not just a database constraint.

### EDGE 5: Promotion During Active Predictions

If you promote a challenger to champion mid-day, existing open predictions from the old champion have `profile_id = old_champion`. The new champion starts generating predictions on the next morning scan. During the transition day, there are predictions from the old champion and none from the new champion. The portfolio orchestrator needs to handle this transition — possibly by treating all open champion predictions (old and new) as valid.

### EDGE 6: Concurrent Morning Scan with Profile Changes

If someone creates/deletes/modifies a profile while the morning scan is running (background job), the scan could see an inconsistent profile list. The `PreloadProfileContextsAsync` should snapshot the profile list at scan start and ignore changes during execution.

---

## 9. Future Maintenance Concerns

### MAINTENANCE 1: Every New `GetRecentPredictionsAsync` Caller Needs Profile Awareness

Any future code that queries predictions must decide: do I want champion only, specific profile, or all profiles? This is an ongoing tax on every developer. A default filter (champion-only unless explicitly overridden) would reduce this burden.

### MAINTENANCE 2: Learning Engine Profile Threading

Threading `profileId` through the 2,983-line learning engine is the most error-prone part of this implementation. Missing even one query means production learning gets contaminated with experimental data. This needs thorough testing.

### MAINTENANCE 3: Schema Evolution

If you add a new configurable value to the scoring engine in the future (e.g., a new evaluator bucket), you need to remember to also add it to the profile config system. There's no compile-time enforcement that profile configs stay in sync with scoring engine parameters.

---

## Final Assessment

### Keep Exactly As Designed

- **Core concept**: Named weight sets that flow through the existing scoring engine. This is the right abstraction.
- **Database tables**: `prediction_profiles` and `prediction_profile_configs` (with simplifications noted below). The key-value config approach is flexible and forward-compatible.
- **Manual promotion only**: Correct. Automatic promotion is a foot-gun.
- **Static vs Adaptive distinction**: Clean, useful, simple to implement.
- **Frontend phasing**: Backend first, frontend later. Correct priority.
- **Example profiles**: The specific profiles proposed (No Technicals, Reduced Technicals, Fundamentals Boost) directly address the TwelveData accuracy problem. Good.

### Redesign Before Implementation

1. **Integration point**: Move the profile loop INSIDE `GeneratePredictionForTickerAsync`, after shared work (indicators, intelligence, VOE) and before scoring. Don't call the full method N times per profile. This is the single most important change.

2. **Drop `is_production`**: Redundant with `role = 'champion'`. Single source of truth.

3. **Drop `profile_name` from `prediction_candidates`**: Store only `profile_id`. Join for display.

4. **Use separate `run_id` per profile**: Or add `profile_id` to `research_runs` so downstream consumers can distinguish. This solves the orchestrator contamination problem cleanly.

5. **Default to champion-only in `GetRecentPredictionsAsync`**: Add an optional `profileId` parameter that defaults to the champion's ID. Callers that want all profiles pass `profileId: null`. This makes the safe path the default path.

6. **FK on `prediction_candidates.profile_id`**: Use `ON DELETE SET NULL` instead of a hard FK, since predictions should survive profile deletion.

7. **Audit all 18 callers of `GetRecentPredictionsAsync` / `GetOpenPredictionsAsync`**: The design identifies 5 downstream consumers in Risk 5 but the grep shows 18 call sites across 11 files. Every single one needs a decision about profile scoping.

### Can Wait Until a Future Version

- **`profile_weight_history` table**: Nice audit trail, not needed for MVP.
- **Profile comparison page**: Can use raw SQL queries until the frontend is built.
- **`profile_learning_snapshots`**: Can compute on-demand initially. Materialize when performance requires it.
- **N challenger support**: Start with one challenger. Expand later.
- **AI explanation for challengers**: Correct to skip this. Defer indefinitely.
- **Prediction Explorer**: The existing predictions page with a profile filter dropdown is sufficient initially.
- **Auto-disable underperforming profiles**: Phase 4 at earliest.

### Risk Assessment

| Risk | Severity | Likelihood | Mitigation |
|------|----------|-----------|------------|
| Dedup blocks cross-profile predictions | **Critical** | **Certain** (if not fixed) | Profile-scoped dedup is a hard requirement, not a "mitigation" |
| Orchestrator consumes challenger predictions | **Critical** | **Certain** (if not fixed) | Separate run_id per profile, or filter in orchestrator |
| Learning contamination | **High** | **Likely** (18 query call sites) | Default champion filter on prediction queries |
| Prediction generator redundant API calls | **Medium** | **Certain** (if designed as written) | Refactor to share pre-scoring work |
| Morning scan duration | **Low** | **Unlikely** (scoring is fast) | Only a concern if pre-scoring work isn't shared |
| Existing frontend shows mixed predictions | **Medium** | **Certain** (if not addressed) | Default champion filter solves this |

**Bottom line**: The concept is right. The integration plan needs surgery in three places: (1) where the profile loop sits in the code, (2) how dedup is scoped, and (3) how downstream consumers are isolated. Fix those three, and the rest of the design is solid.
