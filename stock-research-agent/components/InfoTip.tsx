'use client';

import { useState, useRef, useEffect, type ReactNode } from 'react';

interface InfoTipProps {
  text: string;
  children?: ReactNode;
}

export default function InfoTip({ text, children }: InfoTipProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) return;
    function close(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  return (
    <span ref={ref} className="relative inline-flex items-center">
      {children}
      <button
        onClick={() => setOpen(o => !o)}
        className="ml-1 inline-flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded-full border border-zinc-600 text-[9px] font-bold leading-none text-zinc-400 hover:border-violet-500 hover:text-violet-400 transition-colors"
        aria-label="More info"
      >
        ?
      </button>
      {open && (
        <span className="absolute bottom-full left-1/2 z-50 mb-2 w-64 -translate-x-1/2 rounded-lg border border-zinc-700 bg-zinc-800 px-3 py-2 text-[11px] leading-relaxed text-zinc-300 shadow-xl">
          {text}
          <span className="absolute left-1/2 top-full -translate-x-1/2 border-4 border-transparent border-t-zinc-700" />
        </span>
      )}
    </span>
  );
}

export function InfoBanner({ items }: { items: { term: string; definition: string }[] }) {
  const [open, setOpen] = useState(false);

  return (
    <div className="rounded-lg border border-zinc-800 bg-zinc-900/60">
      <button
        onClick={() => setOpen(o => !o)}
        className="flex w-full items-center justify-between px-4 py-2.5 text-left"
      >
        <span className="text-xs font-medium text-zinc-400">
          {open ? 'Hide glossary' : 'What do these values mean?'}
        </span>
        <svg
          className={`h-4 w-4 text-zinc-500 transition-transform ${open ? 'rotate-180' : ''}`}
          viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2}
        >
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
        </svg>
      </button>
      {open && (
        <div className="border-t border-zinc-800 px-4 py-3">
          <dl className="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-2">
            {items.map(({ term, definition }) => (
              <div key={term}>
                <dt className="text-[11px] font-semibold text-zinc-200">{term}</dt>
                <dd className="text-[11px] leading-relaxed text-zinc-500">{definition}</dd>
              </div>
            ))}
          </dl>
        </div>
      )}
    </div>
  );
}
