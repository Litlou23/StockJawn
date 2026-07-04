import 'server-only';

/**
 * Server-only seam for calling the AI provider. The actual OpenAI call (and
 * the OPENAI_API_KEY) lives in a separate .NET API (stock-research-agent-api),
 * not in this Next.js app. This file just forwards the already-built message
 * list to that .NET API over HTTP, server-to-server.
 *
 * Never import this file from a client component ("use client") or from
 * agentChatService.ts's client-facing path -- AGENT_API_BASE_URL is a
 * server-only env var and this performs a server-to-server call.
 */

export type AiChatRole = 'system' | 'user' | 'assistant' | 'tool';

export interface AiToolCall {
  id: string;
  name: string;
  arguments: string; // JSON string
}

export interface AiChatMessage {
  role: AiChatRole;
  content: string;
  /** Set when role is 'tool' to tie a result back to a tool call. */
  toolCallId?: string;
  /** Set when role is 'assistant' and model requested tool calls. */
  toolCalls?: AiToolCall[];
}

export interface AiToolDefinition {
  type: 'function';
  function: {
    name: string;
    description: string;
    parameters: Record<string, unknown>;
  };
}

export interface AiCompletionRequest {
  messages: AiChatMessage[];
  maxOutputTokens?: number;
  responseFormatJson?: boolean;
  tools?: AiToolDefinition[];
}

export interface AiCompletionResult {
  text: string;
  /** Model string echoed from the .NET API, if it included one. */
  model?: string;
  /** Non-null when the model wants to call tools. */
  toolCalls?: AiToolCall[];
  /** 'stop' for normal text, 'tool_calls' when the model wants tool results. */
  finishReason?: string;
}

export async function requestAiCompletion(request: AiCompletionRequest): Promise<AiCompletionResult> {
  const baseUrl = process.env.AGENT_API_BASE_URL;
  if (!baseUrl) {
    throw new Error('AGENT_API_BASE_URL is not set. Add it to .env.local, e.g. http://localhost:5228');
  }

  const isLocalhostHttps = baseUrl.startsWith('https://localhost');

  const body: Record<string, unknown> = {
    messages: request.messages.map((m) => ({
      role: m.role,
      content: m.content,
      toolCallId: m.toolCallId,
      toolCalls: m.toolCalls?.map((tc) => ({
        id: tc.id,
        name: tc.name,
        arguments: tc.arguments,
      })),
    })),
    maxOutputTokens: request.maxOutputTokens,
    responseFormatJson: request.responseFormatJson ?? false,
  };

  if (request.tools && request.tools.length > 0) {
    body.tools = request.tools;
  }

  const fetchOptions: RequestInit = {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  };

  if (isLocalhostHttps) {
    process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
  }

  try {
    const response = await fetch(`${baseUrl}/api/ai/complete`, fetchOptions);

    if (!response.ok) {
      const errorBody = await response.text().catch(() => '');
      throw new Error(`AI API call failed with status ${response.status}: ${errorBody}`);
    }

    const data = (await response.json()) as {
      text: string;
      model?: string;
      toolCalls?: AiToolCall[];
      finishReason?: string;
    };
    return {
      text: data.text,
      model: data.model,
      toolCalls: data.toolCalls,
      finishReason: data.finishReason,
    };
  } finally {
    if (isLocalhostHttps) {
      delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
    }
  }
}
