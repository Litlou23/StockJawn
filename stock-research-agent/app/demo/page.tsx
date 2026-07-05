const SOURCES = [
  { label: 'The New York Times', url: 'https://www.nytimes.com/' },
  { label: 'The Dispatch', url: 'https://thedispatch.com/' },
  { label: 'Fox News', url: 'https://www.foxnews.com/' },
  { label: 'Al Jazeera', url: 'https://www.aljazeera.com/' },
];

export default function DemoPage() {
  return (
    <div className="flex h-full flex-col gap-6 p-6">
      <div>
        <h1 className="text-xl font-semibold text-zinc-100">Demo</h1>
        <p className="mt-1 text-sm text-zinc-400">
          News source pipeline test — live feeds from four outlets.
        </p>
      </div>

      <div className="grid flex-1 grid-cols-1 gap-4 lg:grid-cols-2">
        {SOURCES.map(({ label, url }) => (
          <div
            key={url}
            className="flex flex-col overflow-hidden rounded-xl border border-zinc-800 bg-zinc-900"
          >
            <div className="flex items-center gap-2 border-b border-zinc-800 px-4 py-2">
              <span className="h-2 w-2 rounded-full bg-green-400" />
              <span className="text-sm font-medium text-zinc-200">{label}</span>
              <a
                href={url}
                target="_blank"
                rel="noopener noreferrer"
                className="ml-auto text-xs text-zinc-500 hover:text-violet-400"
              >
                {url}
              </a>
            </div>
            <iframe
              src={url}
              title={label}
              sandbox="allow-scripts allow-same-origin"
              className="h-96 w-full flex-1 bg-white lg:h-full"
              loading="lazy"
            />
          </div>
        ))}
      </div>
    </div>
  );
}
