/**
 * Contract between the frontend chat service and the server-side
 * /api/agent-chat route. Shared by both sides so they can't drift apart.
 */

import { Pick } from './stockAgent';

export type AgentCardType = 'pick' | 'option' | 'catalyst';

/**
 * Catalyst items live in the information-intake layer (RSS-backed, with a
 * server-only cache), not in a static client-fetchable mock file like picks
 * or options. So unlike 'option' cards (which only carry an id, resolved
 * client-side via signalsService — currently always empty, mock disabled),
 * 'catalyst' and 'pick' cards carry their display data inline — the
 * frontend never needs a separate client-side lookup for either.
 */
export interface AgentCardCatalystData {
  title: string;
  sourceName: string;
  url: string;
  publishedAt: string;
  sentiment: string;
  tickers: string[];
  importanceScore: number;
}

export interface AgentCard {
  type: AgentCardType;
  id: string;
  catalyst?: AgentCardCatalystData;
  /** Full pick data, inline — set only for type 'pick'. Real Supabase-saved picks have ids the client can't resolve via mock data, so this avoids needing a client-side lookup at all. */
  pick?: Pick;
}

export interface AgentChatHistoryItem {
  role: 'user' | 'agent';
  text: string;
}

export interface AgentChatRequestBody {
  message: string;
  ticker?: string;
  history?: AgentChatHistoryItem[];
}

export type DataConfidenceLevel = 'high' | 'medium' | 'low';

/** Diagnostic metadata returned alongside every agent-chat response so the
 *  frontend (and the developer) can tell exactly which provider answered. */
export interface AgentDiagnostics {
  /** Which system produced the final answer: 'dotnet-api' if the .NET backend
   *  responded, 'client-fallback' if the client-side rule-based mock kicked in. */
  provider: 'dotnet-api' | 'client-fallback' | 'unknown';
  /** Model string echoed back from the .NET API, if available. */
  model?: string;
  /** True when the client-side sendAgentMessageMock handled the request. */
  usedFallback: boolean;
  /** Whether the Next.js route attempted to call the .NET API. */
  dotnetApiAttempted: boolean;
  /** Whether that call succeeded (2xx). */
  dotnetApiSucceeded: boolean;
  /** AGENT_API_BASE_URL is set in the environment (value not exposed). */
  agentApiConfigured: boolean;
}

export interface AgentChatApiResponse {
  message: string;
  dataConfidence: DataConfidenceLevel;
  cards: AgentCard[];
  suggestedPrompts: string[];
  riskWarnings: string[];
  /** Only populated on a real Supabase save failure, not the expected "not configured yet" state. */
  persistenceWarnings: string[];
  /** The saved chat_messages row id for this assistant reply, if Supabase is configured — used to attach feedback. */
  chatMessageId?: string;
  /** Diagnostic metadata — always present so the UI can show provider info. */
  diagnostics?: AgentDiagnostics;
}
