---
title: RawViewerWriter (C# v4 binary writer)
status: current
created: 2026-08-25
---

# RawViewerWriter — structure and usage

*↑ [[vault/Pluto|Home]] › [[vault/format/Format map|Format map]]*

`scripts/lib/writers/RawViewerWriter.cs` is the **production writer** for the Pluto v4 binary
([[vault/format/v4-schema|v4-schema]]). It is called by the STAAD post-processing scripts
that have not been ported into this repo yet; its public surface is kept identical to the
v3 writer they were written against, plus an optional beam domain. (`viewer/scripts/format/v4Writer.js`
is *not* a production path — it only exists so the browser demo can fabricate a sample file.)

Compiled and round-trip tested 2026-08-25 (`dotnet 9`, external types stubbed): C# writes →
JS reader parses, values land in the right slots, `geometryHash` recomputed from the blocks
matches META, geometry-only export hashes identical to the full export.

## External types (defined in the STAAD codebase, not here)

| type | used as |
|---|---|
| `Node { int id; xyz{X,Y,Z} }` | node table (written as f64) |
| `Element { int id; int nNodes; Node[] n }` | shell elements, 3 or 4 nodes |
| `StressRecord { LC, elemID, node, Sf[]; StressViewForce(ForceUnit, LengthUnit) }` | per-corner stress |
| `Disp { LC, node, DR[] }` | per-node displacement (tx,ty,tz,rx,ry,rz) |
| `DsrRecord { LC, elemID, node, Values[] }` | per-corner DSR checks |
| `StrRecord { elemID, node, Values[] }` | per-corner LC-independent design strengths |

Types the writer **defines** (new in v4): `Component`, `BeamMember`, `SectionDef`, `BeamRecord`.

## Two-phase contract (unchanged)

```csharp
var w = new RawViewerWriter(path, nodes, elements, loadCaseNames, components);
w.Write();                      // header, directory, geometry, META, NaN-filled fields
w.AppendStresses(stresses);     // in-place seek + write through a memory map
w.AppendDisplacements(disps);
w.AppendDsr(dsrs);              // only if the layout has kind "dsr"
w.AppendStr(strs);              // only if the layout has kind "str"
```csharp

- `Write()` NaN-fills **every** field plane up front, so appends can arrive in any order
  and any subset; anything never written reads as no-data.
- `loadCaseNames` keys are STAAD LC ids; planes are ordered by ascending id and META
  `loadCases[i]` is plane `i`. Every written case is `primary` (envelopes are viewer-side).
- The single `components` list is split: `Kind == "str"` → `FLDC` (`constComponents`),
  everything else → `FLDS` in list order. Each kind must be a contiguous run
  (`ComponentLayout` throws otherwise) because `Append*` writes a run at `Start(kind)`.
- Records whose `node` is not a corner of `elemID` (element-centre results mixed in) are
  silently skipped, as before.
```

## Beams (new)

```csharp
var beams = new Dictionary<int, RawViewerWriter.BeamMember> {
  { 20001, new RawViewerWriter.BeamMember {
        Id = 20001, NodeA = 4021, NodeB = 4022, SectionIndex = 0,
        LocalY = new[] { 0.0, 0.0, 1.0 },        // resolved local-y in WORLD coords
        OffsetAz = -6, OffsetBz = -6 } } };
var sections = new List<RawViewerWriter.SectionDef> {
  RawViewerWriter.SectionDef.IShape("W12x26", 12.2f, 6.5f, 0.38f, 6.5f, 0.38f, 0.23f),
  RawViewerWriter.SectionDef.Pipe("PIPE8", 8.6f, 0.5f) };
var beamComps = new List<RawViewerWriter.Component> {
  new RawViewerWriter.Component("N", "force", "kip"),
  new RawViewerWriter.Component("My", "force", "kip-ft"),
  new RawViewerWriter.Component("Translation X", "displacement", "in"),
  new RawViewerWriter.Component("Translation Y", "displacement", "in"),
  new RawViewerWriter.Component("Translation Z", "displacement", "in") };

var w = new RawViewerWriter(path, nodes, elements, loadCaseNames, components,
                            beams, sections, beamComps, modelId, units);
w.SetBeamLabels(partOidByBeamId);         // optional LABL: one identity string per beam
w.SetNodeLabels(weldOidByNodeId);         // optional LABL: one per node
w.Write();
w.AppendBeamForces(beamRecords);          // BeamRecord { LC, elemID, End (0=A,1=B), Values[] }
w.AppendDisplacements(disps);             // ALSO fans node displacements onto beam ends
```

- Beams become domain 1 (`"beams"`, `family:"beam"`, `maxSlots = 2`); shells stay domain 0.
  Element IDs are per domain, so beam ids may overlap shell ids.
- `AppendBeamForces(records, kind = "force")` writes the run of that kind; pass `kind: null`
  to write the whole component list per end.
- `LocalY` must be resolved by the caller (roll angle / K-node → world vector); the viewer
  never sees solver conventions. Offsets are in section-local (y, z).
- `SectionDef` factories: `Rect, IShape, Box, Pipe, Angle, Channel, Tee, Poly`. Codes per
  schema §6.

## Labels

`SetNodeLabels / SetShellLabels / SetBeamLabels(Dictionary<int,string>)` (keyed by real id,
call before `Write()`) emit `LABL` blocks — one identity string each, shown in the viewer's
hover readout. Categories are sidecar groups, not labels ([[vault/format/v4-schema#3.1 Labels vs. groups|schema §3.1]]).

## Compiler target

**C# 5 / .NET Framework, loaded with `Add-Type` under PowerShell 5.1.** No interpolation,
`?.`, `nameof`, expression bodies, `out var`, auto-property initializers, or `new(...)`.
Verified by compiling with `<LangVersion>5</LangVersion>`.

## Beam-only / geometry-only inputs

`elements`, `components` and `loadCaseNames` may all be `null`/empty. With no shells the
beam domain becomes domain 0; with no load cases no `FLDS` blocks are written (`Write()`
and `Write(false)` are then equivalent). At least one of elements / beams is required.

## Bridges (`scripts/arms/`)

End-to-end usage of the pipe arm (CSV → beams → files → viewer): [[vault/arms/pipe-csv-to-viewer|pipe-csv-to-viewer]].

- `PipeToPluto.cs` — `PipeToPluto.Export(List<PipeBeam>, outBase, modelId, lengthUnit)`:
  dedupes weld points into nodes, one `Pipe` section per distinct diameter (+ `UNSIZED`),
  `PartOid` as beam label, sidecar groups for class / run / star-arms / unsized / src.
  Writes `<outBase>.bin` + `<outBase>.features.json`. Verified C# 5 build and JS read.
- `SteelToPluto.cs` / `CombinedToPluto.cs` -- steel W-shape CSV, and pipes + steel in one
  file: [[vault/arms/steel-csv-to-viewer|steel-csv-to-viewer]].
- `SapToPluto.cs` -- SAP2000 `.s2k` shells + results -> v4 + sidecar via the generic
  `AppendShellValues(List<CornerRecord>, kind)` (raw values, no STAAD unit conversion;
  added 2026-09-03): [[vault/arms/sap-s2k-to-viewer|sap-s2k-to-viewer]].

## Profiles

- `Write()` — results profile: all field planes present (NaN until appended).
- `Write(false)` — **geometry-only** profile: no `FLDS`/`FLDC` in the directory; same
  `geometryHash`. Use this for the predicate-authoring file; the features sidecar authored
  on it binds to the full export.

## Layout emitted

```text
header(32) | directory | NODE(f64) | NDID | [LABL nodes] | ELEM d0 | ELID d0 | [LABL d0]
| [FLDC d0] | [SECT | ELEM d1 | ELID d1 | BPRP d1 | [LABL d1]] | META | FLDS d0 | [FLDS d1]
```
All blocks 8-byte aligned. Directory sits right after the header with final counts (no
`APPEND` flag needed). `GeometryHash` property exposes the hash after construction — hand it
to `FeaturesSidecar.GeometryHash` so the sidecar binds.

## Append methods

`AppendStresses` (STAAD `StressRecord`, converts canonical lb/in units via
`StressViewForce`), `AppendDisplacements` (raw `Disp.DR`, fans to shell corners and beam
ends), `AppendDsr` / `AppendStr`, `AppendBeamForces(records, kind)`, and the generic
`AppendShellValues(records, kind)` for any shell component kind, written raw. The
`StressNames` / `DispNames` / `nameUnits()` / `BuildComponents()` catalogs were ported from
the v3 writer on 2026-09-03.

## Related

- [[vault/format/features-sidecar|features-sidecar]] — `FeaturesSidecar.cs` writes the groups JSON.
- [[vault/format/v3-schema|v3-schema]] — what the previous writer emitted.
