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
