'use client';

import { useRef, useState } from 'react';
import { submitOutcomeAction } from '@/app/actions/learningActions';
import { Pick } from '@/types/stockAgent';

const TIMEFRAMES = ['1d', '5d', '20d', '60d'] as const;

/**
 * Manual outcome entry — no live price API yet, so the user fills in
 * start/end price (or a return percent directly) themselves.
 */
export default function OutcomeEntryForm({ picks }: { picks: Pick[] }) {
  const formRef = useRef<HTMLFormElement>(null);
  const [status, setStatus] = useState<{ ok: boolean; message: string } | null>(null);
  const [pending, setPending] = useState(false);

  const handleSubmit = async (formData: FormData) => {
    setPending(true);
    setStatus(null);
    try {
      const result = await submitOutcomeAction(formData);
      if (result.persisted) {
        setStatus({ ok: true, message: 'Outcome saved.' });
        formRef.current?.reset();
      } else {
        setStatus({ ok: false, message: result.reason ?? 'Save failed.' });
      }
    } finally {
      setPending(false);
    }
  };

  return (
    <div className="rounded-xl border border-zinc-800 bg-zinc-900 p-4">
      <h2 className="text-sm font-semibold text-zinc-100">Log an outcome manually</h2>
      <p className="mt-1 text-[11px] text-zinc-500">
        No live price feed yet — enter what actually happened so the agent can compare belief vs. outcome later.
      </p>

      <form ref={formRef} action={handleSubmit} className="mt-3 grid grid-cols-2 gap-2 text-xs">
        <label className="col-span-2 flex flex-col gap-1">
          Pick
          <select name="pickId" required className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200">
            <option value="">Select a pick…</option>
            {picks.map((p) => (
              <option key={p.id} value={p.id}>
                {p.ticker} — {p.companyName} ({p.datePicked})
              </option>
            ))}
          </select>
        </label>

        <label className="flex flex-col gap-1">
          Ticker (optional override)
          <input name="ticker" className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200" />
        </label>

        <label className="flex flex-col gap-1">
          Evaluation window
          <select name="evaluationWindow" required className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200">
            {TIMEFRAMES.map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </select>
        </label>

        <label className="flex flex-col gap-1">
          Start price
          <input name="startPrice" type="number" step="0.01" className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200" />
        </label>

        <label className="flex flex-col gap-1">
          End price
          <input name="endPrice" type="number" step="0.01" className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200" />
        </label>

        <label className="flex flex-col gap-1">
          Return % (auto if prices given)
          <input name="returnPercent" type="number" step="0.01" className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200" />
        </label>

        <label className="flex flex-col gap-1">
          SPY return %
          <input name="spyReturnPercent" type="number" step="0.01" className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200" />
        </label>

        <label className="flex flex-col gap-1">
          QQQ return %
          <input name="qqqReturnPercent" type="number" step="0.01" className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200" />
        </label>

        <label className="flex flex-col gap-1">
          Max favorable move %
          <input name="maxFavorableMove" type="number" step="0.01" className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200" />
        </label>

        <label className="flex flex-col gap-1">
          Max adverse move %
          <input name="maxAdverseMove" type="number" step="0.01" className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200" />
        </label>

        <label className="flex flex-col gap-1">
          Thesis correct?
          <select name="thesisCorrect" className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200">
            <option value="">Not sure yet</option>
            <option value="true">Yes</option>
            <option value="false">No</option>
          </select>
        </label>

        <label className="flex flex-col gap-1">
          Catalyst played out?
          <select name="catalystPlayedOut" className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200">
            <option value="">N/A</option>
            <option value="true">Yes</option>
            <option value="false">No</option>
          </select>
        </label>

        <label className="col-span-2 flex flex-col gap-1">
          Options setup worked?
          <select name="optionsSetupWorked" className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200">
            <option value="">N/A — no options setup</option>
            <option value="true">Yes</option>
            <option value="false">No</option>
          </select>
        </label>

        <label className="col-span-2 flex flex-col gap-1">
          Notes
          <textarea name="notes" rows={2} className="rounded-lg border border-zinc-700 bg-zinc-950 px-2 py-1.5 text-zinc-200" />
        </label>

        <button
          type="submit"
          disabled={pending}
          className="col-span-2 mt-1 rounded-lg bg-violet-600 px-3 py-1.5 text-xs font-medium text-white transition hover:bg-violet-500 disabled:opacity-50"
        >
          {pending ? 'Saving…' : 'Save outcome'}
        </button>

        {status && (
          <p className={`col-span-2 text-[11px] ${status.ok ? 'text-green-400' : 'text-red-400'}`}>{status.message}</p>
        )}
      </form>
    </div>
  );
}
