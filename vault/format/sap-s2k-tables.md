---
title: SAP2000 .s2k tables Pluto writes, as SAP reads them (the probe's results)
status: current
created: 2026-11-05
---

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

# SAP .s2k tables: what SAP reads

SAP2000 ignores a table or field name it does not know, without a word. So a table counts as confirmed only once SAP
has imported it and exported it again. The SAP probe (`FrameProbe`, `Build-Frame.ps1 -Probe` / `-ProbeCheck`,
[[vault/arms/frame-workbook|frame-workbook]]) writes one of everything the frame model writes. `FrameS2kSchema` lists
the confirmed tables; new rows anywhere else carry an `s2k-unconfirmed` notice.

> [!info]- Round 1: SAP 24.2.0, 2026-11-05
> The probe stamped 22.0.0 was imported as a new model in SAP 24.2.0, then exported with every table (1,318 tables).
> The scrubbed export is the fixture `scripts/frame/testdata/probe.sap-export.s2k`: the path on its first line and the
> licence number are replaced. It reads losslessly, goes to a workbook and back byte for byte, and draws in the viewer.
>
> **Read as written** (1,133 of 1,183 fields):
> - PROGRAM CONTROL;
> - materials 01, 02, 03A;
> - frame sections of every shape;
> - joint coordinates, restraints, local axes and springs (`JOINT SPRING ASSIGNMENTS 1 - UNCOUPLED`);
> - frames: connectivity, section assignments, releases 1 and 2, local axes, cardinal point;
> - link properties 01, 02 (linear), 05 (gap), 06 (hook);
> - links: two-joint, zero-length, one-joint; link property assignments; link local axes;
> - groups 1 and 2;
> - load patterns; joint forces and ground displacements; frame point, distributed and temperature loads;
> - cases 1, 2, 4;
> - combinations.
>
> **SAP's names, which Pluto now writes:**
>
> | Table | Was | SAP's |
> |---|---|---|
> | `FRAME PROPERTY MODIFIERS` | `A2Mod A3Mod I2Mod I3Mod MMod WMod` (the section table's names) | `AMod AS2Mod AS3Mod JMod I22Mod I33Mod MassMod WeightMod` |
> | `LINK PROPERTY DEFINITIONS 05 - GAP`, `06 - HOOK` | `Open` | `TransOpen` (and `RotOpen`) |
> | `FRAME OFFSET ALONG LENGTH ASSIGNMENTS` | `OffsetType`, `SelfWtOpt` | `Type=User`, `LengthI`, `LengthJ`, `RigidFactor` |
> | `FRAME RELEASE ASSIGNMENTS 2 - PARTIAL FIXITY` | all 12 DOFs | only the partly fixed ones (`M3I=1111   M3J=2222`) |
> | `COMBINATION DEFINITIONS` | `CaseType="Nonlinear Static"` (read) | `NonLin Static` |
> | `LINK PROPERTY DEFINITIONS 01 - GENERAL` | `LinkType=Damper` | `Damper - Exponential` |
> | `FRAME SECTION PROPERTIES 01` (General) | `S33`, `S22` | `S33Top S33Bot S22Left S22Right` (the viewer's stresses take the smaller) |
> | `PROGRAM CONTROL` | `MergeTol` | not a field |
>
> **Not read:**
> - **A one-joint link without `JointJ`** was skipped ("JointJ field is blank; record skipped"). A one-joint link is
>   `JointJ = JointI`; SAP exports it with `LinkJoints=SingleJoint`.
> - **The multilinear elastic, friction isolator and damper DOF rows.** The table names were guesses. SAP 24's are
>   `03 - MULTILINEAR`, `08 - SLIDING ISOLATOR` and `04 - DAMPER` (also `13 - LINEAR EXPONENTIAL DAMPER`,
>   `14 - BILINEAR DAMPER` …). Their fields: round 2, below.
> - **Explicit insertion offsets.** `FRAME INSERTION POINT ASSIGNMENTS` came back with `Frame CardinalPt Mirror2 Mirror3
>   Transform` only; the offset fields' names: round 2, below.
> - **The catalog section** (`FromFile=Yes FileName=AISC16.xml SectInFile=W12X26`): "Unable to open property file or
>   section not found", and SAP substituted a default General section. The workbook only carries catalog sections from
>   SAP's own exports.
>
> **Defaults SAP filled** (warnings): `CASE - STATIC 4` SolScheme; `03A` stress-strain fields; the offset-along-length
> Type. The log's errors 1–10 ("Table key Steel Design 2 - NMM Details - Eurocode 3-2005 not found") are SAP's own
> design tables, not the file's.
>
> **SAP adds on export**, which Pluto carries: GUIDs, section properties it works out, `StiffDFact`, link property
> assignments' `LinkType`, `LinkJoints` and `PropMod`, connectivity `Length` and centroids, case design fields,
> `JtPattern` on temperature loads.

> [!info]- Round 2: SAP 24.2.0, 2026-11-05
> The round-2 probe was imported as a new model in SAP 24.2.0. In SAP, frame 7 was given joint offsets (Local: 1 2 3 at
> I, 4 5 6 at J) and L_MLE a five-point curve; the model was then exported with every table. The scrubbed export is
> now the fixture `scripts/frame/testdata/probe.sap-export.s2k` (the first-line path and the licence number replaced).
> `-ProbeCheck`: 1,189 fields as written, 5 changed, 1 missing. Everything round 1 corrected was read as Pluto now
> writes it.
>
> **Confirmed** (now in `FrameS2kSchema`):
>
> | Table | Fields, in SAP's order |
> |---|---|
> | `LINK PROPERTY DEFINITIONS 03 - MULTILINEAR` | `Link DOF Fixed NonLinear TransKE TransCE DJ Point Force Displ` on the first row, then one row per further point: `Link DOF Point Force Displ` |
> | `LINK PROPERTY DEFINITIONS 04 - DAMPER` | `Link DOF Fixed NonLinear TransKE TransCE TransK TransC CExp FLDamper` |
> | `LINK PROPERTY DEFINITIONS 08 - SLIDING ISOLATOR` | `Link DOF Fixed NonLinear TransKE TransCE DJ TransK Slow Fast Rate Radius` (U1 without the friction fields) |
> | `FRAME INSERTION POINT ASSIGNMENTS` | `Frame CardinalPt CoordSys JtOffsetXI JtOffsetYI JtOffsetZI JtOffsetXJ JtOffsetYJ JtOffsetZJ Mirror2 Mirror3 Transform`; with `CoordSys=Local`, X Y Z are local 1 2 3; no offsets: no `CoordSys` and no `JtOffset` fields |
>
> **Findings:**
> - **The damper exponent is `CExp`,** with no Trans / Rot prefix. Pluto's `TransCExp` was dropped and SAP used 1.
> - **A multilinear DOF needs at least three points.** With only one, SAP's import made the DOF linear (log error
>   11). The workbook refuses fewer than three on a nonlinear DOF, and displacements that do not increase.
> - **A General section's `S33Top` and `S22Left` came back 0.** `S33Bot` and `S22Right` were kept. In round 1,
>   `S33` / `S22` set both faces. So SAP 24.2 does not read `S33Top` / `S22Left` on import, although it exports them.
>   The probe writes `S33` / `S22` again. A `.s2k` holding a General section with `S33Top` / `S22Left` gets an
>   `s2k-general-moduli` notice: check its section moduli in SAP after the import. The viewer takes the smaller
>   nonzero face.
> - **Defaults SAP filled** (warnings): `FLDamper` (Yes), and `03A`'s stress-strain fields as in round 1. Errors
>   1–10 are SAP's own design tables again.
>
> **What Pluto does with them:**
> - `FrameLinkProp`: `Points(dof)` / `SetPoints`. A curve's further points are not DOF rows.
> - The Link props sheet has a **Points** column (`force, displacement; …`). The **CExp** column is SAP's plain
>   `CExp`.
> - A property whose type changes moves its DOF rows to the new type's table.
> - `FrameMember.JointOffsets`: the Frames sheet has **Offset I** / **Offset J** columns (`1 2 3` along local 1 2 3).
>   An offset SAP holds in another system shows as `GLOBAL: 1 2 3` and is carried unchanged.
> - The viewer draws the offsets (`SapToPluto.EndOffset` reads `JtOffset…`). Its link card shows `c`, `exp` and the
>   curve's `(d, F)` points.

> [!info]- Temperature gradients: not the workbook's names (STAAD probe, SAP 24.2.0, 2026-11-07)
> The STAAD probe's `FRAME LOADS - TEMPERATURE` rows with `Type="Temp Gradient 2"` and `"Temp Gradient 3"` (the frame
> workbook's names, never in a SAP probe before) were read without an error. SAP exported them as `Type=Temperature`
> with the same values, and analysed them so: a cantilever with 20, gradient 2 = 3 and gradient 3 = −2 grew by
> α × 21 × L and did not bend.
> - **Until SAP's own name is known,** a gradient row reaches SAP as a uniform temperature, and the workbook warns on
>   every such row.
> - **The probe's round 2** tries `Temperature Gradient 2`, `Temperature Gradient 2-2`, `Temp Gradient 2-2` and
>   `Gradient 2` (and 3), one per cantilever. SAP's export shows which it keeps; if none, a gradient assigned in SAP's
>   own dialog and exported gives the name.
