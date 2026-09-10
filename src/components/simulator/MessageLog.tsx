import { useEffect, useMemo, useRef, useState } from 'react';
import type { LogEntry, LogKind } from '../../lib/sim';
import { cn } from '../../utils/cn';
import { Badge, Button } from '../ui';

type Filter = 'all' | LogKind;

const fmtTime = (t: number) => {
  const d = new Date(t);
  return `${d.toLocaleTimeString('ru-RU', { hour12: false })}.${String(d.getMilliseconds()).padStart(3, '0')}`;
};

const fmtBytes = (b: number) => (b > 1024 * 1024 ? `${(b / 1048576).toFixed(2)} MB` : b > 1024 ? `${(b / 1024).toFixed(1)} KB` : `${b} B`);

export function MessageLog({
  log,
  showHeartbeats,
  onToggleHeartbeats,
  onClear,
}: {
  log: LogEntry[];
  showHeartbeats: boolean;
  onToggleHeartbeats: () => void;
  onClear: () => void;
}) {
  const [filter, setFilter] = useState<Filter>('all');
  const [openId, setOpenId] = useState<number | null>(null);
  const [autoScroll, setAutoScroll] = useState(true);
  const listRef = useRef<HTMLDivElement>(null);

  const visible = useMemo(
    () => log.filter((e) => (filter === 'all' || e.kind === filter) && (showHeartbeats || e.msgType !== 'heartbeat')),
    [log, filter, showHeartbeats],
  );

  useEffect(() => {
    if (autoScroll && listRef.current) listRef.current.scrollTop = listRef.current.scrollHeight;
  }, [visible.length, autoScroll]);

  const open = visible.find((e) => e.id === openId);
  const json = useMemo(() => {
    if (!open?.msg) return '';
    const s = JSON.stringify(open.msg, null, 2);
    return s.length > 6000 ? s.slice(0, 6000) + `\n… (обрезано, всего ${s.length.toLocaleString('ru-RU')} символов)` : s;
  }, [open]);

  return (
    <div className="flex h-full flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-slate-800 px-3 py-2">
        <div className="flex rounded-lg bg-slate-900 p-0.5">
          {(
            [
              ['all', 'Все'],
              ['wire', 'Сокет'],
              ['rhino', 'Rhino'],
              ['ue', 'Unreal'],
            ] as [Filter, string][]
          ).map(([f, l]) => (
            <button
              key={f}
              onClick={() => setFilter(f)}
              className={cn(
                'rounded-md px-2.5 py-1 text-[11px] transition',
                filter === f ? 'bg-slate-700 text-white' : 'text-slate-400 hover:text-white',
              )}
            >
              {l}
            </button>
          ))}
        </div>
        <label className="flex items-center gap-1.5 text-[11px] text-slate-400">
          <input type="checkbox" checked={showHeartbeats} onChange={onToggleHeartbeats} className="accent-cyan-500" />
          heartbeat
        </label>
        <label className="flex items-center gap-1.5 text-[11px] text-slate-400">
          <input type="checkbox" checked={autoScroll} onChange={(e) => setAutoScroll(e.target.checked)} className="accent-cyan-500" />
          автопрокрутка
        </label>
        <span className="ml-auto font-mono text-[11px] text-slate-500">{visible.length} записей</span>
        <Button tone="ghost" onClick={onClear}>
          очистить
        </Button>
      </div>
      <div className="grid min-h-0 flex-1 grid-cols-1 md:grid-cols-[1fr_minmax(0,380px)]">
        <div ref={listRef} className="min-h-0 overflow-y-auto font-mono text-[11.5px]">
          {visible.length === 0 && (
            <div className="p-6 text-center text-slate-600">Лог пуст. Запустите Rhino и Unreal выше.</div>
          )}
          {visible.map((e) => (
            <button
              key={e.id}
              onClick={() => setOpenId(e.msg ? (openId === e.id ? null : e.id) : openId)}
              className={cn(
                'flex w-full items-start gap-2 border-b border-slate-800/60 px-3 py-1.5 text-left transition hover:bg-slate-800/40',
                openId === e.id && 'bg-slate-800/60',
                e.msg ? 'cursor-pointer' : 'cursor-default',
              )}
            >
              <span className="shrink-0 text-slate-600">{fmtTime(e.t)}</span>
              <span
                className={cn(
                  'w-12 shrink-0 text-[10px] uppercase',
                  e.kind === 'rhino' ? 'text-amber-400' : e.kind === 'ue' ? 'text-sky-400' : 'text-fuchsia-400',
                )}
              >
                {e.kind === 'wire' ? (e.dir === 'rhino->ue' ? 'R → UE' : 'UE → R') : e.kind}
              </span>
              <span
                className={cn(
                  'min-w-0 flex-1 break-words',
                  e.level === 'error' ? 'text-rose-300' : e.level === 'warn' ? 'text-amber-200' : 'text-slate-300',
                )}
              >
                {e.msgType && <span className="mr-1.5 text-white">{e.msgType}</span>}
                {e.msgType ? e.text.replace(`${e.msgType} — `, '').replace(e.msgType, '') : e.text}
              </span>
              {e.bytes !== undefined && <span className="shrink-0 text-slate-500">{fmtBytes(e.bytes)}</span>}
            </button>
          ))}
        </div>
        <div className="hidden min-h-0 flex-col border-l border-slate-800 md:flex">
          {open?.msg ? (
            <>
              <div className="flex items-center gap-2 border-b border-slate-800 px-3 py-2">
                <Badge tone={open.dir === 'rhino->ue' ? 'amber' : 'sky'}>{open.dir}</Badge>
                <code className="font-mono text-xs text-white">{open.msgType}</code>
                <span className="ml-auto font-mono text-[11px] text-slate-500">{fmtBytes(open.bytes ?? 0)}</span>
              </div>
              <pre className="min-h-0 flex-1 overflow-auto p-3 font-mono text-[11px] leading-relaxed text-emerald-200/90">{json}</pre>
            </>
          ) : (
            <div className="flex flex-1 items-center justify-center p-6 text-center text-xs text-slate-600">
              Кликните по сообщению в логе сокета, чтобы увидеть JSON
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
