import type { ReactNode } from 'react';
import Sidebar from './Sidebar';
import MobileNav from './MobileNav';

export default function AppShell({
  children,
  rightPanel,
}: {
  children: ReactNode;
  rightPanel?: ReactNode;
}) {
  return (
    <div className="flex h-screen bg-zinc-950">
      <Sidebar />

      <div className="flex min-h-0 flex-1 flex-col md:flex-row">
        <main className="min-h-0 flex-1 overflow-y-auto pb-16 md:pb-0">{children}</main>

        {rightPanel && (
          <aside className="hidden w-80 flex-col gap-4 overflow-y-auto border-l border-zinc-800 p-4 md:flex">
            {rightPanel}
          </aside>
        )}
      </div>

      <MobileNav />
    </div>
  );
}
