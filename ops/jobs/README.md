# StockJawn Scheduled Tasks (ops/jobs)

Source of truth for all Claude scheduled task prompts. Both Claude accounts read from here.

## Active Recurring Tasks

| Task | Schedule | Status | Description |
|------|----------|--------|-------------|
| [premarket-picks](premarket-picks.md) | 8:08 AM Mon-Fri | DISABLED | Research & log 2-3 options picks before market open |
| [eod-pick-evaluation](eod-pick-evaluation.md) | 4:22 PM Mon-Fri | ENABLED | Grade picks, update factor performance, adjust weights |
| [trade-executor](trade-executor.md) | — | RETIRED | Replaced by StockJawn's API executor (polls every 30s, waits for triggers). Disable the Claude task. |
| [nightly-research](nightly-research.md) | 9 PM Sun-Thu | ENABLED | Scan for next-day candidates (PEAD, initiations, congress, index) |
| [weekly-learning-review](weekly-learning-review.md) | 10:13 AM Saturday | ENABLED | Deep analysis of all historical picks, tune scoring weights |

## One-Time Tasks (Disabled)

| Task | Description |
|------|-------------|
| check-nightly-research | One-time check of nightly research output |
| ccl-earnings-reminder | One-time CCL earnings reminder |
| nightly-research-phone-alert | One-time notification test |

## Task Flow

```
nightly-research (9 PM)
    |
    v
premarket-picks (8:08 AM) -- reads nightly research candidates
    |
    v
[Lou approves on mobile] -- PIN-verified approval page
    |
    v
StockJawn API executor (every 30s) -- waits for each pick's trigger level, then places the order
    |
    v
eod-pick-evaluation (4:22 PM) -- grades results, updates learning loop
    |
    v
weekly-learning-review (Saturday) -- deep pattern analysis, weight tuning
```

## Key Infrastructure

- **Robinhood Agentic Account:** the one `get_accounts` shows with agentic_allowed = true
- **Supabase Project:** pizoqybgkdhfvxrmnhvx
- **Push Notifications:** ntfy.sh topic `stockjawn-picks-7428`
- **Approval Page:** yvyofficial.com (Netlify)
- **API:** Azure App Service (FREE tier)

## Editing Tasks

These files are the canonical prompts. To update a scheduled task:
1. Edit the `.md` file in this directory
2. Commit and push
3. Update the Claude scheduled task prompt to match (or paste from here)

Both Claude accounts (Pro account = source of truth, second account = executor) should reference these files.
