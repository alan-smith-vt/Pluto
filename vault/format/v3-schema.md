---
title: Binary format v3 (legacy fixed header)
status: frozen
created: 2026-08-25
superseded-by: "[[vault/format/v4-schema|v4-schema]]"
---

# Binary format v3 — frozen

The original StressViewerDev per-corner field format. **Frozen:** v3 files load through
the legacy shim (`viewer/scripts/format/v3Reader.js` + adapter in `pluto.js`); bugs are
fixed by re-exporting as v4, not by extending this reader. Reference writer:
`viewer/RawViewerWriter.cs`; reference generator: `FEASample.buildSampleBlob()`.

All integers little-endian `uint32`. Offsets are absolute bytes. Everything is
shell-only: 3- or 4-node elements, 4 corner slots, one component list.

## Header — 15 × u32, 60 bytes

| off | field               | notes                                                        |
|----:|---------------------|--------------------------------------------------------------|
| 0   | `magic`             | `0x46454156` (`"FEAV"`)                                      |
| 4   | `version`           | `3`                                                          |
| 8   | `headerSize`        | `60`                                                         |
| 12  | `nNodes`            |                                                              |
| 16  | `nElements`         |                                                              |
| 20  | `nFieldLC`          | load cases incl. envelopes — a *hint*, see append mode       |
| 24  | `cornerComponents`  | scalars per corner (e.g. 14 = 8 stress + 6 displacement)     |
| 28  | `maxCorners`        | `4` fixed stride; triangles NaN-pad slot 4                   |
| 32  | `metaOffset`        | → UTF-8 JSON                                                 |
| 36  | `metaLength`        |                                                              |
| 40  | `nodesOffset`       | → `f32[nNodes][3]` x,y,z                                     |
| 44  | `elemsOffset`       | → `u32[nElements][5]` ncount, n0..n3 (unused = any)          |
| 48  | `nodeIdOffset`      | → `u32[nNodes]` real (sparse) node IDs                       |
| 52  | `elemIdOffset`      | → `u32[nElements]` real (sparse) element IDs                 |
| 56  | `cornerFieldOffset` | → `f32[nFieldLC][nElements][maxCorners][cornerComponents]`   |

Reader policy: any block falling outside the file is a hard error ("writer header does
not match this viewer"). Wrong `version`/`headerSize` are warnings only.

## Metadata JSON

```json
{
  "loadCases": [ { "name": "LC1", "type": "primary" } ],
  "components": [ { "name": "Shear X", "kind": "stress", "unit": "psi" } ],
  "displacementVector": [8, 9, 10],
  "strengths": { "offset": 60, "components": [ { "name": "Shear strength", "unit": "psi" } ] }
}
```

- `loadCases[].type`: `"primary"` | `"envelope"`. List is padded (`"LC N"`) or trimmed to
  `nFieldLC`.
- `components[].kind`: `"stress"`, `"displacement"`, `"dsr"`; any other string is a
  generic plottable field (e.g. `"preDSR"`) with default display and none of the DSR
  machinery. Comparisons are exact. `unit` is display-only.
- `displacementVector`: indices of translation X/Y/Z for the deformed shape. Fallback:
  name-match displacement-kind components (`Translation X`, `UX`, `DX`, `TX`, …). If
  neither resolves, deformation is unavailable.
- `strengths`: optional **LC-independent design-strength block** (phi-factored),
  `f32[nElements][maxCorners][nStr]` at `offset`, same slot conventions as the corner
  field. Declared purely in metadata — no header change. Writers should place it before
  `cornerFieldOffset`; the reader also tolerates it after the LC planes by capping the LC
  region at `strengths.offset`.

## Append mode

`nFieldLC` is derived from file size:
`nLC = floor((lcRegionEnd − cornerFieldOffset) / (nElements·maxCorners·cornerComponents·4))`,
where `lcRegionEnd` = `strengths.offset` if that block sits after the field, else EOF.
Header value is a hint only; the file-size count wins and the load-case list is re-padded.
So a writer can emit header + geometry + IDs + meta + LC 0, close, reopen in append, and
write LC 1… without patching the header.

## Element record — `u32[5]`

`ncount (3|4), n0, n1, n2, n3`. Node values are **indices** into the node table (not real
IDs). Slot k of the corner field = corner k. `ncount` outside {3,4} is clamped by the
reader (≥4 → 4, else 3).

## Field block

`f32[nFieldLC][nElements][maxCorners][cornerComponents]`, stress and displacement
components interleaved per corner. Index:
`((lc·nElements + e)·maxCorners + k)·cornerComponents + c`.
One LC plane is sliced at a time (`readLC`); one element's record can be sliced alone
(`readElementRecord`, a few hundred bytes) for the calc-review card. NaN = no data.

## Known limitations (why v4)

- Fixed header → every new block had to be smuggled through `meta` (strengths).
- One element family; no element type, no sections, no beams.
- `f32` nodes lose precision on large-coordinate models.
- 32-bit offsets in the header (reader uses Number math, but the fields are u32).
- `nFieldLC = 0` is technically producible but the UI assumed ≥ 1.

## v3 → v4 mapping

See [[vault/format/v4-schema#9. Legacy shim (v ≤ 3)|v4-schema §9]].
