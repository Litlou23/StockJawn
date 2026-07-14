/**
 * Slim system prompt and OpenAI tool/function definitions for the
 * tool-calling chat loop. The AI calls these tools to fetch only the
 * data it needs instead of receiving the entire database context.
 *
 * Token budget:
 *   - System prompt: ~800 tokens (down from ~5K)
 *   - Tool definitions: ~600 tokens
 *   - Each tool response: 200-500 tokens
 *   - Max 3 tool-call rounds
 */

// ---------------------------------------------------------------------------
// Slim system prompt (~800 tokens vs. the old ~5K)
// ---------------------------------------------------------------------------

export const SLIM_SYSTEM_PROMPT = `You are a skeptical, factual stock and options research assistant for a single private user. You are NOT a financial advisor.

RULES:
1. Base every answer ONLY on data returned by your tools. Never invent prices, signals, IV, Greeks, or news.
2. If data is missing, say what is missing — do not guess.
3. Do not agree just because the user sounds confident. Correct bad assumptions.
4. No hype. Never say "guaranteed", "easy money", "sure thing". Saying "no good setups today" is a valid answer.
5. For any stock/options idea, always include: evidence for, evidence against, what's missing, and what would invalidate it.
6. Label confidence: high (multiple fresh sources agree), medium (partial support), low (thin/contradictory data).
7. Options: be extra strict on IV, liquidity, bid-ask spread, theta decay, breakeven distance. A good stock idea can still be a bad options trade.
8. Never give trade instructions, position sizing, or recommend automatic trading.
9. This system is in LEARNING MODE — all candidates are paper-only, not actionable.
10. You can trigger actions: run morning scans, EOD reviews, learning updates, discovery scans, and recalibration. When the user asks you to run something, use the appropriate tool — don't just describe what they should do.
11. You can explain scoring breakdowns for any ticker and show which signal buckets (trend, momentum, volume, volatility, market context, catalyst, research signals) drove a prediction.
12. You can show trade setup performance — which combinations of signals historically produce positive expected value. Use get_setup_performance for this.
13. You can view and adjust system configuration like the calibration factor.
14. You can adjust scoring weight overrides for individual signal buckets (trend, momentum, volume, volatility, market_context, catalyst, learning, research_signal). Use update_weight_override to boost or dampen a signal's influence, and reset_weight_override to return it to baseline. Adjustment is clamped to ±20%.
15. You can check pipeline job statuses (get_job_statuses) and trigger discovery scans (run_discovery).
16. You can update stock candidate statuses — e.g. expire, close, or mark as watch_only (update_stock_eval).

RESPONSE FORMAT — JSON only, no markdown fences:
{"message": string, "dataConfidence": "high"|"medium"|"low", "suggestedPrompts": string[], "riskWarnings": string[], "thesis"?: {"ticker": string, "setupType"?: string, "thesisSummary": string, "bullishCase"?: string, "bearishCase"?: string, "invalidationPoint"?: string, "expectedTimeframe"?: "1d"|"5d"|"20d"|"60d"}}

For simple factual questions, keep it short. For analysis questions, use: Bottom line → Evidence supporting → Evidence against → Missing confirmation → Data confidence → Suggested next step.`;

// ---------------------------------------------------------------------------
// OpenAI function/tool definitions
// ---------------------------------------------------------------------------

export interface ChatToolDefinition {
  type: 'function';
  function: {
    name: string;
    description: string;
    parameters: Record<string, unknown>;
  };
}

export const CHAT_TOOL_DEFINITIONS: ChatToolDefinition[] = [
  {
    type: 'function',
    function: {
      name: 'get_dashboard_summary',
      description:
        'Get a high-level overview of the system: latest run stats, prediction/candidate/outcome counts, block reasons, mode. Call this first for broad questions like "what happened today?" or "give me a summary".',
      parameters: { type: 'object', properties: {}, required: [] },
    },
  },
  {
    type: 'function',
    function: {
      name: 'get_predictions',
      description:
        'Get AI predictions from the morning scan. Filter by ticker, prediction type (bullish/bearish/watch_only/neutral), or run_id. Use count_only=true for counts without details.',
      parameters: {
        type: 'object',
        properties: {
          ticker: { type: 'string', description: 'Filter to one ticker symbol' },
          prediction_type: {
            type: 'string',
            enum: ['bullish', 'bearish', 'neutral', 'watch_only', 'neutral_no_edge', 'neutral_range_bound'],
            description: 'Filter by prediction type',
          },
          run_id: { type: 'string', description: 'Specific run ID (defaults to latest)' },
          count_only: { type: 'boolean', description: 'Return only counts, not item details' },
          limit: { type: 'integer', description: 'Max items to return (default 10)' },
        },
        required: [],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'get_stock_candidates',
      description:
        'Get paper stock candidates with scoring, quality tier, and option eligibility. Filter by ticker, candidate_mode (learning/actionable_shadow/live_eligible), or quality_tier.',
      parameters: {
        type: 'object',
        properties: {
          ticker: { type: 'string', description: 'Filter to one ticker symbol' },
          candidate_mode: {
            type: 'string',
            enum: ['learning', 'actionable_shadow', 'live_eligible'],
          },
          quality_tier: {
            type: 'string',
            enum: ['very_weak', 'weak', 'medium', 'strong_paper', 'production_candidate'],
          },
          run_id: { type: 'string', description: 'Specific run ID (defaults to latest)' },
          count_only: { type: 'boolean' },
          limit: { type: 'integer', description: 'Max items (default 10)' },
        },
        required: [],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'get_option_candidates',
      description:
        'Get paper option candidates with contract details, Greeks, and scoring. Also returns block reasons for options that were rejected. Filter by ticker or option side (call/put).',
      parameters: {
        type: 'object',
        properties: {
          ticker: { type: 'string', description: 'Filter to one ticker symbol' },
          option_type: { type: 'string', enum: ['call', 'put'], description: 'Filter by option side' },
          count_only: { type: 'boolean' },
          include_block_reasons: {
            type: 'boolean',
            description: 'Include reasons why options were blocked',
          },
          limit: { type: 'integer', description: 'Max items (default 10)' },
        },
        required: [],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'get_ticker_detail',
      description:
        'Get all data for a single ticker: prediction, stock candidate, option candidate (or block reason), and outcome. Use when the user asks about a specific stock.',
      parameters: {
        type: 'object',
        properties: {
          ticker: { type: 'string', description: 'Ticker symbol (e.g. AAPL)' },
        },
        required: ['ticker'],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'get_setup_performance',
      description:
        'Get trade setup performance statistics — which combinations of signals have historically worked. Filter by "top" (positive EV), "degraded" (recently declining), or "negative" (losing setups). Use for questions about which setups work, setup win rates, expected value.',
      parameters: {
        type: 'object',
        properties: {
          filter: {
            type: 'string',
            enum: ['top', 'degraded', 'negative'],
            description: 'Filter setups: top (positive EV), degraded (declining), negative (losing)',
          },
          limit: { type: 'integer', description: 'Max items (default 10)' },
        },
        required: [],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'get_learning_stats',
      description:
        'Get learning engine statistics: signal performance, confidence calibration analysis, weight overrides, and calibration factor. Use for questions about system accuracy, overconfidence, signal reliability, or weight adjustments.',
      parameters: { type: 'object', properties: {}, required: [] },
    },
  },
  {
    type: 'function',
    function: {
      name: 'run_learning_update',
      description:
        'Trigger a full learning cycle: compute signal performance, recalibrate confidence, optimize weights, compute setup analytics, generate insights. Use when asked to "run learning", "update weights", "recalibrate", or "learn from recent data".',
      parameters: { type: 'object', properties: {}, required: [] },
    },
  },
  {
    type: 'function',
    function: {
      name: 'run_morning_scan',
      description:
        'Trigger the morning scan pipeline: generate predictions, create stock/option candidates, classify trade setups. Use when asked to "run the scan", "generate predictions", or "start morning picks".',
      parameters: { type: 'object', properties: {}, required: [] },
    },
  },
  {
    type: 'function',
    function: {
      name: 'run_eod_review',
      description:
        'Trigger end-of-day evaluation: evaluate stock/option outcomes, resolve trade setups, close portfolio positions. Use when asked to "run EOD", "evaluate outcomes", or "check results".',
      parameters: { type: 'object', properties: {}, required: [] },
    },
  },
  {
    type: 'function',
    function: {
      name: 'explain_scoring',
      description:
        'Get a detailed scoring breakdown for a specific ticker: every bucket score (trend, momentum, volume, volatility, market context, catalyst, research signals), confirmation multiplier, data quality, calibration, confidence caps, setup fingerprint and historical performance. Use when asked "why did X get this score?" or "explain the prediction for X".',
      parameters: {
        type: 'object',
        properties: {
          ticker: { type: 'string', description: 'Ticker symbol (e.g. AAPL)' },
        },
        required: ['ticker'],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'get_ticker_accuracy',
      description:
        'Get per-ticker historical prediction accuracy with per-bucket breakdown and reliability factor. Shows win/loss record, which signal buckets (trend, momentum, volume, etc.) are weakest for that ticker, and the Bayesian-smoothed reliability factor that adjusts confidence. Use for questions like "how accurate are we on UBER?", "why do we keep getting UBER wrong?", or "which tickers do we predict worst?"',
      parameters: {
        type: 'object',
        properties: {
          ticker: { type: 'string', description: 'Ticker symbol to look up (e.g. UBER). Omit for all tickers.' },
          limit: { type: 'integer', description: 'Max items to return (default 10)' },
        },
        required: [],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'get_config',
      description:
        'View current system configuration: calibration factor, weight overrides, thresholds for weight adjustment, setup detection parameters. Use when asked about system settings or "what are the current thresholds?".',
      parameters: { type: 'object', properties: {}, required: [] },
    },
  },
  {
    type: 'function',
    function: {
      name: 'update_config',
      description:
        'Change a system configuration value. Currently supports: calibration_factor (0.85-1.15). Use when asked to "set calibration to X" or "adjust the confidence dampening".',
      parameters: {
        type: 'object',
        properties: {
          setting: { type: 'string', description: 'Config setting name (e.g. calibration_factor)' },
          value: { type: 'number', description: 'New value for the setting' },
        },
        required: ['setting', 'value'],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'update_weight_override',
      description:
        'Adjust the scoring weight for a specific signal bucket. Use when the user says things like "boost momentum", "dampen volatility", "increase trend weight by 10%", or "set catalyst adjustment to +15%". Adjustment is clamped to ±20%. Valid signals: trend, momentum, volume, volatility, market_context, catalyst, learning, research_signal.',
      parameters: {
        type: 'object',
        properties: {
          signal_name: {
            type: 'string',
            enum: ['trend', 'momentum', 'volume', 'volatility', 'market_context', 'catalyst', 'learning', 'research_signal'],
            description: 'The signal bucket to adjust',
          },
          adjustment_percent: {
            type: 'number',
            description: 'Adjustment as a decimal (-0.20 to 0.20). E.g. 0.10 means +10%, -0.05 means -5%.',
          },
          reason: {
            type: 'string',
            description: 'Why this adjustment is being made (for audit trail)',
          },
        },
        required: ['signal_name', 'adjustment_percent'],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'reset_weight_override',
      description:
        'Reset a signal weight override back to its default baseline, removing any manual adjustment. Use when asked to "reset momentum weight", "undo the volatility boost", or "clear all overrides" (call once per signal).',
      parameters: {
        type: 'object',
        properties: {
          signal_name: {
            type: 'string',
            enum: ['trend', 'momentum', 'volume', 'volatility', 'market_context', 'catalyst', 'learning', 'research_signal'],
            description: 'The signal bucket to reset',
          },
        },
        required: ['signal_name'],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'get_job_statuses',
      description:
        'Check the status of pipeline jobs (morning_scan, eod_review, learning, discovery, continuous_discovery). Shows whether jobs are running, completed, or failed, plus timing. Use when asked "is the scan running?", "did the EOD finish?", "what jobs ran today?", or "check job status".',
      parameters: {
        type: 'object',
        properties: {
          job_name: {
            type: 'string',
            description: 'Optional: specific job to check (e.g. morning_scan, eod_review, learning, discovery, continuous_discovery). Omit for all jobs.',
          },
        },
        required: [],
      },
    },
  },
  {
    type: 'function',
    function: {
      name: 'run_discovery',
      description:
        'Trigger a full universe discovery scan — searches all providers (FMP, Finnhub, RSS) for new research assets and tickers. Use when asked to "run discovery", "scan for new tickers", or "find new stocks". Runs in the background.',
      parameters: { type: 'object', properties: {}, required: [] },
    },
  },
  {
    type: 'function',
    function: {
      name: 'update_stock_eval',
      description:
        'Change the status of a stock candidate. Use when asked to "close the AAPL position", "expire TSLA candidate", "mark MSFT as evaluated", or "set NVDA to watch only". Valid statuses: open, evaluated, expired, watch_only, unavailable.',
      parameters: {
        type: 'object',
        properties: {
          ticker: { type: 'string', description: 'Ticker symbol (e.g. AAPL)' },
          status: {
            type: 'string',
            enum: ['open', 'evaluated', 'expired', 'watch_only', 'unavailable'],
            description: 'New status for the candidate',
          },
          reason: { type: 'string', description: 'Why the status is being changed (optional, for audit)' },
        },
        required: ['ticker', 'status'],
      },
    },
  },
];

// ---------------------------------------------------------------------------
// Helper: build the .NET chat-tools URL for a tool call
// ---------------------------------------------------------------------------

export function buildToolCallUrl(
  baseUrl: string,
  toolName: string,
  args: Record<string, unknown>,
): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(args)) {
    if (value !== undefined && value !== null && value !== '') {
      params.set(key, String(value));
    }
  }
  const qs = params.toString();
  return `${baseUrl}/api/chat-tools/${toolName}${qs ? `?${qs}` : ''}`;
}
