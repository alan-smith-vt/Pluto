---
title: Viewer overview
status: current
created: 2026-08-26
---

*↑ [[vault/Pluto Home|Home]] › [[vault/viewer/Viewer map|Viewer map]]*

# Viewer overview

## What it is

Browser-based Three.js viewer for plate/shell FEA results with **true per-corner field
visualization** — bilinear interpolation across quads, no nodal averaging, no flat-shaded
triangle seams. Per-corner field planes hold stress and displacement components (and any
generic kind); stress and displacement are selected through the same dropdown, read by
the same strided math, and colored by the same shader. Beams are a second domain (below).

- **Multiple models at once**: the file picker accepts several `.bin`
  files with identical geometry (e.g. the same structure re-run with
  different soil springs). Load cases from all files form one globally
  indexed list — the LC dropdown groups them under file-name dividers
  (LC names/numbers commonly collide across variants), and captions /
  controlling-LC readouts cite "file · LC". The first file supplies
  geometry, components and strengths; other files must match its
  geometry exactly (verified at load) and may differ in component
  order/coverage (remapped by name, missing fields read as no-data).
- **Envelopes** (in-memory, never persisted): min / max / abs(max)
  across all primary LCs **of every loaded model**, each tracking
  **which model + LC controlled** every corner value, plus a **Global
  DSR** envelope (worst check across all LCs *and* all `kind:"dsr"`
  components) tracking the controlling check and controlling LC per
  corner. "By check" recolors the Global DSR view as a categorical
  controlling-check map.
- **Calc review**: pinning a point in a Global DSR view slices the
  controlling LC's element record straight from the file (a few hundred
  bytes) and shows every DSR check, the constituent stress components,
  and the corner's worst check per primary LC.
- **Deformed shape**: GPU-side (`position + dispScale * dispVec`), with
  a ±scale cycling animation. Requires the metadata to tag the
  translation components (see below). Picking is disabled while the
  deformed shape is shown.
- **Design strengths** (phi factors applied): LC-independent
  per-corner fields stored once in the
  file (see the metadata notes below), selectable from the component
  dropdown and summarized in the readout on every pin.
- **Display transforms**: |value| toggle (abs applied after
  interpolation, so interior zero crossings are exact), overstress
  alarm color with an x-ray **Flash** pulse to reveal obscured flagged
  regions, smoothing: **element means** (default since 2026-09-10: each element's mean of its
  own corners, averaged at the node, so a force profile at a shell edge reads without the
  corner extrapolation overshoot), coincident-node averaging, or none.
- **Navigation**: find element/node by real ID (moves the orbit focus
  and flashes the target on top of everything -- no zoom),
  orthographic projection + axis triad, load-case stepping (arrow
  keys), persistent active-view caption. Ctrl+click sets the focus (unless a predicate or cut tool is armed);
  a double-click flies to the point. **Find** takes an ID or a label (a SAP text label such as `A1`, a CAESAR element
  `10-20`): elements in both domains, shells and beams; when a shell and a beam share an ID the shell shows and the log
  says so, and `b101` / `s101` picks the beam / shell (2026-11-21).
- **Panels**: on the left, Model, Field, Display, then Beams, Releases, Links, Supports, Loads (each shown when the
  file has that layer), Deformation and Status. On the right edge, the flyout tabs for Section cuts, Predicates,
  Groups and Checks (shown for a pipe model or a sidecar with a `review`).

## Run

Just open `viewer/index.html` in a browser — no server needed. A synthetic
demo (v4: curved plate of shells + a steel beam frame with I / pipe / rect
sections) loads automatically; use the file picker to open a real
`.bin`. Binary files are read on demand through the file picker, so
multi-GB files never get materialized whole.

## Inspector

Open `viewer/inspector.html` to inspect a `.bin` field by field — header
fields with a raw hex dump, the range-checked block layout, metadata
tables, paged node/element/ID tables, and per-load-case field data
(per-component min/max/mean stats plus a per-element corner × component
matrix). It reuses the frozen `format/v3Reader.js`, so it can never disagree with the
viewer about the v3 layout. **It reads v3 files only** (`inspector.html` binds `FEABinary` to `FEAv3`); every arm
writes v4, so v4 files are checked in the viewer or with the readback tests (below). Use **Download Sample .bin**
to emit a v3 test file from `sampleModel.js`, then open it back through the picker.

## Layout (`viewer/scripts/`)

| File | Role |
|------|------|
| `format/pluto.js` | format entry point: version dispatch, unified model, v3-shaped domain views, `FEABinary` compat |
| `format/v4Reader.js` | v4 block-directory reader |
| `format/v4Writer.js` | demo/test-only in-browser v4 writer (sample generator); production files come from `RawViewerWriter.cs` |
| `format/v3Reader.js` | frozen legacy v3 reader (adapted by `pluto.js`) |
| `modelSet.js` | multi-model set: geometry validation, global LC index, per-file read routing |
| `geometryBuilder.js` | duplicate-vertex shell mesh build, tri/quad triangulation |
| `beamGeometry.js` | extruded cross-section beam mesh (parametric sections), axis param for picking |
| `viewerBeams.js` | beam-domain display layered on viewer.js: own component/range, neutral in envelope views, pick readout; kind `restraint` stays out of its component list |
| `loads.js` | applied loads from the sidecar `loads` (`FEALoads`, Loads panel): forces, moments, imposed displacements and rotations, uniform-load and wind rows as instanced arrows; type / vector filters, size, wind direction, labels; readout + pin card with every vector. Version 2 (SAP): the **Loads of** select (all patterns, a pattern, a case, a combination), point and distributed frame loads, temperature bands |
| `pipe.js` | the pipe model (`FEAPipe`): Checks flyout from the sidecar `review` (click to fly to an item), the model's source line, the element property readout row and pin card from `properties`, node-number labels. SAP frame items too: section, shape, dimensions, material, angle, releases, modifiers; SAP's joint labels as node numbers |
| `labels.js` | screen-space labels (`FEALabels`): node numbers and load values, placed after each render by priority with overlap culling |
| `supports.js` | support symbols from the sidecar `supports` items (`FEASupports`, Supports panel): one instanced shape per restraint kind, coloured by node group / kind / restraint load, deflection lines, readout + pin card with the loads per load case |
| `releases.js` | member end releases from the binary (BPRP slot 7, `FEAReleases`, Releases panel): rings, pins, collars, squares and slots a short way in from each released end, amber where partly fixed; follows the beams' visibility; the readout's Releases row; `FEAReleases.text(e)` |
| `links.js` | two-joint links from the sidecar `links` (`FEALinks`, Links panel): a line and a symbol per link type (coil, gap plates, hooks, friction plate, zig-zag, dashpot, diamond), zero-length ones at the joint, a ghost for links buried in their members; readout + pin card with the DOFs; `FEALinks.text(i)` |
| `attributeUpdaters.js` | rewrite `cornerVals` on component/LC swap (only per-update path) |
| `shaders.js` | bilinear vertex/fragment GLSL, colormap LUTs |
| `pointQuery.js` | raycast → inverse-bilinear → exact field eval |
| `sampleModel.js` | synthetic FEA binary generator + `.bin` download (demo / format reference) |
| `viewer.js` | scene wiring, render loop, UI, hover/pin readout |
| `features.js` | sidecar loader, Color by groups, legend, predicate hook |
| `files.js` | the one way in and out (`FEAFiles`): Choose files… (File System Access API, else a file input), Save features (Ctrl+S), legacy JSON routing, Reload / Auto-reload |
| `overlays.js` | the model rail (`FEAOverlays`): Add overlay…, other models at true coordinates with their own groups, ghost / remove, plan rotation ([[vault/viewer/model-rail\|model-rail]]) |
| `predicates.js` | predicate flyout + engine (`FEAPredicates`) |
| `sectionCut.js` | section cuts: named list (groups, visibility, adjust, delete, numeric centre), probe + plot + isolate on the selected cut (this wall / slab, or a **crop box** of the cut length by a width along a secondary axis, 2026-09-10), sidecar round trip, legacy import (2026-09-04) |
| `viewCube.js` | orientation cube |
| `inspector.html` / `inspector.js` | standalone field-by-field binary inspector (v3 only) |
| `heatmap.html` / `heatmapCore.js` | standalone pipe-weight heatmap over the steel plan ([[vault/arms/plant-combined-to-viewer\|plant-combined-to-viewer]] §6) |
| `../tests/readback_caesar.js` / `readback_sap.js` | headless readback of a `CaesarToPluto` / `SapToPluto` export through the viewer's own reader |

Three.js r128 and OrbitControls are vendored under `lib/`.

## Tests

Headless, in Node, with THREE loaded under `vm` and no DOM. From the repo root:

```bash
for t in viewer/tests/test_*.js; do node "$t"; done     # every test prints ok / FAIL lines and a verdict
node viewer/tests/readback_sap.js <outBase>               # a SapToPluto export (<outBase>.bin + .features.json)
node viewer/tests/readback_caesar.js <outBase> --expect <result.json>   # a CaesarToPluto export, against its counts
```

## Recent additions (newest first)

Fixes found by the vault audit (2026-11-21):
- **Find** searches beams as well as shells, and labels as well as IDs (Navigation, above). Pipe and frame models,
  which have no shells, could find no element before.
- **Auto range** works again: the Auto-reload checkbox had taken its id (`feaAuto`) on 2026-10-22, so Auto range did
  nothing and unticking Auto-reload made the colour range manual. Auto-reload is `feaAutoReload` now.
- **World coordinates:** the readout and predicates add the exporter's `model.units.worldOffset`; saved predicates are
  moved once to keep their meaning ([[vault/viewer/model-rail|model-rail]]).

SAP frame models (2026-11-05, branch `pipestress`; the frame workbook's preview and any SAP export, `SapToPluto`):
- **Releases panel** (`releases.js`): member end releases from BPRP slot 7, drawn a little in from the released end;
  amber where partly fixed; the readout's Releases row.
- **Links panel** (`links.js`): two-joint links from the sidecar `links`, a line and a symbol per type; zero-length
  links at their joint; a faint ghost for links buried in their members; readout and pin card with the DOFs.
- **Supports**: SAP's kinds join CAESAR's: coil for a spring (flat spiral for a rotational one), hook for tension
  only, a hatched plate for friction, a diamond for any other link. Sizes come from the beam section where there is
  no pipe. The export ticks its support groups, so every support shows on load.
- **Loads of** select (Loads panel, sidecar `loads` version 2): every pattern, one pattern, a case or a combination;
  a staged nonlinear case carries the loads it continues from; self weight is listed, not drawn. Remembered per model.
- **Frame properties** (`pipe.js`): hover a frame for its section, length, angle, releases and line; pin it for a card
  with geometry, section, modifiers and end releases. Node numbers show SAP's joint labels, text ones included.
- **Checks** for SAP models: the export's notices and the frame workbook's build messages.
- 2026-11-04: checks and properties highlight their own elements; the ortho view zooms smoothly and keeps the whole
  model in front of the camera.
- Tests: `test_releases.js`, `test_links.js`, `test_supports.js`, `test_loads.js`, `test_review.js`; `readback_sap.js`
  checks the new sections.

The pipe model as a confirmation step (2026-10-23, branch `pipestress`, Stage D; decision
[[vault/decisions/2026-10-23-pipe-model-in-viewer|2026-10-23-pipe-model-in-viewer]]):
- **Loads panel** (under Supports, `loads.js`): the sidecar `loads` as arrows in the colours of the panel's
  type toggles:
  - forces push on the pipe, their tip on its surface;
  - moments and imposed rotations are double-headed arrows out from the pipe (the right-hand rule);
  - imposed displacements are arrows out from the pipe along the movement;
  - uniform loads are rows of arrows along the loaded pipe (teal when in g's);
  - wind is a row of arrows from the side the panel's Wind setting names (the model has no wind direction).
  - Lengths grow with the value (0.4 to 1 of the largest of the kind). Filters by type and by load vector;
    Size; Labels (vector, value, unit). Hover for the load; click for every vector at that node or on that run.
- **Checks flyout** (✓ tab under Groups, `pipe.js`): the build's checks by level with counts as filter chips.
  A click flies to the item's nodes and beams and flashes them. The tab turns amber on warnings and shows `!`
  on errors.
- **Source line** (Model panel, and the Checks header): the file the model came from, when it was saved, when
  the preview was written.
- **Properties**: hovering a pipe beam adds its element's summary to the readout (size, bend radius, T1, P1,
  material, insulation, loads). Pinning it gives a card: geometry, temperatures and pressures by vector,
  material and densities, the component's data, the loads on it and at its ends, and the checks about it.
- **Node numbers** (Model panel): every real node's number on screen, a readable subset when dense
  (`labels.js`). Bend and valve points are not numbered.
- Tests: `viewer/tests/test_loads.js`, `viewer/tests/test_review.js`; the CAESAR readback checks the new
  sections against the export's counts.

Reload and Auto-reload (2026-10-22, branch `pipestress`, pipe workbook):
- **Reload** (Model section) reads the model and its sidecar again from where they came from: the picked files' handles (Choose files…, Chrome / Edge) or the `?bin=` / `&features=` URLs. The camera and the load case stay (`loadModels(entries, { keepView: true })`).
- **Auto-reload** checks every 2 s (file time and size, or the URLs' Last-Modified / length) and reloads once a change has held still for one check; it waits while the features have unsaved edits. It follows `Build-Pipe.ps1 -Watch` ([[vault/arms/pipe-workbook|pipe-workbook]]).
- Files from the plain file input are snapshots the browser cannot read again: Reload says so.
- Test: `viewer/tests/test_reload.js`.

Support symbols (2026-10-04, branch `pipestress`, CAESAR II arm stage 2):
- **`supports.js`** draws the sidecar `supports` items as symbols in CAESAR II's language: a square
  plate through the pipe for an anchor, slim arrows pointing at the pipe for the rest. A hanger is
  one arrow below the pipe; a rotation is a double-headed arrow. The first set (cubes, collars,
  hoops, plates) crowded busy nodes. Symbols, colour modes and visibility rules are in
  [[vault/format/features-sidecar|features-sidecar]] (supports).
  - It is one InstancedMesh per primitive (cone, cylinder, box, ball), headlight-shaded,
    `frustumCulled = false`.
  - Sizes come from the drawn pipe radius at the node, with a floor from the model span.
- **Supports panel** (after Links since 2026-11-05): Show, Size, Colour. Colour is the node group, the restraint
  kind, or a `restraint` load component in the current load case.
- **Picking:** a symbol in front of the pipe wins the hover. The readout gives the support, its
  restraints, and `|F|` / `|M|` in this load case. A pin opens a card with the loads on the
  restraint in every load case (one element-record read per load case) and the node displacement.
- **Deformed shape:** symbols stay where the supports are; a line (edge shader, same scale and
  animation as the beams) runs from each support to its displaced node. CNODE restraints whose
  connected node is apart get a static line.
- **Hooks:**
  - `viewer.js`: load / clear, `syncAuxLayers` → `recolor`, `setDispUniforms`, `refreshDispVecs`,
    `feaPick`, `showReadout`, pin.
  - `features.js`: `sync` → `onFeatures`; new `nodeGroupAt(ni)` (the winning ticked node group,
    and whether any node group lists the node).
  - `sectionCut.js`: `writeVis`.
  - `viewerBeams.js`: `lcData()`.
- Test: `viewer/tests/test_supports.js`.

Pipe-stress groundwork (2026-10-03, branch `pipestress`, for the CAESAR II arm):
- **Tapered beams.** `ELEM` slot 4 gives end B its own section
  ([[vault/decisions/2026-10-03-beam-taper|decision]]). Ring B and cap B come from that section
  when both outlines have the same point count; otherwise end A is used and the mismatch is counted.
  The readout shows `A → B`. The demo has one tapered column. Test: `viewer/tests/test_beamtaper.js`.
- **Mitred joints (2026-10-26).** Where exactly two PIPE members of one OD meet at a point and turn
  by at most 135°, both ends are cut on the plane bisecting the turn, so a chorded bend draws as one
  tube (square cuts left a wedge gap and a cap seam at every chord, the ring seams seen on closely
  spaced bends). A joint is a point, not a node id (a skipped zero-length piece leaves two ids there);
  the OD decides, not the section (a straight and its elbow often differ in wall). A member shorter
  than its cut keeps a sliver and its neighbour's cut closes the joint. Square stays for a sharper
  turn, a tee, another OD or shape, or an offset. Vertex count and order are unchanged. Test:
  `viewer/tests/test_mitre.js`.
- **Deformed shape for files without shells.** The gate (`FEAAttributes.deformStatus`) allows
  deformation when the shells have a displacement vector, or when the beams have one and there are no
  shells. A shell + beam file whose shells lack one stays locked, with a hint. Test:
  `viewer/tests/test_deform.js`.
- **Beam-only files.** The shell-only controls (Type, Component, Smooth, Edges, envelopes) are hidden
  (`body.beam-only`), the Beams panel opens, and the main Min/Max drive the beam range. In shell +
  beam files the manual range stays on the shells.
- **Display settings reach beams.** `syncAuxLayers()` runs on colormap, alarm, range and component
  changes. It also calls `FEASupports.recolor()` (live since 2026-10-04).
- **Load cases with no beam data** draw the beams neutral and say "no beam results in this LC",
  instead of painting them the dark no-data colour.
- **Node-group markers** show whenever a node group is enabled, without Color by groups. Restraint
  locations can sit over a contour.
- **Up axis.** META `upAxis` sets the view at load without saving it. The user's Z-up preference
  returns with the next file that has none.

Files (2026-09-04, `files.js`): one "Choose files…" for .bin + .json (File System Access
API on Chrome / Edge, hidden `<input type=file>` otherwise), one "Save features" (Ctrl+S)
that writes the sidecar back in place through the kept handle, or downloads; legacy
`section_cuts.json` / `predicates.json` route through the same picker. Edits mark the
features name `*`. Verified over localhost with the extension (first time it connected).

Groups tab (2026-09-03): the sidecar-groups UI moved out of the main panel into a flyout
tab under the predicates tab (`#grPanel`, `styles/groups.css`). Per-group enable
(`hidden` flag in the sidecar), colour edit, drag reorder, painted/members counts that
expose shadowing (e.g. WALL 0/720 under twenty COURSE groups), node groups on their own
**Nodes** sub-tab, drawn as coloured square points (`nodeMarkers`, one
THREE.Points, depth-tested, 2026-09-04; undeformed positions; coincident markers are nudged
aside in list order so all show, rows count the nudged ones; orbs on top were tried and
rejected as clutter), every group off until ticked (`hidden: false`), All/None/Invert per
sub-tab, Export. Spec: [[vault/format/features-sidecar|features-sidecar]];
test `viewer/tests/test_groups.js`. URL load `index.html?bin=…&features=…` (same day).

Predicates (2026-09-01, ported from the old viewer — plan:
[[vault/viewer/old-viewer-merge-plan|old-viewer-merge-plan]]):
- `scripts/predicates.js` (`FEAPredicates`), flyout tab under the section-cut tab.
- Trees of plane / finite-plane slab tests (AND/OR, per-node negate), evaluated over
  shell centroids+normals, beam midpoints (no normal test; a beam pick takes its axis
  as the plane normal), and nodes. Drag-drop tree editing, inspector with scroll-nudge,
  match highlighting, finite-extents ghost.
- Stored in the features sidecar `predicates` section in the **production dialect**
  (`kind`/`tol`/`normal_tol_deg`/`negated` — what C# `Groups.cs` parses); points are
  world/plant coordinates (both recenter offsets subtracted at eval).
- `resolveMembers(predicateId)` feeds `features.js` groups → **Group from pred.** button
  colors a predicate's members via Color by groups; Export downloads the whole sidecar;
  Import accepts a sidecar or a legacy `predicates.json` (v1/v3).
- Known old-viewer bugs fixed at port (idx typo, cosNormTol write, overlay tri count);
  undo/redo was never implemented in the old code — still open.

Beam support (2026-08-25, first cut):
- Beam domain renders as solid extruded sections (`beamGeometry.js`), linear end-A→end-B color interpolation (`FEAShaders.beamFragment`).
- Own component dropdown + auto range in the **Beams** panel; shares colormap / abs / alarm with shells.
- Envelope, strength and Global-DSR views draw beams neutral grey — no beam envelopes yet.
- Hover/pick reports beam ID, section, `t` along the axis, end node IDs.
- Deformed shape: beam ends follow the beam domain's displacementVector; shares the shell scale.
- Hollow sections (BOX, PIPE) render outer outline only.

Not yet: beam envelopes / min-max folds, a beam calc card, line-only mode for large models, `nStations > 2` rendering (format supports it; updater reads slots 0 and 1).

Geometry-only / beam-only files (2026-08-25): a file with no shell domain loads through a
synthetic empty shell view; zero load cases short-circuits `selectLC` (beams draw neutral,
groups/labels still work). Pipe facet count is adaptive (24 → 12 → 8 → 6 by beam count) so
~150k pipes stay renderable; a line-only mode is still the next step for very large models.
