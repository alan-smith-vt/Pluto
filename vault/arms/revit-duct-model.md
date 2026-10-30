---
title: Revit + Navisworks ducts → clean duct model (usage and design)
status: current
created: 2026-10-12
---

# Revit + Navisworks ducts → clean duct model

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

One command turns a **Pluto Ducts** run (Navisworks add-in, `scripts/navis`) and a **Pluto Connectors** run (Revit add-in, `scripts/revit`) into: a part-level viewer overlay with discrepancy flags, line / segment / system analysis, a **clean centreline model** (its own overlay), and the SAP input `frames.csv`. `scripts/sap/Run-SapDucts.ps1` builds a SAP2000 model from that file. Built 2026-10-08 → 10-12; job-specific records (rooms, findings, counts) live in the Notes vault handoffs, not here.

**Source of truth per quantity** (decided from a test room, see the Notes handoff 2026-10-12): topology, fabrication flag, size, orientation and run names from **Revit**; geometry is checked against the **Navisworks** mesh; the Navisworks stated Width / Height is documented, not used.

> [!info]- Run it
> ```powershell
> # Revit (production machine): Pluto Connectors -> C:\Temp\hvac\revit\<yyyyMMdd-HHmmss>\ elements.csv, connectors.csv, summary.txt
> # Navisworks: Pluto Ducts -> C:\Temp\hvac\ducts\<yyyyMMdd-HHmmss>\ ducts.csv, ducts_tri.bin, duct_parts.csv
> powershell -ExecutionPolicy Bypass -File scripts\navis\Export-DuctsViewer.ps1 -Run <ducts run> -Revit <revit run> -Room <codes> [-Part <Revit ElementId>]
> powershell -ExecutionPolicy Bypass -File scripts\sap\Run-SapDucts.ps1 -Frames <ducts run>\revit_<room>.frames.csv -SavePath <x.sdb>
> ```
> `-Room` takes one Custom room number or a list (`"R1,R2"`). `-Part` replaces the NEW block with one part's report (connector states, nearest connectors). **`-Revit` wins over `-Probe`**: leave `-Revit` off for the probe's own `-Part`. Builds: Revit add-in with `scripts\revit\Build-RevitPlugin.ps1` (Framework csc, no SDK); Navisworks add-in with `scripts\navis\Build-NavisPlugin.ps1`.

> [!info]- Outputs (`<ducts run>\revit_<room>.*`)
> | file | what |
> |---|---|
> | `.bin` / `.features.json` | part-level overlay: every Revit part as a box / pipe at its size and orientation, links (near / side / rigid stubs), joint node groups, `Flag: <name>` groups, `System <name>` groups; hover label = part, kind, system (own name and its source), line / segment, voted size, flags |
> | `_clean.bin` / `_clean.features.json` | the clean model: `Clean: <system>`, `Clean: rigid links`, `Clean: transitions`, `Flag: OVERLAP_SIZE_CONFLICT`; label = system, size (→ other end for transitions), length, source parts, flags |
> | `.supports.csv` | duct-to-steel supports (SAP input with `.frames.csv` and the steel CSVs): `SupportId, Type, DuctJoint, DX,DY,DZ, SteelJoint, SX,SY,SZ, SteelMember, AxisX..Z` (duct direction = link local 1), `Gap_in, System, Section, Flags`; inches, the frames.csv frame |
> | `.frames.csv` | **SAP input** = the clean model: joints, inches (Navisworks frame, absolute), shape (`rect` / `round` / `rigid`), W / H / Dia, W2 / H2 / Dia2 (a transition's other end), source Revit ids, flags |
> | `.parts_frames.csv` | the part-level frames (pre-clean), for comparison |
> | `.flags.csv` | one row per flagged part: both ids, both GUIDs, both categories, match method (`guid` / `position` / `none`), sizes, flags, details |
> | `.runs.csv` | one row per segment / transition: system component, line, kind, parts, length, voted size + votes, run name, deviations, `RunEnds` (why the line stops) |
> | `.match.txt` | NEW block (fit, room counts, joints), then flag counts, frames, mesh match, runs, RunName check, **systems**, **clean model** checks, box orientation; then lists (unmatched, category differs, located pairs, RunName → lines, system name → components, blocked joins) |

> [!info]- Mesh check overlays (2026-10-13): Navisworks and Revit meshes beside the clean model
> The `-Revit -Room <codes>` overlay also writes the room boxes (per room code: bbox of its Navisworks rows + 1 ft): `C:\Temp\hvac\box\room-boxes.txt` (Navisworks document units) and `C:\Temp\hvac\revit\mesh-boxes.txt` (Revit internal ft, the fit inverted, plus a `fit` line). Then:
> 1. Navisworks **Pluto Box**, option "Use the room boxes" (optionally "only HVAC"): every geometry item touching a box, no property search, no category or IfcGUID-nesting filter (the Pluto Ducts mesh missed fittings / accessories, nested parts and parts without a room number, and drew hidden items).
> 2. Revit **Pluto Mesh**: triangulated solids of every model element in the boxes (no category filter) (no view needed).
> 3. `Export-DuctsViewer.ps1 -Run <box run>` → `box_run.bin` + `box_norun.bin`; `-Run <revit-mesh run>` → `revit_mesh_run.bin` + `revit_mesh_norun.bin` (already in the Navisworks frame). Split by RunName because a hidden group still costs memory and frame time (one room: 500 MB, 2.5 M triangles without a run name): load the `_run` files beside `revit_<room>_clean.bin`, the `_norun` ones only when needed. Viewer: overlay rail **edges** checkbox (wireframe), Groups tab **Hide all / Show all** (eyes of the rows the filter shows).
>
> Groups (one of each kind per element; any eye off hides it): `Category: <cat>`, `Run: <RunName>` / `Run: (no run name)`, Navisworks `Visible` / `Hidden: self` / `Hidden: by ancestor` (red; the label names the hiding item), Revit `Fabrication` / `Not fabrication`. Navisworks visibility = the item's or an ancestor's hidden flag in the current view; section planes and transparency overrides are not read.

## Pipeline

All stages run inside `DuctsToPluto.ExportRevit` (`scripts/arms/DuctsRevitOverlay.cs`) except where a file is named.

> [!info]- 1. Load, fit, scope
> - `elements.csv` / `connectors.csv` → `RvEl` / `RvConn` (connector origin, direction `BasisZ`, width direction `BasisX`, W / H / Dia, connected-to). Revit `Curve` connectors connected to nothing are dropped (fab straights carry them).
> - Frame: rotation about Z + translation fitted on 1:1 IfcGUID pairs (`FitRigidXY`, trimmed twice). GUID from the parameter, else computed from `UniqueId` (`IfcGuidFromUniqueId`).
> - Scope: Revit elements whose GUID is on a room row (centre in the room box + 3 ft) plus unmatched elements in the box (+1 ft). Unmatched ones are located in a Navisworks-only row by position (`LocRow`: centre in its bbox + 0.1 ft, smallest box).

> [!info]- 2. Joints and links
> A connector's node is shared with the connectors it is connected to (`ConnectedTo`), then:
> - **touching**: two open ends within 0.02 ft
> - **attached**: a one-connector part (flange, support, cap) whose open end lies within 1 in of any connector: shares that joint (no open end, no link)
> - **near link**: open ends facing (angle ≥ 154°), ≤ 12 in apart, ≤ 1 in off axis (gap / overlap)
> - **side link**: an open end whose axis meets another part's line inside its span, within its half size + 3 in (a branch butting into a side)

> [!info]- 3. Pieces (geometry per part)
> `e.Segs` = the pieces a part becomes (node pairs; also the SAP frames before cleaning):
> - 2 connectors: one piece; a **bend** = two legs meeting at `BendCorner` (closest point of the two connector axes, inward); a **size-changing eccentric** part (shoe tap, eccentric reducer: ends opposite, centres > 1 in apart) = a piece along the outlet axis back to the inlet's plane + a rigid offset stub (`EccentricOutlet`, seg code -3); a same-size offset (S-bend) = its diagonal.
> - 3+ connectors: the through pair (`ThroughPair`) split at each branch junction (`BranchJunction`) + one piece per branch; a connected `Curve` connector (tap point on a straight) → rigid tap stub; no through pair → star from the centre.
> - Size (`SegSize`): the Revit connector (free size), the larger end on a transition, the segment's voted size when it has one; Navisworks stated size only when Revit has none. Orientation: box width along the `BasisX` of the connector at that piece's end (bends turn their width with them).

> [!info]- 4. Discrepancy flags (computed facts, no review status)
> | flag | meaning |
> |---|---|
> | `FAB_CATEGORY_MISMATCH` | Revit fabrication vs a Navisworks Ducts / Fittings / Accessories row |
> | `GUID_MISMATCH` | same part by position, different IfcGUID |
> | `SPLIT` | Revit pieces vs Navisworks rows not 1:1 (shared fab assembly GUIDs count too) |
> | `REVIT_ONLY` / `NAVIS_ONLY` | no partner by GUID or position (`NAVIS_ONLY` rows a Revit part runs through are "covered": listed, no frame) |
> | `SIZE_DIFFERS` | Revit connector vs every ring the mesh has at that end (end rings + interior rings within 12 in; probe `MeshConnectors`), > 1 in |
> | `NAVIS_SIZE_DIFFERS` | Navisworks stated size vs Revit (documented; the stated size is unexplained) |
> | `OPEN_END` / `TOUCHING_NOT_CONNECTED` / `NEAR_GAP` | connectivity state of the part's ends |
> | `SIZE_OUTLIER` / `SIZE_CHANGE_WITHOUT_TRANSITION` / `RUNNAME_DEVIATION` / `SYSTEM_CHANGE` | from the line analysis (5) |
> | `JOINED_BY_PROXIMITY` / `SYSTEM_NAME_CONFLICT` | from the systems (6) |
> | `OVERLAP_SIZE_CONFLICT` | clean model: overlapping collinear pieces of different sizes (both kept) |

> [!info]- 5. Lines and segments (`DuctsRunGraph.cs`)
> Kinds: straight, bend, transition (2 ends of different size), branch (3+), end (1), equipment. **Run ports** = both ends of 2-end parts and the through pair of a branch fitting; a joint continues a line when exactly two run ports meet there (an attachment may sit on it too), and near links continue it. Lines are cut into **segments** at transitions (and reducing tees). Size vote per segment: each Revit connector 1, each mesh-confirmed connector 1 more, each Navisworks stated size 0.5. `RunEnds` says why each line stops.

> [!info]- 6. Systems (`DuctsSystems.cs`)
> A system (what the user calls a run, e.g. `<SYS>`) is a connected component. **Strong joins** (always): Revit connection, touching, attachment, near link, side link, **end contact** (a *free* end inside another part's box + 6 in). **Weak join**: proximity only (a centreline point inside another part's box + 6 in, no free end involved) → joins only when the two groups' **majority** names agree or one has none; else blocked and listed. Two passes: name-agreeing proximity first, then the rest. Name per part: its own Revit `RunName`, else the Navisworks RunName on a **GUID** match only (never a position match), else the Revit system name without its trailing number; RunName `A-<bldg>-<service>-DUCT-<n>` → service. Majority per system; parts that differ → `SYSTEM_NAME_CONFLICT`.

> [!info]- 7. Clean model (`DuctsClean.cs`)
> From the pieces (sized / oriented as drawn): joints merged (near pairs, anything within 1 in); gaps closed (same-size collinear pieces of one system whose free ends face each other across up to 12 in along, 2 in sideways; an end-contact rigid link across the gap is dropped); side links and end contacts → rigid links from the duct face to the other centreline, which is split there; collinear overlaps (axes within 2°, 1 in) merged into one span, different sizes kept + flagged; same-size collinear chains collapsed (nodes only at bend corners, branch points, transitions, ends); transitions keep both end sizes; attachments drop out. Checks: duct length before / after, overlaps merged (length), connectors > 1 in from the clean line (worst five).

> [!info]- 8. SAP (`scripts/sap/SapDucts.cs`)
> New blank model (kip, in), `DUCT_STEEL` placeholder material (E 29000 ksi), `SetTube` / `SetPipe` per size (0.06 in wall default), rigid stubs = `TAP_STUB` (stiff, weightless), groups `DUCT_ALL`, `SRC_<source>`, `FLAG_<flag>`, `TAP_STUBS`; joints named from the CSV (`MergeOff`). No loads or cases. Steel + supports (`SapSteel.cs`, 2026-10-30): see the steel rows in [[vault/arms/sap-controller|sap-controller]]. **To do:** transitions as non-prismatic sections (`PropFrame.SetNonPrismatic`, start / end tube from `W/H` and `W2/H2`).

> [!info]- 8a. Orientation: larger side vertical (`scripts/duct/model/DuctsCleanOrient.cs`, 2026-10-30)
> Runs in `BuildClean` before the supports (the support gap uses the rotated depth).
> - `LARGE_SIDE_VERTICAL`: a horizontal rect clean frame (within 10 deg) whose vertical extent beats its horizontal extent by more than 1 in. A transition is flagged only when both of its ends are, so a 16x28 -> 28x16 twist is left alone.
> - **Rotation** (user decision: about the centreline): the width direction turns 90 deg about the frame's own axis. W / H, the joints and the centreline stay; frames.csv `WX..WZ` carry it to SAP. It spreads through the frame's **size run**: rect frames of the same W x H joined at joints holding exactly these two clean frames, through bends and risers. It stops at transitions (each one is a run of its own), branch joints, rigid links and open ends. Rotated frames get `ROTATED`.
> - A run that also holds a horizontal frame with its larger side already horizontal is not rotated: `ROTATION_CONFLICT` on all its frames, listed in match.txt.
> - The bottom of a rotated duct rises (H - W) / 2, so supports can drop off (demo: the 18x36 loses its rack supports, expected).
> - `DuctRotateLargeVertical = $false` in duct-config.psd1: flag only. match.txt: `NEW orientation:` line, and the run list in the clean model block.
> - Tests: `scripts\duct\tests\Test-DuctOrient.ps1` (geometry); the fixture covers the runs.

> [!info]- 8b. Supports and unsupported spans (`scripts/duct/model/`: `DuctsCleanSupports.cs`, `DuctSupports.cs`, `DuctSupportShapes.cs`, 2026-10-30; compiled with the arms by `scripts/lib/Config.ps1`)
> Runs in `BuildClean` when there is steel (`DuctSteel`), after the `SteelModel` is built ([[vault/arms/steel-csv-to-viewer|steel-csv-to-viewer]]).
> - **Support** = a horizontal steel frame crossing in plan **under** a horizontal clean duct frame (within 10 deg of horizontal, plan angle >= 20 deg), gap duct bottom -> steel top (`SteelModel.TopAt`) in [-1, `DuctSupportGapIn`] in (default 4.5). Duct bottom = centreline minus the half depth from its width direction (a duct with its larger side vertical counts its width). Gap < 0 = `STEEL_OVERLAP` (still a support); below -1 in = steel through the duct (counted, no support). Steel running along the duct under it is counted only.
> - Per support: the clean frame is split at the crossing (a joint within 1 in of an end is reused), the steel frame is split there (`SteelModel.SplitAt`, joint flag `SUPPORT`), and a `TAP_STUB` link joins the two joints in SAP (`.supports.csv`). Type `guide` for every support for now; the guide / anchor DOF table (alias -> DOF pattern) comes later, in the config.
> - **Span check**: horizontal duct frames joined at shared joints (rigid links join them but add no length and are not flagged), cut at support joints and at non-horizontal frames; a stretch longer than `DuctSpanFt` (10) -> `UNSUPPORTED_SPAN` on **every duct frame of that stretch** (pieces of any length; frames.csv has more rows because frames are split at supports) (frames.csv Flags, SAP `FLAG_UNSUPPORTED_SPAN`) and on their parts (flags.csv, with the length in Detail). A branch off a supported main counts into the main's stretch.
> - **match.txt**: `NEW supports:` line (count per type, steel through ducts, span count and the longest); in the clean model block the span list (system, length, supports at its ends, frames) and a **gap histogram** of every crossing (-12..48 in, 1 in bins) to choose `DuctSupportGapIn`.
> - **Viewer** (`_clean.bin`): stub = thin beam, group `Support: TAP_STUB`; icon = closed solid shells, one group per type (`Support: guide`, SAP name `SUPPORT_GUIDE`); `Flag: UNSUPPORTED_SPAN` (beams). Hover label: `support SP<n> | type | gap | duct size, system, joint | steel section, joint`. **Icons** (`DuctSupportShapes.Icon`), 3 in above the duct top, size 0.6 x duct width (10..30 in): guide = a square ring square to the duct with a double-headed arrow along it (slides axially); anchor = a cube on a downward pyramid; any other type = an octahedron.
> - Tests: `scripts\duct\tests\Test-DuctSupports.ps1` (PS 5.1).

> [!info]- Decisions and limits
> - Flags are facts; no review status (the upstream models get fixed, the model is regenerated).
> - Two systems never connect into each other (user): an end butting into a duct is a connection; a duct passing by is not.
> - Taps start at the face of the duct they come off + rigid link (ducts never pass inside ducts). Asymmetric tapered taps are drawn on the outlet axis at the larger size (accepted simplification; options recorded in the Notes handoff).
> - Supports come from the steel the ducts pass over, not from duct attachments (8b).
> - Mesh (`ducts_tri.bin`) has triangles for straights and fab parts only, not fittings / accessories: mesh checks cover straights mostly.
> - Not handled yet: round wyes (category Duct Fittings, no triangles), Navisworks `Material` as a third name source, whole-building runs (pairing checked in one room only).

## Compartmentalizing (plan, 2026-10-12)

The duct code grew in place over a week: `DuctsToPluto.cs` 2142 lines, `DuctsRevitOverlay.cs` 1160 (of which `ExportRevit` is ~650 in one method), `DuctsRunGraph.cs` 296, `DuctsSystems.cs` 244, `DuctsClean.cs` 336. Order of work, each step behaviour-neutral (same outputs byte for byte on the synthetic room):

> [!info]- 0. A regression fixture first
> The synthetic room every change was tested on is a throwaway script. Commit it as `pythonTools/ducts/make_synthetic_room.py` (generic names only) + a PS 5.1 runner that runs the overlay and diffs `match.txt`, `flags.csv`, `runs.csv`, `frames.csv` against checked-in expected files. Cases it already covers: chain with an outlier and a RunName deviation, reducer, tee + branch, 90° bend with a gap, tap point on a straight (connected / unconnected), shoe tap, S-offset, flange attachment, overlap, parallel duct, crossing duct of another system, mislabelled branch piece, end contact. Without this, every refactor below is a guess.
>
> **Done 2026-10-30:** `scripts/duct/tests/Test-DuctFixture.ps1` generates `pythonTools/ducts/make_demo_data.py` (fixed seed) into a temp folder, runs `Export-DuctsViewer.ps1 -Run -Revit` with the demo config and diffs every text output (match, flags, runs, frames, parts_frames, features incl. geometryHash, log) against `scripts/duct/tests/expected/`. Group GUIDs and the run time are normalised. `-Update` rewrites expected/ (intended output changes only, say so in the commit). Run it before merging any duct change.

> [!info]- 1. A context object instead of one 650-line method
> `ExportRevit` passes ~25 locals between stages (`scope`, `conns`, `touch`, `attach`, `nearPairs`, `sidePairs`, `sideNodes`, `nodes`, `beams`, `beamDim`, `rowsOf`, …) and the systems / clean stages take 10–15 parameters each. Introduce `RevitDuctRoom` (the loaded + fitted data and every derived structure) and make each stage a method on it: `Load`, `Fit`, `Scope`, `Joints`, `Pieces`, `Flags`, `Lines`, `Systems`, `Clean`, `Write*`. `ExportRevit` becomes the 15-line sequence. Static mutable state (`Blocked`, `FreeEnds`, `EndContacts`) moves onto it.

> [!info]- 2. One geometry model, drawn twice
> Pieces are built together with viewer beams, and the clean model then recovers size / orientation by matching pieces back to beams (`beamDim`, `LocalY`). Make a `Piece` record (nodes, size, width direction, kind: duct / rigid, source part) the single product of stage 3; the part overlay, `parts_frames.csv`, the systems box test and the clean model all read pieces. Removes the beam ↔ piece matching and `SegSize`'s second overload.

> [!info]- 3. Split by subject, not by date
> | new file | from |
> |---|---|
> | `DuctsRevitLoad.cs` (csv, fit, scope, GUID) | `DuctsRevitOverlay.cs` top of `ExportRevit`, `FitRigidXY`, `IfcGuidFromUniqueId` |
> | `DuctsJoints.cs` (touch, attach, near / side links, nodes) | `ExportRevit` middle |
> | `DuctsPieces.cs` (bends, eccentric, tees, taps, sizes, orientation) | `BendCorner`, `EccentricOutlet`, `SegSize`, `WidthAxis`, the drawing loop |
> | `DuctsFlags.cs` (discrepancies, mesh match, flags.csv, part report) | `MeshDiff`, `SizeDiff`, `MatchMeshConnectors`, `WriteFlagsCsv`, `PartReport` |
> | `DuctsRunGraph.cs`, `DuctsSystems.cs`, `DuctsClean.cs` | as now |
> | `DuctsOutputs.cs` (viewer, match.txt, runs / frames csv) | sidecar + report blocks of `ExportRevit` |
> | `DuctsMath.cs` | `Dot`/`Dot3`, `Sub3`, `Unit`/`Normalize`, `Dist2`, `Project`, `Lerp`, `Len` (two copies of several today) |
>
> The Revit path only needs `Row`, `ReadCsv`, `MakeNode`, `Beam`, `Opening` / `MeshConnectors` from `DuctsToPluto.cs`: move those to a shared `DuctsCommon.cs` and the Revit side can stop being a `partial` of the Navisworks-only exporter.

> [!info]- 4. Retire what the Revit path superseded (confirm first)
> In `DuctsToPluto.cs`: the Navisworks-only duct export's mesh sizing (`a70209c`, flagged for revert since 10-08), fab-mismatch listing, Revit centreline export (`-Centrelines`), Navisworks bbox fallbacks. In the overlay: the part-level `WriteFramesCsv` once the clean model is accepted, `SizeDiff` / `NAVIS_SIZE_DIFFERS` if the stated size stays unexplained and unused. The probe (`-Probe`) stays: `MeshConnectors` is shared. Move retired code to `archive/`, do not delete.

> [!info]- 5. Fewer flags
> 17 flags today. Candidates to merge: `SPLIT` + `GUID_MISMATCH` (both "matched by position"), `OPEN_END` / `TOUCHING_NOT_CONNECTED` / `NEAR_GAP` → one `JOINT_STATE` with a value, `SIZE_OUTLIER` + `SIZE_CHANGE_WITHOUT_TRANSITION`. Decide with the user after the clean model is accepted: which ones drive debugging, which are noise.
