---
title: Features sidecar JSON
status: draft
created: 2026-08-25
---

# Features sidecar (`*.features.json`)

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

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
  "springs":     { "version": 1, "items": [] },
  "properties":  { "version": 1, "units": {}, "items": [] },
  "loads":       { "version": 1, "units": {}, "items": [] },
  "review":      { "version": 1, "summary": "", "items": [] }
}
```

The last three (and `model.source`) are a pipe model's, written by the CAESAR export (below).

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
  points in the group colour (depth-tested). Since 2026-10-03 the markers show whenever a node
  group is enabled, **independent of Color by groups**, which now drives element painting only,
  so restraint locations can sit on top of a field contour. **One marker per
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
- `crop: { axis: "x"|"y"|"z", width, depth? }` (2026-09-10) is the item's crop box for
  the viewer's "Crop box" isolate mode: elements whose centroid lies within `halfWidth` of
  `point` along the cut direction, within `width / 2` along the crop axis and, when `depth`
  is given, within `depth / 2` along the third direction (cut direction × crop axis) are
  kept, on any panel; beams are kept when both end joints lie in the box. Which isolate
  mode is showing (off / this wall or slab / crop box) is view state, not stored.
- `groups[]` carries per-group visibility; collapsed/expanded is view state only.
- Unknown keys on an item survive a round trip (kept on the runtime object's `_raw`).
- The archived viewer's standalone `section_cuts.json` (`{version, groups, cuts:[{point
  (inches), axis, length (inches)}]}`) imports through the panel's Import button; points
  convert to the model unit and the panel normal is found by probing.
- `halfHeight` and `domains: ["beams"]` are reserved for the C# free-body cut; the viewer
  writes `domains: ["shells"]` and leaves `halfHeight` alone.

### supports (live 2026-10-04)

Where the model is held: one item per support, drawn by the viewer as 3D symbols. Written by
an exporter that knows the supports (CAESAR II first, [[vault/arms/caesar-to-viewer|caesar-to-viewer]])
or by hand. Decision: [[vault/decisions/2026-10-03-imported-restraints|2026-10-03-imported-restraints]].

```json
"supports": { "version": 1, "items": [
  { "id": "…", "name": "2110 +Y", "nodeIds": [2110],
    "dof": { "ty": "+" },
    "tags": ["caesar", "support"],
    "restraints": [ { "type": "+Y", "kind": "oneway", "direction": [0, 1, 0], "sign": "+", "axis": [0, 0, 1] } ],
    "source": { "exporter": "caesar", "node": 2110 } },
  { "id": "…", "name": "Pile 3", "nodeIds": [4021],
    "dof": { "tx": "fixed", "ty": "fixed", "tz": "spring", "rx": "free", "ry": "free", "rz": "free" },
    "stiffness": { "tz": 1500 } }
] }
```

- **Item:** `id`, `name`, `nodeIds`, `dof` (keys `tx ty tz rx ry rz`; values `fixed`, `+` / `-`
  for one-way in the direction it pushes, `gap`, `spring`, `guide`, `limit`, `hanger`, `imposed`,
  `free`), `tags`, optional `hidden: true` (that item is not drawn). Any other key is kept on a
  round trip.
- **`restraints`** (optional, written by an exporter that knows them): one entry per restraint
  at the node.
  - `type`: the program's own code (`ANC`, `+Y`, `GUI`, `LIM`, `RX` …).
  - `kind`: `anchor | translation | oneway | guide | limit | rotation | hanger | nozzle | imposed | other`.
  - `direction`: unit vector the restraint acts along. For a one-way it is the way it pushes; for a
    guide, across the pipe. It is absent for an anchor, and for a guide on a vertical pipe, which
    carries `vertical: true` instead.
  - `axis`: the pipe axis at the node.
  - As the program has them: `gap`, `friction`, `stiffness` (absent = rigid), `cnode`, `tag`. A
    hanger has `design` (`designed`: CAESAR designs it; `spring`: a rate and cold load as given;
    `constant`: a constant-effort support), `coldLoad` / `hotLoad`, and as given `stiffness` (its rate),
    `constantLoad`, `table`, `count`; a nozzle has `nozzleType` and its vessel node as `cnode`; an imposed displacement has `fixed[6]`.
  - An item without `restraints` is drawn from its `dof` letters.
- **Loads are not here.** Results stay in the binary. CAESAR writes the loads on the supports as
  beam components of kind `restraint` (`Restraint FX … |M|`): node totals on every beam end at the
  support, NaN elsewhere ([[vault/format/v4-schema|v4-schema]] §4.1).
- **Viewer** (`viewer/scripts/supports.js`, `FEASupports`; **Supports** panel under Beams):
  - **Symbols** in CAESAR II's language: a plate for an anchor, slim arrows pointing at the pipe
    along the line of action for the rest. (A first set of cubes, collars, hoops and plates crowded
    busy nodes and was replaced the same day.)
    - anchor: a square plate through the pipe, square to it. An imposed displacement draws the
      same, since it sits on an anchor point.
    - translation: two arrows, one from each side.
    - one-way: one arrow, from the side it pushes from (`+Y`: below the pipe).
    - guide: two arrows across the pipe (four on a riser).
    - limit: two arrows along the pipe pointing at the node, on its top surface, where they don't
      hide inside it. An axial translation draws the same, and an axial one-way draws one of them.
    - rotation: a double-headed arrow (moment vector) at ¾ size on one side, beyond where a
      translation arrow on that line ends.
    - hanger: one arrow below the pipe, holding it up.
    - other: a small ball on top.
  - **Size and gaps:** sized from the drawn pipe radius at the node, times the Size slider. The
    arrow tips stay on the pipe, and a restraint with a gap stops short of it.
  - **Colour:**
    - the node's winning node group (the CAESAR export writes one group per restraint combination);
    - or the restraint kind;
    - or any `restraint` component in the current load case, through the active colormap over the
      drawn supports' own range (abs value applies).
  - **Visibility:** an item `hidden: true`, a node listed only by unticked node groups, the
    section-cut isolate, or Show off hides the symbol.
  - **Readout and pin card:** hovering gives the support, its restraints and `|F|` / `|M|` in the
    current load case. Pinning gives a card with the loads in every load case and the displacement
    in the current one.
  - **Deformed shape:** the symbols stay put and a line runs to each displaced node. CNODE
    restraints whose connected node is apart get a line to it.
  - The viewer never edits the section, so it round-trips verbatim.
  - Test: `viewer/tests/test_supports.js`.
- **C#:** `FeaturesSidecar.AddSupport(name, nodeIds, dof, tags, extraJson)` with
  `WriteSupports = true`; `extraJson` carries the extension keys. See the merge rules below.

### The pipe model: loads, properties, review, model.source (live 2026-10-23)

The viewer is the confirmation step for a pipe model ([[vault/arms/pipe-workbook|pipe-workbook]]): it shows
everything the model holds before CAESAR imports it. The CAESAR export (`CaesarToPluto` with
`PipeSidecar.cs`) writes these sections from the pipe model: the caller's (`Build-Pipe.ps1 -Preview`, through
`CaesarPreview`), or one imported from the `.cii` itself (`Run-Caesar.ps1`). Decision:
[[vault/decisions/2026-10-23-pipe-model-in-viewer|2026-10-23-pipe-model-in-viewer]].

```json
"model": { "modelId": "pipe/line", "geometryHash": "sha256:…", "units": { "length": "in", "worldOffset": "0 0 0" },
           "source": { "file": "line.xlsx", "kind": "xlsx", "bytes": 48213, "saved": "2026-10-23T10:41:07Z",
                       "sha256": "…", "model": "…", "written": "2026-10-23T10:41:09Z" } },
"properties": { "version": 1, "units": { "length": "in.", "diameter": "in.", "temperature": "F", "pressure": "lb./sq.in.", … },
  "items": [ { "element": 3, "from": 30, "to": 40, "kind": "Bend", "name": "", "line": "",
               "members": [{ "domain": "beams", "ids": [300, 301, 302] }],
               "values": { "dx": 0, "dy": 0, "dz": 120, "length": 120, "od": 6.625, "wall": 0.28, "T": [300], "P": [150],
                           "material": 106, "bend": { "radius": -4.0404, "named": "Short", "code": 4, "type": 0, "nodes": [["M", 35]] } } } ] },
"loads": { "version": 1, "units": { "force": "lb.", "moment": "in.lb.", "length": "in.", "rotation": "deg", "uniform": "lb./in.", "g": "g's" },
  "items": [
    { "id": "caesar-force-40", "name": "F 40", "kind": "force", "nodeIds": [40], "elements": [3, 4],
      "vectors": [{ "v": 1, "values": [0, -1200, 0, 0, 0, 3000] }] },
    { "id": "caesar-displacement-200", "name": "D 200", "kind": "displacement", "nodeIds": [200],
      "vectors": [{ "v": 2, "values": [null, -0.25, null, null, null, null] }] },
    { "id": "caesar-uniform-1", "name": "U 10-60", "kind": "uniform", "elements": [1, 2, 3], "nodeIds": [10, 20, 30, 60],
      "members": [{ "domain": "beams", "ids": [100, 200, 300] }], "vectors": [{ "v": 1, "values": [0, -5, 0], "g": false }] },
    { "id": "caesar-wind-1", "name": "Wind 10-60", "kind": "wind", "elements": [1, 2, 3], "members": [ … ],
      "wind": { "type": "wind", "shape": 0.7 } } ] },
"review": { "version": 1, "summary": "0 error(s), 1 warning(s), 2 notice(s), 1 info",
  "items": [ { "id": "r1", "level": "warning", "code": "tee-no-sif", "text": "3 elements meet at node 30 with no SIF / tee entry …",
               "nodeIds": [30], "elements": [2, 3, 6], "members": [{ "domain": "beams", "ids": [200, 300, 600] }] } ] }
```

- **Values are in the job's units**, as CAESAR writes them. Each section's `units` gives CAESAR's own labels
  once (`in.`, `lb./sq.in.`). Element numbers are CAESAR's input order; `members` are the element's beams in
  the binary (element × 100 + piece).
- **`model.source`**: where the model came from. `file` is the name only (no folder), `kind` is
  `xlsx | toml | cii | json`, `saved` is the file's last write (UTC), `sha256` is the file's, `model` is the
  model hash the runner's build stamp uses, and `written` is when this preview was made.
- **`properties`**: one item per element. `values` holds what is set: deltas and length, OD / wall,
  insulation, corrosion, `T` and `P` (T1… / P1…, the unused tail cut), material, E, Poisson, densities,
  `hydro` (hydrotest pressure), `millMinus` / `millPlus` (mill tolerance, %), `seamWeld` (1), and
  `bend` (radius, the name of a coded radius, the code, `noRadius` for a code CAESAR reads as none, angle
  nodes), `rigid` (weight, type), `expJoint` (stiffnesses, effective ID), `reducer` (the TO end), `uniform`,
  `wind`.
- **`loads`**: forces / moments and imposed displacements per node (vectors F1–F9 / D1–D9, six values each;
  a free displacement DOF is `null`, an imposed 0 holds it). Uniform loads and wind per run of consecutive
  elements with the same values acting (a load CAESAR carries forward included) (U1–U9 with the g flag; wind type and shape factor). Only vectors that carry
  something are written. **Load cases are not in the model**: CAESAR builds them. Wind's direction and
  speed are in CAESAR's load cases too, so the viewer asks which way to draw it.
- **`review`**: the build's checks, errors first (`error | warning | notice | info`), each with the nodes,
  elements and beams it is about. Codes: `unsupported`, `tee-no-sif`, `free-end`, `element-self`,
  `element-zero`, `element-twice`, `off-model`, `cnode-off-model`, `bend-corner`, `bend-room`, `bend-code`,
  `node-twice`, `uniform-mixed`; CAESAR's error checker (`PipeReview.CaesarRules`, [[vault/arms/caesar-checks|caesar-checks]]):
  `node-range`, `corrosion-wall`, `temperature-alpha`, `displacement-restrained`, `loop-closure`, `bend-angle`,
  `bend-node-angle`, `corner-no-bend`, `intersection`, `sif-below-one`, `sif-at-bend`, `thin-wall`, `reducer-angle`,
  `expansion-joint`, `expansion-joint-thrust`, `wave-added-mass`, `rigid-weightless`, `uniform-repeated`, `hangers`,
  `hangers-given`, `hanger-twice`, `hanger-rate-only`, `hanger-free`, `nozzles`, `nozzle-restrained`, `nozzle-axis`, `sif-pad` (`PipeReview.cs`); `restraint-code`, `sif-type` and `nozzle-layout` (a code or a layout from CAESAR's documentation that no CAESAR file has shown yet:
  `CaesarNeutralWriter.NoticeItems`); `build` (the builder's or the workbook reader's
  warnings); `export` (this export's own warnings; how the model is drawn is `info`).
- **The exporter owns these sections.** A re-export replaces them rather than passing the previous copy
  through (`FeaturesSidecar.Sections`), so they always match the model. The viewer never edits them; they
  round-trip verbatim.
- **Property groups**: the same export writes hidden element groups `T1 300 F`, `P1 150 lb./sq.in.`
  (per vector and value, blue → red), `Mat 106`, `Insulation 2 in.`, `Fluid 0.036 lb./cu.in.`,
  `Wind shape 0.7` (tags `caesar` + `temperature | pressure | material | insulation | fluid | wind`). Tick
  one in Groups to see where a value applies.
- **Viewer**:
  - `loads.js` (`FEALoads`, the **Loads** panel): arrows pushing on the pipe for forces, double-headed arrows
    for moments and imposed rotations, arrows out from the pipe for imposed displacements, rows of arrows for
    uniform loads (teal when in g's) and wind. Lengths grow with the value. The panel filters by type and
    vector, sets the size, the wind direction to draw, and labels.
  - `pipe.js` (`FEAPipe`): the **Checks** flyout (a click flies to the item and flashes it; the tab counts
    errors and warnings), the source line in the Model panel, the **Properties** row in the readout and a
    pinned element's card (every field, the loads on it, the checks about it), and **Node numbers**.
  - `labels.js` (`FEALabels`): screen labels, culled where they would overlap.
  - Tests: `viewer/tests/test_loads.js`, `viewer/tests/test_review.js`.

### springs (planned)
Same envelope, one item per spring: `{ id, name, nodeIds, dof, stiffness }`.

## Re-export keeps the user's layer (2026-09-04)

An exporter rewrites the sidecar on every run, so `FeaturesSidecar.MergeFrom(previous,
exporterTag)` runs first and carries over: every top-level section the writer does not
model (`sectionCuts`, `predicates`, anything unknown) verbatim; groups **without** the
exporter's tag (hand-made groups) verbatim; and for exporter groups matched by name their
`id`, `color` and `hidden`. `SapToPluto` does this with tag `sap` and reports
`merged previous sidecar: …` in its summary. So section cuts, predicates and group styling
survive `run_sap.py`; only the exporter's own groups are regenerated. A group whose ids
no longer match the new geometry shows as unmatched in the viewer rather than vanishing.

**Supports (2026-10-04)** follow the same rules, but only for an exporter that writes them
(`WriteSupports = true`, `CaesarToPluto` with tag `caesar`):
- exporter items matched by name keep their `id` and `hidden`;
- items without the tag (hand-made) are kept verbatim;
- the summary adds `… user support(s) kept, … exporter support(s) restyled`.

Without `WriteSupports` a previous `supports` section passes through untouched, as before, so
`SapToPluto`'s output is byte-identical.

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
