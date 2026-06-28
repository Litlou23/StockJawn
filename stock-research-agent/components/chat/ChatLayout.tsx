import type { ReactNode } from 'react';

/**
 * Structural shell for the chat experience: a header that never scrolls, a
 * message area that's the only thing that scrolls, and a composer pinned
 * at the bottom — the standard ChatGPT-style layout. Plain flex children
 * rather than `position: sticky`, which is more robust when this sits
 * inside AppShell's own scrollable `<main>`.
 */
export default function ChatLayout({
  header,
  composer,
  children,
}: {
  header?: ReactNode;
  composer: ReactNode;
  children: ReactNode;
}) {
  return (
    <div className="flex h-full flex-col overflow-hidden">
      {header && <div className="shrink-0 border-b border-zinc-800 px-4 py-3">{header}</div>}

      <div className="flex-1 overflow-y-auto">
        <div className="mx-auto flex max-w-3xl flex-col gap-4 px-4 py-4">{children}</div>
      </div>

      <div className="shrink-0 border-t border-zinc-800 bg-zinc-950 px-4 py-3">{composer}</div>
    </div>
  );
}
