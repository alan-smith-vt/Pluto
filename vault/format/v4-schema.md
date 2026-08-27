---
title: Binary format v4 (block directory + element domains)
status: draft
created: 2026-08-25
supersedes: "[[vault/format/v3-schema|v3-schema]]"
---

# Binary format v4

Goal: one file that carries **multiple element families** (shells today, beams next,
solids/springs later) behind a **block directory** so readers skip what they don't know
and writers never need a header change to add data. v3 files keep loading through a
legacy shim — see [[#9. Legacy shim (v ≤ 3)]].

All integers little-endian. All offsets/lengths are `u64`. Readers must do offset math
in a 64-bit-safe way (in JS: plain Number, never bitwise ops).

## 1. Header (32 bytes)

| off | type | field      | notes                     |
|----:|------|------------|---------------------------|
| 0   | u32  | magic      | `0x46454156` (`"FEAV"`)   |
| 4   | u32  | version    | `4`                       |
| 8   | u32  | headerSize | `32`                      |
| 12  | u32  | nBlocks    | entries in the directory  |
| 16  | u64  | dirOffset  | → block directory         |
| 24  | u64  | reserved   | 0                         |

Dispatch on `version` happens before anything else is read:
`≤3 → legacy shim`, `4 → this reader`, else refuse.

## 2. Block directory (`nBlocks × 32 bytes`)

| off | type | field  | notes                                                               |
|----:|------|--------|---------------------------------------------------------------------|
| 0   | u32  | tag    | four-char code, see §3                                              |
| 4   | u32  | domain | domain index (§4) this block belongs to; `0xFFFFFFFF` = file-global |
| 8   | u64  | offset | absolute byte offset                                                |
| 16  | u64  | length | byte length                                                         |
| 24  | u32  | count  | tag-specific count (e.g. nLC for `FLDS`)                            |
| 28  | u32  | flags  | bit 0: `APPEND` — length may be derived from file size              |

Rules:
- Unknown tags are skipped, with a log line. Never an error.
- Every block must fit inside the file; one that doesn't is a hard error
  ("writer header does not match reader"), same policy as v3.
- Duplicate `(tag, domain)` pairs: last wins. Lets an append-mode writer re-emit a
  directory at end of file — the reader honours `dirOffset` only, so a writer patching
  just 8 bytes at offset 16 can point at a fresh directory.
- Directory may live anywhere (typically end of file for streaming writers).

## 3. Block tags

| tag    | scope  | payload                                                                            |
|--------|--------|------------------------------------------------------------------------------------|
| `META` | global | UTF-8 JSON (§5). Exactly one.                                                      |
| `NODE` | global | `f64[nNodes][3]` xyz. Exactly one — all domains share nodes. (f64: 50k nodes = 1.2 MB; precision for state-plane coords.) |
| `NDID` | global | `u32[nNodes]` real (sparse) node IDs                                               |
| `ELEM` | domain | `u32[nElem][elemRecordU32]` — record layout per family (§4.2)                     |
| `ELID` | domain | `u32[nElem]` real element IDs                                                      |
| `FLDS` | domain | `f32[nLC][nElem][maxSlots][nComp]` per-LC slot fields; `count` = nLC               |
| `FLDC` | domain | `f32[nElem][maxSlots][nCompConst]` LC-independent slot fields (was v3 `strengths`) |
| `SECT` | global | section table (§6), binary                                                         |
| `BPRP` | domain | beam properties (§4.3)                                                             |
| `BTAP` | domain | optional beam taper: `f32[nElem][2]` = (scaleA, scaleB) (§4.4). Absent = prismatic. Excluded from `geometryHash`. |
| `LABL` | both   | identity labels: `u32[n+1]` byte offsets + UTF-8 pool. Global = one label per node; domain d = one per element. Display-only (hover readout). Excluded from `geometryHash`. |

Field blocks never store per-node data; everything is per element-**slot**. A slot is a
corner for shells and a result station for beams. Unused slots are NaN-padded (tri in a
4-slot shell domain → slot 4 NaN; 2-station beam in a 3-slot domain → slot 3 NaN). NaN
renders as no-data, exactly as v3.

### 3.1 Labels vs. groups

`LABL` carries **identity** — one string per node / element (the source system's oid,
a member mark). It is never rendered as 3D text; the viewer shows it in the readout. Every
*categorical* attribute (class, run, room, chord/star, unsized, …) is a **group** in the
[[vault/format/features-sidecar|features sidecar]]: ID lists are cheap, colorable and
STAAD-exportable, and don't bloat the binary. Rule: identity in the binary, categories in
the sidecar; a per-element key/value bag is neither and is not supported.

## 4. Domains

A domain is one element family with its own element table, slot count and component
list. Declared in `META.domains[]` (array order = domain index). Blocks reference
domains by index. A shells-only file has one domain and is structurally v3.

### 4.1 Domain descriptor (META)

```json
{
  "name": "shells",
  "family": "shell",
  "maxSlots": 4,
  "components":      [ {"name": "...", "kind": "...", "unit": "..."} ],
  "constComponents": [ {"name": "...", "kind": "...", "unit": "..."} ],
  "displacementVector": [0, 1, 2]
}
```

- `family`: `"shell"` | `"beam"` (future: `"solid"`, `"spring"`).
- `maxSlots`: stride; ≥ the slots any element in the domain uses.
- `components` / `constComponents`: lists for `FLDS` / `FLDC`. `constComponents` optional.
- `displacementVector`: optional indices into `components`; same name-matching fallback as v3.
- `kind` semantics carry over unchanged: `"stress"`, `"displacement"`, `"dsr"`, `"str"`
  (const block), anything else = generic field with default display.
- **DSR status:** `kind:"dsr"` and the Global-DSR machinery are retained for shells but
  are **de-prioritised**. They must not constrain the domain model; if a conflict arises
  the DSR feature is archived (kept loadable via the v3 shim) rather than the schema bent.

### 4.2 `ELEM` record layouts

Record width `elemRecordU32` is fixed per family (currently 6 for both) so stride math
stays trivial. The first `u32` is always the used-slot count, so a generic reader can
NaN-pad without knowing the family.

**shell** — `u32[6]`: `nNodes (3|4), n0, n1, n2, n3, reserved`. Slot k = corner k.

**beam** — `u32[6]`: `nStations, n0, n1, sectionIdx, reserved, reserved`.
Slot k = station k, evenly spaced from n0 (t=0) to n1 (t=1). `nStations ≥ 2`.
**First implementation: `nStations = 2`, `maxSlots = 2`** (end A, end B). The format
allows more; the viewer interpolates linearly between whatever stations exist.

### 4.3 `BPRP` beam properties — `f32[nElem][8]`

| idx | field                                                                                            |
|----:|--------------------------------------------------------------------------------------------------|
| 0–2 | local **y** axis, unit vector in world frame — resolved by the importer; the viewer never sees roll angles / K-nodes |
| 3–4 | offset at end A (local y, z)                                                                     |
| 5–6 | offset at end B (local y, z)                                                                     |
| 7   | release bitmask (u32 bit pattern stored in the f32 slot; 0 = none). Display hint only.           |

Local x = n0→n1; local z = x × y; reader re-orthogonalises y against x.

### 4.4 `BTAP` beam taper — `f32[nElem][2]` (optional)

Per beam, in beam order: `scaleA, scaleB`. The section outline (§6) is multiplied by `scaleA` at end A (n0) and `scaleB` at end B (n1); the viewer linearly interpolates between the two rings, so a `PIPE` becomes a frustum. `1, 1` = prismatic. Writers omit the block when every beam is `1, 1`; readers treat an absent block as all ones. The section stays the identity of the beam (one section per catalog size; the taper is a display attribute of the member, not a new section type), so sidecar groups keyed on section index are unaffected, and the block is **excluded from `geometryHash`** — a change in taper alone does not invalidate a sidecar. First use: pipe reducers and reducing-tee branches (SP3D v4.1, sizes per end).

## 5. META JSON

```json
{
  "generator": "RawViewerWriter 4.0",
  "modelId": "ProjectX/Model7/rev3",
  "geometryHash": "sha256:…",
  "units": { "length": "in", "force": "kip" },
  "loadCases": [ { "name": "D+L", "type": "primary" } ],
  "domains":   [ { "...": "see 4.1" } ],
  "sections":  [ { "name": "W12x26", "type": "I" } ]
}
```

- `loadCases` are **file-global**: every domain's `FLDS` has the same `nLC` in the same
  order. A domain with no results for an LC writes a NaN plane. Short lists are padded
  with generated labels, as in v3.
- `sections` is a human-readable mirror of `SECT` (names live here); `SECT` is
  authoritative for geometry.
- `modelId`: importer-assigned, free text, stable across re-runs of the same model.
- `geometryHash`: SHA-256 over the raw bytes of `NODE NDID ELEM ELID SECT BPRP`, in that
  order, each domain's blocks in domain order. Excludes `META` and all field blocks, so a
  geometry-only export and the full-results export of the same run hash identical. This is
  the key the [[vault/format/features-sidecar|features-sidecar]] binds to.
- Element IDs are **per domain**: shell `101` and beam `101` may coexist. Find-by-ID
  searches every domain and disambiguates when more than one hits.

## 6. Sections

Parametric first, polygon as escape hatch. Stored in `SECT` as binary records so the
viewer builds geometry without JSON parsing.

`SECT`: `u32 nSections`, then per section `u32 type, u32 nParams, f32[nParams]`,
followed for `POLY` by `u32 nPts, f32[nPts][2]`.

| type | code | params (local y, z; file length units)                    |
|------|-----:|-----------------------------------------------------------|
| RECT | 1    | b, h                                                      |
| I    | 2    | d, bf_top, tf_top, bf_bot, tf_bot, tw                     |
| BOX  | 3    | b, h, t                                                   |
| PIPE | 4    | od, t                                                     |
| L    | 5    | b, h, t                                                   |
| C    | 6    | d, bf, tf, tw                                             |
| T    | 7    | d, bf, tf, tw                                             |
| POLY | 100  | none; outline points follow (CCW, implicitly closed)      |

Section origin = centroid unless `BPRP` offsets shift it.

## 7. Rendering contract

- **shell**: unchanged from v3 — duplicate-vertex tris, bilinear per-corner fragment eval.
- **beam**: extrude the section outline along n0→n1 with a ring at each station; each
  vertex carries `t` plus the two bracketing station values → linear fragment
  interpolation. Same LUT / abs / alarm / flash uniforms. Deformation: ring at station k
  displaced by that station's displacement vector. Picking recovers `t` from the hit and
  evaluates exactly. Optional line-only mode for large models.
- Envelopes, global DSR, calc-review and find-by-ID operate **per domain**; the UI
  presents one element picker keyed `(domain, index)`.

## 7a. File profiles

Same format, same reader; a profile is just which blocks are present.

| profile         | blocks                                                | viewer behaviour                                              |
|-----------------|-------------------------------------------------------|---------------------------------------------------------------|
| geometry-only   | `META NODE NDID ELEM ELID [SECT BPRP]`                 | mesh in neutral material, no component dropdown; predicates, section cuts, supports editable |
| results         | geometry-only + `FLDS` (+ `FLDC`)                     | full field display                                            |
| append-in-progress | results with `APPEND` flag and partial planes     | loads the complete planes, logs the count                     |

Rules: `nLC = 0` is valid everywhere (reader, envelopes, UI). A geometry-only file is a
byte-prefix of its results file when the writer emits geometry blocks first (it must, for
append mode), so `strip` = "copy prefix, write new directory". Because `geometryHash`
excludes field blocks, features authored on the light file bind to the heavy one.
Attach-results-later: the multi-model loader accepts a results file whose hash matches an
open geometry-only model and contributes its LCs to the global list.

## 8. Append-mode writers

Write `NODE NDID ELEM ELID SECT BPRP META FLDC` plus a directory whose `FLDS` entries
carry `flags.APPEND`, then stream LC planes. On close, rewrite the directory with final
`count`s (preferred). If the writer dies first, the reader derives
`nLC = floor(availableBytes / planeStride)` for any `APPEND` block, where
`availableBytes` runs to the next block start or EOF. Multi-domain streaming: give each
domain its own `FLDS` region with an explicit count — don't interleave.

## 9. Legacy shim (v ≤ 3)

`viewer/scripts/format/v3Reader.js` (the old `binaryReader.js`, frozen) wrapped by `PlutoFormat.fromV3` in `pluto.js`, an adapter that
emits the v4 in-memory model:

| v3                            | v4 model                                             |
|-------------------------------|------------------------------------------------------|
| header geometry/ID offsets    | global `NODE` / `NDID`, domain 0 `ELEM` / `ELID`     |
| `elems u32[5]`                | shell records (padded to 6, `reserved` = 0)          |
| `cornerComponents/maxCorners` | domain 0 `components`, `maxSlots = 4`                |
| corner field                  | domain 0 `FLDS`, `count = nFieldLC` (file-size rule) |
| `meta.strengths`              | domain 0 `FLDC` + `constComponents`                  |
| `meta.displacementVector`     | domain 0 `displacementVector`                        |

Downstream code (`modelSet`, `attributeUpdaters`, `pointQuery`, envelopes, inspector)
only ever sees the v4 model. v3 bugs get fixed by re-exporting, not by extending the shim.

## 10. In-memory model (reader output, both paths)

```js
{
  version, file,
  nNodes, nodes: Float32Array, nodeIds: Uint32Array,
  loadCases: [...],
  sections: [...],                        // decoded SECT
  domains: [{
    name, family, maxSlots, components, constComponents, dispVector,
    nElem, elemRecordU32, elems: Uint32Array, elemIds: Uint32Array,
    fields:      { offset, nLC, planeStride },   // readLC / readElementRecord
    constFields: { offset } | null,
    beamProps:   Float32Array | null
  }]
}
```

`readLC(model, d, lc)` and `readElementRecord(model, d, lc, e)` are the v3 functions with
a domain argument; `planeStride = nElem * maxSlots * nComp * 4`.

## Decisions log

- 2026-08-25 — Stations: 2 for now; `nStations` field kept so more can come later.
- 2026-08-25 — `NODE` is `f64` (v3 shim widens f32 → f64 on load).
- 2026-08-25 — DSR de-prioritised; retained for shells, archived if it conflicts.
- 2026-08-25 — Element IDs per domain.
- 2026-08-25 — Features (predicates, section cuts, supports, …) live in a JSON sidecar, never in the binary → [[vault/format/features-sidecar|features-sidecar]].
- 2026-08-25 — `LABL` block added (identity strings); categories stay in the sidecar.
- 2026-08-25 — All C# (writers, exporters) must be C# 5 / Add-Type PS 5.1 compatible.
- 2026-08-27 — `BTAP` block added (per-beam end scales) for tapered pipe; optional, hash-neutral, section identity unchanged. A `TaperedPipe` section type was rejected: every (odA, odB) pair would become its own section and fragment the size groups.
