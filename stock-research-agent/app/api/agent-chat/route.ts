import { NextRequest, NextResponse } from 'next/server';
import { AgentChatApiResponse, AgentChatRequestBody, AgentDiagnostics, DataConfidenceLevel } from '@/types/agentChat';
import { AiChatMessage, AiToolCall, requestAiCompletion } from '@/lib/ai/aiClient';
import { SLIM_SYSTEM_PROMPT, CHAT_TOOL_DEFINITIONS, buildToolCallUrl } from '@/lib/ai/chatToolDefinitions';
import { saveChatMessage } from '@/services/persistence/chatRepository';
import { saveThesis } from '@/services/persistence/learningRepository';
import { NOT_CONFIGURED, PersistenceResult } from '@/services/persistence/persistenceTypes';
import { ConfidenceLevel } from '@/types/learning';

export const runtime = 'nodejs';

const MAX_TOOL_ROUNDS = 3;
const MAX_OUTPUT_TOKENS = 900;

// ---------------------------------------------------------------------------
// Types for parsed AI response
// ---------------------------------------------------------------------------

interface ParsedAgentThesis {
  ticker: string;
  setupType?: string;
  thesisSummary: string;
  bullishCase?: string;
  bearishCase?: string;
  invalidationPoint?: string;
  expectedTimeframe?: '1d' | '5d' | '20d' | '60d';
}

interface ParsedAgentJson {
  message: string;
  dataConfidence?: string;
  suggestedPrompts?: string[];
  riskWarnings?: string[];
  thesis?: ParsedAgentThesis;
}

const VALID_CONFIDENCE_LEVELS: DataConfidenceLevel[] = ['high', 'medium', 'low'];

function normalizeConfidence(value: string | undefined): DataConfidenceLevel {
  return VALID_CONFIDENCE_LEVELS.includes(value as DataConfidenceLevel) ? (value as DataConfidenceLevel) : 'medium';
}

function parseAgentJson(raw: string): ParsedAgentJson {
  try {
    const parsed = JSON.parse(raw);
    if (parsed && typeof parsed.message === 'string') {
      return parsed as ParsedAgentJson;
    }
  } catch {
    // fall through to plain-text fallback
  }
  return { message: raw, dataConfidence: 'medium', suggestedPrompts: [], riskWarnings: [] };
}

function persistenceWarning(label: string, result: PersistenceResult): string | null {
  if (result.persisted) return null;
  if (result.reason === NOT_CONFIGURED.reason) return null;
  return `${label}: Supabase save failed (${result.reason ?? 'unknown reason'}).`;
}

// ---------------------------------------------------------------------------
// Execute a single tool call against the .NET chat-tools endpoints
// ---------------------------------------------------------------------------

async function executeToolCall(toolCall: AiToolCall): Promise<string> {
  const baseUrl = process.env.AGENT_API_BASE_URL;
  if (!baseUrl) return JSON.stringify({ error: 'AGENT_API_BASE_URL not set' });

  let args: Record<string, unknown> = {};
  try {
    args = JSON.parse(toolCall.arguments);
  } catch {
    return JSON.stringify({ error: 'Invalid tool arguments' });
  }

  const url = buildToolCallUrl(baseUrl, toolCall.name, args);

  const isLocalhostHttps = baseUrl.startsWith('https://localhost');
  if (isLocalhostHttps) {
    process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
  }

  try {
    const response = await fetch(url, {
      method: 'GET',
      headers: { 'Content-Type': 'application/json' },
    });

    if (!response.ok) {
      const errorText = await response.text().catch(() => '');
      return JSON.stringify({ error: `Tool ${toolCall.name} failed: ${response.status}`, details: errorText.slice(0, 200) });
    }

    return await response.text();
  } catch (err) {
    return JSON.stringify({ error: `Tool ${toolCall.name} fetch error: ${err instanceof Error ? err.message : 'unknown'}` });
  } finally {
    if (isLocalhostHttps) {
      delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
    }
  }
}

// ---------------------------------------------------------------------------
// POST /api/agent-chat
// ---------------------------------------------------------------------------

export async function POST(req: NextRequest) {
  let body: AgentChatRequestBody;
  try {
    body = await req.json();
  } catch {
    return NextResponse.json({ error: 'Invalid JSON body' }, { status: 400 });
  }

  const message = body.message?.trim();
  if (!message) {
    return NextResponse.json({ error: 'message is required' }, { status: 400 });
  }

  // Save user message (best-effort, non-blocking)
  const userSavePromise = saveChatMessage({ role: 'user', text: message });

  const agentApiConfigured = Boolean(process.env.AGENT_API_BASE_URL);

  // Build conversation history from client
  const historyMessages: AiChatMessage[] = (body.history ?? []).slice(-8).map((h) => ({
    role: h.role === 'agent' ? 'assistant' as const : 'user' as const,
    content: h.text,
  }));

  // Start with slim system prompt + history + user message
  const chatMessages: AiChatMessage[] = [
    { role: 'system', content: SLIM_SYSTEM_PROMPT },
    ...historyMessages,
    { role: 'user', content: message },
  ];

  try {
    let finalText = '';
    let toolRounds = 0;

    // Tool-calling loop: up to MAX_TOOL_ROUNDS
    while (toolRounds <= MAX_TOOL_ROUNDS) {
      const isLastRound = toolRounds === MAX_TOOL_ROUNDS;

      console.log(`[agent-chat] round ${toolRounds}, messages: ${chatMessages.length}`);

      const completion = await requestAiCompletion({
        messages: chatMessages,
        responseFormatJson: true,
        maxOutputTokens: MAX_OUTPUT_TOKENS,
        // Don't send tools on last round — force text response
        tools: isLastRound ? undefined : CHAT_TOOL_DEFINITIONS,
      });

      // If model returned text (no tool calls), we're done
      if (completion.finishReason !== 'tool_calls' || !completion.toolCalls?.length) {
        finalText = completion.text;
        break;
      }

      // Model wants to call tools
      console.log(`[agent-chat] tool calls: ${completion.toolCalls.map(tc => tc.name).join(', ')}`);

      // Add the assistant's tool-call message to the conversation
      chatMessages.push({
        role: 'assistant',
        content: '',
        toolCalls: completion.toolCalls,
      });

      // Execute all tool calls in parallel
      const toolResults = await Promise.all(
        completion.toolCalls.map(async (tc) => {
          const result = await executeToolCall(tc);
          return { toolCallId: tc.id, result };
        }),
      );

      // Add tool results to conversation
      for (const { toolCallId, result } of toolResults) {
        chatMessages.push({
          role: 'tool',
          content: result,
          toolCallId,
        });
      }

      toolRounds++;
    }

    // Parse the final text response
    const parsed = parseAgentJson(finalText);

    // Save assistant message
    const userSaveResult = await userSavePromise;
    const assistantSaveResult = await saveChatMessage({
      role: 'agent',
      text: parsed.message,
      suggestedPrompts: parsed.suggestedPrompts ?? [],
    });

    const persistenceWarnings = [
      persistenceWarning('User message', userSaveResult),
      persistenceWarning('Assistant message', assistantSaveResult),
    ].filter((w): w is string => Boolean(w));

    // Best-effort thesis capture
    if (parsed.thesis?.ticker && parsed.thesis.thesisSummary) {
      void saveThesis({
        ticker: parsed.thesis.ticker,
        setupType: parsed.thesis.setupType,
        thesisSummary: parsed.thesis.thesisSummary,
        bullishCase: parsed.thesis.bullishCase,
        bearishCase: parsed.thesis.bearishCase,
        invalidationPoint: parsed.thesis.invalidationPoint,
        expectedTimeframe: parsed.thesis.expectedTimeframe,
        confidenceAtCreation: normalizeConfidence(parsed.dataConfidence) as ConfidenceLevel,
        dataConfidenceAtCreation: normalizeConfidence(parsed.dataConfidence) as ConfidenceLevel,
        sourcesUsed: ['chat-tools'],
        missingDataWarnings: [],
        chatMessageId: assistantSaveResult.id,
      }).catch((err) => console.warn('agent-chat: thesis save failed', err));
    }

    const diagnostics: AgentDiagnostics = {
      provider: 'dotnet-api',
      model: undefined, // model info not needed in diagnostics
      usedFallback: false,
      dotnetApiAttempted: true,
      dotnetApiSucceeded: true,
      agentApiConfigured,
    };

    const responseBody: AgentChatApiResponse = {
      message: parsed.message,
      dataConfidence: normalizeConfidence(parsed.dataConfidence),
      cards: [], // cards no longer needed — data comes from tools
      suggestedPrompts: parsed.suggestedPrompts ?? [],
      riskWarnings: parsed.riskWarnings ?? [],
      persistenceWarnings,
      chatMessageId: assistantSaveResult.id,
      diagnostics,
    };

    return NextResponse.json(responseBody);
  } catch (err) {
    console.error('[agent-chat] failed:', err instanceof Error ? err.message : err);

    // Await user save so we don't leak
    await userSavePromise.catch(() => {});

    const failDiagnostics: AgentDiagnostics = {
      provider: 'unknown',
      usedFallback: false,
      dotnetApiAttempted: true,
      dotnetApiSucceeded: false,
      agentApiConfigured,
    };
    return NextResponse.json(
      { error: 'AI call failed', diagnostics: failDiagnostics },
      { status: 502 },
    );
  }
}
