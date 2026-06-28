/**
 * Dependency-free "lite" markdown renderer for assistant messages. The
 * agent's responses are plain text with light structure (labeled sections
 * like "Bottom line:", "- " bullet lists, occasional **bold**) — this
 * covers that without pulling in a markdown library.
 */

function renderInline(text: string, keyPrefix: string) {
  const parts = text.split(/\*\*(.+?)\*\*/g);
  return parts.map((part, i) =>
    i % 2 === 1 ? (
      <strong key={`${keyPrefix}-${i}`} className="font-semibold text-zinc-50">
        {part}
      </strong>
    ) : (
      <span key={`${keyPrefix}-${i}`}>{part}</span>
    ),
  );
}

export default function MarkdownLite({ text }: { text: string }) {
  const lines = text.split('\n');
  const blocks: { type: 'p' | 'ul'; lines: string[] }[] = [];

  for (const line of lines) {
    const isBullet = /^\s*[-*]\s+/.test(line);
    const lastBlock = blocks[blocks.length - 1];

    if (isBullet) {
      const content = line.replace(/^\s*[-*]\s+/, '');
      if (lastBlock?.type === 'ul') lastBlock.lines.push(content);
      else blocks.push({ type: 'ul', lines: [content] });
    } else if (line.trim() === '') {
      if (lastBlock && lastBlock.lines[lastBlock.lines.length - 1] !== '') {
        blocks.push({ type: 'p', lines: [''] });
      }
    } else if (lastBlock?.type === 'p') {
      lastBlock.lines.push(line);
    } else {
      blocks.push({ type: 'p', lines: [line] });
    }
  }

  return (
    <div className="space-y-2">
      {blocks.map((block, i) => {
        if (block.lines.every((l) => l === '')) return null;
        if (block.type === 'ul') {
          return (
            <ul key={i} className="list-disc space-y-1 pl-4">
              {block.lines.map((l, j) => (
                <li key={j}>{renderInline(l, `${i}-${j}`)}</li>
              ))}
            </ul>
          );
        }
        return (
          <p key={i} className="whitespace-pre-line">
            {block.lines.map((l, j) => (
              <span key={j}>
                {j > 0 && <br />}
                {renderInline(l, `${i}-${j}`)}
              </span>
            ))}
          </p>
        );
      })}
    </div>
  );
}
