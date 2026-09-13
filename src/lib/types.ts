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

// Stage 2 — road network

export type RoadNodeType = 'dead_end' | 'through' | 'intersection';

export interface RoadNodePayload {
  id: string;
  position: [number, number, number];
  degree: number;
  node_type: RoadNodeType;
  connected_edge_ids: string[];
}

export interface RoadEdgePayload {
  edge_id: string;
  start_node_id: string;
  end_node_id: string;
  length_m: number;
  road_class: string;
  lanes: number;
  width_m: number;
}

export type IssueSeverity = 'info' | 'warning' | 'error';

export interface NetworkIssuePayload {
  type: string;
  severity: IssueSeverity;
  message: string;
  related_node_id?: string | null;
  related_edge_ids?: string[];
}

export interface NetworkStatsPayload {
  total_length_m: number;
  length_by_class: Record<string, number>;
  intersection_count: number;
  dead_end_count: number;
  component_count: number;
}

export interface RoadNetworkUpdateMessage {
  type: 'road_network_update';
  nodes: RoadNodePayload[];
  edges: RoadEdgePayload[];
  issues: NetworkIssuePayload[];
  stats: NetworkStatsPayload;
}

export type BridgeMessage =
  | { type: 'full_sync'; document_id: string; units: string; objects: ObjectPayload[] }
  | { type: 'object_upserted'; object: ObjectPayload }
  | { type: 'batch_upsert'; objects: ObjectPayload[] }
  | { type: 'object_deleted'; id: string }
  | { type: 'heartbeat'; timestamp: number }
  | { type: 'request_full_sync' }
  | RoadNetworkUpdateMessage;

export type MessageType = BridgeMessage['type'];

export type AssetHint = 'Zone' | 'Road' | 'Building' | 'Generic';

export const BRIDGE_PORT = 7890;
export const BRIDGE_URL = `ws://localhost:${BRIDGE_PORT}`;
export const HEARTBEAT_INTERVAL_MS = 5000;
export const RECONNECT_BASE_MS = 1000;
export const RECONNECT_MAX_MS = 10000;
