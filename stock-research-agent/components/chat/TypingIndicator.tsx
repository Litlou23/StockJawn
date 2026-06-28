export default function TypingIndicator() {
  return (
    <div className="flex items-start gap-2">
      <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-violet-600/20 text-sm">
        🤖
      </div>
      <div className="flex items-center gap-1 rounded-2xl rounded-tl-sm border border-zinc-800 bg-zinc-900 px-4 py-3">
        <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-zinc-500 [animation-delay:-0.3s]" />
        <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-zinc-500 [animation-delay:-0.15s]" />
        <span className="h-1.5 w-1.5 animate-bounce rounded-full bg-zinc-500" />
      </div>
    </div>
  );
}
