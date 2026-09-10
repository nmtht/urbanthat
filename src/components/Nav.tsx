import { useEffect, useState } from 'react';
import { cn } from '../utils/cn';

export const NAV = [
  { id: 'overview', label: 'Обзор' },
  { id: 'architecture', label: 'Архитектура' },
  { id: 'protocol', label: 'Протокол' },
  { id: 'simulator', label: 'Симулятор' },
  { id: 'classifier', label: 'Классификатор' },
  { id: 'acceptance', label: 'Приёмка' },
  { id: 'scope', label: 'Скоуп и репо' },
];

export function Nav() {
  const [active, setActive] = useState('overview');
  const [open, setOpen] = useState(false);

  useEffect(() => {
    const obs = new IntersectionObserver(
      (entries) => {
        for (const e of entries) if (e.isIntersecting) setActive(e.target.id);
      },
      { rootMargin: '-40% 0px -55% 0px' },
    );
    NAV.forEach((n) => {
      const el = document.getElementById(n.id);
      if (el) obs.observe(el);
    });
    return () => obs.disconnect();
  }, []);

  return (
    <header className="sticky top-0 z-40 border-b border-slate-800/80 bg-slate-950/80 backdrop-blur">
      <div className="mx-auto flex h-14 max-w-7xl items-center justify-between px-5 md:px-8">
        <a href="#overview" className="flex items-center gap-2.5">
          <span className="relative flex h-7 w-7 items-center justify-center rounded-lg bg-gradient-to-br from-amber-400 via-fuchsia-500 to-sky-500">
            <span className="h-2.5 w-2.5 rounded-sm bg-slate-950" />
          </span>
          <span className="font-semibold tracking-tight text-white">UrbanBridge</span>
          <span className="hidden font-mono text-[11px] text-slate-500 sm:inline">Rhino → Unreal · Phase 0–1</span>
        </a>
        <nav className="hidden items-center gap-1 md:flex">
          {NAV.map((n) => (
            <a
              key={n.id}
              href={`#${n.id}`}
              className={cn(
                'rounded-md px-3 py-1.5 text-sm transition',
                active === n.id ? 'bg-slate-800 text-white' : 'text-slate-400 hover:text-white',
              )}
            >
              {n.label}
            </a>
          ))}
        </nav>
        <button
          className="rounded-md p-2 text-slate-300 md:hidden"
          onClick={() => setOpen((o) => !o)}
          aria-label="Меню"
        >
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            {open ? <path d="M6 6l12 12M18 6L6 18" /> : <path d="M4 7h16M4 12h16M4 17h16" />}
          </svg>
        </button>
      </div>
      {open && (
        <nav className="flex flex-col border-t border-slate-800 px-5 py-2 md:hidden">
          {NAV.map((n) => (
            <a
              key={n.id}
              href={`#${n.id}`}
              onClick={() => setOpen(false)}
              className={cn('rounded-md px-3 py-2 text-sm', active === n.id ? 'text-white' : 'text-slate-400')}
            >
              {n.label}
            </a>
          ))}
        </nav>
      )}
    </header>
  );
}
