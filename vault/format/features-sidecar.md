---
title: Features sidecar JSON
status: draft
created: 2026-08-25
---

# Features sidecar (`*.features.json`)

Everything a user **defines** in the viewer — predicates, section cuts, supports, springs,
whatever comes next — lives in one JSON file beside the model, never in the binary
([[v4-schema]]). The binary is results: large, write-once, solver-produced. Features are
intent: small, hand-edited, iterated, consumed by other programs (the C# pre/post scripts).
Opposite lifecycles → separate files.

Theme: **define in viewer → export features → analysis script consumes.** Each feature
type is a viewer tab that owns one section of the envelope.

## Envelope

```json
{
  "format": "pluto-features",
  "version": 1,
  "model": {
    "modelId": "ProjectX/Model7/rev3",
    "geometryHash": "sha256:…",
    "units": { "length": "in", "force": "kip" }
  },
  "predicates":  { "version": 1, "items": [] },
  "sectionCuts": { "version": 1, "items": [] },
  "supports":    { "version": 1, "items": [] },
  "springs":     { "version": 1, "items": [] }
}
```

Rules:
- Every section is `{ version, items[] }`, versioned independently so predicates can
  evolve without breaking section cuts.
- Every item has `id` (GUID, stable — lets the C# side diff), `name`, optional `tags[]`.
- **Unknown sections and unknown item keys are preserved round-trip** by every consumer.
  The viewer must not drop a `springs` block it doesn't render yet. Same "skip unknown"
  policy as the binary directory.
- `model.geometryHash` must match the loaded model's `META.geometryHash`; the viewer warns,
  scripts may refuse. Because the hash excludes field blocks, a features file authored on
  a geometry-only export binds to the full-results export of the same run.
- Multi-model sessions (identical geometry verified at load) share one features file.

## Addressing geometry

- **Never by index.** Indices are an artifact of one export. Use real IDs.
- Element references carry the domain: `{ "domain": "beams", "ids": [101, 102] }`
  (IDs are per domain in v4).
- Node references: `{ "nodeIds": [...] }`.
- Geometric definitions (planes, boxes) reference nothing and are stated in
  `model.units` in the global frame.

## Sections

### predicates
Tree of half-space / slab tests combined with boolean ops. Store the **tree** (intent);
resolution against the model happens in both the viewer and the scripts. Optionally cache
the resolved membership with the hash it was resolved against.

```json
{
  "id": "…", "name": "Deck plate",
  "target": "elements",                       // "elements" | "nodes"
  "domains": ["shells"],                      // optional filter
  "tree": {
    "op": "and",
    "children": [
      { "plane": { "point": [0,0,10], "normal": [0,0,1] }, "side": "positive", "finite": { "extent": [50, 50] } },
      { "op": "not", "children": [ { "plane": { "point": [20,0,0], "normal": [1,0,0] }, "side": "positive" } ] }
    ]
  },
  "resolved": { "geometryHash": "sha256:…", "ids": [ … ] }   // optional cache
}
```

`op` ∈ `and | or | not`. A leaf is a `plane` with `side` and optional `finite` extent
(rectangle in the plane, centred on `point`, axes from `normal` + `up`). The C# side emits
group definitions from this.

### sectionCuts
Mirrors the viewer's current section-cut definition (plane + bounds + which domains) so
the dedicated C# section-cut analysis uses exactly the geometry the user saw.

```json
{ "id": "…", "name": "Cut A-A", "plane": { "point": [], "normal": [] },
  "bounds": { "up": [0,0,1], "halfWidth": 30, "halfHeight": 10 }, "domains": ["shells","beams"] }
```

### supports / springs (planned)
```json
{ "id": "…", "name": "Pile 3", "nodeIds": [4021],
  "dof": { "tx": "fixed", "ty": "fixed", "tz": "spring", "rx": "free", "ry": "free", "rz": "free" },
  "stiffness": { "tz": 1500 } }
```

## Naming / discovery

`model.bin` + `model.features.json` side by side. The viewer file picker accepts either or
both; export writes back to the same name. Exfiltration: the JSON is plain text and small.

## Open

- [ ] Predicate `finite` extent representation — rectangle vs. arbitrary polygon.
- [ ] Should section cuts store the *result* slice too (for review without recompute)? Probably no — results belong in a binary.
