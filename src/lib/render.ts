import { curveSamples, UNIT_TO_METERS, type BridgeActor, type RhinoDocument, type Vec2, type Vec3 } from './geometry';

export interface View {
  scale: number; // px на метр
  ox: number;
  oy: number;
}

export function project(x: number, y: number, z: number, v: View): Vec2 {
  const sx = (x - y) * 0.866;
  const sy = (x + y) * 0.5 - z;
  return [v.ox + sx * v.scale, v.oy + sy * v.scale];
}

const depth = (x: number, y: number, z: number) => x + y + z;

export function fitView(bounds: { min: Vec3; max: Vec3 }, w: number, h: number, pad = 28): View {
  const corners: Vec3[] = [];
  for (const x of [bounds.min[0], bounds.max[0]])
    for (const y of [bounds.min[1], bounds.max[1]])
      for (const z of [bounds.min[2], bounds.max[2]]) corners.push([x, y, z]);
  const unit: View = { scale: 1, ox: 0, oy: 0 };
  let minX = Infinity,
    maxX = -Infinity,
    minY = Infinity,
    maxY = -Infinity;
  for (const c of corners) {
    const [sx, sy] = project(c[0], c[1], c[2], unit);
    minX = Math.min(minX, sx);
    maxX = Math.max(maxX, sx);
    minY = Math.min(minY, sy);
    maxY = Math.max(maxY, sy);
  }
  const scale = Math.min((w - pad * 2) / Math.max(1e-6, maxX - minX), (h - pad * 2) / Math.max(1e-6, maxY - minY));
  return {
    scale,
    ox: w / 2 - ((minX + maxX) / 2) * scale,
    oy: h / 2 - ((minY + maxY) / 2) * scale,
  };
}

export function docBoundsMeters(doc: RhinoDocument): { min: Vec3; max: Vec3 } {
  const k = UNIT_TO_METERS[doc.units];
  const min: Vec3 = [Infinity, Infinity, 0];
  const max: Vec3 = [-Infinity, -Infinity, 0];
  for (const o of doc.objects) {
    for (const [x, y] of o.footprint) {
      min[0] = Math.min(min[0], x * k);
      min[1] = Math.min(min[1], y * k);
      max[0] = Math.max(max[0], x * k);
      max[1] = Math.max(max[1], y * k);
    }
    max[2] = Math.max(max[2], (o.z + o.height) * k);
  }
  if (!isFinite(min[0])) return { min: [0, 0, 0], max: [100, 100, 30] };
  return { min, max };
}

function drawGrid(ctx: CanvasRenderingContext2D, v: View, bounds: { min: Vec3; max: Vec3 }, color: string) {
  const step = 24;
  const x0 = Math.floor(bounds.min[0] / step) * step - step;
  const x1 = Math.ceil(bounds.max[0] / step) * step + step;
  const y0 = Math.floor(bounds.min[1] / step) * step - step;
  const y1 = Math.ceil(bounds.max[1] / step) * step + step;
  ctx.strokeStyle = color;
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let x = x0; x <= x1; x += step) {
    const a = project(x, y0, 0, v);
    const b = project(x, y1, 0, v);
    ctx.moveTo(a[0], a[1]);
    ctx.lineTo(b[0], b[1]);
  }
  for (let y = y0; y <= y1; y += step) {
    const a = project(x0, y, 0, v);
    const b = project(x1, y, 0, v);
    ctx.moveTo(a[0], a[1]);
    ctx.lineTo(b[0], b[1]);
  }
  ctx.stroke();
}

const RHINO_LAYER_COLORS: Record<string, string> = {
  Zones: '#0e7490',
  Roads: '#dc2626',
  Buildings: '#1e293b',
};

function rhinoLayerColor(layer: string) {
  return RHINO_LAYER_COLORS[layer.split('::')[0]] ?? '#db2777';
}

/** Rhino-вьюпорт: исходный документ, wireframe по слоям */
export function renderRhino(
  ctx: CanvasRenderingContext2D,
  w: number,
  h: number,
  doc: RhinoDocument,
  selectedId: string | null,
): Map<string, Vec2> {
  const bounds = docBoundsMeters(doc);
  const v = fitView(bounds, w, h);
  const k = UNIT_TO_METERS[doc.units];
  const centers = new Map<string, Vec2>();

  ctx.fillStyle = '#eef1f5';
  ctx.fillRect(0, 0, w, h);
  drawGrid(ctx, v, bounds, 'rgba(15,23,42,0.08)');

  // порядок: зоны, дороги, здания
  const order = { Brep: 0, Curve: 1, Point: 2, Extrusion: 3 } as const;
  const objs = [...doc.objects].sort((a, b) => order[a.kind] - order[b.kind]);

  for (const o of objs) {
    const sel = o.id === selectedId;
    const color = sel ? '#eab308' : rhinoLayerColor(o.layer);
    const pts = o.footprint.map(([x, y]) => [x * k, y * k] as Vec2);
    const z0 = o.z * k;
    let cx = 0,
      cy = 0;
    for (const p of pts) {
      cx += p[0];
      cy += p[1];
    }
    cx /= pts.length || 1;
    cy /= pts.length || 1;
    ctx.lineWidth = sel ? 2.5 : 1.2;
    ctx.strokeStyle = color;

    if (o.kind === 'Brep') {
      ctx.beginPath();
      pts.forEach((p, i) => {
        const s = project(p[0], p[1], z0, v);
        if (i === 0) ctx.moveTo(s[0], s[1]);
        else ctx.lineTo(s[0], s[1]);
      });
      ctx.closePath();
      ctx.fillStyle = sel ? 'rgba(234,179,8,0.25)' : 'rgba(14,116,144,0.10)';
      ctx.fill();
      ctx.stroke();
      centers.set(o.id, project(cx, cy, z0, v));
    } else if (o.kind === 'Curve') {
      const samples = curveSamples(o.footprint, k, 2);
      ctx.beginPath();
      samples.forEach((p, i) => {
        const s = project(p[0], p[1], z0, v);
        if (i === 0) ctx.moveTo(s[0], s[1]);
        else ctx.lineTo(s[0], s[1]);
      });
      ctx.stroke();
      const mid = samples[Math.floor(samples.length / 2)];
      centers.set(o.id, project(mid[0], mid[1], z0, v));
    } else if (o.kind === 'Extrusion') {
      const z1 = (o.z + o.height) * k;
      const bottom = pts.map((p) => project(p[0], p[1], z0, v));
      const top = pts.map((p) => project(p[0], p[1], z1, v));
      ctx.beginPath();
      for (let i = 0; i < pts.length; i++) {
        const j = (i + 1) % pts.length;
        ctx.moveTo(bottom[i][0], bottom[i][1]);
        ctx.lineTo(bottom[j][0], bottom[j][1]);
        ctx.moveTo(top[i][0], top[i][1]);
        ctx.lineTo(top[j][0], top[j][1]);
        ctx.moveTo(bottom[i][0], bottom[i][1]);
        ctx.lineTo(top[i][0], top[i][1]);
      }
      ctx.stroke();
      centers.set(o.id, project(cx, cy, (z0 + z1) / 2, v));
    } else {
      const s = project(cx, cy, z0, v);
      ctx.strokeStyle = sel ? '#eab308' : '#db2777';
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.moveTo(s[0] - 5, s[1]);
      ctx.lineTo(s[0] + 5, s[1]);
      ctx.moveTo(s[0], s[1] - 5);
      ctx.lineTo(s[0], s[1] + 5);
      ctx.stroke();
      centers.set(o.id, s);
    }
  }
  return centers;
}

/** Unreal-вьюпорт: рендер акторов из реестра (треугольники в см → метры) */
export function renderUnreal(
  ctx: CanvasRenderingContext2D,
  w: number,
  h: number,
  actors: BridgeActor[],
  selectedId: string | null,
  colorOf: (a: BridgeActor) => string,
  fallbackBounds: { min: Vec3; max: Vec3 },
): Map<string, Vec2> {
  const centers = new Map<string, Vec2>();
  const min: Vec3 = [Infinity, Infinity, 0];
  const max: Vec3 = [-Infinity, -Infinity, 0];
  for (const a of actors) {
    for (const t of a.tris) for (const p of [t.a, t.b, t.c]) accum(p, min, max);
    for (const p of a.points) accum(p, min, max);
  }
  const bounds = isFinite(min[0]) ? { min, max } : fallbackBounds;
  const v = fitView(bounds, w, h);

  const g = ctx.createLinearGradient(0, 0, 0, h);
  g.addColorStop(0, '#0b1120');
  g.addColorStop(1, '#111a2e');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, w, h);
  drawGrid(ctx, v, bounds, 'rgba(148,163,184,0.08)');

  type Item = { d: number; draw: () => void };
  const items: Item[] = [];
  const light: Vec3 = [-0.4, -0.5, 0.77];

  for (const a of actors) {
    const sel = a.id === selectedId;
    const base = colorOf(a);
    const [r, gc, b] = hexToRgb(base);
    if (a.cls === 'AGenericBridgeMeshActor') {
      const flat = a.hint === 'Zone';
      for (const t of a.tris) {
        const A = t.a,
          B = t.b,
          C = t.c;
        const d = (depth(A[0], A[1], A[2]) + depth(B[0], B[1], B[2]) + depth(C[0], C[1], C[2])) / 3 / 100;
        const lambert = Math.max(0, t.n[0] * light[0] + t.n[1] * light[1] + t.n[2] * light[2]);
        const shade = 0.45 + 0.55 * lambert;
        const alpha = flat ? 0.5 : 1;
        const fill = sel
          ? `rgba(250,204,21,${flat ? 0.6 : 1})`
          : `rgba(${Math.round(r * shade)},${Math.round(gc * shade)},${Math.round(b * shade)},${alpha})`;
        items.push({
          d: flat ? d - 1e6 : d, // зоны — всегда под всем остальным
          draw: () => {
            const pa = project(A[0] / 100, A[1] / 100, A[2] / 100, v);
            const pb = project(B[0] / 100, B[1] / 100, B[2] / 100, v);
            const pc = project(C[0] / 100, C[1] / 100, C[2] / 100, v);
            ctx.beginPath();
            ctx.moveTo(pa[0], pa[1]);
            ctx.lineTo(pb[0], pb[1]);
            ctx.lineTo(pc[0], pc[1]);
            ctx.closePath();
            ctx.fillStyle = fill;
            ctx.fill();
            if (!flat) {
              ctx.strokeStyle = fill;
              ctx.lineWidth = 0.6;
              ctx.stroke();
            }
          },
        });
      }
    } else {
      const d = depth(a.center[0], a.center[1], a.center[2]) / 100 - 5e5;
      items.push({
        d,
        draw: () => {
          ctx.beginPath();
          a.points.forEach((p, i) => {
            const s = project(p[0] / 100, p[1] / 100, p[2] / 100, v);
            if (i === 0) ctx.moveTo(s[0], s[1]);
            else ctx.lineTo(s[0], s[1]);
          });
          ctx.strokeStyle = sel ? '#facc15' : base;
          ctx.lineWidth = sel ? 4 : 2.5;
          ctx.lineCap = 'round';
          ctx.lineJoin = 'round';
          ctx.stroke();
        },
      });
    }
    centers.set(a.id, project(a.center[0] / 100, a.center[1] / 100, a.center[2] / 100, v));
  }

  items.sort((p, q) => p.d - q.d);
  for (const it of items) it.draw();
  return centers;
}

function accum(p: Vec3, min: Vec3, max: Vec3) {
  min[0] = Math.min(min[0], p[0] / 100);
  min[1] = Math.min(min[1], p[1] / 100);
  max[0] = Math.max(max[0], p[0] / 100);
  max[1] = Math.max(max[1], p[1] / 100);
  max[2] = Math.max(max[2], p[2] / 100);
}

function hexToRgb(hex: string): [number, number, number] {
  const n = parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

export function nearest(centers: Map<string, Vec2>, x: number, y: number, radius = 22): string | null {
  let best: string | null = null;
  let bd = radius * radius;
  centers.forEach((c, id) => {
    const d = (c[0] - x) ** 2 + (c[1] - y) ** 2;
    if (d < bd) {
      bd = d;
      best = id;
    }
  });
  return best;
}
