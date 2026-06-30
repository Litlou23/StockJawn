'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useState } from 'react';
import { navEntries, entryMatchesPath, entryPrimaryHref, type NavEntry } from './navItems';

export default function MobileNav() {
  const pathname = usePathname();
  const [openGroup, setOpenGroup] = useState<string | null>(null);

  const closeSheet = () => setOpenGroup(null);

  const expandedEntry = openGroup
    ? navEntries.find(e => e.label === openGroup) ?? null
    : null;

  return (
    <>
      {/* Sub-sheet that slides up when a group is tapped */}
      {expandedEntry && expandedEntry.children && (
        <>
          <button
            aria-label="Close menu"
            onClick={closeSheet}
            className="fixed inset-0 z-20 bg-zinc-950/70 backdrop-blur-sm md:hidden"
          />
          <div className="fixed bottom-14 left-2 right-2 z-30 rounded-xl border border-zinc-800 bg-zinc-900 p-2 shadow-xl md:hidden">
            <div className="px-2 py-1 text-[10px] uppercase tracking-wide text-zinc-500">
              {expandedEntry.label}
            </div>
            <div className="flex flex-col">
              {expandedEntry.children.map(child => {
                const active = pathname?.startsWith(child.href);
                return (
                  <Link
                    key={child.href}
                    href={child.href}
                    onClick={closeSheet}
                    className={`rounded-lg px-3 py-2 text-sm ${
                      active ? 'bg-violet-600/20 text-violet-300' : 'text-zinc-200'
                    }`}
                  >
                    {child.label}
                  </Link>
                );
              })}
            </div>
          </div>
        </>
      )}

      <nav className="fixed bottom-0 left-0 right-0 z-10 border-t border-zinc-800 bg-zinc-950 md:hidden">
        <div className="flex justify-around">
          {navEntries.map(entry => (
            <MobileEntry
              key={entry.label}
              entry={entry}
              pathname={pathname}
              isOpen={openGroup === entry.label}
              onTap={() => {
                if (entry.children) {
                  setOpenGroup(prev => prev === entry.label ? null : entry.label);
                } else {
                  closeSheet();
                }
              }}
            />
          ))}
        </div>
      </nav>
    </>
  );
}

function MobileEntry({
  entry, pathname, isOpen, onTap,
}: {
  entry: NavEntry;
  pathname: string | null;
  isOpen: boolean;
  onTap: () => void;
}) {
  const active = entryMatchesPath(entry, pathname);
  const cls = `flex flex-1 flex-col items-center gap-0.5 py-2.5 text-[10px] font-medium ${
    active || isOpen ? 'text-violet-400' : 'text-zinc-500'
  }`;

  // Leaf → straight link
  if (!entry.children) {
    const href = entryPrimaryHref(entry);
    if (!href) return null;
    return (
      <Link href={href} className={cls} onClick={onTap}>
        {entry.icon}
        {entry.label}
      </Link>
    );
  }

  // Group → opens the sub-sheet
  return (
    <button type="button" onClick={onTap} className={cls}>
      {entry.icon}
      {entry.label}
    </button>
  );
}
