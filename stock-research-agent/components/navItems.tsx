import type { ReactNode } from 'react';

export interface NavLeaf {
  href: string;
  label: string;
  icon?: ReactNode;
}

export interface NavGroup {
  label: string;
  icon: ReactNode;
  /** Group landing page. If omitted, clicking the group falls back to the first child. */
  href?: string;
  children?: NavLeaf[];
}

export type NavEntry = NavGroup;

// ---------------------------------------------------------------------------
// Icons (kept here so the entry table stays compact below)
// ---------------------------------------------------------------------------

const ChatIcon = (
  <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
    <path strokeLinecap="round" strokeLinejoin="round" d="M21 12a8.5 8.5 0 1 1-3.2-6.6M21 12 17 12M21 12l-3 3" />
    <path strokeLinecap="round" strokeLinejoin="round" d="M4 19l1.2-3.6A8.5 8.5 0 0 1 4 12a8.5 8.5 0 0 1 8.5-8.5" />
  </svg>
);

const DashboardIcon = (
  <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
    <rect x="3" y="3" width="7" height="9" rx="1.5" />
    <rect x="14" y="3" width="7" height="5" rx="1.5" />
    <rect x="14" y="12" width="7" height="9" rx="1.5" />
    <rect x="3" y="16" width="7" height="5" rx="1.5" />
  </svg>
);

const ResearchIcon = (
  <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
    <circle cx="11" cy="11" r="6.5" />
    <path strokeLinecap="round" strokeLinejoin="round" d="m20.5 20.5-4.5-4.5M8 11h6M11 8v6" />
  </svg>
);

const OptionsIcon = (
  <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
    <path strokeLinecap="round" strokeLinejoin="round" d="M3 17l4-4 4 3 6-7 4 4M3 21h18" />
  </svg>
);

const SystemIcon = (
  <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
    <circle cx="12" cy="12" r="3" />
    <path strokeLinecap="round" strokeLinejoin="round"
      d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09a1.65 1.65 0 0 0-1.08-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06A1.65 1.65 0 0 0 5 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06A1.65 1.65 0 0 0 9 4.68h.18a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9c.13.46.2.94.2 1.43v.14a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1.08z" />
  </svg>
);

// ---------------------------------------------------------------------------
// Entry table — five top-level entries, two leaves + three groups
// ---------------------------------------------------------------------------

export const navEntries: NavEntry[] = [
  {
    label: 'Chat',
    icon: ChatIcon,
    href: '/chat',
  },
  {
    label: 'Dashboard',
    icon: DashboardIcon,
    href: '/dashboard',
  },
  {
    label: 'Research',
    icon: ResearchIcon,
    children: [
      { href: '/stock-lab', label: 'Stock Lab' },
      { href: '/watchlist', label: 'Watchlist' },
      { href: '/predictions', label: 'Predictions' },
      { href: '/results', label: 'Results' },
      { href: '/history', label: 'History' },
    ],
  },
  {
    label: 'Options',
    icon: OptionsIcon,
    children: [
      { href: '/options-research', label: 'Options Data' },
      { href: '/options-lab', label: 'Options Lab' },
      { href: '/paper-options', label: 'Paper Options' },
    ],
  },
  {
    label: 'System',
    icon: SystemIcon,
    children: [
      { href: '/learning', label: 'Learning' },
      { href: '/connectivity', label: 'Connectivity' },
      { href: '/settings', label: 'Settings' },
    ],
  },
];

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Returns true if the path falls under any href owned by this entry. */
export function entryMatchesPath(entry: NavEntry, pathname: string | null | undefined): boolean {
  if (!pathname) return false;
  if (entry.href && pathname.startsWith(entry.href)) return true;
  if (entry.children?.some(c => pathname.startsWith(c.href))) return true;
  return false;
}

/** First-child fallback so groups without their own href still navigate somewhere. */
export function entryPrimaryHref(entry: NavEntry): string | null {
  return entry.href ?? entry.children?.[0]?.href ?? null;
}
