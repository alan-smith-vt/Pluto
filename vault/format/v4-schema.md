---
title: Binary format v4 (block directory + element domains)
status: draft
created: 2026-08-25
supersedes: "[[v3-schema]]"
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
| `NODE` | global | `f32[nNodes][3]` xyz. Exactly one — all domains share nodes.                       |
| `NDID` | global | `u32[nNodes]` real (sparse) node IDs                                               |
| `ELEM` | domain | `u32[nElem][elemRecordU32]` — record layout per family (§4.2)                     |
| `ELID` | domain | `u32[nElem]` real element IDs                                                      |
| `FLDS` | domain | `f32[nLC][nElem][maxSlots][nComp]` per-LC slot fields; `count` = nLC               |
| `FLDC` | domain | `f32[nElem][maxSlots][nCompConst]` LC-independent slot fields (was v3 `strengths`) |
| `SECT` | global | section table (§6), binary                                                         |
| `BPRP` | domain | beam properties (§4.3)                                                             |

Field blocks never store per-node data; everything is per element-**slot**. A slot is a
corner for shells and a result station for beams. Unused slots are NaN-padded (tri in a
4-slot shell domain → slot 4 NaN; 2-station beam in a 3-slot domain → slot 3 NaN). NaN
renders as no-data, exactly as v3.

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

### 4.2 `ELEM` record layouts

Record width `elemRecordU32` is fixed per family (currently 6 for both) so stride math
stays trivial. The first `u32` is always the used-slot count, so a generic reader can
NaN-pad without knowing the family.

**shell** — `u32[6]`: `nNodes (3|4), n0, n1, n2, n3, reserved`. Slot k = corner k.

**beam** — `u32[6]`: `nStations, n0, n1, sectionIdx, reserved, reserved`.
Slot k = station k, evenly spaced from n0 (t=0) to n1 (t=1). `nStations ≥ 2`.

### 4.3 `BPRP` beam properties — `f32[nElem][8]`

| idx | field                                                                                            |
|----:|--------------------------------------------------------------------------------------------------|
| 0–2 | local **y** axis, unit vector in world frame — resolved by the importer; the viewer never sees roll angles / K-nodes |
| 3–4 | offset at end A (local y, z)                                                                     |
| 5–6 | offset at end B (local y, z)                                                                     |
| 7   | release bitmask (u32 bit pattern stored in the f32 slot; 0 = none). Display hint only.           |

Local x = n0→n1; local z = x × y; reader re-orthogonalises y against x.

## 5. META JSON

```json
{
  "generator": "RawViewerWriter 4.0",
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

## 8. Append-mode writers

Write `NODE NDID ELEM ELID SECT BPRP META FLDC` plus a directory whose `FLDS` entries
carry `flags.APPEND`, then stream LC planes. On close, rewrite the directory with final
`count`s (preferred). If the writer dies first, the reader derives
`nLC = floor(availableBytes / planeStride)` for any `APPEND` block, where
`availableBytes` runs to the next block start or EOF. Multi-domain streaming: give each
domain its own `FLDS` region with an explicit count — don't interleave.

## 9. Legacy shim (v ≤ 3)

`legacy/v3Reader.js` = today's `binaryReader.js`, frozen, wrapped by an adapter that
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

## Open questions

- [ ] Station count: fixed 2 for the first importer, or expose `nStations` now? (Format allows it; viewer can start with 2.)
- [ ] `NODE` as `f64`? v3 is `f32`; state-plane coordinates lose precision. Cheap to decide now.
- [ ] Beam DSR constituent checks via the `preDSR`-style `kind` mechanism — same as shells?
- [ ] Should real element IDs be unique across domains (one picker namespace) or per domain?
