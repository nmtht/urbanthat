# UrbanBridge protocol (Phase 1)

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

## Delivery and recovery

The sender never allows an unsupported or unmeshable Rhino object to abort a
sync; it writes an `[UrbanBridge]` diagnostic and continues. The receiver must
treat `upsert` as idempotent and may request a full sync after connecting or
reconnecting. A client may ignore heartbeats other than for connection health.
