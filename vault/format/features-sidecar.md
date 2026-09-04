---
title: Features sidecar JSON
status: draft
created: 2026-08-25
---

# Features sidecar (`*.features.json`)

*↑ [[vault/Home|Home]] › [[vault/format/Format map|Format map]]*

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

Display flag: `"hidden"` on a group item. **Absent or `true` = listed, not painted** (out
of the precedence chain); only `"hidden": false` paints (2026-09-04: every group starts
unticked, so a fresh sidecar shows the model uncoloured until you tick). Exporters never
write it; the viewer's Groups tab writes it explicitly on every tick, and Export keeps it.

Viewer behaviour (`viewer/scripts/features.js`, **Groups tab** on the right edge, 2026-09-03):
- One switch, **Color by groups**: every element painted by its group's color through a
  palette LUT (`catIdx` vertex attribute + `uGroupMode`); ungrouped = neutral grey. Works
  on shells and beams; envelope/field views are untouched when the switch is off.
- Precedence: the **last enabled** group listing an element wins → order coarse-to-fine
  ("all W shapes", then "pipes by size", then "insulated"). The tab shows each group as
  a row: enable tick (`hidden`), colour swatch (edits `color`), name, and
  **painted / members** counts — an orange count means members are shadowed by a group
  lower in the list. Drag rows to reorder (mutates `groups.items` order); All / None /
  Invert; Export writes the whole sidecar with order, colours and `hidden` flags.
- Node groups (`nodeIds` members) paint too (2026-09-04): their nodes are drawn as square
  points in the group colour while Color by groups is on (depth-tested). **One marker per
  node**, coloured by the last enabled group that lists it — the same precedence as
  elements, so a node in two groups is one square in the lower group's colour and the
  upper group's row reports it as shadowed. Only **distinct** nodes at the same position
  (a plate joint over its ground joint) are nudged apart, 0.4 % of the model span per
  occupant (+x, +y, +z, diagonals…), the winning group's list order deciding who keeps
  the true spot. Rows show painted/total, shadowed and nudged counts; the section-cut
  isolate hides points off the kept panel. Like every group they start unticked until the item
  carries `hidden: false`. The Groups tab lists them on a separate **Nodes** sub-tab; All /
  None / Invert act on the sub-tab showing.
- `color` omitted → auto palette. Hover/pick readout shows the winning group name.
- Headless test: `node viewer/tests/test_groups.js`.

Predicate-referenced members are accepted by `features.js` today (counted as unresolved
until the predicate module is ported); the hook point is `resolveMember()`.

C# side: `scripts/lib/sidecar/FeaturesSidecar.cs` — `AddGroup(...)`, `AddNodeGroup(...)`,
`ComputeGeometryHash(blocks)`, `ToJson()`, `ToStaadGroupBlock()` (bridge to the existing
STAAD group writer; adjust prefixes to its conventions when ported).

Steel + pipes trib study: color W shapes one group, pipes grouped by size / insulation via
tags; the trib analysis reads the same groups back to know which pipes sit on which steel.

### predicates
Tree of slab tests combined with boolean ops. Store the **tree** (intent); resolution
against the model happens in both the viewer and the scripts.

**Decision (2026-09-01):** the node grammar is the PRODUCTION dialect the existing C#
parser (`Groups.cs` `ParsePlane`/`ParseFinitePlane`, transcribed in
`scripts/lib/readers/Groups.cs`) and the old viewer already speak — not the
half-space/`side`/`not`-op sketch this section previously carried. Negation is a
`negated` flag on any node; a leaf is a slab (|distance| ≤ `tol`, both sides) plus a
normal-alignment test (`normal_tol_deg`, applied to shell elements only — beams and
nodes have no meaningful surface normal) and an optional finite rectangle (`width` ×
`length` in the plane; u axis = world axis least aligned with the normal projected into
the plane, rotated by `angle_deg`; v = normal × u). Points/normals are **world (plant)
coordinates** in `model.units`; the viewer subtracts its recenter offsets before testing.

```json
{
  "id": "p-…", "name": "Deck plate",
  "target": "elements",                       // "elements" | "nodes"
  "domains": ["shells"],                      // optional filter ("shells" | "beams")
  "tree": {
    "kind": "and",                            // "and" | "or"  (ops)
    "negated": false,
    "children": [
      { "kind": "finitePlane", "negated": false,
        "point": [0,0,120], "normal": [0,0,1],
        "tol": 0.1, "normal_tol_deg": 5.0,
        "width": 240, "length": 240, "angle_deg": 0 },
      { "kind": "plane", "negated": true,
        "point": [240,0,0], "normal": [1,0,0],
        "tol": 0.1, "normal_tol_deg": 5.0 }
    ]
  }
}
```

Viewer behaviour (`viewer/scripts/predicates.js`, `FEAPredicates`): tree editor +
click placement; `resolveMembers(predicateId)` expands to per-domain **real-ID** member
lists, which is how groups with `{ "predicateId": … }` members resolve. The C# side emits
STAAD group definitions from the same trees.

### sectionCuts
Mirrors the viewer's section-cut definition (plane + bounds + which domains) so the
dedicated C# section-cut analysis uses exactly the geometry the user saw. Live since
2026-09-04: the viewer keeps many named cuts here (`sectionCut.js`, ported from the
archived viewer's management layer) and round-trips them.

```json
"sectionCuts": { "version": 1,
  "groups": [ { "name": "Walls", "visible": true } ],
  "items": [
    { "id": "c-…", "name": "Cut A-A", "group": "Walls", "visible": true, "axis": "x",
      "plane":  { "point": [x, y, z], "normal": [nx, ny, nz] },
      "bounds": { "up": [ux, uy, uz], "halfWidth": 15 },
      "domains": ["shells"] }
  ] }
```

- `plane` is the **section plane**: `point` = the cut centre on the panel, `normal` = the
  plane's normal. `bounds.up` = the panel's normal at the centre (the probe ray direction);
  `halfWidth` = half the cut length. The probe line direction is derived, `dir = normal ×
  up`, so a sloped direction round-trips without an extra field.
- `axis` (`"x" | "y" | "z"`) is the axis the user chose; the list colour-codes by it.
  `sloped: true` bends that axis onto the panel (dir = axis minus its component along
  `up`, normalised): Z on a roof runs up the slope, X on a curved wall follows the tangent.
  `wrap: true` (implies sloped) makes the probe **march over the surface**, re-bending the
  axis on every face it lands on: a Z cut climbs the wall, turns the eave and continues up
  the roof; an X cut follows the wall around as an arc (checked on TANK-A: 40 ft of arc
  = 68.2°, radius held to 0.03 ft). Faces whose normal is within 15° of the axis end the
  march (a Z march stops at the baseplate and near the crown; an X cut placed where X is
  the wall normal marches nowhere). Stations are recomputed from the mesh on load, never
  stored. An item with no `axis` (a foreign direction) reads as sloped, amber.
- `groups[]` carries per-group visibility; collapsed/expanded is view state only.
- Unknown keys on an item survive a round trip (kept on the runtime object's `_raw`).
- The archived viewer's standalone `section_cuts.json` (`{version, groups, cuts:[{point
  (inches), axis, length (inches)}]}`) imports through the panel's Import button; points
  convert to the model unit and the panel normal is found by probing.
- `halfHeight` and `domains: ["beams"]` are reserved for the C# free-body cut; the viewer
  writes `domains: ["shells"]` and leaves `halfHeight` alone.

### supports / springs (planned)
```json
{ "id": "…", "name": "Pile 3", "nodeIds": [4021],
  "dof": { "tx": "fixed", "ty": "fixed", "tz": "spring", "rx": "free", "ry": "free", "rz": "free" },
  "stiffness": { "tz": 1500 } }
```

## Re-export keeps the user's layer (2026-09-04)

An exporter rewrites the sidecar on every run, so `FeaturesSidecar.MergeFrom(previous,
exporterTag)` runs first and carries over: every top-level section the writer does not
model (`sectionCuts`, `predicates`, anything unknown) verbatim; groups **without** the
exporter's tag (hand-made groups) verbatim; and for exporter groups matched by name their
`id`, `color` and `hidden`. `SapToPluto` does this with tag `sap` and reports
`merged previous sidecar: …` in its summary. So section cuts, predicates and group styling
survive `run_sap.py`; only the exporter's own groups are regenerated. A group whose ids
no longer match the new geometry shows as unmatched in the viewer rather than vanishing.

## Naming / discovery

`model.bin` + `model.features.json` side by side. **One picker, one save** (2026-09-04,
`viewer/scripts/files.js`): "Choose files…" takes .bin and .json together (Ctrl-click);
the sidecar is matched to the model by base name (`X.features.json` or `X.json` ↔ `X.bin`).
JSON is classified by shape — a `pluto-features` sidecar, the archived viewer's
`section_cuts.json` (`{cuts:[…]}`) or `predicates.json` (`{expressions:[…]}` /
`{groups:[{predicate}]}`) — and the legacy files merge into the current sidecar. Edits
(groups, predicates, cuts) mark the file `*`; **Save features** (Ctrl+S) writes it back.
Chrome / Edge: the File System Access API keeps a handle to the picked sidecar, so Save
writes over it in place; a first save opens the picker beside the model with the model's
name suggested. Elsewhere it downloads. The per-tab Import / Export buttons are gone.
Exfiltration: the JSON is plain text and small.

## Open

- [ ] Predicate `finite` extent representation — rectangle vs. arbitrary polygon.
- [ ] Should section cuts store the *result* slice too (for review without recompute)? Probably no — results belong in a binary.
