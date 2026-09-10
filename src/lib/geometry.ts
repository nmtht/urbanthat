import type { AssetHint, ObjectPayload } from './types';

export type Vec2 = [number, number];
export type Vec3 = [number, number, number];

// ---------- Rhino-сторона: модель документа ----------

export type RhinoGeomKind = 'Brep' | 'Extrusion' | 'Curve' | 'Point';

export interface RhinoObj {
  id: string;
  layer: string;
  kind: RhinoGeomKind;
  /** Опорные точки в единицах документа. Для Curve — контрольные точки (2 или 3). */
  footprint: Vec2[];
  z: number; // единицы документа
  height: number; // единицы документа (только Extrusion)
  attributes: Record<string, string>;
}

export type UnitSystem = 'meters' | 'millimeters' | 'centimeters' | 'feet';

export const UNIT_TO_METERS: Record<UnitSystem, number> = {
  meters: 1,
  millimeters: 0.001,
  centimeters: 0.01,
  feet: 0.3048,
};

export const UNIT_LABEL: Record<UnitSystem, string> = {
  meters: 'м',
  millimeters: 'мм',
  centimeters: 'см',
  feet: 'фт',
};

export interface RhinoDocument {
  id: string;
  units: UnitSystem;
  objects: RhinoObj[];
}

// ---------- Утилиты ----------

export function mulberry32(seed: number) {
  let a = seed >>> 0;
  return function () {
    a += 0x6d2b79f5;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export function guid(rng: () => number): string {
  const h = (n: number) =>
    Array.from({ length: n }, () => Math.floor(rng() * 16).toString(16)).join('');
  return `${h(8)}-${h(4)}-4${h(3)}-${h(4)}-${h(12)}`;
}

const pick = <T,>(rng: () => number, arr: readonly T[]): T => arr[Math.floor(rng() * arr.length)];

// ---------- Генерация тестового документа ----------

const ZONE_TYPES = ['residential', 'commercial', 'mixed', 'green', 'industrial'] as const;
const BUILDING_FUNCTIONS = ['housing', 'office', 'retail', 'school', 'parking'] as const;

export function generateDocument(blocksPerSide: number, units: UnitSystem, seed: number): RhinoDocument {
  const rng = mulberry32(seed);
  const s = 1 / UNIT_TO_METERS[units]; // метры → единицы документа
  const block = 48;
  const road = 12;
  const pitch = block + road;
  const objects: RhinoObj[] = [];

  const toDoc = (p: Vec2[]): Vec2[] => p.map(([x, y]) => [x * s, y * s]);

  for (let i = 0; i < blocksPerSide; i++) {
    for (let j = 0; j < blocksPerSide; j++) {
      const x0 = i * pitch;
      const y0 = j * pitch;
      const zoneType = pick(rng, ZONE_TYPES);
      const heightMax = pick(rng, [12, 18, 24, 30, 40, 55]);
      let poly: Vec2[] = [
        [x0, y0],
        [x0 + block, y0],
        [x0 + block, y0 + block],
        [x0, y0 + block],
      ];
      if (rng() < 0.3) {
        const c = 12;
        poly = [
          [x0, y0],
          [x0 + block, y0],
          [x0 + block, y0 + block - c],
          [x0 + block - c, y0 + block],
          [x0, y0 + block],
        ];
      }
      const zoneLayer = zoneType === 'green' ? 'Zones::Green' : zoneType === 'industrial' ? 'Zones::Industrial' : 'Zones';
      objects.push({
        id: guid(rng),
        layer: zoneLayer,
        kind: 'Brep',
        footprint: toDoc(poly),
        z: 0,
        height: 0,
        attributes: {
          zone_type: zoneType,
          far: (0.8 + rng() * 3).toFixed(1),
          height_max: String(heightMax),
        },
      });

      if (zoneType !== 'green') {
        const count = 1 + Math.floor(rng() * 3);
        for (let b = 0; b < count; b++) {
          const w = 10 + rng() * 12;
          const d = 10 + rng() * 12;
          const bx = x0 + 4 + rng() * (block - 8 - w);
          const by = y0 + 4 + rng() * (block - 8 - d);
          const floors = 2 + Math.floor(rng() * Math.max(2, heightMax / 3 - 1));
          const h = floors * 3;
          objects.push({
            id: guid(rng),
            layer: 'Buildings',
            kind: 'Extrusion',
            footprint: toDoc([
              [bx, by],
              [bx + w, by],
              [bx + w, by + d],
              [bx, by + d],
            ]),
            z: 0,
            height: h * s,
            attributes: {
              floors: String(floors),
              height: String(h),
              function: pick(rng, BUILDING_FUNCTIONS),
            },
          });
        }
      }
    }
  }

  const extent = blocksPerSide * pitch - road;
  for (let k = 0; k <= blocksPerSide; k++) {
    const c = k * pitch - road / 2;
    const cls = k === 0 || k === blocksPerSide ? 'primary' : rng() < 0.4 ? 'secondary' : 'local';
    const width = cls === 'primary' ? 24 : cls === 'secondary' ? 16 : 10;
    const mkRoad = (pts: Vec2[]) =>
      objects.push({
        id: guid(rng),
        layer: cls === 'primary' ? 'Roads::Primary' : 'Roads',
        kind: 'Curve',
        footprint: toDoc(pts),
        z: 0,
        height: 0,
        attributes: { road_class: cls, lanes: String(width / 4), width: String(width) },
      });
    // горизонтальные
    const hPts: Vec2[] = [
      [-road, c],
      [extent + road, c],
    ];
    if (rng() < 0.35) hPts.splice(1, 0, [extent / 2, c + (rng() - 0.5) * 12]);
    mkRoad(hPts);
    // вертикальные
    const vPts: Vec2[] = [
      [c, -road],
      [c, extent + road],
    ];
    if (rng() < 0.35) vPts.splice(1, 0, [c + (rng() - 0.5) * 12, extent / 2]);
    mkRoad(vPts);
  }

  return { id: guid(rng), units, objects };
}

// ---------- Классификатор (раздел 5.2) ----------

export function classifyLayer(layerName: string): AssetHint {
  const root = layerName.split('::')[0].trim();
  switch (root) {
    case 'Zones':
      return 'Zone';
    case 'Roads':
      return 'Road';
    case 'Buildings':
      return 'Building';
    default:
      return 'Generic';
  }
}

// ---------- Сериализация (аналог ObjectSerializer.cs, раздел 4.2) ----------

export interface SerializeResult {
  payload: ObjectPayload | null;
  skippedReason?: string;
}

/** Точки кривой в метрах (2 точки — отрезок, 3 — квадратичная кривая через среднюю точку). */
export function curveSamples(ctrl: Vec2[], k: number, segmentLength: number): Vec2[] {
  const p = ctrl.map(([x, y]) => [x * k, y * k] as Vec2);
  if (p.length < 3) {
    const [a, b] = p;
    const len = Math.hypot(b[0] - a[0], b[1] - a[1]);
    const n = Math.max(1, Math.ceil(len / segmentLength));
    const out: Vec2[] = [];
    for (let i = 0; i <= n; i++) {
      const t = i / n;
      out.push([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t]);
    }
    return out;
  }
  const [p0, pm, p2] = p;
  // контрольная точка Безье, чтобы кривая прошла через pm при t = 0.5
  const c: Vec2 = [2 * pm[0] - 0.5 * (p0[0] + p2[0]), 2 * pm[1] - 0.5 * (p0[1] + p2[1])];
  const approx = Math.hypot(pm[0] - p0[0], pm[1] - p0[1]) + Math.hypot(p2[0] - pm[0], p2[1] - pm[1]);
  const n = Math.max(2, Math.ceil(approx / segmentLength));
  const out: Vec2[] = [];
  for (let i = 0; i <= n; i++) {
    const t = i / n;
    const mt = 1 - t;
    out.push([
      mt * mt * p0[0] + 2 * mt * t * c[0] + t * t * p2[0],
      mt * mt * p0[1] + 2 * mt * t * c[1] + t * t * p2[1],
    ]);
  }
  return out;
}

export function serializeObject(obj: RhinoObj, units: UnitSystem, segmentLength = 0.5): SerializeResult {
  const k = UNIT_TO_METERS[units]; // конвертация в метры на стороне плагина (4.3)
  const base = { id: obj.id, layer: obj.layer, attributes: { ...obj.attributes } };

  if (obj.kind === 'Curve') {
    const pts = curveSamples(obj.footprint, k, segmentLength);
    const z = obj.z * k;
    const flat: number[] = [];
    for (const [x, y] of pts) flat.push(round(x), round(y), round(z));
    return { payload: { ...base, geometry_type: 'polyline', mesh: null, polyline: { points: flat } } };
  }

  if (obj.kind === 'Brep') {
    const z = obj.z * k;
    const vertices: number[] = [];
    const normals: number[] = [];
    for (const [x, y] of obj.footprint) {
      vertices.push(round(x * k), round(y * k), round(z));
      normals.push(0, 0, 1);
    }
    const indices: number[] = [];
    for (let i = 1; i < obj.footprint.length - 1; i++) indices.push(0, i, i + 1);
    return { payload: { ...base, geometry_type: 'mesh', mesh: { vertices, indices, normals }, polyline: null } };
  }

  if (obj.kind === 'Extrusion') {
    const n = obj.footprint.length;
    const z0 = obj.z * k;
    const z1 = (obj.z + obj.height) * k;
    const vertices: number[] = [];
    for (const [x, y] of obj.footprint) vertices.push(round(x * k), round(y * k), round(z0));
    for (const [x, y] of obj.footprint) vertices.push(round(x * k), round(y * k), round(z1));
    const indices: number[] = [];
    // крышка
    for (let i = 1; i < n - 1; i++) indices.push(n, n + i, n + i + 1);
    // стенки
    for (let i = 0; i < n; i++) {
      const j = (i + 1) % n;
      indices.push(i, j, n + j, i, n + j, n + i);
    }
    // Extrusion с общими вершинами → нормали не передаём, Unreal посчитает сам (3.4)
    return { payload: { ...base, geometry_type: 'mesh', mesh: { vertices, indices, normals: null }, polyline: null } };
  }

  return { payload: null, skippedReason: `${obj.kind} — тип геометрии вне скоупа (4.2), пропущен` };
}

const round = (v: number) => Math.round(v * 1000) / 1000;

// ---------- Unreal-сторона: акторы ----------

export interface Tri {
  a: Vec3;
  b: Vec3;
  c: Vec3;
  n: Vec3;
}

export type ActorClass = 'AGenericBridgeMeshActor' | 'AGenericBridgePolylineActor';

export interface BridgeActor {
  id: string;
  cls: ActorClass;
  hint: AssetHint;
  layer: string;
  attributes: Record<string, string>;
  /** Треугольники в UE-юнитах (см) */
  tris: Tri[];
  /** Точки сплайна в UE-юнитах (см) */
  points: Vec3[];
  vertexCount: number;
  triangleCount: number;
  normalsFromRhino: boolean;
  center: Vec3; // см
  version: number;
  createdAt: number;
  updatedAt: number;
}

const UE_SCALE = 100; // метры → сантиметры (5.4)

export function buildActor(p: ObjectPayload, existing: BridgeActor | undefined, now: number): BridgeActor {
  const hint = classifyLayer(p.layer);
  const tris: Tri[] = [];
  const points: Vec3[] = [];
  let vertexCount = 0;
  let normalsFromRhino = false;
  const min: Vec3 = [Infinity, Infinity, Infinity];
  const max: Vec3 = [-Infinity, -Infinity, -Infinity];
  const acc = (v: Vec3) => {
    for (let i = 0; i < 3; i++) {
      if (v[i] < min[i]) min[i] = v[i];
      if (v[i] > max[i]) max[i] = v[i];
    }
  };

  if (p.geometry_type === 'mesh' && p.mesh) {
    const { vertices, indices, normals } = p.mesh;
    vertexCount = vertices.length / 3;
    normalsFromRhino = !!normals;
    const vert = (i: number): Vec3 => {
      const v: Vec3 = [vertices[i * 3] * UE_SCALE, vertices[i * 3 + 1] * UE_SCALE, vertices[i * 3 + 2] * UE_SCALE];
      return v;
    };
    for (let t = 0; t < indices.length; t += 3) {
      const a = vert(indices[t]);
      const b = vert(indices[t + 1]);
      const c = vert(indices[t + 2]);
      acc(a);
      acc(b);
      acc(c);
      let n: Vec3;
      if (normals) {
        const i0 = indices[t] * 3;
        const i1 = indices[t + 1] * 3;
        const i2 = indices[t + 2] * 3;
        n = normalize([
          normals[i0] + normals[i1] + normals[i2],
          normals[i0 + 1] + normals[i1 + 1] + normals[i2 + 1],
          normals[i0 + 2] + normals[i1 + 2] + normals[i2 + 2],
        ]);
      } else {
        n = faceNormal(a, b, c);
      }
      tris.push({ a, b, c, n });
    }
  } else if (p.geometry_type === 'polyline' && p.polyline) {
    const pts = p.polyline.points;
    vertexCount = pts.length / 3;
    for (let i = 0; i < pts.length; i += 3) {
      const v: Vec3 = [pts[i] * UE_SCALE, pts[i + 1] * UE_SCALE, pts[i + 2] * UE_SCALE];
      acc(v);
      points.push(v);
    }
  }

  const center: Vec3 = isFinite(min[0])
    ? [(min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2]
    : [0, 0, 0];

  return {
    id: p.id,
    cls: p.geometry_type === 'mesh' ? 'AGenericBridgeMeshActor' : 'AGenericBridgePolylineActor',
    hint,
    layer: p.layer,
    attributes: p.attributes,
    tris,
    points,
    vertexCount,
    triangleCount: tris.length,
    normalsFromRhino,
    center,
    version: existing ? existing.version + 1 : 1,
    createdAt: existing ? existing.createdAt : now,
    updatedAt: now,
  };
}

function faceNormal(a: Vec3, b: Vec3, c: Vec3): Vec3 {
  const u: Vec3 = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
  const v: Vec3 = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
  return normalize([u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]]);
}

function normalize(v: Vec3): Vec3 {
  const l = Math.hypot(v[0], v[1], v[2]) || 1;
  return [v[0] / l, v[1] / l, v[2] / l];
}

// ---------- Цвета для отладочной визуализации (5.2) ----------

export const ZONE_COLORS: Record<string, string> = {
  residential: '#f59e0b',
  commercial: '#3b82f6',
  mixed: '#a855f7',
  green: '#22c55e',
  industrial: '#94a3b8',
};

export const HINT_COLORS: Record<AssetHint, string> = {
  Zone: '#14b8a6',
  Road: '#fbbf24',
  Building: '#cbd5e1',
  Generic: '#ec4899',
};

export function actorColor(a: { hint: AssetHint; attributes: Record<string, string> }): string {
  if (a.hint === 'Zone') return ZONE_COLORS[a.attributes.zone_type] ?? HINT_COLORS.Zone;
  return HINT_COLORS[a.hint];
}
