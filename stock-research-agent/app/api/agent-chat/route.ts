import { NextRequest, NextResponse } from 'next/server';
import { AgentCard, AgentChatApiResponse, AgentChatRequestBody, AgentDiagnostics, DataConfidenceLevel } from '@/types/agentChat';
import { AGENT_CHAT_SYSTEM_PROMPT } from '@/lib/ai/prompts';
import { AiChatMessage, requestAiCompletion } from '@/lib/ai/aiClient';
import { AgentChatContext, buildAgentChatContext } from '@/services/serverContextBuilder';
import { saveChatMessage } from '@/services/persistence/chatRepository';
import { saveThesis } from '@/services/persistence/learningRepository';
import { NOT_CONFIGURED, PersistenceResult } from '@/services/persistence/persistenceTypes';
import { ExpectedTimeframe, ConfidenceLevel } from '@/types/learning';

export const runtime = 'nodejs';

function buildCardsFromContext(context: AgentChatContext): AgentCard[] {
  const cards: AgentCard[] = [];

  for (const pick of context.savedPicksContext.picks.slice(0, 5)) {
    cards.push({ type: 'pick', id: pick.id, pick });
  }

  for (const item of context.catalystContext.items.slice(0, 3)) {
    cards.push({
      type: 'catalyst',
      id: item.id,
      catalyst: {
        title: item.title,
        sourceName: item.sourceName,
        url: item.url,
        publishedAt: item.publishedAt,
        sentiment: item.sentiment,
        tickers: item.tickers,
        importanceScore: item.importanceScore,
      },
    });
  }

  return cards;
}

interface ParsedAgentThesis {
  ticker: string;
  setupType?: string;
  thesisSummary: string;
  bullishCase?: string;
  bearishCase?: string;
  invalidationPoint?: string;
  expectedTimeframe?: ExpectedTimeframe;
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
    // fall through to plain-text fallback below
  }
  return { message: raw, dataConfidence: 'medium', suggestedPrompts: [], riskWarnings: [] };
}

/** Only surface a warning for a real failure, not the expected "not configured yet" state. */
function persistenceWarning(label: string, result: PersistenceResult): string | null {
  if (result.persisted) return null;
  if (result.reason === NOT_CONFIGURED.reason) return null;
  return `${label}: Supabase save failed (${result.reason ?? 'unknown reason'}).`;
}

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

  const [context, userSaveResult] = await Promise.all([
    buildAgentChatContext(message, body.ticker),
    saveChatMessage({ role: 'user', text: message }),
  ]);

  const cards = buildCardsFromContext(context);

  const historyMessages: AiChatMessage[] = (body.history ?? []).slice(-8).map((h) => ({
    role: h.role === 'agent' ? 'assistant' : 'user',
    content: h.text,
  }));

  const chatMessages: AiChatMessage[] = [
    { role: 'system', content: AGENT_CHAT_SYSTEM_PROMPT },
    {
      role: 'system',
      content:
        'Structured app context (JSON) — this is the only data you may treat as fact. ' +
        'Each context bundle reports its own "source" (e.g. "supabase", "mock", "rss-live", "none") — ' +
        'treat "mock" and "missing" data as exactly that, never as confirmed real data. ' +
        `If something the user asks about is not in here, say so.\n${JSON.stringify(context)}`,
    },
    ...historyMessages,
    { role: 'user', content: message },
  ];

  const agentApiConfigured = Boolean(process.env.AGENT_API_BASE_URL);
  console.log('[agent-chat] diagnostics: AGENT_API_BASE_URL configured =', agentApiConfigured);

  try {
    console.log('[agent-chat] calling .NET API at', process.env.AGENT_API_BASE_URL ? '<set>' : '<NOT SET>');
    const completion = await requestAiCompletion({
      messages: chatMessages,
      responseFormatJson: true,
      maxOutputTokens: 900,
    });
    console.log('[agent-chat] .NET API call succeeded, model =', completion.model ?? 'not reported');

    const parsed = parseAgentJson(completion.text);

    const assistantSaveResult = await saveChatMessage({
      role: 'agent',
      text: parsed.message,
      pickIds: cards.filter((c) => c.type === 'pick').map((c) => c.id),
      catalystRefs: cards.filter((c) => c.type === 'catalyst').map((c) => c.catalyst),
      suggestedPrompts: parsed.suggestedPrompts ?? [],
    });

    const persistenceWarnings = [
      persistenceWarning('User message', userSaveResult),
      persistenceWarning('Assistant message', assistantSaveResult),
    ].filter((w): w is string => Boolean(w));

    if (persistenceWarnings.length > 0) {
      console.warn('agent-chat: Supabase persistence issue', persistenceWarnings);
    }

    // Best-effort thesis capture
    if (parsed.thesis?.ticker && parsed.thesis.thesisSummary) {
      const matchingPick = context.savedPicksContext.picks.find(
        (p) => p.ticker.toUpperCase() === parsed.thesis!.ticker.toUpperCase(),
      );
      void saveThesis({
        ticker: parsed.thesis.ticker,
        pickId: matchingPick?.id,
        setupType: parsed.thesis.setupType,
        thesisSummary: parsed.thesis.thesisSummary,
        bullishCase: parsed.thesis.bullishCase,
        bearishCase: parsed.thesis.bearishCase,
        invalidationPoint: parsed.thesis.invalidationPoint,
        expectedTimeframe: parsed.thesis.expectedTimeframe,
        confidenceAtCreation: normalizeConfidence(parsed.dataConfidence) as ConfidenceLevel,
        dataConfidenceAtCreation: normalizeConfidence(parsed.dataConfidence) as ConfidenceLevel,
        sourcesUsed: [
          context.savedPicksContext.source,
          context.catalystContext.source,
          context.optionsContext.status,
        ],
        missingDataWarnings: context.dataQualityContext.warnings,
        chatMessageId: assistantSaveResult.id,
      }).catch((err) => console.warn('agent-chat: thesis save failed', err));
    }

    const diagnostics: AgentDiagnostics = {
      provider: 'dotnet-api',
      model: completion.model,
      usedFallback: false,
      dotnetApiAttempted: true,
      dotnetApiSucceeded: true,
      agentApiConfigured,
    };

    const responseBody: AgentChatApiResponse = {
      message: parsed.message,
      dataConfidence: normalizeConfidence(parsed.dataConfidence),
      cards,
      suggestedPrompts: parsed.suggestedPrompts ?? [],
      riskWarnings: parsed.riskWarnings ?? [],
      persistenceWarnings,
      chatMessageId: assistantSaveResult.id,
      diagnostics,
    };
    return NextResponse.json(responseBody);
  } catch (err) {
    console.error('[agent-chat] .NET API call FAILED:', err instanceof Error ? err.message : err);
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
