---
title: Features sidecar JSON
status: draft
created: 2026-08-25
---

# Features sidecar (`*.features.json`)

Everything a user **defines** in the viewer — predicates, section cuts, supports, springs,
whatever comes next — lives in one JSON file beside the model, never in the binary
([[vault/format/v4-schema|v4-schema]]). The binary is results: large, write-once, solver-produced. Features are
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
  "groups":      { "version": 1, "items": [] },
  "predicates":  { "version": 1, "items": [] },
  "sectionCuts": { "version": 1, "items": [] },
  "supports":    { "version": 1, "items": [] },
  "springs":     { "version": 1, "items": [] }
}
```text

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

### groups — *the* group object (viewer color ⇄ STAAD groups)

A group is a named list of member IDs. It is the **same object** the existing C# workflow
already has: `predicates → groups (ID lists) → STAAD GROUP block`. The sidecar just makes
it a file, and adds `color` as a display attribute. Nothing about "color groups" is
separate from "STAAD groups" — one list, two consumers.

```json
{ "id": "g-…", "name": "Pipe 8in insulated", "color": "#e0913a",
  "tags": ["pipe", "8", "insulated"],
  "source": { "predicateId": "p-…" },          // optional: which predicate produced it
  "export": { "staadName": "PIPE8_INS" },        // optional: name in the STAAD GROUP block
  "members": [
    { "domain": "beams", "ids": [20001, 20002] },   // explicit element members, per-domain IDs
    { "domain": "nodes", "nodeIds": [4021] },       // explicit node members
    { "predicateId": "p-…" }                        // RUNTIME members: resolved from the predicate
  ] }
```

Members are either **explicit** (ID lists — what the STAAD exporter ultimately needs) or
**by predicate** (a reference; the predicate is lightweight geometry and its element IDs
are computed at load / on demand, so a 10k-element group stores nothing but the ID). A
group may mix both. Consumers that cannot evaluate predicates (or a model whose hash
differs) treat predicate members as empty and say so; the C# exporter resolves them before
writing STAAD groups. `source.predicateId` remains for the "materialised from" audit trail
when IDs *are* written out.

Viewer behaviour (`viewer/scripts/features.js`):
- One switch, **Color by groups**: every element painted by its group's color through a
  palette LUT (`catIdx` vertex attribute + `uGroupMode`); ungrouped = neutral grey. Works
  on shells and beams; envelope/field views are untouched when the switch is off.
- Precedence: last group listing an element wins → order coarse-to-fine ("all W shapes",
  then "pipes by size", then "insulated").
- `color` omitted → auto palette. Legend lists name + resolved member count.
- Hover/pick readout shows the group name.

Predicate-referenced members are accepted by `features.js` today (counted as unresolved
until the predicate module is ported); the hook point is `resolveMember()`.

C# side: `viewer/FeaturesSidecar.cs` — `AddGroup(...)`, `AddNodeGroup(...)`,
`ComputeGeometryHash(blocks)`, `ToJson()`, `ToStaadGroupBlock()` (bridge to the existing
STAAD group writer; adjust prefixes to its conventions when ported).

Steel + pipes trib study: color W shapes one group, pipes grouped by size / insulation via
tags; the trib analysis reads the same groups back to know which pipes sit on which steel.

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
```text

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
```text

## Naming / discovery

`model.bin` + `model.features.json` side by side. The viewer file picker accepts either or
both; export writes back to the same name. Exfiltration: the JSON is plain text and small.

## Open

- [ ] Predicate `finite` extent representation — rectangle vs. arbitrary polygon.
- [ ] Should section cuts store the *result* slice too (for review without recompute)? Probably no — results belong in a binary.

```