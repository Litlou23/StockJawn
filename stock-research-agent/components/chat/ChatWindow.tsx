'use client';

import { useEffect, useRef, useState, useCallback } from 'react';
import { ChatMessage } from '@/types/stockAgent';
import { sendAgentMessage } from '@/services/agentChatService';
import { getPickById } from '@/services/picksService';
import { getOptionsSignalById } from '@/services/signalsService';
import ChatMessageBubble, { DisplayMessage } from './ChatMessageBubble';
import ChatComposer from './ChatComposer';
import ChatLayout from './ChatLayout';
import SuggestedPrompts from './SuggestedPrompts';
import TypingIndicator from './TypingIndicator';

const INITIAL_PROMPTS = [
  'What should I check this morning?',
  'What catalysts matter today?',
  'Show options setups worth reviewing.',
  'What has the strongest risk/reward?',
  'What should I avoid today?',
  'Compare AMD and NVDA.',
];

const STORAGE_KEY = 'agent-chat-history';

function timestamp(): string {
  return new Date().toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
}

let messageCounter = 0;
function nextId(): string {
  messageCounter += 1;
  return `msg-${messageCounter}`;
}

function loadMessages(): DisplayMessage[] {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as DisplayMessage[];
    // Restore the counter so new IDs don't collide
    for (const m of parsed) {
      const num = parseInt(m.id.replace('msg-', ''), 10);
      if (!isNaN(num) && num > messageCounter) messageCounter = num;
    }
    return parsed;
  } catch {
    return [];
  }
}

function saveMessages(messages: DisplayMessage[]) {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(messages));
  } catch {
    // sessionStorage full or unavailable -- silently ignore
  }
}

export default function ChatWindow({ initialMessages = [] }: { initialMessages?: ChatMessage[] }) {
  // Start with initialMessages for SSR; hydrate from sessionStorage on mount.
  const [messages, setMessages] = useState<DisplayMessage[]>(initialMessages);
  const [isThinking, setIsThinking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const bottomRef = useRef<HTMLDivElement>(null);
  const hydratedRef = useRef(false);

  // On first client mount, restore from sessionStorage if available
  useEffect(() => {
    if (!hydratedRef.current) {
      hydratedRef.current = true;
      const stored = loadMessages();
      if (stored.length > 0) setMessages(stored);
    }
  }, []);

  // Persist messages to sessionStorage whenever they change
  useEffect(() => {
    if (hydratedRef.current) saveMessages(messages);
  }, [messages]);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages, isThinking]);

  const handleSend = useCallback(async (text: string) => {
    setError(null);

    const userMessage: ChatMessage = {
      id: nextId(),
      role: 'user',
      text,
      timestamp: timestamp(),
    };
    setMessages((prev) => [...prev, userMessage]);
    setIsThinking(true);

    try {
      const response = await sendAgentMessage(text, messages);
      const [picks, options] = await Promise.all([
        response.picks && response.picks.length > 0
          ? Promise.resolve(response.picks)
          : response.pickIds
            ? Promise.all(response.pickIds.map((id) => getPickById(id))).then((r) => r.filter((p) => p !== undefined))
            : Promise.resolve(undefined),
        response.optionsSignalIds
          ? Promise.all(response.optionsSignalIds.map((id) => getOptionsSignalById(id))).then((r) =>
              r.filter((o) => o !== undefined),
            )
          : Promise.resolve(undefined),
      ]);

      const agentMessage: DisplayMessage = {
        id: nextId(),
        role: 'agent',
        text: response.text,
        timestamp: timestamp(),
        action: response.action,
        pickIds: response.pickIds,
        optionsSignalIds: response.optionsSignalIds,
        suggestedPrompts: response.suggestedPrompts,
        picks,
        options,
        catalysts: response.catalysts,
        chatMessageId: response.chatMessageId,
        diagnostics: response.diagnostics,
      };

      setMessages((prev) => [...prev, agentMessage]);
    } catch (err) {
      console.error('ChatWindow: failed to get an agent response', err);
      setError("Something went wrong sending that message. Check your connection and try again.");
    } finally {
      setIsThinking(false);
    }
  }, [messages]);

  const handleNewChat = () => {
    setMessages([]);
    setError(null);
    try {
      sessionStorage.removeItem(STORAGE_KEY);
    } catch {
      // ignore
    }
  };

  return (
    <ChatLayout
      header={
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-base font-semibold text-zinc-100">Chat with your agent</h1>
            <p className="text-xs text-zinc-500">Stock &amp; options research -- not financial advice.</p>
          </div>
          {messages.length > 0 && (
            <button
              type="button"
              onClick={handleNewChat}
              className="shrink-0 rounded-lg border border-zinc-700 px-3 py-1.5 text-xs font-medium text-zinc-300 transition hover:border-zinc-500 hover:text-zinc-100"
            >
              New chat
            </button>
          )}
        </div>
      }
      composer={
        <>
          {error && (
            <p className="mx-auto mb-2 max-w-3xl text-center text-xs text-red-400">{error}</p>
          )}
          <ChatComposer onSend={handleSend} disabled={isThinking} />
        </>
      }
    >
      {messages.length === 0 && (
        <div className="flex flex-1 flex-col items-center justify-center gap-5 py-12 text-center">
          <div className="text-3xl">🤖</div>
          <div>
            <h2 className="text-lg font-semibold text-zinc-100">What are we researching today?</h2>
            <p className="mx-auto mt-1 max-w-sm text-sm text-zinc-500">
              Ask about today&apos;s watchlist, options setups, a specific ticker, risk, or past results.
            </p>
          </div>
          <SuggestedPrompts prompts={INITIAL_PROMPTS} onSelect={handleSend} />
        </div>
      )}

      {messages.map((message, index) => (
        <ChatMessageBubble
          key={message.id}
          message={message}
          onSelectPrompt={handleSend}
          showSuggestedPrompts={index === messages.length - 1}
        />
      ))}

      {isThinking && <TypingIndicator />}

      <div ref={bottomRef} />
    </ChatLayout>
  );
}
