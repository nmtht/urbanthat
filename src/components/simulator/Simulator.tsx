import { useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { actorColor, UNIT_LABEL, UNIT_TO_METERS, type UnitSystem } from '../../lib/geometry';
import { docBoundsMeters, nearest, renderRhino, renderUnreal } from '../../lib/render';
import { sim, type ConnState } from '../../lib/sim';
import { BRIDGE_URL } from '../../lib/types';
import { cn } from '../../utils/cn';
import { Badge, Button, Section } from '../ui';
import { MessageLog } from './MessageLog';
import { RegistryPanel } from './RegistryPanel';
import { Viewport } from './Viewport';

const CONN_LABEL: Record<ConnState, string> = {
  disconnected: 'Disconnected',
  connecting: 'Connecting',
  connected: 'Connected',
  reconnecting: 'Reconnecting',
};

function Toggle({ on, onChange, tone }: { on: boolean; onChange: (v: boolean) => void; tone: 'amber' | 'sky' }) {
  return (
    <button
      onClick={() => onChange(!on)}
      className={cn(
        'relative h-6 w-11 shrink-0 rounded-full transition',
        on ? (tone === 'amber' ? 'bg-amber-500' : 'bg-sky-500') : 'bg-slate-700',
      )}
      aria-pressed={on}
    >
      <span className={cn('absolute top-0.5 h-5 w-5 rounded-full bg-white shadow transition', on ? 'left-[22px]' : 'left-0.5')} />
    </button>
  );
}

export function Simulator() {
  useSyncExternalStore(sim.subscribe, () => sim.version);
  const s = sim.state;
  const [tab, setTab] = useState<'log' | 'registry'>('log');
  const [blocks, setBlocks] = useState(3);
  const [units, setUnits] = useState<UnitSystem>('millimeters');
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    sim.start();
    const t = window.setInterval(() => setNow(Date.now()), 250);
    return () => {
      sim.stop();
      window.clearInterval(t);
    };
  }, []);

  const actors = useMemo(() => Array.from(s.registry.values()), [s.registry]);
  const docBounds = useMemo(() => docBoundsMeters(s.doc), [s.doc]);
  const selectedObj = s.doc.objects.find((o) => o.id === s.selectedId) ?? null;
  const selectedActor = s.selectedId ? s.registry.get(s.selectedId) ?? null : null;
  const retryIn = s.nextRetryAt ? Math.max(0, (s.nextRetryAt - now) / 1000) : null;
  const connected = s.conn === 'connected';
  const k = UNIT_TO_METERS[s.doc.units];

  const hbAge = (t: number | null) => (t ? `${((now - t) / 1000).toFixed(0)} s назад` : '—');

  const connTone =
    s.conn === 'connected' ? 'text-emerald-300' : s.conn === 'disconnected' ? 'text-slate-500' : 'text-amber-300';

  return (
    <Section
      id="simulator"
      eyebrow="03 · Playground"
      title="Симулятор моста"
      lead={
        <>
          Слева — мок Rhino-документа (единицы документа, слои, User Text), справа — Unreal-реестр акторов, построенный
          исключительно из принятых по «сокету» JSON-сообщений. Рвите соединение, правьте объекты и смотрите на лог.
        </>
      }
    >
      {/* Панель процессов */}
      <div className="grid gap-3 rounded-2xl border border-slate-800 bg-slate-900/60 p-3 md:grid-cols-[1fr_auto_1fr] md:items-center">
        <div className="flex items-center gap-3 rounded-xl bg-slate-950/60 p-3">
          <Toggle on={s.rhinoRunning} onChange={(v) => sim.setRhino(v)} tone="amber" />
          <div className="min-w-0">
            <div className="text-sm font-semibold text-white">Rhino + UrbanBridgePlugin</div>
            <div className="truncate font-mono text-[11px] text-slate-500">
              {s.rhinoRunning ? `WS-сервер слушает ${BRIDGE_URL}` : 'Rhino не запущен'}
            </div>
          </div>
          <Badge tone={s.rhinoRunning ? 'amber' : 'slate'} className="ml-auto">
            {s.rhinoRunning ? 'server up' : 'offline'}
          </Badge>
        </div>

        <div className="flex flex-col items-center px-2 py-1">
          <div className="flex items-center gap-2">
            <span className={cn('h-px w-10 md:w-14', connected ? 'bg-gradient-to-r from-amber-400 to-fuchsia-400' : 'bg-slate-700')} />
            <span
              className={cn(
                'relative flex h-3 w-3 items-center justify-center',
              )}
            >
              {connected && <span className="absolute h-3 w-3 animate-ping rounded-full bg-emerald-400/60" />}
              <span
                className={cn(
                  'h-2.5 w-2.5 rounded-full',
                  connected ? 'bg-emerald-400' : s.conn === 'disconnected' ? 'bg-slate-600' : 'bg-amber-400 animate-pulse',
                )}
              />
            </span>
            <span className={cn('h-px w-10 md:w-14', connected ? 'bg-gradient-to-r from-fuchsia-400 to-sky-400' : 'bg-slate-700')} />
          </div>
          <div className={cn('mt-1.5 font-mono text-xs font-semibold', connTone)}>
            {CONN_LABEL[s.conn]}
            {retryIn !== null && ` · retry in ${retryIn.toFixed(1)}s`}
          </div>
          <div className="font-mono text-[10px] text-slate-500">
            {s.conn === 'reconnecting' || s.conn === 'connecting'
              ? `попытка #${s.attempt} · backoff ${Math.min(s.backoffMs, 10000) / 1000}s`
              : connected
                ? `hb R ${hbAge(s.stats.lastHbFromRhino)} · UE ${hbAge(s.stats.lastHbFromUe)}`
                : 'клиент не запущен'}
          </div>
        </div>

        <div className="flex items-center gap-3 rounded-xl bg-slate-950/60 p-3">
          <Toggle on={s.unrealRunning} onChange={(v) => sim.setUnreal(v)} tone="sky" />
          <div className="min-w-0">
            <div className="text-sm font-semibold text-white">Unreal + UrbanBridgeReceiver</div>
            <div className="truncate font-mono text-[11px] text-slate-500">
              {s.unrealRunning ? `UBridgeConnection → ${BRIDGE_URL}` : 'приложение не запущено'}
            </div>
          </div>
          <Badge tone={s.unrealRunning ? 'sky' : 'slate'} className="ml-auto">
            {s.unrealRunning ? 'client' : 'offline'}
          </Badge>
        </div>
      </div>

      {/* Сценарии */}
      <div className="mt-3 flex flex-wrap items-center gap-2 text-xs">
        <span className="font-mono text-[11px] uppercase tracking-wider text-slate-500">сценарии:</span>
        <Button
          onClick={() => {
            sim.setRhino(true);
            sim.setUnreal(true);
          }}
          tone="primary"
        >
          ▶ Запустить оба
        </Button>
        <Button
          onClick={() => {
            sim.setRhino(false);
            window.setTimeout(() => sim.setRhino(true), 3500);
          }}
          disabled={!s.rhinoRunning}
          title="Закрыть Rhino и открыть снова через 3.5 с — проверка reconnect"
        >
          Перезапустить Rhino (3.5 с)
        </Button>
        <Button
          onClick={() => {
            sim.setUnreal(false);
            window.setTimeout(() => sim.setUnreal(true), 1500);
          }}
          disabled={!s.unrealRunning}
        >
          Перезапустить Unreal
        </Button>
        <Button
          onClick={() => {
            sim.setRhino(false);
            sim.setUnreal(false);
          }}
          tone="ghost"
        >
          Остановить всё
        </Button>
      </div>

      {/* Вьюпорты */}
      <div className="mt-6 grid gap-4 lg:grid-cols-2">
        {/* Rhino */}
        <div className="overflow-hidden rounded-2xl border border-amber-500/20 bg-slate-900/60">
          <div className="flex items-center justify-between border-b border-slate-800 px-4 py-2.5">
            <div className="flex items-center gap-2">
              <span className="h-2 w-2 rounded-full bg-amber-400" />
              <span className="text-sm font-semibold text-white">Rhino · Perspective</span>
              <Badge tone="amber">{s.doc.objects.length} объектов</Badge>
              <Badge tone="slate">units: {s.doc.units}</Badge>
            </div>
            <span className="font-mono text-[10px] text-slate-500">doc {s.doc.id.slice(0, 8)}</span>
          </div>
          <Viewport
            deps={[s.doc, s.selectedId]}
            render={(ctx, w, h) => renderRhino(ctx, w, h, s.doc, s.selectedId)}
            onPick={(centers, x, y) => sim.select(nearest(centers, x, y))}
            overlay={
              <div className="pointer-events-none absolute left-3 top-3 space-y-1 font-mono text-[10px] text-slate-600">
                <div>Layers: Zones · Zones::Green · Zones::Industrial · Roads · Roads::Primary · Buildings</div>
                <div>click → select object</div>
              </div>
            }
          />
          <div className="space-y-3 border-t border-slate-800 p-3">
            <div className="flex flex-wrap items-center gap-1.5">
              <span className="mr-1 font-mono text-[10px] uppercase tracking-wider text-slate-500">добавить</span>
              <Button onClick={() => sim.addObject('Zone')}>+ Zone (Brep)</Button>
              <Button onClick={() => sim.addObject('Building')}>+ Building (Extrusion)</Button>
              <Button onClick={() => sim.addObject('Road')}>+ Road (Curve)</Button>
              <Button onClick={() => sim.addObject('Point')} tone="ghost" title="Неподдерживаемый тип — плагин запишет в лог и пропустит">
                + Point (вне скоупа)
              </Button>
            </div>
            <div className="flex flex-wrap items-center gap-1.5">
              <span className="mr-1 font-mono text-[10px] uppercase tracking-wider text-slate-500">выбранный</span>
              <Button disabled={!selectedObj} onClick={() => sim.moveSelected(6, 0)}>
                Move +6 м X
              </Button>
              <Button disabled={!selectedObj} onClick={() => sim.moveSelected(0, 6)}>
                Move +6 м Y
              </Button>
              <Button disabled={!selectedObj} onClick={() => sim.deleteSelected()} tone="danger">
                Delete
              </Button>
              {selectedObj && (
                <span className="ml-auto font-mono text-[11px] text-slate-400">
                  {selectedObj.kind} · {selectedObj.layer} · {selectedObj.id.slice(0, 8)}…
                </span>
              )}
            </div>
            <div className="flex flex-wrap items-center gap-1.5">
              <span className="mr-1 font-mono text-[10px] uppercase tracking-wider text-slate-500">группой</span>
              <Button onClick={() => sim.batchEdit('raiseBuildings')}>+1 этаж всем зданиям</Button>
              <Button onClick={() => sim.batchEdit('rezone')}>Сменить zone_type у зон</Button>
              <Button onClick={() => sim.batchEdit('shiftRoads')}>Сдвинуть дороги</Button>
            </div>
            <div className="flex flex-wrap items-center gap-1.5">
              <span className="mr-1 font-mono text-[10px] uppercase tracking-wider text-slate-500">документ</span>
              <select
                value={blocks}
                onChange={(e) => setBlocks(Number(e.target.value))}
                className="rounded-lg border border-slate-700 bg-slate-800 px-2 py-1.5 text-xs text-slate-200"
              >
                <option value={2}>2×2 кварталов (~18 об.)</option>
                <option value={3}>3×3 (~33 об.)</option>
                <option value={5}>5×5 (~85 об.)</option>
                <option value={8}>8×8 (~190 об.)</option>
                <option value={10}>10×10 (~290 об.)</option>
                <option value={13}>13×13 (~460 об.)</option>
              </select>
              <select
                value={units}
                onChange={(e) => setUnits(e.target.value as UnitSystem)}
                className="rounded-lg border border-slate-700 bg-slate-800 px-2 py-1.5 text-xs text-slate-200"
              >
                {(['millimeters', 'centimeters', 'meters', 'feet'] as UnitSystem[]).map((u) => (
                  <option key={u} value={u}>
                    {u} ({UNIT_LABEL[u]})
                  </option>
                ))}
              </select>
              <Button onClick={() => sim.openDocument(blocks, units)} tone="primary">
                Открыть новый файл
              </Button>
              <label className="ml-auto flex items-center gap-2 font-mono text-[11px] text-slate-400">
                сегмент кривой
                <input
                  type="range"
                  min={0.25}
                  max={5}
                  step={0.25}
                  value={s.segmentLength}
                  onChange={(e) => sim.setSegmentLength(Number(e.target.value))}
                  className="w-20 accent-amber-400"
                />
                {s.segmentLength.toFixed(2)} м
              </label>
            </div>
            {selectedObj && (
              <div className="rounded-lg border border-slate-800 bg-slate-950/60 p-2.5">
                <div className="mb-1.5 flex items-center gap-2 text-[11px] text-slate-400">
                  <span className="font-mono uppercase tracking-wider text-slate-500">User Text</span>
                  <span>Object Properties → Attributes → User Text</span>
                </div>
                <div className="grid gap-1.5 sm:grid-cols-3">
                  {Object.entries(selectedObj.attributes).map(([key, val]) => (
                    <label key={key} className="flex items-center gap-1.5 font-mono text-[11px]">
                      <span className="w-20 shrink-0 truncate text-sky-300">{key}</span>
                      <input
                        value={val}
                        onChange={(e) => sim.setSelectedAttribute(key, e.target.value)}
                        className="w-full min-w-0 rounded border border-slate-800 bg-slate-900 px-1.5 py-0.5 text-slate-200 outline-none focus:border-amber-400/60"
                      />
                    </label>
                  ))}
                </div>
                <div className="mt-1.5 font-mono text-[10px] text-slate-600">
                  координаты в документе: {selectedObj.footprint[0][0].toFixed(0)}, {selectedObj.footprint[0][1].toFixed(0)}{' '}
                  {UNIT_LABEL[s.doc.units]} → по проводу: {(selectedObj.footprint[0][0] * k).toFixed(2)},{' '}
                  {(selectedObj.footprint[0][1] * k).toFixed(2)} м
                </div>
              </div>
            )}
          </div>
        </div>

        {/* Unreal */}
        <div className="overflow-hidden rounded-2xl border border-sky-500/20 bg-slate-900/60">
          <div className="flex items-center justify-between border-b border-slate-800 px-4 py-2.5">
            <div className="flex items-center gap-2">
              <span className="h-2 w-2 rounded-full bg-sky-400" />
              <span className="text-sm font-semibold text-white">Unreal · UrbanBridge viewport</span>
              <Badge tone="sky">{actors.length} акторов</Badge>
              <Badge tone="slate">1 м = 100 uu</Badge>
            </div>
            <Button onClick={() => sim.requestFullSync()} disabled={!connected} tone="ghost" title="Debug-команда (5.5)">
              ⟳ request_full_sync
            </Button>
          </div>
          <Viewport
            deps={[s.registry, s.selectedId, s.unrealRunning]}
            render={(ctx, w, h) => {
              if (!s.unrealRunning) {
                ctx.fillStyle = '#05070d';
                ctx.fillRect(0, 0, w, h);
                return new Map();
              }
              return renderUnreal(ctx, w, h, actors, s.selectedId, actorColor, docBounds);
            }}
            onPick={(centers, x, y) => sim.select(nearest(centers, x, y))}
            overlay={
              <>
                <div className="pointer-events-none absolute right-3 top-3 rounded-md bg-black/50 px-2 py-1 font-mono text-[11px]">
                  <span className="text-slate-500">UrbanBridge: </span>
                  <span className={connTone}>
                    {s.unrealRunning ? CONN_LABEL[s.conn] : 'App closed'}
                    {retryIn !== null && ` (${retryIn.toFixed(1)}s)`}
                  </span>
                </div>
                <div className="pointer-events-none absolute bottom-3 left-3 flex flex-wrap gap-2 font-mono text-[10px]">
                  {[
                    ['residential', '#f59e0b'],
                    ['commercial', '#3b82f6'],
                    ['mixed', '#a855f7'],
                    ['green', '#22c55e'],
                    ['industrial', '#94a3b8'],
                    ['Road', '#fbbf24'],
                    ['Building', '#cbd5e1'],
                    ['Generic', '#ec4899'],
                  ].map(([l, c]) => (
                    <span key={l} className="flex items-center gap-1 rounded bg-black/40 px-1.5 py-0.5 text-slate-400">
                      <span className="h-2 w-2 rounded-sm" style={{ background: c }} />
                      {l}
                    </span>
                  ))}
                </div>
                {!s.unrealRunning && (
                  <div className="pointer-events-none absolute inset-0 flex items-center justify-center font-mono text-xs text-slate-600">
                    Unreal-приложение не запущено
                  </div>
                )}
              </>
            }
          />
          <div className="space-y-3 border-t border-slate-800 p-3">
            <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
              <Stat label="сообщений" value={String(s.stats.sent)} />
              <Stat label="байт по сокету" value={fmtBytes(s.stats.bytes)} />
              <Stat
                label="последний full_sync"
                value={s.stats.lastFullSync ? `${s.stats.lastFullSync.objects} об.` : '—'}
                sub={s.stats.lastFullSync ? fmtBytes(s.stats.lastFullSync.bytes) : ''}
              />
              <Stat
                label="serialize + parse"
                value={
                  s.stats.lastFullSync
                    ? `${(s.stats.lastFullSync.serializeMs + s.stats.lastFullSync.parseMs).toFixed(1)} ms`
                    : '—'
                }
                sub={s.stats.lastFullSync ? `цель < 5000 ms / 200 об.${s.stats.lastFullSync.skipped ? ` · пропущено ${s.stats.lastFullSync.skipped}` : ''}` : ''}
                good={!!s.stats.lastFullSync && s.stats.lastFullSync.serializeMs + s.stats.lastFullSync.parseMs < 5000}
              />
            </div>
            <div className="rounded-lg border border-slate-800 bg-slate-950/60 p-2.5">
              <div className="mb-1.5 font-mono text-[10px] uppercase tracking-wider text-slate-500">
                инспектор актора (debug-вывод, не финальный UI)
              </div>
              {selectedActor ? (
                <div className="grid gap-x-4 gap-y-1 font-mono text-[11px] sm:grid-cols-2">
                  <Row k="class" v={selectedActor.cls} />
                  <Row k="hint" v={selectedActor.hint} color={actorColor(selectedActor)} />
                  <Row k="layer" v={selectedActor.layer} />
                  <Row k="rhino id" v={selectedActor.id} />
                  <Row
                    k="geometry"
                    v={
                      selectedActor.cls === 'AGenericBridgeMeshActor'
                        ? `${selectedActor.vertexCount} vertices · ${selectedActor.triangleCount} tris · normals ${selectedActor.normalsFromRhino ? 'from Rhino' : 'computed'}`
                        : `${selectedActor.vertexCount} spline points`
                    }
                  />
                  <Row
                    k="location"
                    v={`(${selectedActor.center.map((c) => c.toFixed(0)).join(', ')}) uu · v${selectedActor.version}`}
                  />
                  <div className="sm:col-span-2">
                    <span className="text-slate-500">attributes </span>
                    <span className="text-emerald-300">{JSON.stringify(selectedActor.attributes)}</span>
                  </div>
                </div>
              ) : (
                <div className="font-mono text-[11px] text-slate-600">
                  {s.selectedId && s.unrealRunning
                    ? 'объект выбран в Rhino, но актора нет в реестре (не доставлен или вне скоупа)'
                    : 'кликните по актору во вьюпорте или строке реестра'}
                </div>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* Лог / реестр */}
      <div className="mt-4 overflow-hidden rounded-2xl border border-slate-800 bg-slate-900/60">
        <div className="flex items-center gap-1 border-b border-slate-800 px-3 pt-2">
          {(
            [
              ['log', 'Лог сообщений'],
              ['registry', 'Реестр акторов (Unreal)'],
            ] as const
          ).map(([id, l]) => (
            <button
              key={id}
              onClick={() => setTab(id)}
              className={cn(
                'rounded-t-lg border-b-2 px-3 py-2 text-xs transition',
                tab === id ? 'border-cyan-400 text-white' : 'border-transparent text-slate-400 hover:text-white',
              )}
            >
              {l}
            </button>
          ))}
        </div>
        <div className="h-[380px]">
          {tab === 'log' ? (
            <MessageLog
              log={s.log}
              showHeartbeats={s.showHeartbeats}
              onToggleHeartbeats={() => sim.toggleHeartbeats()}
              onClear={() => sim.clearLog()}
            />
          ) : (
            <RegistryPanel actors={actors} selectedId={s.selectedId} onSelect={(id) => sim.select(id)} />
          )}
        </div>
      </div>
    </Section>
  );
}

function Stat({ label, value, sub, good }: { label: string; value: string; sub?: string; good?: boolean }) {
  return (
    <div className="rounded-lg border border-slate-800 bg-slate-950/60 px-3 py-2">
      <div className="font-mono text-[10px] uppercase tracking-wider text-slate-500">{label}</div>
      <div className={cn('mt-0.5 font-mono text-sm font-semibold', good ? 'text-emerald-300' : 'text-white')}>{value}</div>
      {sub && <div className="truncate font-mono text-[10px] text-slate-500">{sub}</div>}
    </div>
  );
}

function Row({ k, v, color }: { k: string; v: string; color?: string }) {
  return (
    <div className="flex min-w-0 gap-2">
      <span className="w-16 shrink-0 text-slate-500">{k}</span>
      <span className="flex min-w-0 items-center gap-1.5 truncate text-slate-200">
        {color && <span className="h-2 w-2 shrink-0 rounded-sm" style={{ background: color }} />}
        <span className="truncate">{v}</span>
      </span>
    </div>
  );
}

const fmtBytes = (b: number) =>
  b > 1024 * 1024 ? `${(b / 1048576).toFixed(2)} MB` : b > 1024 ? `${(b / 1024).toFixed(1)} KB` : `${b} B`;
