# UrbanBridge protocol (Phase 1 + Stage 2 roads)

Rhino is a local WebSocket **server** at `ws://localhost:7890`; Unreal connects as a
client. Each WebSocket text frame contains exactly one UTF-8 JSON object. All
coordinates sent by Rhino are metres; Unreal converts them to centimetres.

## Messages

| Type | Direction | Meaning |
| --- | --- | --- |
| `full_sync` | Rhino → Unreal | Replace the receiver registry with the complete document. |
| `object_upserted` | Rhino → Unreal | Create or replace one object. |
| `batch_upsert` | Rhino → Unreal | Create or replace several objects from one edit operation. |
| `object_deleted` | Rhino → Unreal | Destroy the actor for the Rhino object GUID. |
| `heartbeat` | both | Liveness message, sent every five seconds. |
| `request_full_sync` | Unreal → Rhino | Ask Rhino to resend the active document. |
| `road_network_update` | Rhino → Unreal | Computed road graph (nodes, edges, issues, stats) from the Roads layer. |

`full_sync` contains `document_id` (the Rhino document runtime serial number), `units: "meters"`, and `objects`.
`object_upserted` contains `object`; `batch_upsert` contains `objects`; and
`object_deleted` contains `id`.

## Object payload

```json
{
  "id": "Rhino object GUID",
  "layer": "Zones::Residential",
  "geometry_type": "mesh",
  "mesh": {
    "vertices": [0, 0, 0, 1, 0, 0, 0, 1, 0],
    "indices": [0, 1, 2],
    "normals": [0, 0, 1, 0, 0, 1, 0, 0, 1]
  },
  "polyline": null,
  "attributes": { "zone_type": "residential" }
}
```

Exactly one of `mesh` and `polyline` is non-null. A `mesh` has flat XYZ
`vertices`, triangle `indices`, and optional flat XYZ `normals`. A `polyline`
has flat XYZ `points`; Rhino samples curves so an arc-length segment is at most
0.5 metres. `attributes` is a string-to-string copy of Rhino User Text.

## road_network_update (Stage 2)

Sent after every rebuild of the road graph (debounced ~300 ms after the last
change on the `Roads` / `Roads::*` layers). Geometry continues to flow via the
existing object messages; this is a parallel, derived layer.

```json
{
  "type": "road_network_update",
  "nodes": [
    {
      "id": "n_120_340_0",
      "position": [12.0, 34.0, 0.0],
      "degree": 3,
      "node_type": "intersection",
      "connected_edge_ids": ["a1b2c3...", "d4e5f6...", "a7b8c9..."]
    }
  ],
  "edges": [
    {
      "edge_id": "a1b2c3...",
      "start_node_id": "n_120_340_0",
      "end_node_id": "n_200_340_0",
      "length_m": 80.0,
      "road_class": "secondary",
      "lanes": 2,
      "width_m": 12.0
    }
  ],
  "issues": [
    {
      "type": "dangling_end",
      "severity": "warning",
      "message": "Тупик без флага is_terminal",
      "related_node_id": "n_450_10_0",
      "related_edge_ids": ["f1a2..."]
    }
  ],
  "stats": {
    "total_length_m": 1240.5,
    "length_by_class": { "primary": 400.0, "secondary": 600.5, "local": 240.0 },
    "intersection_count": 8,
    "dead_end_count": 5,
    "component_count": 1
  }
}
```

Node IDs are stable hashes of coordinates rounded to the snap tolerance (0.5 m):
`n_{round(x/tol)}_{round(y/tol)}_{round(z/tol)}`.

Issue types: `dangling_end`, `disconnected_component`, `self_intersecting`,
`duplicate_overlap`, `degenerate_segment`, `crossing_without_node`,
`missing_attributes`. Severities: `info`, `warning`, `error`.

## Delivery and recovery

The sender never allows an unsupported or unmeshable Rhino object to abort a
sync; it writes an `[UrbanBridge]` diagnostic and continues. The receiver must
treat `upsert` as idempotent and may request a full sync after connecting or
reconnecting. A client may ignore heartbeats other than for connection health.
A `request_full_sync` also triggers a fresh `road_network_update`.
