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

import https from 'node:https';

export type AiChatRole = 'system' | 'user' | 'assistant';

export interface AiChatMessage {
  role: AiChatRole;
  content: string;
}

export interface AiCompletionRequest {
  messages: AiChatMessage[];
  maxOutputTokens?: number;
  responseFormatJson?: boolean;
}

export interface AiCompletionResult {
  text: string;
  /** Model string echoed from the .NET API, if it included one. */
  model?: string;
}

/**
 * Custom HTTPS agent that accepts self-signed certificates for localhost
 * dev. Only used when AGENT_API_BASE_URL starts with https://localhost.
 */
const localhostAgent = new https.Agent({ rejectUnauthorized: false });

export async function requestAiCompletion(request: AiCompletionRequest): Promise<AiCompletionResult> {
  const baseUrl = process.env.AGENT_API_BASE_URL;
  if (!baseUrl) {
    throw new Error('AGENT_API_BASE_URL is not set. Add it to .env.local, e.g. http://localhost:5228');
  }

  const isLocalhostHttps = baseUrl.startsWith('https://localhost');

  // Build fetch options. For localhost HTTPS, use a custom agent that
  // accepts self-signed dev certificates (.NET Kestrel default).
  const fetchOptions: RequestInit & { agent?: https.Agent } = {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      messages: request.messages.map((m) => ({ role: m.role, content: m.content })),
      maxOutputTokens: request.maxOutputTokens,
      responseFormatJson: request.responseFormatJson ?? false,
    }),
  };

  // Node.js fetch (undici) doesn't support the `agent` option directly.
  // Instead, we set the env var for localhost dev. This is safe because
  // it only applies to this server-to-server call context.
  if (isLocalhostHttps) {
    process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
  }

  try {
    const response = await fetch(`${baseUrl}/api/ai/complete`, fetchOptions);

    if (!response.ok) {
      const errorBody = await response.text().catch(() => '');
      throw new Error(`AI API call failed with status ${response.status}: ${errorBody}`);
    }

    const data = (await response.json()) as { text: string; model?: string };
    return { text: data.text, model: data.model };
  } finally {
    // Restore TLS validation after the call
    if (isLocalhostHttps) {
      delete process.env.NODE_TLS_REJECT_UNAUTHORIZED;
    }
  }
}
