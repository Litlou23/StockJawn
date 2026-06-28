export default function SuggestedPrompts({
  prompts,
  onSelect,
  scrollable = false,
}: {
  prompts: string[];
  onSelect: (prompt: string) => void;
  /** Single-row horizontal scroll instead of wrapping — used near the composer. */
  scrollable?: boolean;
}) {
  if (prompts.length === 0) return null;

  return (
    <div className={scrollable ? 'flex gap-2 overflow-x-auto pb-1' : 'flex flex-wrap justify-center gap-2'}>
      {prompts.map((prompt) => (
        <button
          key={prompt}
          onClick={() => onSelect(prompt)}
          className="shrink-0 rounded-full border border-zinc-700 bg-zinc-900 px-3 py-1.5 text-xs font-medium text-zinc-300 transition hover:border-violet-500/50 hover:bg-violet-600/10 hover:text-violet-300"
        >
          {prompt}
        </button>
      ))}
    </div>
  );
}
