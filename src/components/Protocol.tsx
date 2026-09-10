import { useState } from 'react';
import { Badge, Card, CodeBlock, Section } from './ui';
import { cn } from '../utils/cn';

type Dir = 'rhino→ue' | 'ue→rhino' | '↔';

const MESSAGES: { type: string; dir: Dir; when: string; desc: string; code: string }[] = [
  {
    type: 'full_sync',
    dir: 'rhino→ue',
    when: 'сразу после соединения · по request_full_sync · EndOpenDocument',
    desc: 'Полная синхронизация документа. Unreal очищает реестр и создаёт акторы заново.',
    code: `{
  "type": "full_sync",
  "document_id": "guid-документа-rhino",
  "units": "meters",
  "objects": [ /* массив object_payload */ ]
}`,
  },
  {
    type: 'object_upserted',
    dir: 'rhino→ue',
    when: 'AddRhinoObject · ReplaceRhinoObject',
    desc: 'Один объект добавлен или изменён. Если ID уже в реестре — актор обновляется, иначе создаётся.',
    code: `{
  "type": "object_upserted",
  "object": { /* object_payload */ }
}`,
  },
  {
    type: 'batch_upsert',
    dir: 'rhino→ue',
    when: 'групповые операции (массовое редактирование)',
    desc: 'Несколько объектов разом — вместо потока одиночных object_upserted.',
    code: `{
  "type": "batch_upsert",
  "objects": [ /* массив object_payload */ ]
}`,
  },
  {
    type: 'object_deleted',
    dir: 'rhino→ue',
    when: 'DeleteRhinoObject',
    desc: 'Unreal находит актор по ID, вызывает Destroy() и удаляет запись из реестра.',
    code: `{ "type": "object_deleted", "id": "rhino-object-guid" }`,
  },
  {
    type: 'heartbeat',
    dir: '↔',
    when: 'каждые 5 секунд в обе стороны',
    desc: 'Обнаружение разрыва соединения быстрее, чем это сделает сам WebSocket.',
    code: `{ "type": "heartbeat", "timestamp": 1234567890 }`,
  },
  {
    type: 'request_full_sync',
    dir: 'ue→rhino',
    when: 'старт Unreal · после переподключения · debug-команда',
    desc: 'Запрос полной синхронизации. Rhino отвечает full_sync.',
    code: `{ "type": "request_full_sync" }`,
  },
];

const PAYLOAD = `{
  "id": "rhino-object-guid",
  "layer": "Zones",
  "geometry_type": "mesh",
  "mesh": {
    "vertices": [0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0, 0.0],
    "indices": [0, 1, 2],
    "normals": [0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 1.0]
  },
  "polyline": null,
  "attributes": {
    "zone_type": "residential",
    "far": "2.5",
    "height_max": "40"
  }
}`;

const FIELDS: [string, string, string][] = [
  ['id', 'string', 'obj.Id.ToString() — GUID объекта Rhino, ключ реестра акторов'],
  ['layer', 'string', 'doc.Layers[obj.Attributes.LayerIndex].FullPath, например "Zones::Green"'],
  ['geometry_type', '"mesh" | "polyline"', 'mesh — Brep/Extrusion/Surface/Mesh; polyline — любая Curve'],
  ['mesh.vertices', 'number[]', 'плоский массив [x0,y0,z0, x1,y1,z1, …] в метрах'],
  ['mesh.indices', 'number[]', 'индексы треугольников'],
  ['mesh.normals', 'number[] | null', 'опционально; null → Unreal считает нормали по треугольникам'],
  ['polyline.points', 'number[]', 'плоский массив координат, дискретизация с сегментом ≤ 0.5 м'],
  ['attributes', 'Record<string,string>', 'весь User Text как есть; приведение типов — на стороне Unreal'],
];

const dirTone = (d: Dir) => (d === 'rhino→ue' ? 'amber' : d === 'ue→rhino' ? 'sky' : 'violet');

export function Protocol() {
  const [active, setActive] = useState(0);
  const m = MESSAGES[active];
  return (
    <Section
      id="protocol"
      eyebrow="02 · docs/protocol.md"
      title="Семь типов сообщений, одно поле type"
      lead="Протокол фиксируется отдельным файлом до кода и является живым источником правды для обеих сторон. Все сообщения — JSON-объекты."
    >
      <div className="grid gap-4 lg:grid-cols-[280px_1fr]">
        <div className="flex gap-2 overflow-x-auto lg:flex-col lg:overflow-visible">
          {MESSAGES.map((x, i) => (
            <button
              key={x.type}
              onClick={() => setActive(i)}
              className={cn(
                'flex shrink-0 items-center justify-between gap-3 rounded-xl border px-4 py-3 text-left transition',
                i === active
                  ? 'border-cyan-500/40 bg-cyan-500/10'
                  : 'border-slate-800 bg-slate-900/40 hover:border-slate-700',
              )}
            >
              <code className={cn('font-mono text-sm', i === active ? 'text-white' : 'text-slate-300')}>{x.type}</code>
              <Badge tone={dirTone(x.dir)}>{x.dir}</Badge>
            </button>
          ))}
        </div>
        <Card className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <code className="font-mono text-lg text-white">{m.type}</code>
            <Badge tone={dirTone(m.dir)}>{m.dir}</Badge>
          </div>
          <p className="mt-2 text-sm text-slate-300">{m.desc}</p>
          <p className="mt-1 text-xs text-slate-500">
            <span className="font-mono uppercase tracking-wider">когда:</span> {m.when}
          </p>
          <CodeBlock code={m.code} title={`${m.type}.json`} className="mt-4" />
        </Card>
      </div>

      <div className="mt-10 grid gap-4 lg:grid-cols-2">
        <div>
          <div className="mb-3 flex items-center gap-2">
            <h3 className="text-base font-semibold text-white">object_payload</h3>
            <Badge tone="slate">3.4</Badge>
          </div>
          <CodeBlock code={PAYLOAD} title="object_payload.json" />
        </div>
        <div>
          <h3 className="mb-3 text-base font-semibold text-white">Поля</h3>
          <div className="overflow-hidden rounded-xl border border-slate-800">
            <table className="w-full text-left text-sm">
              <tbody>
                {FIELDS.map(([k, t, d]) => (
                  <tr key={k} className="border-b border-slate-800 last:border-0">
                    <td className="whitespace-nowrap px-3 py-2.5 align-top font-mono text-[12px] text-sky-300">{k}</td>
                    <td className="whitespace-nowrap px-3 py-2.5 align-top font-mono text-[11px] text-slate-500">{t}</td>
                    <td className="px-3 py-2.5 align-top text-xs text-slate-400">{d}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="mt-3 text-xs leading-relaxed text-slate-500">
            Ровно одно из полей <code className="font-mono">mesh</code> / <code className="font-mono">polyline</code>{' '}
            заполнено. Точки, аннотации, блоки — вне скоупа: плагин пишет их в лог и не отправляет.
          </p>
        </div>
      </div>
    </Section>
  );
}
