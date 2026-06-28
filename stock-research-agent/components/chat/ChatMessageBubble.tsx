import { AgentAction, AgentResponseCatalyst, ChatMessage, OptionsSignal, Pick } from '@/types/stockAgent';
import SuggestedPrompts from './SuggestedPrompts';
import ResponseCards from './ResponseCards';
import MarkdownLite from './MarkdownLite';
import FeedbackButtons from '../learning/FeedbackButtons';

export interface DisplayMessage extends ChatMessage {
  action?: AgentAction;
  picks?: Pick[];
  options?: OptionsSignal[];
  catalysts?: AgentResponseCatalyst[];
  chatMessageId?: string;
  diagnostics?: {
    provider: string;
    model?: string;
    usedFallback: boolean;
    dotnetApiAttempted: boolean;
    dotnetApiSucceeded: boolean;
    agentApiConfigured: boolean;
  };
}

export default function ChatMessageBubble({
  message,
  onSelectPrompt,
  showSuggestedPrompts = true,
}: {
  message: DisplayMessage;
  onSelectPrompt: (prompt: string) => void;
  /** Only the latest agent message should offer next-step suggestions -- older ones in history should not. */
  showSuggestedPrompts?: boolean;
}) {
  const isUser = message.role === 'user';

  if (isUser) {
    return (
      <div className="flex justify-end">
        <div className="max-w-[85%] rounded-2xl rounded-br-sm bg-violet-600 px-4 py-2.5 text-sm text-white sm:max-w-[75%]">
          <p className="whitespace-pre-line">{message.text}</p>
          <div className="mt-1 text-right text-[10px] text-violet-200">{message.timestamp}</div>
        </div>
      </div>
    );
  }

  return (
    <div className="flex items-start gap-3">
      <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-violet-600/20 text-sm">
        🤖
      </div>
      <div className="flex min-w-0 flex-1 flex-col gap-3">
        <div className="rounded-2xl rounded-tl-sm border border-zinc-800 bg-zinc-900 px-4 py-3 text-sm text-zinc-200">
          <MarkdownLite text={message.text} />
          <div className="mt-2 text-[10px] text-zinc-500">{message.timestamp}</div>
          {process.env.NODE_ENV === 'development' && message.diagnostics && (
            <div className="mt-2 rounded border border-zinc-700 bg-zinc-800/50 px-2 py-1.5 text-[10px] text-zinc-400">
              <span className="font-semibold text-zinc-300">Provider:</span> {message.diagnostics.provider}
              {message.diagnostics.model && (
                <>{' · '}<span className="font-semibold text-zinc-300">Model:</span> {message.diagnostics.model}</>
              )}
              {' · '}
              <span className={message.diagnostics.usedFallback ? 'text-yellow-400' : 'text-green-400'}>
                {message.diagnostics.usedFallback ? 'FALLBACK' : 'LIVE'}
              </span>
              {' · '}
              API {message.diagnostics.dotnetApiSucceeded ? '✓' : '✗'}
            </div>
          )}
        </div>

        <ResponseCards
          action={message.action}
          picks={message.picks}
          options={message.options}
          catalysts={message.catalysts}
        />

        {/* Suggested prompts and feedback buttons removed for cleaner UI */}
      </div>
    </div>
  );
}
