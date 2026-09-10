import { useState, type ReactNode } from 'react';
import { cn } from '../utils/cn';

export function Section({
  id,
  eyebrow,
  title,
  lead,
  children,
  className,
}: {
  id: string;
  eyebrow: string;
  title: string;
  lead?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section id={id} className={cn('scroll-mt-24 py-16 md:py-24', className)}>
      <div className="mx-auto max-w-7xl px-5 md:px-8">
        <div className="mb-10 max-w-3xl">
          <div className="mb-3 font-mono text-xs uppercase tracking-[0.25em] text-cyan-400">{eyebrow}</div>
          <h2 className="text-3xl font-semibold tracking-tight text-white md:text-4xl">{title}</h2>
          {lead && <p className="mt-4 text-base leading-relaxed text-slate-400 md:text-lg">{lead}</p>}
        </div>
        {children}
      </div>
    </section>
  );
}

export function Badge({
  children,
  tone = 'slate',
  className,
}: {
  children: ReactNode;
  tone?: 'slate' | 'amber' | 'sky' | 'violet' | 'emerald' | 'rose' | 'cyan';
  className?: string;
}) {
  const tones = {
    slate: 'bg-slate-800 text-slate-300 ring-slate-700',
    amber: 'bg-amber-500/10 text-amber-300 ring-amber-500/30',
    sky: 'bg-sky-500/10 text-sky-300 ring-sky-500/30',
    violet: 'bg-violet-500/10 text-violet-300 ring-violet-500/30',
    emerald: 'bg-emerald-500/10 text-emerald-300 ring-emerald-500/30',
    rose: 'bg-rose-500/10 text-rose-300 ring-rose-500/30',
    cyan: 'bg-cyan-500/10 text-cyan-300 ring-cyan-500/30',
  };
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 rounded-md px-2 py-0.5 font-mono text-[11px] font-medium ring-1 ring-inset',
        tones[tone],
        className,
      )}
    >
      {children}
    </span>
  );
}

export function Card({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div className={cn('rounded-2xl border border-slate-800 bg-slate-900/60 p-5 shadow-[0_0_0_1px_rgba(255,255,255,0.02)_inset]', className)}>
      {children}
    </div>
  );
}

export function CodeBlock({ code, title, className }: { code: string; title?: string; className?: string }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
      setTimeout(() => setCopied(false), 1200);
    } catch {
      /* noop */
    }
  };
  return (
    <div className={cn('overflow-hidden rounded-xl border border-slate-800 bg-slate-950', className)}>
      <div className="flex items-center justify-between border-b border-slate-800 px-3 py-1.5">
        <span className="font-mono text-[11px] text-slate-500">{title ?? 'json'}</span>
        <button
          onClick={copy}
          className="rounded px-2 py-0.5 font-mono text-[11px] text-slate-400 transition hover:bg-slate-800 hover:text-white"
        >
          {copied ? 'скопировано' : 'копировать'}
        </button>
      </div>
      <pre className="overflow-x-auto p-4 font-mono text-[12.5px] leading-relaxed text-slate-300">
        <code dangerouslySetInnerHTML={{ __html: highlight(code) }} />
      </pre>
    </div>
  );
}

function highlight(src: string): string {
  const esc = src.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  return esc.replace(
    /("(?:\\.|[^"\\])*")(\s*:)?|\b(true|false|null)\b|(-?\d+(?:\.\d+)?)|(\/\/[^\n]*|\/\*[\s\S]*?\*\/)/g,
    (m, str, colon, kw, num, comment) => {
      if (comment) return `<span class="text-slate-600 italic">${comment}</span>`;
      if (str) {
        if (colon) return `<span class="text-sky-300">${str}</span>${colon}`;
        return `<span class="text-emerald-300">${str}</span>`;
      }
      if (kw) return `<span class="text-violet-300">${kw}</span>`;
      if (num) return `<span class="text-amber-300">${num}</span>`;
      return m;
    },
  );
}

export function Button({
  children,
  onClick,
  disabled,
  tone = 'default',
  className,
  title,
}: {
  children: ReactNode;
  onClick?: () => void;
  disabled?: boolean;
  tone?: 'default' | 'primary' | 'danger' | 'ghost';
  className?: string;
  title?: string;
}) {
  const tones = {
    default: 'bg-slate-800 text-slate-200 hover:bg-slate-700 ring-slate-700',
    primary: 'bg-cyan-500 text-slate-950 hover:bg-cyan-400 ring-cyan-400/50 font-semibold',
    danger: 'bg-rose-500/15 text-rose-300 hover:bg-rose-500/25 ring-rose-500/30',
    ghost: 'bg-transparent text-slate-300 hover:bg-slate-800 ring-slate-800',
  };
  return (
    <button
      title={title}
      onClick={onClick}
      disabled={disabled}
      className={cn(
        'inline-flex items-center justify-center gap-1.5 rounded-lg px-3 py-1.5 text-xs ring-1 ring-inset transition disabled:cursor-not-allowed disabled:opacity-40',
        tones[tone],
        className,
      )}
    >
      {children}
    </button>
  );
}
