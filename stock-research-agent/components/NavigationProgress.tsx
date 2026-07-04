'use client';

import { createContext, useContext, useState, useCallback, useEffect, type ReactNode } from 'react';
import { usePathname } from 'next/navigation';

/* ------------------------------------------------------------------ */
/*  Context                                                           */
/* ------------------------------------------------------------------ */

const NavProgressCtx = createContext<{ start: () => void }>({ start: () => {} });

export function useNavProgress() {
  return useContext(NavProgressCtx);
}

/* ------------------------------------------------------------------ */
/*  Provider — wraps the app layout                                   */
/* ------------------------------------------------------------------ */

export function NavigationProgressProvider({ children }: { children: ReactNode }) {
  const [navigating, setNavigating] = useState(false);
  const pathname = usePathname();

  // Navigation complete — hide bar
  useEffect(() => {
    setNavigating(false);
  }, [pathname]);

  const start = useCallback(() => {
    setNavigating(true);
  }, []);

  return (
    <NavProgressCtx.Provider value={{ start }}>
      {navigating && (
        <div className="fixed inset-x-0 top-0 z-[100] h-[3px]">
          <div className="nav-progress-bar h-full rounded-r-full bg-violet-500" />
        </div>
      )}
      {children}
    </NavProgressCtx.Provider>
  );
}
