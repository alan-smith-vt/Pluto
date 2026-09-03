# Pluto

**Current state:** this repository is a standalone browser-based Three.js
viewer for plate/shell FEA results (formerly `StressViewerDev`). Everything
below the "Roadmap" section documents that viewer as it exists today.

## Roadmap: the Pluto program

The viewer is being migrated into an **arm** of a larger program, *Pluto* —
a FEMAP-style hub for finite-element data. The intended shape:

```text
  Solver A  ──import──┐                       ┌──export──▶  Solver A
  Solver B  ──import──┤                       ├──export──▶  Solver B
  Solver C  ──import──┼──▶  common model  ──▶ ┤
  ...                 │      (Pluto core)     └──export──▶  ...
                      └──▶  Viewer arm  (this code: generic point of convergence)
```

- **Multiple FEM import/export pipelines**, each an independent arm that
  reads or writes a solver's native format.
- **A common model** the pipelines converge on (the current v3 `.bin` corner
  field format is the seed of it).
- **The viewer** stays solver-agnostic: it only ever consumes the common
  model, so every pipeline lands in the same visualization, inspection and
  envelope tooling.

## Repo layout

```text
scripts/           production C#/PowerShell toolchain (STAAD post-processing, v4 writer, sidecar, CSV arms)
  lib/             single Add-Type batch via lib/Config.ps1 (C# 5, PowerShell 5.1)
  arms/            pipe / steel / combined CSV / SAP -> .bin + sidecar
archive/           superseded code (old viewer, v3 writer) -- reference only
pythonTools/       development-side Python (sap/: SAP2000 tank generator -> .s2k; Python by design)
viewer/            the viewer arm (open viewer/index.html)
  scripts/         viewer JS modules
  styles/          CSS
  lib/             vendored Three.js r128 + OrbitControls
vault/             Obsidian vault — ALL project documentation lives here
.claude/skills/    project-scoped agent skills (placeholder)
```

---

# Viewer (current)

Browser-based Three.js viewer for plate/shell FEA results with **true
per-corner field visualization** — bilinear interpolation across quads,
no nodal averaging, no flat-shaded triangle seams.

A per-corner field block holds 8 stress components (shear X/Y/IP, axial
X/Y, moment X/Y/IP) and 6 displacement components (translation X/Y/Z,
rotation X/Y/Z). Stress and displacement are selected through the same
dropdown, read by the same strided math, and colored by the same shader.

Viewer features on top of the raw field display:

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
  regions, coincident-node smoothing.
- **Navigation**: find element/node by real ID (moves the orbit focus
  and flashes the target on top of everything -- no zoom),
  orthographic projection + axis triad, load-case stepping (arrow
  keys), persistent active-view caption.

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
viewer about the file layout. Use **Download Sample .bin** to emit a
test file from `sampleModel.js`, then open it back through the picker.

## Binary format

The current target is **v4** (block directory + element domains); the spec lives in
`vault/format/v4-schema.md`. v3 files still load through a legacy shim; the frozen v3
spec is in `vault/format/v3-schema.md` and summarised below.

### v3 (legacy)

Header — 15 little-endian `uint32`, 60 bytes. The writer must produce
exactly this layout; the reader fails loudly if a block falls outside
the file.

| off | field | notes |
|----:|-------|-------|
| 0  | `magic` | `0x46454156` |
| 4  | `version` | `3` |
| 8  | `headerSize` | `60` |
| 12 | `nNodes` | |
| 16 | `nElements` | |
| 20 | `nFieldLC` | load cases, envelopes included |
| 24 | `cornerComponents` | scalars per corner (e.g. 14) |
| 28 | `maxCorners` | `4` (fixed stride; triangles NaN-pad slot 4) |
| 32 | `metaOffset` | → UTF-8 JSON metadata |
| 36 | `metaLength` | |
| 40 | `nodesOffset` | → `float32[nNodes][3]` x,y,z |
| 44 | `elemsOffset` | → `uint32[nElements][5]` ncount,id1..id4 |
| 48 | `nodeIdOffset` | → `uint32[nNodes]` real (sparse) node IDs |
| 52 | `elemIdOffset` | → `uint32[nElements]` real (sparse) element IDs |
| 56 | `cornerFieldOffset` | → `float32[nFieldLC][nElements][maxCorners][cornerComponents]` |

Metadata JSON:
`{ loadCases:[{name,type}], components:[{name,kind,unit?}], displacementVector?:[ix,iy,iz], strengths?:{offset,components:[{name,unit?}]} }`
— `type` is `"primary"` or `"envelope"`; `kind` is `"stress"`,
`"displacement"` or `"dsr"`. Any other kind string (e.g. `"preDSR"`
for constituent checks of a combined DSR) is a **generic field**: it
renders as its own group in the component dropdown with default
display behavior, and triggers none of the DSR machinery (Global DSR
envelope, [0,1] range, auto-alarm) — kind comparisons are exact. The
DSR calc card lists every component at the pinned corner in the
controlling LC, grouped by kind, so combined checks can be traced back
to their constituents. Missing labels fall back to generated names. `unit` is an optional display string shown in the legend and
readout. `displacementVector` tags which three component indices hold
translation X/Y/Z for the deformed-shape view; without it the viewer
falls back to matching displacement-kind component names
("Translation X", "UX", "DX", ...), and if neither resolves,
deformation is simply unavailable.

`strengths` declares an optional **LC-independent design-strength
block** (phi factors applied; the values demands are compared against
for DSR = demand-to-strength ratio):
per-corner fields that do not vary with load case, stored ONCE as
`float32[nElements][maxCorners][nStr]` at `offset` (same slot
conventions as the corner field — triangles NaN-pad slot 4). It is
declared purely in the metadata: no header change, no version bump,
older viewers ignore the key and files without it read as before.
Writers should place the block before `cornerFieldOffset` (the
reference writer puts it between the element-ID table and the
metadata) so the append-mode LC-count-from-file-size derivation stays
valid; the reader also tolerates a block placed after the LC planes by
capping the LC region at the strength offset. Design-strength components
appear in the viewer's component dropdown under "Design Strength
(LC-independent)" and behave like any other field (colormap, abs,
alarm/flash, smoothing, point query); pinning a point additionally
lists every design-strength value at that corner in the readout panel.
Envelopes ignore strengths — there is nothing to fold.

**Append-mode writers:** the reader derives `nFieldLC` from file size,
so a writer that can't hold all stress data in RAM can write the
header + geometry + IDs + meta + LC 0, close the file, reopen in
append, and write LC 1, etc. The header's `nFieldLC` field is treated
as a hint; the file-size count wins. Meta load-case names are
auto-padded (`"LC N"`) or trimmed to match.

## Layout (`viewer/scripts/`)

| File | Role |
|------|------|
| `format/pluto.js` | format entry point: version dispatch, unified model, v3-shaped domain views, `FEABinary` compat |
| `format/v4Reader.js` | v4 block-directory reader |
| `format/v4Writer.js` | demo/test-only in-browser v4 writer (sample generator); production files come from `RawViewerWriter.cs` |
| `RawViewerWriter.cs` / `FeaturesSidecar.cs` | C# production writers: v4 binary (shells + beams, two-phase append) and features sidecar |
| `format/v3Reader.js` | frozen legacy v3 reader (adapted by `pluto.js`) |
| `modelSet.js` | multi-model set: geometry validation, global LC index, per-file read routing |
| `geometryBuilder.js` | duplicate-vertex shell mesh build, tri/quad triangulation |
| `beamGeometry.js` | extruded cross-section beam mesh (parametric sections), axis param for picking |
| `viewerBeams.js` | beam-domain display layered on viewer.js: own component/range, neutral in envelope views, pick readout |
| `attributeUpdaters.js` | rewrite `cornerVals` on component/LC swap (only per-update path) |
| `shaders.js` | bilinear vertex/fragment GLSL, colormap LUTs |
| `pointQuery.js` | raycast → inverse-bilinear → exact field eval |
| `sampleModel.js` | synthetic FEA binary generator + `.bin` download (demo / format reference) |
| `viewer.js` | scene wiring, render loop, UI, hover/pin readout |
| `inspector.html` / `inspector.js` | standalone field-by-field binary inspector |

Three.js r128 and OrbitControls are vendored under `lib/`.
