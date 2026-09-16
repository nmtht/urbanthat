# Branch: refactor/cleanup-namespace-hub

## 1. Namespace rename
`UrbanBridge.Rhino` → `UrbanBridge.Plugin` (all `.cs` + `RootNamespace` in csproj).

Reason: nested name under the same root as RhinoCommon (`Rhino.*`) caused the compiler to resolve
`Rhino.Geometry` as `UrbanBridge.Rhino.Geometry` inside our types. Workarounds with `global::` were
spreading; one rename removes the trap.

## 2. Hub geometry — single solid per layer
`BuildHub` no longer stacks sharp boolean tails **and** fillet pads as separate overlapping Breps.

- Roadway: `Union(road tails ∪ corner pads)` → one planar Brep set
- Sidewalk: `(full outer − roadway) ∪ outer pads` → one planar Brep set
- Arc curves remain as light decoration

Friendlier for DWG export (no coincident duplicate faces).

## 3. Boolean failure is not exceptional
Failed `CreateBooleanUnion` on hub tails used to throw `InvalidOperationException`.
It now falls back to individual tail polygons and continues — matching the TZ intent that sharp
corners / failed booleans are an expected path, not an unexpected error.

## 4. Process note
Prefer reviewing `git diff origin/stage2-road-network...HEAD` before merge; avoid accepting opaque "restore" commits without a real diff.
