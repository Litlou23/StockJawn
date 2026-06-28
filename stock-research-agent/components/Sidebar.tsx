'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { navItems } from './navItems';

export default function Sidebar() {
  const pathname = usePathname();

  return (
    <aside className="hidden h-screen w-60 flex-col border-r border-zinc-800 bg-zinc-950 px-3 py-4 md:flex">
      <div className="mb-6 flex items-center gap-2 px-2">
        <span className="text-xl">📈</span>
        <div>
          <div className="flex items-center gap-1 text-sm font-semibold text-zinc-100">
            Stock Agent <span className="text-violet-400">+</span>
          </div>
          <div className="text-[11px] text-zinc-500">Personal Research Assistant</div>
        </div>
      </div>

      <nav className="flex flex-1 flex-col gap-1">
        {navItems.map((item) => {
          const active = pathname?.startsWith(item.href);
          return (
            <Link
              key={item.href}
              href={item.href}
              className={`flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition ${
                active ? 'bg-violet-600/15 text-violet-300' : 'text-zinc-400 hover:bg-zinc-900 hover:text-zinc-200'
              }`}
            >
              {item.icon}
              {item.label}
            </Link>
          );
        })}
      </nav>

      <div className="mt-4 flex items-center gap-2 rounded-lg border border-zinc-800 bg-zinc-900 px-3 py-2">
        <div className="flex h-7 w-7 items-center justify-center rounded-full bg-violet-600 text-xs font-semibold text-white">
          M
        </div>
        <div>
          <div className="text-xs font-medium text-zinc-200">My Account</div>
          <div className="text-[11px] text-green-400">Private Mode</div>
        </div>
      </div>
    </aside>
  );
}
