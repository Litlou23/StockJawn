import AppShell from '@/components/AppShell';
import { getSignalWeightsFromDb } from '@/services/persistence/picksRepository';

export default async function SettingsPage() {
  const signalWeights = await getSignalWeightsFromDb();

  return (
    <AppShell>
      <div className="mx-auto max-w-3xl space-y-4 p-4">
        <h1 className="text-lg font-bold text-zinc-100">Settings</h1>
        <p className="text-sm text-zinc-500">
          This shows how much importance the system gives each signal when scoring stocks.
          These will be adjustable in a future update — for now, this is a preview.
        </p>

        <div className="rounded-xl border border-zinc-800 bg-zinc-900">
          {signalWeights.length === 0 && (
            <p className="px-4 py-3 text-sm text-zinc-500">No signal settings saved yet.</p>
          )}
          {signalWeights.map((sw, i) => (
            <div
              key={sw.signalName}
              className={`flex items-center justify-between px-4 py-3 text-sm ${
                i !== signalWeights.length - 1 ? 'border-b border-zinc-800' : ''
              }`}
            >
              <div>
                <div className="font-medium text-zinc-200">{sw.signalName.replace(/_/g, ' ')}</div>
                {sw.notes && <div className="text-xs text-zinc-500">{sw.notes}</div>}
              </div>
              <div className="flex items-center gap-2">
                <span className="text-zinc-300">{sw.weight.toFixed(1)}×</span>
                <span
                  className={`rounded-full px-2 py-0.5 text-[11px] ${
                    sw.active ? 'bg-green-500/10 text-green-400' : 'bg-zinc-800 text-zinc-500'
                  }`}
                >
                  {sw.active ? 'active' : 'inactive'}
                </span>
              </div>
            </div>
          ))}
        </div>
      </div>
    </AppShell>
  );
}
