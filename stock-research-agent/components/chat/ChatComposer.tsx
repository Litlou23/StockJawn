'use client';

import { useRef, useState } from 'react';
import type { ChangeEvent, KeyboardEvent } from 'react';

const MAX_TEXTAREA_HEIGHT_PX = 200;

export default function ChatComposer({
  onSend,
  disabled,
}: {
  onSend: (message: string) => void;
  disabled?: boolean;
}) {
  const [value, setValue] = useState('');
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const submit = () => {
    const trimmed = value.trim();
    if (!trimmed || disabled) return;
    onSend(trimmed);
    setValue('');
    if (textareaRef.current) textareaRef.current.style.height = 'auto';
  };

  const handleKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      submit();
    }
    // Shift+Enter falls through to the textarea's default newline behavior.
  };

  const handleChange = (e: ChangeEvent<HTMLTextAreaElement>) => {
    setValue(e.target.value);
    const el = e.target;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, MAX_TEXTAREA_HEIGHT_PX)}px`;
  };

  return (
    <div className="mx-auto w-full max-w-3xl">
      <div className="flex items-end gap-2 rounded-2xl border border-zinc-700 bg-zinc-900 p-2 pl-4 shadow-lg shadow-black/20 focus-within:border-violet-500/60">
        <textarea
          ref={textareaRef}
          value={value}
          onChange={handleChange}
          onKeyDown={handleKeyDown}
          disabled={disabled}
          rows={1}
          placeholder="Ask about catalysts, options setups, risk, or today's watchlist…"
          className="max-h-[200px] flex-1 resize-none bg-transparent py-2 text-sm text-zinc-100 placeholder-zinc-500 outline-none disabled:opacity-60"
        />
        <button
          onClick={submit}
          disabled={disabled || value.trim().length === 0}
          aria-label="Send message"
          className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-violet-600 text-white transition hover:bg-violet-500 disabled:bg-zinc-700 disabled:text-zinc-500"
        >
          <svg viewBox="0 0 24 24" fill="none" strokeWidth={2} stroke="currentColor" className="h-4 w-4">
            <path strokeLinecap="round" strokeLinejoin="round" d="M12 19V5M5 12l7-7 7 7" />
          </svg>
        </button>
      </div>
      <p className="mt-1.5 px-1 text-center text-[11px] text-zinc-600">
        Enter to send · Shift+Enter for a new line · Research only, not financial advice.
      </p>
    </div>
  );
}
