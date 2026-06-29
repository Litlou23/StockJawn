import type { ReactNode } from 'react';

export interface NavItem {
  href: string;
  label: string;
  icon: ReactNode;
}

export const navItems: NavItem[] = [
  {
    href: '/chat',
    label: 'Chat',
    icon: (
      <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
        <path strokeLinecap="round" strokeLinejoin="round" d="M21 12a8.5 8.5 0 1 1-3.2-6.6M21 12 17 12M21 12l-3 3" />
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          d="M4 19l1.2-3.6A8.5 8.5 0 0 1 4 12a8.5 8.5 0 0 1 8.5-8.5"
        />
      </svg>
    ),
  },
  {
    href: '/dashboard',
    label: 'Dashboard',
    icon: (
      <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
        <rect x="3" y="3" width="7" height="9" rx="1.5" />
        <rect x="14" y="3" width="7" height="5" rx="1.5" />
        <rect x="14" y="12" width="7" height="9" rx="1.5" />
        <rect x="3" y="16" width="7" height="5" rx="1.5" />
      </svg>
    ),
  },
  {
    href: '/history',
    label: 'History',
    icon: (
      <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
        <circle cx="12" cy="12" r="8.5" />
        <path strokeLinecap="round" strokeLinejoin="round" d="M12 7.5V12l3 2" />
      </svg>
    ),
  },
  {
    href: '/watchlist',
    label: 'Watchlist',
    icon: (
      <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          d="M12 3.5l2.6 5.4 5.9.8-4.3 4.2 1 5.9-5.2-2.8-5.2 2.8 1-5.9-4.3-4.2 5.9-.8z"
        />
      </svg>
    ),
  },
  {
    href: '/results',
    label: 'Results',
    icon: (
      <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
        <path strokeLinecap="round" strokeLinejoin="round" d="M4 19V5m0 14h16M9 19v-6m5 6v-9" />
      </svg>
    ),
  },
  {
    href: '/learning',
    label: 'Learning',
    icon: (
      <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
        <path strokeLinecap="round" strokeLinejoin="round" d="M4 19.5V6a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v13.5" />
        <path strokeLinecap="round" strokeLinejoin="round" d="M4 19.5h16M8 11l3-3 3 2 4-4" />
      </svg>
    ),
  },
  {
    href: '/connectivity',
    label: 'Connectivity',
    icon: (
      <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
        <path strokeLinecap="round" strokeLinejoin="round" d="M9 3v2m6-2v2M9 19v2m6-2v2M3 9h2m-2 6h2m14-6h2m-2 6h2" />
        <rect x="7" y="7" width="10" height="10" rx="2" />
        <circle cx="12" cy="12" r="2" />
      </svg>
    ),
  },
  {
    href: '/settings',
    label: 'Settings',
    icon: (
      <svg viewBox="0 0 24 24" fill="none" strokeWidth={1.8} stroke="currentColor" className="h-5 w-5">
        <circle cx="12" cy="12" r="3" />
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          d="M19.4 13.5a1.7 1.7 0 0 0 .3 1.9l.1.1a2 2 0 1 1-2.9 2.9l-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.5V20a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1a2 2 0 1 1-2.9-2.9l.1-.1a1.7 1.7 0 0 0 .3-1.9 1.7 1.7 0 0 0-1.5-1H4a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9l-.1-.1a2 2 0 1 1 2.9-2.9l.1.1a1.7 1.7 0 0 0 1.9.3h0a1.7 1.7 0 0 0 1-1.5V4a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1a2 2 0 1 1 2.9 2.9l-.1.1a1.7 1.7 0 0 0-.3 1.9v0a1.7 1.7 0 0 0 1.5 1H20a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.6 1z"
        />
      </svg>
    ),
  },
];
