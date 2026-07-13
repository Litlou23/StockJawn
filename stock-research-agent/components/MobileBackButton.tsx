'use client';

import { usePathname, useRouter } from 'next/navigation';
import { navEntries } from './navItems';

/** Top-level paths that are directly reachable from the sidebar/bottom nav — no back button needed. */
const topLevelPaths = new Set(
  navEntries.flatMap((e) => [e.href, ...(e.children?.map((c) => c.href) ?? [])]).filter(Boolean) as string[],
);

export default function BackButton() {
  const pathname = usePathname();
  const router = useRouter();

  // Don't show on pages that are directly in the nav
  if (!pathname || topLevelPaths.has(pathname)) return null;

  return (
    <button
      onClick={() => router.back()}
      className="sticky top-0 z-10 flex items-center gap-1.5 border-b border-zinc-800 bg-zinc-950/90 px-4 py-2.5 text-sm font-medium text-zinc-400 backdrop-blur-sm"
    >
      <svg viewBox="0 0 24 24" fill="none" strokeWidth={2} stroke="currentColor" className="h-4 w-4">
        <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
      </svg>
      Back
    </button>
  );
}
