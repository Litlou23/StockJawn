'use client';

import { useState, useMemo, useCallback, type ReactNode } from 'react';

// ---------------------------------------------------------------------------
// Sort types
// ---------------------------------------------------------------------------

export type SortDirection = 'asc' | 'desc' | null;

export interface SortState {
  key: string;
  direction: SortDirection;
}

// ---------------------------------------------------------------------------
// Hook — generic sort for any array
// ---------------------------------------------------------------------------

export function useSort<T>(items: T[], defaultKey?: string, defaultDir: SortDirection = null) {
  const [sort, setSort] = useState<SortState>({ key: defaultKey ?? '', direction: defaultDir });

  const toggle = useCallback((key: string) => {
    setSort(prev => {
      if (prev.key !== key) return { key, direction: 'asc' };
      if (prev.direction === 'asc') return { key, direction: 'desc' };
      if (prev.direction === 'desc') return { key: '', direction: null };
      return { key, direction: 'asc' };
    });
  }, []);

  const sorted = useMemo(() => {
    if (!sort.key || !sort.direction) return items;

    return [...items].sort((a, b) => {
      const av = (a as Record<string, unknown>)[sort.key];
      const bv = (b as Record<string, unknown>)[sort.key];

      if (av == null && bv == null) return 0;
      if (av == null) return 1;
      if (bv == null) return -1;

      let cmp = 0;
      if (typeof av === 'number' && typeof bv === 'number') {
        cmp = av - bv;
      } else if (typeof av === 'boolean' && typeof bv === 'boolean') {
        cmp = (av ? 1 : 0) - (bv ? 1 : 0);
      } else {
        cmp = String(av).localeCompare(String(bv), undefined, { numeric: true, sensitivity: 'base' });
      }

      return sort.direction === 'desc' ? -cmp : cmp;
    });
  }, [items, sort]);

  return { sorted, sort, toggle };
}

// ---------------------------------------------------------------------------
// Sortable header cell component
// ---------------------------------------------------------------------------

interface SortHeaderProps {
  label: string;
  sortKey: string;
  current: SortState;
  onToggle: (key: string) => void;
  className?: string;
}

export function SortHeader({ label, sortKey, current, onToggle, className = '' }: SortHeaderProps) {
  const active = current.key === sortKey;
  return (
    <th
      onClick={() => onToggle(sortKey)}
      className={`cursor-pointer select-none whitespace-nowrap px-2 py-2 transition-colors hover:text-zinc-200 ${className}`}
    >
      <span className="inline-flex items-center gap-1">
        {label}
        <SortArrow active={active} direction={active ? current.direction : null} />
      </span>
    </th>
  );
}

function SortArrow({ active, direction }: { active: boolean; direction: SortDirection }) {
  return (
    <span className={`inline-flex flex-col text-[8px] leading-none ${active ? 'text-violet-400' : 'text-zinc-600'}`}>
      <span className={direction === 'asc' ? 'text-violet-400' : ''}>▲</span>
      <span className={direction === 'desc' ? 'text-violet-400' : ''}>▼</span>
    </span>
  );
}

// ---------------------------------------------------------------------------
// Helper: sort by nested accessor (for objects with nested data)
// ---------------------------------------------------------------------------

export function useSortWithAccessor<T>(
  items: T[],
  accessors: Record<string, (item: T) => unknown>,
  defaultKey?: string,
  defaultDir: SortDirection = null,
) {
  const [sort, setSort] = useState<SortState>({ key: defaultKey ?? '', direction: defaultDir });

  const toggle = useCallback((key: string) => {
    setSort(prev => {
      if (prev.key !== key) return { key, direction: 'asc' };
      if (prev.direction === 'asc') return { key, direction: 'desc' };
      if (prev.direction === 'desc') return { key: '', direction: null };
      return { key, direction: 'asc' };
    });
  }, []);

  const sorted = useMemo(() => {
    if (!sort.key || !sort.direction || !accessors[sort.key]) return items;

    const accessor = accessors[sort.key];
    return [...items].sort((a, b) => {
      const av = accessor(a);
      const bv = accessor(b);

      if (av == null && bv == null) return 0;
      if (av == null) return 1;
      if (bv == null) return -1;

      let cmp = 0;
      if (typeof av === 'number' && typeof bv === 'number') {
        cmp = av - bv;
      } else if (typeof av === 'boolean' && typeof bv === 'boolean') {
        cmp = (av ? 1 : 0) - (bv ? 1 : 0);
      } else {
        cmp = String(av).localeCompare(String(bv), undefined, { numeric: true, sensitivity: 'base' });
      }

      return sort.direction === 'desc' ? -cmp : cmp;
    });
  }, [items, sort, accessors]);

  return { sorted, sort, toggle };
}
