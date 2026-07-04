import AppShell from '@/components/AppShell';

export default function Loading() {
  return (
    <AppShell>
      <div className="flex h-full items-center justify-center">
        <div className="flex flex-col items-center gap-4">
          <div className="loading-spinner h-10 w-10 rounded-full border-[3px] border-zinc-700 border-t-violet-500" />
          <span className="text-sm text-zinc-500">Loading…</span>
        </div>
      </div>
    </AppShell>
  );
}
