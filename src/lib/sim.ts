import {
  buildActor,
  generateDocument,
  guid,
  mulberry32,
  serializeObject,
  UNIT_TO_METERS,
  type BridgeActor,
  type RhinoDocument,
  type RhinoObj,
  type UnitSystem,
  type Vec2,
} from './geometry';
import {
  BRIDGE_URL,
  HEARTBEAT_INTERVAL_MS,
  RECONNECT_BASE_MS,
  RECONNECT_MAX_MS,
  type BridgeMessage,
  type ObjectPayload,
} from './types';

export type ConnState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';
export type Direction = 'rhino->ue' | 'ue->rhino';
export type LogKind = 'wire' | 'rhino' | 'ue';

export interface LogEntry {
  id: number;
  t: number;
  kind: LogKind;
  level: 'info' | 'warn' | 'error';
  text: string;
  dir?: Direction;
  msgType?: string;
  bytes?: number;
  msg?: BridgeMessage;
}

export interface FullSyncStats {
  objects: number;
  skipped: number;
  bytes: number;
  serializeMs: number;
  parseMs: number;
  at: number;
}

export interface SimState {
  rhinoRunning: boolean;
  unrealRunning: boolean;
  conn: ConnState;
  attempt: number;
  backoffMs: number;
  nextRetryAt: number | null;
  doc: RhinoDocument;
  registry: Map<string, BridgeActor>;
  log: LogEntry[];
  selectedId: string | null;
  segmentLength: number;
  showHeartbeats: boolean;
  stats: {
    sent: number;
    bytes: number;
    lastFullSync: FullSyncStats | null;
    lastHbFromRhino: number | null;
    lastHbFromUe: number | null;
  };
}

type Listener = () => void;

const MAX_LOG = 250;
const encoder = new TextEncoder();

export class BridgeSim {
  state: SimState;
  version = 0;
  private listeners = new Set<Listener>();
  private logId = 0;
  private timer: number | null = null;
  private lastHbSent = 0;
  private hadConnection = false;
  private rng = mulberry32(Date.now() & 0xffff);

  constructor() {
    this.state = {
      rhinoRunning: false,
      unrealRunning: false,
      conn: 'disconnected',
      attempt: 0,
      backoffMs: RECONNECT_BASE_MS,
      nextRetryAt: null,
      doc: generateDocument(3, 'millimeters', 42),
      registry: new Map(),
      log: [],
      selectedId: null,
      segmentLength: 0.5,
      showHeartbeats: false,
      stats: { sent: 0, bytes: 0, lastFullSync: null, lastHbFromRhino: null, lastHbFromUe: null },
    };
    this.pushLog('rhino', 'info', `Документ открыт: ${this.state.doc.objects.length} объектов, ModelUnitSystem = ${this.state.doc.units}`);
  }

  // ---------- подписка ----------
  subscribe = (fn: Listener) => {
    this.listeners.add(fn);
    return () => {
      this.listeners.delete(fn);
    };
  };
  private emit() {
    this.version++;
    this.listeners.forEach((l) => l());
  }

  start() {
    if (this.timer !== null) return;
    this.timer = window.setInterval(() => this.tick(), 250);
  }
  stop() {
    if (this.timer !== null) window.clearInterval(this.timer);
    this.timer = null;
  }

  // ---------- лог ----------
  private pushLog(kind: LogKind, level: LogEntry['level'], text: string, extra: Partial<LogEntry> = {}) {
    const entry: LogEntry = { id: ++this.logId, t: Date.now(), kind, level, text, ...extra };
    const log = [...this.state.log, entry];
    if (log.length > MAX_LOG) log.splice(0, log.length - MAX_LOG);
    this.state = { ...this.state, log };
  }

  clearLog() {
    this.state = { ...this.state, log: [] };
    this.emit();
  }

  toggleHeartbeats() {
    this.state = { ...this.state, showHeartbeats: !this.state.showHeartbeats };
    this.emit();
  }

  select(id: string | null) {
    this.state = { ...this.state, selectedId: id };
    this.emit();
  }

  setSegmentLength(v: number) {
    this.state = { ...this.state, segmentLength: v };
    this.emit();
  }

  // ---------- процессы ----------
  setRhino(on: boolean) {
    if (on === this.state.rhinoRunning) return;
    this.state = { ...this.state, rhinoRunning: on };
    if (on) {
      this.pushLog('rhino', 'info', `[UrbanBridge] OnLoad: WebSocket-сервер поднят на ${BRIDGE_URL}`);
      this.pushLog('rhino', 'info', '[UrbanBridge] Подписки: AddRhinoObject, ReplaceRhinoObject, DeleteRhinoObject, EndOpenDocument');
    } else {
      this.pushLog('rhino', 'warn', '[UrbanBridge] Rhino закрыт, WebSocket-сервер остановлен');
      if (this.state.conn === 'connected') this.onConnectionLost('сервер закрыл сокет (close frame 1001)');
    }
    this.emit();
  }

  setUnreal(on: boolean) {
    if (on === this.state.unrealRunning) return;
    this.state = { ...this.state, unrealRunning: on };
    if (on) {
      this.hadConnection = false;
      this.pushLog('ue', 'info', `UBridgeConnection::Initialize → FWebSocketsModule::CreateWebSocket(${BRIDGE_URL})`);
      this.state = { ...this.state, conn: 'connecting', attempt: 0, backoffMs: RECONNECT_BASE_MS, nextRetryAt: null };
      this.tryConnect();
    } else {
      this.pushLog('ue', 'warn', 'Unreal-приложение закрыто, UBridgeConnection::Deinitialize, реестр акторов уничтожен');
      this.state = {
        ...this.state,
        conn: 'disconnected',
        nextRetryAt: null,
        attempt: 0,
        backoffMs: RECONNECT_BASE_MS,
        registry: new Map(),
      };
      if (this.state.rhinoRunning) this.pushLog('rhino', 'info', '[UrbanBridge] Клиент отключился');
    }
    this.emit();
  }

  private tryConnect() {
    if (this.state.rhinoRunning) {
      this.hadConnection = true;
      this.state = { ...this.state, conn: 'connected', attempt: 0, backoffMs: RECONNECT_BASE_MS, nextRetryAt: null };
      this.lastHbSent = Date.now();
      this.pushLog('wire', 'info', `WebSocket handshake OK — ${BRIDGE_URL} (Rhino: сервер, Unreal: клиент)`);
      this.pushLog('ue', 'info', 'OnConnected → отправляю request_full_sync');
      this.send('ue->rhino', { type: 'request_full_sync' });
    } else {
      const { backoffMs, attempt } = this.state;
      this.pushLog(
        'ue',
        'warn',
        `OnConnectionError: ECONNREFUSED ${BRIDGE_URL} — попытка #${attempt + 1}, повтор через ${(backoffMs / 1000).toFixed(0)}s`,
      );
      this.state = {
        ...this.state,
        conn: this.hadConnection ? 'reconnecting' : 'connecting',
        nextRetryAt: Date.now() + backoffMs,
        attempt: attempt + 1,
        backoffMs: Math.min(backoffMs * 2, RECONNECT_MAX_MS),
      };
    }
  }

  private onConnectionLost(reason: string) {
    this.pushLog('wire', 'error', `Соединение потеряно: ${reason}`);
    this.pushLog('ue', 'warn', `OnClosed → статус Reconnecting, backoff ${RECONNECT_BASE_MS / 1000}s → 2s → 4s → max ${RECONNECT_MAX_MS / 1000}s`);
    this.hadConnection = true;
    this.state = {
      ...this.state,
      conn: 'reconnecting',
      attempt: 1,
      backoffMs: Math.min(RECONNECT_BASE_MS * 2, RECONNECT_MAX_MS),
      nextRetryAt: Date.now() + RECONNECT_BASE_MS,
    };
  }

  private tick() {
    const now = Date.now();
    let changed = false;
    if (this.state.unrealRunning && this.state.nextRetryAt !== null) {
      if (now >= this.state.nextRetryAt) {
        this.state = { ...this.state, nextRetryAt: null };
        this.tryConnect();
      }
      changed = true; // обновляем обратный отсчёт
    }
    if (this.state.conn === 'connected' && now - this.lastHbSent >= HEARTBEAT_INTERVAL_MS) {
      this.lastHbSent = now;
      this.send('rhino->ue', { type: 'heartbeat', timestamp: now });
      this.send('ue->rhino', { type: 'heartbeat', timestamp: now });
      changed = true;
    }
    if (changed) this.emit();
  }

  // ---------- транспорт ----------
  private send(dir: Direction, msg: BridgeMessage) {
    if (this.state.conn !== 'connected') return;
    const json = JSON.stringify(msg);
    const bytes = encoder.encode(json).length;
    const summary = describe(msg);
    this.state = {
      ...this.state,
      stats: { ...this.state.stats, sent: this.state.stats.sent + 1, bytes: this.state.stats.bytes + bytes },
    };
    this.pushLog('wire', 'info', summary, { dir, msgType: msg.type, bytes, msg });
    if (dir === 'rhino->ue') this.handleOnUnreal(msg, bytes);
    else this.handleOnRhino(msg);
  }

  private handleOnRhino(msg: BridgeMessage) {
    switch (msg.type) {
      case 'request_full_sync':
        this.pushLog('rhino', 'info', '[UrbanBridge] Получен request_full_sync → сериализую документ');
        this.sendFullSync();
        break;
      case 'heartbeat':
        this.state = { ...this.state, stats: { ...this.state.stats, lastHbFromUe: msg.timestamp } };
        break;
      default:
        this.pushLog('rhino', 'warn', `[UrbanBridge] Неожиданное сообщение от клиента: ${msg.type}`);
    }
  }

  private handleOnUnreal(msg: BridgeMessage, bytes: number) {
    const now = Date.now();
    switch (msg.type) {
      case 'full_sync': {
        const t0 = performance.now();
        // имитация FBridgeMessageParser: разбор JSON в промежуточные структуры
        const parsed = JSON.parse(JSON.stringify(msg)) as Extract<BridgeMessage, { type: 'full_sync' }>;
        const registry = new Map<string, BridgeActor>();
        for (const p of parsed.objects) registry.set(p.id, buildActor(p, undefined, now));
        const parseMs = performance.now() - t0;
        this.state = {
          ...this.state,
          registry,
          stats: {
            ...this.state.stats,
            lastFullSync: {
              objects: parsed.objects.length,
              skipped: this.pendingSkipped,
              bytes,
              serializeMs: this.pendingSerializeMs,
              parseMs,
              at: now,
            },
          },
        };
        this.pushLog(
          'ue',
          'info',
          `full_sync (${parsed.units}, doc ${parsed.document_id.slice(0, 8)}…): реестр очищен, создано ${registry.size} акторов за ${parseMs.toFixed(1)} ms на GameThread`,
        );
        break;
      }
      case 'object_upserted':
        this.upsertActor(msg.object, now);
        break;
      case 'batch_upsert': {
        let created = 0;
        let updated = 0;
        const registry = new Map(this.state.registry);
        for (const p of msg.objects) {
          const existing = registry.get(p.id);
          registry.set(p.id, buildActor(p, existing, now));
          if (existing) updated++;
          else created++;
        }
        this.state = { ...this.state, registry };
        this.pushLog('ue', 'info', `batch_upsert: ${msg.objects.length} объектов → обновлено ${updated}, создано ${created} (одна задача AsyncTask на GameThread)`);
        break;
      }
      case 'object_deleted': {
        const registry = new Map(this.state.registry);
        const a = registry.get(msg.id);
        if (a) {
          registry.delete(msg.id);
          this.state = { ...this.state, registry, selectedId: this.state.selectedId === msg.id ? null : this.state.selectedId };
          this.pushLog('ue', 'info', `object_deleted ${msg.id.slice(0, 8)}… → ${a.cls}::Destroy(), удалён из реестра`);
        } else {
          this.pushLog('ue', 'warn', `object_deleted ${msg.id.slice(0, 8)}… — ID не найден в реестре, игнорирую`);
        }
        break;
      }
      case 'heartbeat':
        this.state = { ...this.state, stats: { ...this.state.stats, lastHbFromRhino: msg.timestamp } };
        break;
      default:
        this.pushLog('ue', 'warn', `Неизвестный тип сообщения: ${(msg as { type: string }).type}`);
    }
  }

  private upsertActor(p: ObjectPayload, now: number) {
    const registry = new Map(this.state.registry);
    const existing = registry.get(p.id);
    const actor = buildActor(p, existing, now);
    registry.set(p.id, actor);
    this.state = { ...this.state, registry };
    const geom =
      actor.cls === 'AGenericBridgeMeshActor'
        ? `CreateMeshSection(${actor.vertexCount} верш., ${actor.triangleCount} тр.${actor.normalsFromRhino ? '' : ', нормали посчитаны по треугольникам'})`
        : `USplineComponent(${actor.vertexCount} точек)`;
    this.pushLog(
      'ue',
      'info',
      `${existing ? 'Обновлён' : 'Создан'} ${actor.cls} [${actor.hint}] ${p.id.slice(0, 8)}… → ${geom}`,
    );
  }

  // ---------- Rhino: сериализация и события ----------
  private pendingSerializeMs = 0;
  private pendingSkipped = 0;

  private serializeAll(): ObjectPayload[] {
    const { doc, segmentLength } = this.state;
    const out: ObjectPayload[] = [];
    let skipped = 0;
    const t0 = performance.now();
    for (const o of doc.objects) {
      const r = serializeObject(o, doc.units, segmentLength);
      if (r.payload) out.push(r.payload);
      else {
        skipped++;
        this.pushLog('rhino', 'warn', `[UrbanBridge] ${o.id.slice(0, 8)}… (${o.layer}): ${r.skippedReason}`);
      }
    }
    this.pendingSerializeMs = performance.now() - t0;
    this.pendingSkipped = skipped;
    return out;
  }

  private sendFullSync() {
    const objects = this.serializeAll();
    this.pushLog(
      'rhino',
      'info',
      `[UrbanBridge] Сериализовано ${objects.length} объектов за ${this.pendingSerializeMs.toFixed(1)} ms (единицы → метры, k=${UNIT_TO_METERS[this.state.doc.units]})`,
    );
    this.send('rhino->ue', { type: 'full_sync', document_id: this.state.doc.id, units: 'meters', objects });
  }

  requestFullSync() {
    if (this.state.conn !== 'connected') {
      this.pushLog('ue', 'warn', 'request_full_sync: нет соединения');
      this.emit();
      return;
    }
    this.pushLog('ue', 'info', 'Debug-команда: UrbanBridge.RequestFullSync');
    this.send('ue->rhino', { type: 'request_full_sync' });
    this.emit();
  }

  private sendUpsert(obj: RhinoObj, event: string) {
    const r = serializeObject(obj, this.state.doc.units, this.state.segmentLength);
    if (!r.payload) {
      this.pushLog('rhino', 'warn', `[UrbanBridge] ${event}: ${r.skippedReason}`);
      return;
    }
    if (this.state.conn !== 'connected') {
      this.pushLog('rhino', 'info', `[UrbanBridge] ${event}: нет клиента — изменение уйдёт в следующем full_sync`);
      return;
    }
    this.send('rhino->ue', { type: 'object_upserted', object: r.payload });
  }

  private sceneExtentDoc(): number {
    let m = 0;
    for (const o of this.state.doc.objects) for (const [x, y] of o.footprint) m = Math.max(m, x, y);
    return m || 100 / UNIT_TO_METERS[this.state.doc.units];
  }

  addObject(kind: 'Zone' | 'Building' | 'Road' | 'Point') {
    const { doc } = this.state;
    const s = 1 / UNIT_TO_METERS[doc.units];
    const extentM = this.sceneExtentDoc() / s;
    const rx = this.rng() * Math.max(20, extentM - 20);
    const ry = this.rng() * Math.max(20, extentM - 20);
    const toDoc = (p: Vec2[]): Vec2[] => p.map(([x, y]) => [x * s, y * s]);
    let obj: RhinoObj;
    if (kind === 'Zone') {
      const w = 20 + this.rng() * 20;
      const types = ['residential', 'commercial', 'mixed', 'green', 'industrial'];
      const zt = types[Math.floor(this.rng() * types.length)];
      obj = {
        id: guid(this.rng),
        layer: 'Zones',
        kind: 'Brep',
        footprint: toDoc([
          [rx, ry],
          [rx + w, ry],
          [rx + w, ry + w * 0.7],
          [rx, ry + w * 0.7],
        ]),
        z: 0,
        height: 0,
        attributes: { zone_type: zt, far: (1 + this.rng() * 2).toFixed(1), height_max: '30' },
      };
    } else if (kind === 'Building') {
      const w = 8 + this.rng() * 10;
      const d = 8 + this.rng() * 10;
      const floors = 3 + Math.floor(this.rng() * 12);
      obj = {
        id: guid(this.rng),
        layer: 'Buildings',
        kind: 'Extrusion',
        footprint: toDoc([
          [rx, ry],
          [rx + w, ry],
          [rx + w, ry + d],
          [rx, ry + d],
        ]),
        z: 0,
        height: floors * 3 * s,
        attributes: { floors: String(floors), height: String(floors * 3), function: 'housing' },
      };
    } else if (kind === 'Road') {
      const len = 40 + this.rng() * 60;
      const ang = this.rng() * Math.PI;
      const ex = rx + Math.cos(ang) * len;
      const ey = ry + Math.sin(ang) * len;
      obj = {
        id: guid(this.rng),
        layer: 'Roads',
        kind: 'Curve',
        footprint: toDoc([
          [rx, ry],
          [(rx + ex) / 2 + (this.rng() - 0.5) * 20, (ry + ey) / 2 + (this.rng() - 0.5) * 20],
          [ex, ey],
        ]),
        z: 0,
        height: 0,
        attributes: { road_class: 'local', lanes: '2', width: '8' },
      };
    } else {
      obj = {
        id: guid(this.rng),
        layer: 'Annotations',
        kind: 'Point',
        footprint: toDoc([[rx, ry]]),
        z: 0,
        height: 0,
        attributes: { note: 'survey point' },
      };
    }
    this.state = { ...this.state, doc: { ...doc, objects: [...doc.objects, obj] }, selectedId: obj.id };
    this.pushLog('rhino', 'info', `RhinoDoc.AddRhinoObject: ${obj.kind} на слое "${obj.layer}" (${obj.id.slice(0, 8)}…)`);
    this.sendUpsert(obj, 'AddRhinoObject');
    this.emit();
  }

  moveSelected(dxM: number, dyM: number) {
    const id = this.state.selectedId;
    if (!id) return;
    const { doc } = this.state;
    const s = 1 / UNIT_TO_METERS[doc.units];
    const objects = doc.objects.map((o) =>
      o.id === id ? { ...o, footprint: o.footprint.map(([x, y]) => [x + dxM * s, y + dyM * s] as Vec2) } : o,
    );
    const obj = objects.find((o) => o.id === id)!;
    this.state = { ...this.state, doc: { ...doc, objects } };
    this.pushLog('rhino', 'info', `RhinoDoc.ReplaceRhinoObject: Move (${dxM}, ${dyM}) м → ${obj.kind} ${id.slice(0, 8)}…`);
    this.sendUpsert(obj, 'ReplaceRhinoObject');
    this.emit();
  }

  setSelectedAttribute(key: string, value: string) {
    const id = this.state.selectedId;
    if (!id) return;
    const { doc } = this.state;
    const objects = doc.objects.map((o) => (o.id === id ? { ...o, attributes: { ...o.attributes, [key]: value } } : o));
    const obj = objects.find((o) => o.id === id)!;
    this.state = { ...this.state, doc: { ...doc, objects } };
    this.pushLog('rhino', 'info', `User Text изменён: ${key} = "${value}" → ReplaceRhinoObject ${id.slice(0, 8)}…`);
    this.sendUpsert(obj, 'ReplaceRhinoObject');
    this.emit();
  }

  deleteSelected() {
    const id = this.state.selectedId;
    if (!id) return;
    const { doc } = this.state;
    const obj = doc.objects.find((o) => o.id === id);
    this.state = { ...this.state, doc: { ...doc, objects: doc.objects.filter((o) => o.id !== id) }, selectedId: null };
    this.pushLog('rhino', 'info', `RhinoDoc.DeleteRhinoObject: ${obj?.kind ?? '?'} ${id.slice(0, 8)}…`);
    if (this.state.conn === 'connected' && obj && obj.kind !== 'Point') {
      this.send('rhino->ue', { type: 'object_deleted', id });
    }
    this.emit();
  }

  batchEdit(kind: 'raiseBuildings' | 'shiftRoads' | 'rezone') {
    const { doc } = this.state;
    const s = 1 / UNIT_TO_METERS[doc.units];
    const changed: RhinoObj[] = [];
    const objects = doc.objects.map((o) => {
      if (kind === 'raiseBuildings' && o.kind === 'Extrusion') {
        const floors = Number(o.attributes.floors ?? 1) + 1;
        const n = { ...o, height: o.height + 3 * s, attributes: { ...o.attributes, floors: String(floors), height: String(floors * 3) } };
        changed.push(n);
        return n;
      }
      if (kind === 'shiftRoads' && o.kind === 'Curve') {
        const n = { ...o, footprint: o.footprint.map(([x, y]) => [x + 2 * s, y] as Vec2) };
        changed.push(n);
        return n;
      }
      if (kind === 'rezone' && o.kind === 'Brep') {
        const order = ['residential', 'commercial', 'mixed', 'green', 'industrial'];
        const zt = order[(order.indexOf(o.attributes.zone_type) + 1) % order.length];
        const n = { ...o, attributes: { ...o.attributes, zone_type: zt } };
        changed.push(n);
        return n;
      }
      return o;
    });
    this.state = { ...this.state, doc: { ...doc, objects } };
    const label =
      kind === 'raiseBuildings' ? '+1 этаж всем зданиям' : kind === 'shiftRoads' ? 'сдвиг всех дорог на 2 м' : 'смена zone_type у всех зон';
    this.pushLog('rhino', 'info', `Групповая операция (${label}): ${changed.length} × ReplaceRhinoObject → объединяю в batch_upsert`);
    if (this.state.conn === 'connected') {
      const payloads: ObjectPayload[] = [];
      for (const o of changed) {
        const r = serializeObject(o, doc.units, this.state.segmentLength);
        if (r.payload) payloads.push(r.payload);
      }
      this.send('rhino->ue', { type: 'batch_upsert', objects: payloads });
    }
    this.emit();
  }

  openDocument(blocksPerSide: number, units: UnitSystem) {
    const doc = generateDocument(blocksPerSide, units, Math.floor(this.rng() * 1e9));
    this.state = { ...this.state, doc, selectedId: null };
    this.pushLog('rhino', 'info', `RhinoDoc.EndOpenDocument: ${doc.objects.length} объектов, ModelUnitSystem = ${units}`);
    if (this.state.conn === 'connected') this.sendFullSync();
    else this.pushLog('rhino', 'info', '[UrbanBridge] Клиент не подключён — full_sync отправится при подключении');
    this.emit();
  }
}

function describe(msg: BridgeMessage): string {
  switch (msg.type) {
    case 'full_sync':
      return `full_sync — ${msg.objects.length} объектов, units=${msg.units}`;
    case 'object_upserted':
      return `object_upserted — ${msg.object.geometry_type} на "${msg.object.layer}" (${msg.object.id.slice(0, 8)}…)`;
    case 'batch_upsert':
      return `batch_upsert — ${msg.objects.length} объектов`;
    case 'object_deleted':
      return `object_deleted — ${msg.id.slice(0, 8)}…`;
    case 'heartbeat':
      return `heartbeat — ${new Date(msg.timestamp).toLocaleTimeString()}`;
    case 'request_full_sync':
      return 'request_full_sync';
  }
}

export const sim = new BridgeSim();
