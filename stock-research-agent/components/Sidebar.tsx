'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useState } from 'react';
import { navEntries, entryMatchesPath, type NavEntry } from './navItems';

export default function Sidebar() {
  const pathname = usePathname();

  // Auto-open the group the user is currently in.
  const initiallyOpen = new Set<string>(
    navEntries.filter(e => e.children && entryMatchesPath(e, pathname)).map(e => e.label),
  );
  const [open, setOpen] = useState<Set<string>>(initiallyOpen);

  const toggle = (label: string) => {
    setOpen(prev => {
      const next = new Set(prev);
      if (next.has(label)) next.delete(label);
      else next.add(label);
      return next;
    });
  };

  return (
    <aside className="hidden h-screen w-60 flex-col border-r border-zinc-800 bg-zinc-950 px-3 py-4 md:flex">
      <div className="mb-6 flex items-center gap-2 px-2">
        <span className="text-xl">📈</span>
        <div>
          <div className="flex items-center gap-1 text-sm font-semibold text-zinc-100">
            Stock Agent <span className="text-violet-400">+</span>
          </div>
          <div className="text-[11px] text-zinc-500">Personal Research Assistant</div>
        </div>
      </div>

      <nav className="flex flex-1 flex-col gap-1 overflow-y-auto">
        {navEntries.map((entry) =>
          entry.children
            ? <Group key={entry.label} entry={entry} pathname={pathname} open={open.has(entry.label)} onToggle={() => toggle(entry.label)} />
            : <Leaf key={entry.label} entry={entry} pathname={pathname} />,
        )}
      </nav>

      <div className="mt-4 flex items-center gap-2 rounded-lg border border-zinc-800 bg-zinc-900 px-3 py-2">
        <div className="flex h-7 w-7 items-center justify-center rounded-full bg-violet-600 text-xs font-semibold text-white">
          M
        </div>
        <div>
          <div className="text-xs font-medium text-zinc-200">My Account</div>
          <div className="text-[11px] text-green-400">Private Mode</div>
        </div>
      </div>
    </aside>
  );
}

function Leaf({ entry, pathname }: { entry: NavEntry; pathname: string | null }) {
  const active = entry.href && pathname?.startsWith(entry.href);
  if (!entry.href) return null;
  return (
    <Link
      href={entry.href}
      className={`flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition ${
        active ? 'bg-violet-600/15 text-violet-300' : 'text-zinc-400 hover:bg-zinc-900 hover:text-zinc-200'
      }`}
    >
      {entry.icon}
      {entry.label}
    </Link>
  );
}

function Group({
  entry, pathname, open, onToggle,
}: {
  entry: NavEntry;
  pathname: string | null;
  open: boolean;
  onToggle: () => void;
}) {
  const active = entryMatchesPath(entry, pathname);
  return (
    <div>
      <button
        type="button"
        onClick={onToggle}
        className={`flex w-full items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition ${
          active ? 'text-violet-200' : 'text-zinc-400 hover:bg-zinc-900 hover:text-zinc-200'
        }`}
      >
        {entry.icon}
        <span className="flex-1 text-left">{entry.label}</span>
        <svg
          viewBox="0 0 24 24"
          fill="none"
          strokeWidth={2}
          stroke="currentColor"
          className={`h-3.5 w-3.5 transition-transform ${open ? 'rotate-90' : ''}`}
        >
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 6l6 6-6 6" />
        </svg>
      </button>

      {open && entry.children && (
        <div className="mt-0.5 ml-3 flex flex-col gap-0.5 border-l border-zinc-800 pl-3">
          {entry.children.map((child) => {
            const childActive = pathname?.startsWith(child.href);
            return (
              <Link
                key={child.href}
                href={child.href}
                className={`rounded-md px-3 py-1.5 text-xs font-medium transition ${
                  childActive
                    ? 'bg-violet-600/15 text-violet-300'
                    : 'text-zinc-500 hover:bg-zinc-900 hover:text-zinc-200'
                }`}
              >
                {child.label}
              </Link>
            );
          })}
        </div>
      )}
    </div>
  );
}
