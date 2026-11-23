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

The CAESAR check model's seed also writes three tables the list does not hold: `ACTIVE DEGREES OF FREEDOM`,
`FRAME AUTO MESH ASSIGNMENTS` and `TABLES AUTOMATICALLY SAVED AFTER ANALYSIS`. They are the seed's own rows, not new
ones, so they carry no notice ([[vault/format/pipe-to-frame|pipe-to-frame]]).

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
>   **Since (2026-11-23):** SAP 24.2's own export of a W10X33 from its library settles why.
>   - **The library.** SAP 24's section libraries are `Property Libraries\Sections\*.xml` in its program folder. They
>     are `AISC14.xml`, `AISC15.xml` and the metric `…M.xml`; there is no `AISC16.xml`, the file the probe named.
>   - **SAP's own row.** It is `FromFile=Yes SectInFile=W10X33`, with `FileName` the full path to `aisc15.xml` (lower
>     case, quoted). The row also carries the dimensions, `FilletRadius=0.5` (fillets: Area 9.71, not the 9.49 of the
>     bare plates) and SAP's computed properties.
>   - **SAP reads its own row back** without an error or a warning. A catalog section from a SAP export therefore
>     survives the frame model's `.s2k`, which writes its row as read. A row whose `FileName` the importing machine
>     does not have gets SAP's default section, with only a dialog to say so.
>   - **The catalog probe** (five frames, the same day) settles what a row needs:
>
>     | The row | SAP 24.2 built |
>     |---|---|
>     | SAP's own, the full path | the catalog shape, no dialog |
>     | the reference only (`FromFile=Yes SectInFile=W12X26`, the full path) | the whole shape from its library: dimensions, fillet, Area 7.65, I33 204; no dialog |
>     | the reference, or the whole row, with the bare `AISC15.xml` | the catalog shape, after a dialog per section: "not found, reimported from" its own `Sections` folder |
>     | a file it does not have (`aisc16.xml`) | **a General section with every property 1** (Area 1, I 1), after one dialog |
>
>     So a catalog row needs only its reference and a library file SAP has; a missing one is silently a section of all
>     ones (the rack's soft columns). `Build-Frame -S2k` now checks every catalog row against SAP's library on the
>     machine (`FrameSapLibrary`; `-SapLibrary <folder>` names it, else the installed SAP2000's for the model's
>     version):
>     - a path the machine has is kept;
>     - a file name the library has gets its full path, so SAP shows no dialog;
>     - a file or a shape it has not refuses the `.s2k`, each named.
>
>     With no SAP on the machine, the rows are written as they are, with a notice.
>   - **The library's entries** (`AISC15.xml`'s W10X33, from the user). An element per shape, its kind as the tag:
>     `<STEEL_I_SECTION>`, with `LABEL`, `EDI_STD`, `DESIGNATION`, then `D BF TF TW KDES A I33 Z33 AS3 I22 Z22 AS2 J S33POS
>     S33NEG S22POS S22NEG R33 R22`, in inches. SAP's own row takes them as they are: t3 = D, t2 = t2b = BF, tf = tfb =
>     TF, tw = TW, Area = A, TorsConst = J, S33Top / S33Bot = S33POS / S33NEG. It adds two of its own:
>     - FilletRadius = KDES − TF (0.935 − 0.435 = 0.5);
>     - Cw = TF · BF³ · (D − TF)² / 24 (789.797958228851, to all 15 digits).
>
>     So `StdToFrame` builds a `TABLE ST` shape picked in STAAD (no SAP row in the `.std`) as SAP's catalog row
>     (`FrameSapLibrary.Row`). It reads the newest US AISC file that has the shape (`AISC15.xml` before `AISC14.xml`,
>     not the metric ones), in the model's length unit, with `FileName` the file's full path. Only I sections (W, M, S,
>     HP) are read: another kind, a shape no file has, or no library refuses the `.std`, named. A file whose header
>     gives its lengths in another unit is refused too.
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
> | `LINK PROPERTY DEFINITIONS 08 - SLIDING ISOLATOR` | U2 / U3: `Link DOF Fixed NonLinear TransKE TransCE DJ TransK Slow Fast Rate Radius`; U1: `Link DOF Fixed NonLinear TransKE TransCE TransK TransC` |
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
> - **Round 2's guesses failed too:** `Temperature Gradient 2`, `Temperature Gradient 2-2` and `Temp Gradient 2-2` came
>   back as `Temperature`, and `Gradient 2` / `Gradient 3` were dropped.
> - **SAP's own form**, from a gradient assigned in SAP 24.2's dialog and exported: `Type=Gradient2 TempGrad2=3` and
>   `Type=Gradient3 TempGrad3=-2`. The value has its own field (`TempGrad2` / `TempGrad3`, °F per in), not `Temp`. The
>   table's columns are `Frame LoadPat Type Temp TempGrad2 TempGrad3 JtPattern`.
> - **SAP reads the form back:** round 3's import read all four temperature rows.
> - **Pluto now writes SAP's form.** The workbook lists Temperature, Gradient2 and Gradient3. A workbook read turns an
>   older name into SAP's, moves its value to the gradient's field in place, and says so. `-Std` refuses any other
>   name.

> [!info]- The ten "Table key … not found" import errors (2026-11-07)
> Every SAP 24.2 import here logs the same ten errors, "Table key … not found; table skipped". They name design-detail
> tables: Steel Design / Details 2 - NMM Details for Eurocode 3-2005 and Italian NTC 2008 / 2018, and Cold Formed
> Design / Details 3a / 3b - Flexure Details for Eurocode 3 1-3 2006.
> - The keys appear in no file, Pluto's or SAP's own exports; those carry `TABLES AUTOMATICALLY SAVED AFTER ANALYSIS`
>   with `SaveFile=No` only.
> - The STAAD probe's round 3 carried that row. SAP read it ("1 of 1 records") and logged the same ten errors, before
>   any table, with no table name.
> - So they come from SAP's own default list of tables saved after analysis, which names tables this install does not
>   have. They change nothing in the model; the fix, if one is wanted, is on SAP's side (CSI, or a repair install).


> [!info]- A ground displacement through a `Fixed` link DOF (the CAESAR probe, SAP 24.2.0, 2026-11-16)
> The CAESAR check model's probe tied a pipe joint to a joint below it by a zero-length link with U1 `Fixed`. The joint
> below was restrained and moved down 0.25 in (`JOINT LOADS - GROUND DISPLACEMENT`). SAP moved the pipe joint the full
> 0.25 in, but the pipe's next joints stayed put. The supported joint reported 238 kip, where CAESAR has 2.2 kip.
> - The link's own forces read 0.
> - The frames' joint forces at the next joints did not balance: 172 kip and 66 kip, the same as the reaction.
> - In every case without the ground displacement they balanced.
>
> So SAP turned the displacement into loads on those joints and never solved for their response. The same DOF as a
> stiffness (`Fixed=No`, KE = 1e12 lb/in, CAESAR's rigid value) takes the displacement as any spring would: a solver of
> the same model gives CAESAR's 2,199 lb. The CAESAR check model writes rigid link DOFs that way
> ([[vault/format/pipe-to-frame|pipe-to-frame]]).
>
> The same round confirmed a zero-length two-joint link's default axes: local 1 = +Z, 2 = +X, 3 = +Y, as for a
> one-joint link.

> [!info]- Frames join joints on their span (the CAESAR probe, SAP 24.2.0, 2026-11-16)
> SAP exports `FRAME AUTO MESH ASSIGNMENTS` with `AutoMesh=Yes AtJoints=Yes` for every frame, and an import without the
> table takes the same: a frame is joined to any joint lying on its span. The CAESAR probe's limit stop had its support
> points on the pipe's axis beside the node. SAP joined them to the pipe and held the pipe through them: the stop's gap
> was gone and the anchored leg took 395 kip instead of CAESAR's 234. The CAESAR check model writes `AutoMesh=No
> AtJoints=No AtFrames=No NumSegments=0 MaxLength=0 MaxDegrees=0` (SAP's own field order) for every frame it seeds.

> [!info]- A link shorter than the merge tolerance is zero-length (the CAESAR probe, SAP 24.2.0, 2026-11-16)
> The CAESAR check model's coupling had six gap links 0.01 in long, each from a support point to the node along the side
> it acts on. SAP reported one deformation for all six, in a zero-length link's axes (local 1 = +Z, 2 = +X, 3 = +Y). So
> every gap acted vertically and the coupling's axial gap never closed. SAP takes a two-joint link shorter than its
> merge tolerance as zero-length, whatever the line between its joints (taken as the threshold: the probe shows only
> that 0.01 in is under it). SAP 24.2's export gives the tolerance as `PREFERENCES - DIMENSIONAL ... MergeTol=0.1`, in
> the export's units (in). It rejects `MergeTol` in `PROGRAM CONTROL`.
>
> The CAESAR check model keeps every link either zero-length or longer than 1 in, and takes a tie under 0.1 in as
> zero-length ([[vault/format/pipe-to-frame|pipe-to-frame]], round 2c).
