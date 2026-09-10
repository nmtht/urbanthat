import { useEffect, useState } from 'react';
import { cn } from '../utils/cn';
import { Badge, Card, Section } from './ui';

const PHASES = [
  {
    id: 'p0',
    title: 'Phase 0 — спайк',
    goal: 'подтвердить, что подход работает',
    done: 'Ручная демонстрация: открыт Rhino с одним объектом, запущено Unreal-приложение, объект виден в обеих сценах, при редактировании в Rhino обновление видно в Unreal в течение ~1 секунды.',
    items: [
      'Rhino-плагин поднимает WebSocket-сервер, Unreal-клиент подключается.',
      'Один простой объект (куб) из Rhino передаётся и появляется как меш в Unreal.',
      'Изменение позиции объекта в Rhino отражается в Unreal (пересоздание меша) без перезапуска Unreal-приложения.',
    ],
  },
  {
    id: 'p1',
    title: 'Phase 1 — полный протокол',
    goal: 'все типы геометрии, батчи, reconnect, производительность',
    done: 'Тестовый Rhino-файл с ~200–500 объектами на трёх слоях (Zones/Roads/Buildings) с заполненными User Text полностью и корректно отображается в Unreal; живые правки на всех трёх слоях синхронизируются без перезапуска.',
    items: [
      'Поддержаны все три типа исходной геометрии (Curve → polyline, Brep/Extrusion/Surface → mesh) плюс лог для неподдерживаемых типов.',
      'full_sync обрабатывает документ из 200+ объектов без падений и за разумное время (цель < 5 секунд).',
      'Атрибуты User Text доезжают до Unreal и видны в логе/дебаг-выводе.',
      'Классификация по слоям работает, объекты на разных слоях визуально различаются (цвет/материал).',
      'Reconnect: перезапуск Rhino или Unreal восстанавливает соединение и запрашивает full_sync без ручных действий.',
      'Удаление объекта в Rhino удаляет соответствующий актор в Unreal.',
    ],
  },
];

const STEPS = [
  ['docs/protocol.md', 'зафиксировать протокол отдельным файлом до кода'],
  ['Rhino-плагин', 'сервер + сериализация одного куба → лог в консоль Rhino, без Unreal'],
  ['Unreal-модуль', 'клиент, парсинг, AGenericBridgeMeshActor по захардкоженному JSON — рендер меша без сети'],
  ['Соединить', 'критерий приёмки Phase 0'],
  ['Расширить', 'Curve → polyline, batch_upsert, reconnect — Phase 1'],
  ['Прогон', 'файл 200–500 объектов, замер производительности full_sync'],
];

const KEY = 'urbanbridge.acceptance.v1';

export function Acceptance() {
  const [checked, setChecked] = useState<Record<string, boolean>>({});
  useEffect(() => {
    try {
      const raw = localStorage.getItem(KEY);
      if (raw) setChecked(JSON.parse(raw));
    } catch {
      /* noop */
    }
  }, []);
  const toggle = (k: string) =>
    setChecked((c) => {
      const n = { ...c, [k]: !c[k] };
      localStorage.setItem(KEY, JSON.stringify(n));
      return n;
    });

  return (
    <Section
      id="acceptance"
      eyebrow="05 · Контрольные точки"
      title="Критерии приёмки и порядок разработки"
      lead="Чек-листы сохраняются локально в браузере — удобно вести статус спайка по ходу сессий."
    >
      <div className="grid gap-4 lg:grid-cols-2">
        {PHASES.map((p) => {
          const n = p.items.filter((_, i) => checked[`${p.id}.${i}`]).length;
          const pct = Math.round((n / p.items.length) * 100);
          return (
            <Card key={p.id}>
              <div className="flex items-start justify-between gap-3">
                <div>
                  <div className="text-lg font-semibold text-white">{p.title}</div>
                  <div className="text-xs text-slate-500">цель: {p.goal}</div>
                </div>
                <Badge tone={pct === 100 ? 'emerald' : 'slate'}>
                  {n}/{p.items.length}
                </Badge>
              </div>
              <div className="mt-3 h-1.5 overflow-hidden rounded-full bg-slate-800">
                <div className="h-full rounded-full bg-gradient-to-r from-cyan-400 to-emerald-400 transition-all" style={{ width: `${pct}%` }} />
              </div>
              <ul className="mt-4 space-y-2">
                {p.items.map((it, i) => {
                  const k = `${p.id}.${i}`;
                  return (
                    <li key={k}>
                      <label className="flex cursor-pointer items-start gap-3 rounded-lg p-2 transition hover:bg-slate-800/50">
                        <input type="checkbox" checked={!!checked[k]} onChange={() => toggle(k)} className="mt-0.5 accent-cyan-500" />
                        <span className={cn('text-sm', checked[k] ? 'text-slate-500 line-through' : 'text-slate-300')}>{it}</span>
                      </label>
                    </li>
                  );
                })}
              </ul>
              <div className="mt-4 rounded-lg border border-slate-800 bg-slate-950/60 p-3 text-xs leading-relaxed text-slate-400">
                <span className="font-mono uppercase tracking-wider text-slate-500">критерий готовности · </span>
                {p.done}
              </div>
            </Card>
          );
        })}
      </div>

      <div className="mt-8">
        <h3 className="mb-4 text-base font-semibold text-white">Порядок разработки (для сессий с Claude Code)</h3>
        <ol className="grid gap-3 md:grid-cols-3 lg:grid-cols-6">
          {STEPS.map(([t, d], i) => (
            <li key={t} className="relative rounded-xl border border-slate-800 bg-slate-900/60 p-4">
              <div className="font-mono text-2xl font-semibold text-slate-700">{String(i + 1).padStart(2, '0')}</div>
              <div className="mt-1 text-sm font-medium text-white">{t}</div>
              <div className="mt-1 text-xs text-slate-500">{d}</div>
            </li>
          ))}
        </ol>
      </div>
    </Section>
  );
}
