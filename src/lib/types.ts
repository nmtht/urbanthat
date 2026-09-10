// Контракт протокола — зеркало docs/protocol.md (раздел 3 ТЗ)

export interface MeshData {
  vertices: number[]; // плоский массив [x0,y0,z0, x1,y1,z1, ...], метры
  indices: number[]; // треугольники
  normals: number[] | null; // опционально; null → Unreal считает сам
}

export interface PolylineData {
  points: number[]; // плоский массив [x0,y0,z0, ...], метры
}

export interface ObjectPayload {
  id: string;
  layer: string;
  geometry_type: 'mesh' | 'polyline';
  mesh: MeshData | null;
  polyline: PolylineData | null;
  attributes: Record<string, string>;
}

export type BridgeMessage =
  | { type: 'full_sync'; document_id: string; units: string; objects: ObjectPayload[] }
  | { type: 'object_upserted'; object: ObjectPayload }
  | { type: 'batch_upsert'; objects: ObjectPayload[] }
  | { type: 'object_deleted'; id: string }
  | { type: 'heartbeat'; timestamp: number }
  | { type: 'request_full_sync' };

export type MessageType = BridgeMessage['type'];

export type AssetHint = 'Zone' | 'Road' | 'Building' | 'Generic';

export const BRIDGE_PORT = 7890;
export const BRIDGE_URL = `ws://localhost:${BRIDGE_PORT}`;
export const HEARTBEAT_INTERVAL_MS = 5000;
export const RECONNECT_BASE_MS = 1000;
export const RECONNECT_MAX_MS = 10000;
