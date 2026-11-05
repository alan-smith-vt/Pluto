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
>   `14 - BILINEAR DAMPER` …). Their fields are still to confirm: round 2.
> - **Explicit insertion offsets.** `FRAME INSERTION POINT ASSIGNMENTS` came back with `Frame CardinalPt Mirror2 Mirror3
>   Transform` only; the offset fields' names are still to learn.
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

> [!info]- Round 2 (to run)
> `Build-Frame.ps1 -Probe <folder>` now writes round 2:
> - the corrected names above, to see them read;
> - the multilinear, sliding isolator and damper rows in `03`, `04` and `08`, to see their fields.
>
> Two things are easier set in SAP before exporting:
> 1. **Frame 7:** Assign > Frame > Insertion Point, joint offsets (Local), I end 1 2 3 and J end 4 5 6. This shows the
>    offset fields' names.
> 2. **Link property L_MLE:** Define > Section Properties > Link/Support Properties, U2 nonlinear, three or four
>    force–deformation points. This shows where SAP keeps the points.
>
> Then export with every table and run `-ProbeCheck`.
