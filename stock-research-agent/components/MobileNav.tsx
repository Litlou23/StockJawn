'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { navItems } from './navItems';

export default function MobileNav() {
  const pathname = usePathname();

  return (
    <nav className="fixed bottom-0 left-0 right-0 z-10 border-t border-zinc-800 bg-zinc-950 md:hidden">
      <div className="flex justify-around">
        {navItems.map((item) => {
          const active = pathname?.startsWith(item.href);
          return (
            <Link
              key={item.href}
              href={item.href}
              className={`flex flex-1 flex-col items-center gap-0.5 py-2.5 text-[10px] font-medium ${
                active ? 'text-violet-400' : 'text-zinc-500'
              }`}
            >
              {item.icon}
              {item.label}
            </Link>
          );
        })}
      </div>
    </nav>
  );
}
