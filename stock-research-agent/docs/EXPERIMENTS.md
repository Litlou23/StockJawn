# STOCKJAWN — Experiment Log

> A living record of research experiments. The goal is to document hypotheses,
> track results, and build institutional knowledge about what works and what doesn't.
>
> Every experiment should ultimately answer: "Does this change help grow the portfolio?"
>
> See [PRODUCT_VISION.md](PRODUCT_VISION.md) for what constitutes a meaningful improvement.

---

## How to Use This Log

1. Before starting an experiment, write the hypothesis and success metrics.
2. Update the status as work progresses.
3. Record results honestly — negative results are valuable.
4. Write lessons learned even if the experiment is abandoned.
5. Link to any code changes, config changes, or decisions that resulted.

**Status values:** `proposed` · `in-progress` · `completed` · `abandoned`

---

## EXP-001 — Cheap Options vs Expensive Options

| Field | Value |
|---|---|
| **ID** | EXP-001 |
| **Hypothesis** | Cheaper options contracts (< $1.00 premium) produce higher percentage returns but lower absolute returns and higher total-loss rate compared to contracts in the $1.00–$5.00 range. For a $100 account, the optimal zone is $0.50–$2.00 premiums. |
| **Motivation** | The portfolio starts at $100. Contract selection must balance affordability against quality. Cheap OTM options have high gamma but decay fast; slightly more expensive ATM/NTM options have better probability of profit. |
| **Status** | `proposed` |
| **Start Date** | — |
| **Success Metrics** | Compare win rate, average return %, average return $, and total-loss rate across premium buckets: < $0.50, $0.50–$1.00, $1.00–$2.00, $2.00–$5.00. Determine which bucket maximizes expected value for a $100 account. |
| **Results** | — |
| **Lessons Learned** | — |
| **Next Steps** | — |

---

## EXP-002 — Holding Period Optimization

| Field | Value |
|---|---|
| **ID** | EXP-002 |
| **Hypothesis** | The current default holding period for predictions (variable, typically 5–14 days) is not optimized. There exists a sweet spot where prediction accuracy is highest relative to holding cost (theta decay for options, opportunity cost for stocks). |
| **Motivation** | Holding too long increases exposure to mean reversion and theta decay. Exiting too early leaves gains on the table. The learning engine tracks outcomes but doesn't yet analyze holding period as a variable. |
| **Status** | `proposed` |
| **Start Date** | — |
| **Success Metrics** | For completed predictions, bucket by actual holding period (1–3d, 4–7d, 8–14d, 15–30d). Compare accuracy, average return, and max adverse excursion per bucket. Identify the period with highest risk-adjusted return. |
| **Results** | — |
| **Lessons Learned** | — |
| **Next Steps** | — |

---

## EXP-003 — Aggressive vs Conservative Portfolio Profiles

| Field | Value |
|---|---|
| **ID** | EXP-003 |
| **Hypothesis** | Running two parallel portfolio profiles — one aggressive (higher confidence threshold, larger positions, options-heavy) and one conservative (lower threshold, smaller positions, stock-only) — will reveal which approach better achieves the $100→$1,000 objective. |
| **Motivation** | The system currently has a single implicit risk profile. A $100 account may need an aggressive early phase to build capital, then shift conservative to protect gains. This experiment tests both in parallel. |
| **Status** | `proposed` |
| **Start Date** | — |
| **Success Metrics** | After 30 simulated trading days, compare: total return, max drawdown, Sharpe ratio, longest losing streak, and probability of reaching $250 milestone. |
| **Results** | — |
| **Lessons Learned** | — |
| **Next Steps** | Requires position sizing engine (see [CHECKLIST.md](CHECKLIST.md) Critical) before this experiment can begin. |

---

## EXP-004 — News/Catalyst Signal Weighting

| Field | Value |
|---|---|
| **ID** | EXP-004 |
| **Hypothesis** | Catalyst events (earnings surprises, FDA approvals, M&A) produce a short-lived but strong directional signal. Increasing the catalyst scoring bucket weight by 50% for the first 48 hours after a catalyst event will improve short-term prediction accuracy without degrading longer-term predictions. |
| **Motivation** | The catalyst classifier already identifies and categorizes events, but the scoring engine applies a static weight. Market reactions to catalysts are strongest immediately after the event and decay quickly. The scoring engine should reflect this time-sensitivity. |
| **Status** | `proposed` |
| **Start Date** | — |
| **Success Metrics** | Compare prediction accuracy for catalyst-driven predictions (within 48h of event) before and after weight increase. Target: ≥5% accuracy improvement without >2% degradation on non-catalyst predictions. |
| **Results** | — |
| **Lessons Learned** | — |
| **Next Steps** | — |

---

## EXP-005 — Confidence Threshold Optimization

| Field | Value |
|---|---|
| **ID** | EXP-005 |
| **Hypothesis** | The system currently generates predictions across the full confidence range. Setting a minimum confidence threshold for trade entry will improve win rate and expected value, even though it reduces trade volume. The optimal threshold is likely between 65% and 75%. |
| **Motivation** | Low-confidence predictions dilute portfolio returns. If the system is poorly calibrated (e.g., 60%-confidence predictions only win 45% of the time), then filtering them out directly improves performance. This experiment finds the right cutoff. |
| **Status** | `proposed` |
| **Start Date** | — |
| **Success Metrics** | Using historical prediction_outcomes data, simulate portfolio performance at thresholds of 50%, 55%, 60%, 65%, 70%, 75%, 80%. Measure: trade count, win rate, average return, max drawdown, and terminal portfolio value for each threshold. |
| **Results** | — |
| **Lessons Learned** | — |
| **Next Steps** | Can be run as a backtest against existing data — no code changes required to begin. |

---

## Template

Copy this template for new experiments:

```markdown
## EXP-XXX — Title

| Field | Value |
|---|---|
| **ID** | EXP-XXX |
| **Hypothesis** | State what you believe will happen and why. |
| **Motivation** | Why is this worth testing? What problem does it address? |
| **Status** | `proposed` |
| **Start Date** | — |
| **Success Metrics** | How will you know if the hypothesis is correct? Be specific and quantitative. |
| **Results** | — |
| **Lessons Learned** | — |
| **Next Steps** | — |
```

---

*Cross-references: [PRODUCT_VISION.md](PRODUCT_VISION.md) · [ROADMAP.md](ROADMAP.md) · [CHECKLIST.md](CHECKLIST.md) · [PRODUCT_IDEAS.md](PRODUCT_IDEAS.md) · [DECISIONS.md](DECISIONS.md) · [GLOSSARY.md](GLOSSARY.md) · [DATA_MODEL.md](DATA_MODEL.md) · [AGENTS.md](../AGENTS.md)*
