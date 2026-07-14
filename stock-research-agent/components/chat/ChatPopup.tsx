'use client';

import { useState, useEffect, useCallback } from 'react';
import { usePathname } from 'next/navigation';
import ChatWindow from './ChatWindow';

export default function ChatPopup() {
  const [open, setOpen] = useState(false);
  const pathname = usePathname();

  // Don't show on the dedicated chat page
  if (pathname === '/chat') return null;

  // Close on Escape
  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [open]);

  const toggle = useCallback(() => setOpen((v) => !v), []);

  return (
    <>
      {/* Chat panel */}
      {open && (
        <div className="fixed bottom-20 right-4 z-50 flex h-[70vh] w-[420px] max-w-[calc(100vw-2rem)] flex-col rounded-2xl border border-zinc-700 bg-zinc-950 shadow-2xl shadow-black/60 md:bottom-6 md:right-6">
          {/* Close bar */}
          <div className="flex items-center justify-between border-b border-zinc-800 px-4 py-2">
            <span className="text-xs font-semibold text-zinc-300">Chat</span>
            <button
              type="button"
              onClick={() => setOpen(false)}
              className="rounded-md p-1 text-zinc-500 transition hover:bg-zinc-800 hover:text-zinc-200"
              aria-label="Close chat"
            >
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} className="h-4 w-4">
                <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>

          {/* Chat content — fills remaining space */}
          <div className="flex-1 overflow-hidden">
            <ChatWindow />
          </div>
        </div>
      )}

      {/* Floating button */}
      <button
        type="button"
        onClick={toggle}
        className={`fixed bottom-20 right-4 z-50 flex h-12 w-12 items-center justify-center rounded-full shadow-lg transition-all md:bottom-6 md:right-6 ${
          open
            ? 'bg-zinc-800 text-zinc-400 hover:bg-zinc-700'
            : 'bg-violet-600 text-white hover:bg-violet-500'
        }`}
        aria-label={open ? 'Close chat' : 'Open chat'}
      >
        {open ? (
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} className="h-5 w-5">
            <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
          </svg>
        ) : (
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} className="h-5 w-5">
            <path strokeLinecap="round" strokeLinejoin="round" d="M8 10h.01M12 10h.01M16 10h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z" />
          </svg>
        )}
      </button>
    </>
  );
}
