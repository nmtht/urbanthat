import { useMemo, useState } from 'react';
import { actorColor, type BridgeActor } from '../../lib/geometry';
import { cn } from '../../utils/cn';
import { Badge } from '../ui';

export function RegistryPanel({
  actors,
  selectedId,
  onSelect,
}: {
  actors: BridgeActor[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  const [q, setQ] = useState('');
  const rows = useMemo(() => {
    const s = q.trim().toLowerCase();
    const list = s
      ? actors.filter((a) => a.id.includes(s) || a.layer.toLowerCase().includes(s) || a.hint.toLowerCase().includes(s))
      : actors;
    return list.slice(0, 400);
  }, [actors, q]);

  const byHint = useMemo(() => {
    const m: Record<string, number> = {};
    for (const a of actors) m[a.hint] = (m[a.hint] ?? 0) + 1;
    return m;
  }, [actors]);

  return (
    <div className="flex h-full flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-slate-800 px-3 py-2">
        <code className="font-mono text-[11px] text-slate-400">TMap&lt;FGuid, AActor*&gt;</code>
        <span className="font-mono text-[11px] text-white">{actors.length}</span>
        {Object.entries(byHint).map(([h, n]) => (
          <Badge key={h} tone={h === 'Zone' ? 'emerald' : h === 'Road' ? 'amber' : h === 'Building' ? 'slate' : 'rose'}>
            {h} {n}
          </Badge>
        ))}
        <input
          value={q}
          onChange={(e) => setQ(e.target.value)}
          placeholder="фильтр: id / слой / hint"
          className="ml-auto w-44 rounded-md border border-slate-800 bg-slate-900 px-2 py-1 font-mono text-[11px] text-slate-200 outline-none focus:border-cyan-500/50"
        />
      </div>
      <div className="min-h-0 flex-1 overflow-auto">
        <table className="w-full text-left font-mono text-[11px]">
          <thead className="sticky top-0 bg-slate-900 text-[10px] uppercase text-slate-500">
            <tr>
              <th className="px-3 py-1.5 font-medium">id</th>
              <th className="px-3 py-1.5 font-medium">класс</th>
              <th className="px-3 py-1.5 font-medium">hint</th>
              <th className="px-3 py-1.5 font-medium">слой</th>
              <th className="px-3 py-1.5 font-medium">геометрия</th>
              <th className="px-3 py-1.5 font-medium">v</th>
              <th className="px-3 py-1.5 font-medium">attributes</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((a) => (
              <tr
                key={a.id}
                onClick={() => onSelect(a.id)}
                className={cn(
                  'cursor-pointer border-t border-slate-800/60 transition hover:bg-slate-800/40',
                  a.id === selectedId && 'bg-yellow-500/10',
                )}
              >
                <td className="px-3 py-1.5 text-slate-400">{a.id.slice(0, 8)}…</td>
                <td className="px-3 py-1.5 text-sky-300">{a.cls.replace('AGenericBridge', '').replace('Actor', '')}</td>
                <td className="px-3 py-1.5">
                  <span className="inline-flex items-center gap-1.5">
                    <span className="h-2 w-2 rounded-sm" style={{ background: actorColor(a) }} />
                    {a.hint}
                  </span>
                </td>
                <td className="px-3 py-1.5 text-slate-300">{a.layer}</td>
                <td className="px-3 py-1.5 text-slate-400">
                  {a.cls === 'AGenericBridgeMeshActor'
                    ? `${a.vertexCount}v / ${a.triangleCount}t${a.normalsFromRhino ? '' : ' · n=calc'}`
                    : `spline ${a.vertexCount} pts`}
                </td>
                <td className="px-3 py-1.5 text-slate-500">{a.version}</td>
                <td className="max-w-[260px] truncate px-3 py-1.5 text-slate-500">
                  {Object.entries(a.attributes)
                    .map(([k, v]) => `${k}=${v}`)
                    .join(' ')}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {actors.length === 0 && <div className="p-6 text-center text-xs text-slate-600">Реестр пуст — Unreal не подключён или full_sync ещё не пришёл.</div>}
        {rows.length < actors.length && (
          <div className="p-3 text-center font-mono text-[11px] text-slate-600">показано {rows.length} из {actors.length}</div>
        )}
      </div>
    </div>
  );
}
