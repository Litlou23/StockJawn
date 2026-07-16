# Prediction Profiles — Design Document

> **Status**: Proposed  
> **Author**: Claude + Lou  
> **Date**: 2026-07-15  
> **Constraint**: This is an integration task. Do NOT modify the Scoring Engine, Prediction Engine, or Learning Engine internals. Do NOT create new services. Profiles inject different *configuration* into the existing pipeline.

---

## 1. Overview

Prediction Profiles let StockJawn run multiple prediction configurations against the same market data simultaneously, then compare their performance to determine which configuration works best before promoting it to production.

The key insight from the architecture review: the entire scoring pipeline is already parameterized by a `Dictionary<string, double> weights` dictionary loaded via `PreloadSharedContextAsync()`. Profiles are essentially named, persistent weight sets — each profile loads its own weights, passes them through the same `ScoringEngine.Evaluate()` call, and produces a separate `PredictionCandidate` tagged with the profile ID.

---

## 2. Architecture Principle — Shared Pipeline, Divergent Configuration

```
Discovery → Research Universe → GetResearchCandidatesAsync()
                                         ↓
                              BuildMarketSnapshotAsync()     ← ONCE per ticker
                                         ↓
                              ┌──────────────────────────┐
                              │  For each enabled profile │
                              │  ┌──────────────────────┐ │
                              │  │ Load profile weights  │ │
                              │  │ ScoringEngine.Evaluate│ │
                              │  │ PredictionGenerator   │ │
                              │  │ Save with profile_id  │ │
                              │  └──────────────────────┘ │
                              └──────────────────────────┘
                                         ↓
                              EOD evaluates ALL profiles
                                         ↓
                              Learning runs PER profile
```

**What's shared** (done once): Discovery, research candidates, market snapshots (API calls), technical indicators computation, market intelligence pipeline, AI explanations (optional — can skip for challenger profiles to save OpenAI cost).

**What diverges** (done per profile): Weight loading, scoring, confidence calculation, prediction assembly, prediction persistence, outcome evaluation, learning cycle.

---

## 3. Database Schema

### 3.1 New Table: `prediction_profiles`

```sql
CREATE TABLE prediction_profiles (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name            TEXT NOT NULL UNIQUE,
    description     TEXT,
    is_production   BOOLEAN NOT NULL DEFAULT false,
    is_enabled      BOOLEAN NOT NULL DEFAULT true,
    learning_enabled BOOLEAN NOT NULL DEFAULT true,
    -- Champion/Challenger designation
    role            TEXT NOT NULL DEFAULT 'challenger'
                    CHECK (role IN ('champion', 'challenger')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Exactly one champion at any time
CREATE UNIQUE INDEX idx_prediction_profiles_champion
    ON prediction_profiles (role) WHERE role = 'champion';

-- Seed the production profile from current state
INSERT INTO prediction_profiles (name, description, is_production, role)
VALUES ('Production', 'Current production configuration — weights learned from live data', true, 'champion');
```

### 3.2 New Table: `prediction_profile_configs`

Stores the per-profile weight/threshold overrides. Profiles that don't override a weight inherit the production default from `research_scoring_weights` and `scoring_weight_overrides`.

```sql
CREATE TABLE prediction_profile_configs (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id      UUID NOT NULL REFERENCES prediction_profiles(id) ON DELETE CASCADE,
    config_key      TEXT NOT NULL,
    config_value    DOUBLE PRECISION NOT NULL,
    description     TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (profile_id, config_key)
);
```

**Config keys** are the same keys already used in the weights dictionary:

| Category | Keys | Current defaults |
|----------|------|-----------------|
| Bucket weights | `trend`, `momentum`, `volume`, `volatility`, `market_context`, `catalyst`, `learning`, `research_signal` | 1.0, 1.0, 0.8, 0.7, 0.9, 1.1, 0.5, 1.0 |
| Calibration | `calibration_factor` | 1.0 |
| Risk tuning | `risk_cap_boost` | 0 |
| Decision thresholds | `min_edge_margin`, `min_score_for_direction`, `min_ratio_for_direction` | 10, 20, 1.4 |
| Research | `research_universe_weight` | 1.0 |

A profile with zero rows in `prediction_profile_configs` is identical to production. A "No Technicals" profile would have three rows: `trend=0`, `momentum=0`, `volume=0`.

### 3.3 Modifications to `prediction_candidates`

Add two columns:

```sql
ALTER TABLE prediction_candidates
    ADD COLUMN profile_id   UUID REFERENCES prediction_profiles(id),
    ADD COLUMN profile_name TEXT;

-- Backfill existing predictions as production
UPDATE prediction_candidates
SET profile_name = 'Production'
WHERE profile_id IS NULL;

-- Index for per-profile queries
CREATE INDEX idx_predictions_profile ON prediction_candidates (profile_id, created_at DESC);
```

**Backward compatibility**: `profile_id` is nullable. All existing code that doesn't pass a profile continues to work — those predictions are implicitly "Production." The production profile's UUID gets backfilled after creation.

### 3.4 New Table: `profile_learning_snapshots`

Captures per-profile performance metrics after each learning cycle, enabling the comparison view.

```sql
CREATE TABLE profile_learning_snapshots (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id              UUID NOT NULL REFERENCES prediction_profiles(id) ON DELETE CASCADE,
    snapshot_date           DATE NOT NULL,
    total_predictions       INT NOT NULL DEFAULT 0,
    evaluated_predictions   INT NOT NULL DEFAULT 0,
    bullish_correct         INT NOT NULL DEFAULT 0,
    bullish_total           INT NOT NULL DEFAULT 0,
    bearish_correct         INT NOT NULL DEFAULT 0,
    bearish_total           INT NOT NULL DEFAULT 0,
    neutral_correct         INT NOT NULL DEFAULT 0,
    neutral_total           INT NOT NULL DEFAULT 0,
    avg_confidence          DOUBLE PRECISION,
    avg_expected_value      DOUBLE PRECISION,
    avg_return_percent      DOUBLE PRECISION,
    calibration_error       DOUBLE PRECISION,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (profile_id, snapshot_date)
);
```

### 3.5 New Table: `profile_weight_history`

Tracks weight changes over time for adaptive profiles.

```sql
CREATE TABLE profile_weight_history (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id      UUID NOT NULL REFERENCES prediction_profiles(id) ON DELETE CASCADE,
    config_key      TEXT NOT NULL,
    old_value       DOUBLE PRECISION NOT NULL,
    new_value       DOUBLE PRECISION NOT NULL,
    reason          TEXT,
    changed_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_profile_weight_history_lookup
    ON profile_weight_history (profile_id, config_key, changed_at DESC);
```

---

## 4. Backend Design

### 4.1 Profile Weight Resolution

The core integration point is `PredictionGenerator.PreloadSharedContextAsync()`. Currently it loads one weight set. With profiles, it loads one weight set *per enabled profile*.

```csharp
// New method alongside existing PreloadSharedContextAsync
public async Task<Dictionary<string, SharedPredictionContext>> PreloadProfileContextsAsync()
{
    // 1. Load base weights + overrides (production defaults)
    var baseContext = await PreloadSharedContextAsync();

    // 2. Load all enabled profiles
    var profiles = await _repo.GetEnabledProfilesAsync();

    // 3. For each profile, merge its config overrides onto the base
    var result = new Dictionary<string, SharedPredictionContext>();
    foreach (var profile in profiles)
    {
        var profileWeights = new Dictionary<string, double>(baseContext.Weights);
        var overrides = await _repo.GetProfileConfigsAsync(profile.Id);
        foreach (var o in overrides)
            profileWeights[o.ConfigKey] = o.ConfigValue;

        result[profile.Id] = new SharedPredictionContext(profileWeights, baseContext.Lessons)
            with { ProfileId = profile.Id, ProfileName = profile.Name };
    }

    return result;
}
```

**Changes to `SharedPredictionContext`**: Add `ProfileId` and `ProfileName` properties.

### 4.2 Morning Scan Integration

In `DailyResearchRunService.RunMorningScanAsync()`, after building snapshots (shared), loop over profiles:

```
// Existing: build snapshots once
var snapshots = await BuildSnapshots(tickers);

// NEW: load all profile contexts
var profileContexts = await _predGen.PreloadProfileContextsAsync();

foreach (var (profileId, context) in profileContexts)
{
    var (predictions, inputs, supersessions) = await _predGen
        .GeneratePredictionsForWatchlistAsync(tickers, runId, snapshots, assetLookup, context);

    // Tag each prediction with profile
    foreach (var pred in predictions)
    {
        pred.ProfileId = profileId;
        pred.ProfileName = context.ProfileName;
    }

    // Save (existing save logic, now includes profile_id and profile_name columns)
    await SavePredictions(predictions, inputs);

    // Handle supersessions per-profile (only within same profile)
    await HandleSupersessions(supersessions, profileId);
}
```

**AI explanations**: Skip for challenger profiles to save OpenAI cost. The scoring/direction/confidence are the valuable parts — explanations can be generated on-demand from the frontend if someone wants to inspect a challenger prediction.

### 4.3 EOD Review Integration

`OutcomeEvaluator` already evaluates all open predictions regardless of how they were created. No changes needed — it evaluates by `status = 'open'`, which includes all profiles.

The `profile_id` column on `prediction_candidates` means outcomes are automatically associated with the right profile through the existing `prediction_id` FK on `prediction_outcomes`.

### 4.4 Learning Engine Isolation

This is the most critical integration. The learning engine must run *per profile* so weight adjustments are isolated.

**Current flow**: `LearningEngine.RunFullLearningCycleAsync()` loads recent predictions globally, computes stats, adjusts weights in `scoring_weight_overrides`.

**New flow**: Add a `RunProfileLearningCycleAsync(string profileId)` method that:

1. Filters `GetRecentPredictionsAsync` to only predictions with matching `profile_id`
2. Computes signal performance, calibration, weight optimization — all scoped to that profile's predictions
3. Writes weight adjustments to `prediction_profile_configs` (not `scoring_weight_overrides`) for challenger profiles
4. Writes weight adjustments to `scoring_weight_overrides` for the champion profile (preserving current behavior)
5. Saves a `profile_learning_snapshot` row
6. Records weight changes in `profile_weight_history`

**Static profiles** (`learning_enabled = false`): The learning cycle skips them. Their weights are frozen at whatever was set during creation/cloning.

**Key constraint**: Production learning must never include experimental predictions. The `profile_id` filter on prediction queries enforces this naturally.

### 4.5 API Endpoints

New controller: `PredictionProfileController`

```
GET    /api/profiles                          — List all profiles with summary stats
GET    /api/profiles/{id}                     — Profile detail + config + performance
POST   /api/profiles                          — Create profile
PUT    /api/profiles/{id}                     — Update profile metadata
DELETE /api/profiles/{id}                     — Delete (blocked if champion)
POST   /api/profiles/{id}/clone               — Clone from existing profile
PUT    /api/profiles/{id}/config              — Update weight configuration
POST   /api/profiles/{id}/promote             — Promote to champion (manual)
GET    /api/profiles/{id}/weight-history      — Weight change history
GET    /api/profiles/compare?ids=a,b,c        — Compare multiple profiles
GET    /api/profiles/{id}/predictions         — Predictions for this profile
GET    /api/profiles/{id}/learning-snapshots  — Daily performance snapshots
```

### 4.6 Promotion Flow

Promoting a challenger to champion:

1. Validate the challenger has sufficient data (configurable minimum, e.g., 50 evaluated predictions)
2. Demote current champion to `role = 'challenger'`
3. Copy challenger's `prediction_profile_configs` → `scoring_weight_overrides` (so existing code picks them up)
4. Set challenger's `role = 'champion'`, `is_production = true`
5. Log the promotion event in `profile_weight_history`
6. Old champion keeps its predictions and learning history intact

**No automatic promotion.** The UI shows the comparison; Lou makes the call.

---

## 5. Frontend Design

### 5.1 Navigation

Add under the **System** nav group:

```typescript
{ href: '/profiles', label: 'Prediction Profiles' },
```

### 5.2 Profiles List Page (`/profiles`)

A table showing all profiles with key metrics:

| Column | Source |
|--------|--------|
| Name | `prediction_profiles.name` |
| Role | Champion badge / Challenger badge |
| Enabled | Toggle |
| Learning | Adaptive / Static badge |
| Total Predictions | Count from `prediction_candidates` |
| Evaluated | Count where `status = 'evaluated'` |
| Bull Accuracy | `bullish_correct / bullish_total` from latest snapshot |
| Bear Accuracy | `bearish_correct / bearish_total` from latest snapshot |
| Avg Confidence | From latest snapshot |
| Expected Value | From latest snapshot |
| Avg Return | From latest snapshot |
| Last Run | Most recent prediction `created_at` |

Actions: Create Profile, Clone Profile buttons.

### 5.3 Profile Detail Page (`/profiles/[id]`)

Four tabs:

**Configuration Tab**
- Bucket weights editor: slider or number input for each of the 8 bucket weights
- Decision thresholds: `min_edge_margin`, `min_score_for_direction`, `min_ratio_for_direction`
- Calibration: `calibration_factor`, `risk_cap_boost`, `research_universe_weight`
- Visual diff against production weights (highlight what's different)
- "Reset to Production" button

**Performance Tab**
- Accuracy over time chart (line chart, daily snapshots)
- Bull/Bear/Neutral accuracy breakdown
- Confidence calibration chart (predicted vs actual by confidence bucket)
- Expected Value trend
- Win/Loss pie chart
- Prediction distribution by type (bullish/bearish/neutral)

**Weight History Tab** (adaptive profiles only)
- Timeline of weight changes
- Chart showing each weight's value over time
- Reason column from learning engine

**Predictions Tab**
- Filterable table of this profile's predictions
- Columns: Date, Ticker, Direction, Confidence, EV, Target, Stop, Outcome, Return %
- Filters: date range, ticker, direction, confidence range, outcome

### 5.4 Profile Comparison Page (`/profiles/compare`)

Select 2-4 profiles to compare side-by-side.

| Metric | Production | No Technicals | Reduced Technicals |
|--------|-----------|---------------|-------------------|
| Bull Accuracy | 42.6% | — | — |
| Bear Accuracy | 22.2% | — | — |
| Avg Confidence | 58 | — | — |
| Expected Value | 0.3% | — | — |
| Avg Return | 0.8% | — | — |
| Total Predictions | 61 | — | — |
| Win Rate | 45% | — | — |

Plus overlay charts for accuracy over time, EV over time.

### 5.5 Profile Management

- **Create**: Name, description, learning enabled toggle. Starts with production defaults (no config overrides).
- **Clone**: Copy all `prediction_profile_configs` from source profile. New name required.
- **Enable/Disable**: Toggle `is_enabled`. Disabled profiles are skipped during morning scan.
- **Delete**: Blocked for champion. Cascades to `prediction_profile_configs`, `profile_learning_snapshots`, `profile_weight_history`. Predictions remain (orphaned `profile_id` is fine — they're historical data).
- **Promote**: Confirmation dialog showing current champion vs challenger metrics. Manual action only.

---

## 6. Implementation Phases

### Phase 1 — MVP (Backend Core)
**Goal**: Profiles exist, production profile is seeded, predictions are tagged.

1. Create database tables: `prediction_profiles`, `prediction_profile_configs`, `profile_learning_snapshots`, `profile_weight_history`
2. Add `profile_id` and `profile_name` columns to `prediction_candidates`
3. Seed "Production" profile, backfill existing predictions
4. Add `ProfileId`/`ProfileName` to `PredictionCandidate` model and `SharedPredictionContext`
5. Add `PredictionProfileRepository` with CRUD operations
6. Modify `PreloadSharedContextAsync` → `PreloadProfileContextsAsync`
7. Modify morning scan to loop over enabled profiles
8. Add profile API endpoints (list, get, create, update, delete, clone)
9. Update prediction save to include `profile_id`, `profile_name`

**Deliverable**: System generates predictions tagged with the Production profile. Creating a challenger profile via API causes it to also generate predictions with that profile's weights.

### Phase 2 — Learning Isolation
**Goal**: Each profile learns independently.

1. Add `RunProfileLearningCycleAsync(profileId)` to `LearningEngine`
2. Filter prediction queries by `profile_id` in learning pipeline
3. Write challenger weight adjustments to `prediction_profile_configs`
4. Save `profile_learning_snapshots` after each cycle
5. Record weight changes in `profile_weight_history`
6. Skip learning for static profiles (`learning_enabled = false`)

**Deliverable**: Adaptive challenger profiles evolve independently. Static profiles maintain frozen weights.

### Phase 3 — Frontend
**Goal**: Full management UI.

1. Profiles list page with summary metrics
2. Profile detail page with configuration editor
3. Performance charts (accuracy over time, calibration, EV)
4. Weight history timeline
5. Profile comparison view
6. Create/Clone/Delete/Promote actions
7. Nav integration

### Phase 4 — Polish & Advanced Features
**Goal**: Production-ready refinements.

1. Prediction Explorer with profile filter
2. Confusion matrix per profile
3. API cost tracking per profile (how many extra API calls challengers add — though with shared snapshots, this is just OpenAI explanation cost)
4. Auto-disable profiles that haven't improved after N days
5. Profile export/import (JSON config)

---

## 7. Architectural Risks & Mitigations

### Risk 1: Morning Scan Duration
**Problem**: Each additional profile runs the scoring engine again per ticker. At 150 tickers × 3 profiles, that's 450 scoring runs instead of 150.

**Mitigation**: Scoring is pure computation (no API calls) — it's sub-millisecond per ticker. The expensive part (API calls for snapshots) is shared. Even 5 profiles adds negligible time. The AI explanation call is the only costly per-profile operation, and we skip it for challengers.

### Risk 2: Database Growth
**Problem**: N profiles × 150 tickers = N×150 prediction rows per day instead of 150.

**Mitigation**: At 3 profiles × 150 tickers × 365 days = ~164K rows/year. Supabase handles this easily. Add `profile_id` index for query performance.

### Risk 3: Learning Engine Complexity
**Problem**: Running learning per profile means N× the learning computation.

**Mitigation**: Learning runs nightly during low-traffic hours. Each cycle is independent and idempotent. Profile learning can be parallelized (different profiles don't share state). The learning engine already runs in ~30 seconds — even 5× is fine.

### Risk 4: Dedup Conflicts
**Problem**: Current dedup checks `existingByTickerAndWindow` to prevent duplicate predictions. With profiles, the same ticker+window is expected to have multiple predictions (one per profile).

**Mitigation**: Dedup must be scoped by profile. The `existingByTickerAndWindow` lookup needs a `WHERE profile_id = @profileId` filter. This is a critical integration point — getting it wrong would cause profiles to block each other.

### Risk 5: Downstream Consumers
**Problem**: Portfolio orchestrator, stock candidate service, dynamic picks — these consume predictions and might pick up challenger predictions.

**Mitigation**: All downstream consumers should filter by `profile_id = champion_profile_id` (or `profile_name = 'Production'`). Until Phase 1 is complete and tested, this is the default behavior since existing predictions have no profile_id and the champion profile is the implicit default.

---

## 8. Example Profiles to Create Immediately

Based on the TwelveData analysis (65.7% accuracy without technicals vs 42.6% with):

| Profile | Learning | Config Overrides |
|---------|----------|-----------------|
| Production | Adaptive | (current weights — no overrides) |
| No Technicals | Static | `trend=0`, `momentum=0`, `volume=0` |
| Reduced Technicals | Adaptive | `trend=0.3`, `momentum=0.3`, `volume=0.2` |
| Fundamentals Boost | Adaptive | `catalyst=1.5`, `research_signal=1.3`, `trend=0.5`, `momentum=0.5` |

After 2 weeks of parallel data, compare accuracy/EV across profiles and promote the winner.

---

## 9. Files Modified (Estimated)

### Backend (Phase 1 + 2)
- `Models/ResearchEngineModels.cs` — Add `ProfileId`, `ProfileName` to `PredictionCandidate`; add `PredictionProfile` model
- `Models/PredictionProfileModels.cs` — New file: profile, config, snapshot, weight history models
- `Services/Supabase/PredictionProfileRepository.cs` — New file: CRUD for profiles, configs, snapshots, weight history
- `Services/Supabase/ResearchRepository.cs` — Add profile filter to prediction queries used by learning
- `Services/ResearchEngine/PredictionGenerator.cs` — `PreloadProfileContextsAsync`, add profile to `SharedPredictionContext`
- `Services/ResearchEngine/DailyResearchRunService.cs` — Loop over profiles in morning scan
- `Services/ResearchEngine/LearningEngine.cs` — Add `RunProfileLearningCycleAsync`
- `Controllers/PredictionProfileController.cs` — New file: API endpoints
- `Program.cs` — Register new repository

### Frontend (Phase 3)
- `app/profiles/page.tsx` — New: profiles list
- `app/profiles/[id]/page.tsx` — New: profile detail
- `app/profiles/compare/page.tsx` — New: comparison view
- `components/navItems.tsx` — Add profiles nav entry
- `app/api/profiles/route.ts` — Proxy to backend

### Database
- Migration: 4 new tables, 2 new columns on `prediction_candidates`, indexes, seed data

---

*Cross-references: [CHECKLIST.md](CHECKLIST.md) · [PRODUCT_VISION.md](PRODUCT_VISION.md) · [ROADMAP.md](ROADMAP.md)*
