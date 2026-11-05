---
title: Frame workbook (SAP2000 frame models from Excel; in progress)
status: draft
created: 2026-11-05
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# Frame workbook

A workbook for SAP2000 models of piping, ducting and structural steel, all as frames, with SAP's special elements
mirroring pipe-stress features: gap, hook and friction links, springs, linear and multilinear links. It works like
the CAESAR [[vault/arms/pipe-workbook|pipe-workbook]]:
- edit in Excel;
- every save rebuilds the viewer preview (supports, pinned ends, links, loads);
- a launcher writes the `.s2k` for SAP;
- an existing SAP model round-trips through it.

The model: [[vault/format/frame-model|frame-model]]. The decision: [[vault/decisions/2026-11-05-frame-model|2026-11-05-frame-model]].

**Built in phases on branch `pipestress`:**
- 0a: the SAP probe.
- 1: the lossless core and the runner.
- 0b: the shared workbook kit ([[vault/arms/workbook-kit|workbook-kit]]). **Done 2026-11-05**, every CAESAR
  workbook byte-identical.
- 2: the viewer (releases, supports, links, loads by pattern / case). **Done 2026-11-05**: any SAP `.s2k` draws
  text labels, local axis angles, explicit offsets and end releases ([[vault/arms/sap-s2k-to-viewer|sap-s2k-to-viewer]]);
  the viewer shows releases (`releases.js`), links (`links.js`), springs / hooks / friction supports, and loads by
  pattern, case or combination ([[vault/format/features-sidecar|features-sidecar]]).
- 3: the workbook. **3a done 2026-11-05** (the sheets, reading, refresh, the runner's workbook switches, launchers);
  3b, the 3D View and `-Open` in SAP, is next.
- 4: the pipe-stress layer (support presets, contents, insulation, thermal, equipment).
- 5: the review.
- 6: unit changes.
- 7: dry runs on complex models.

> [!info]- The workbook (`FrameWorkbook`, `scripts/arms/frame/FrameWorkbook.cs`)
> One sheet per kind of object, in the model's units (headers show them):
>
> | Sheet | Rows |
> |---|---|
> | Joints | Id, Joint, X Y Z (global), Restraint (`fixed`, `pin`, or the DOFs held: `U1 U2 U3 R3`), Local A B C |
> | Frames | Id, Frame, Joint I, Joint J, optional DX DY DZ (a J not on Joints is made at I + the delta; blank J = the next free number), Length (live, read-only), Section, Kind, Line, Release (a preset), Angle, Insertion (cardinal point), Modifiers (a preset) |
> | Sections | Id, Name, Shape, Material, t3 t2 tf tw t2b tfb, Area I33 I22 J (General sections), Catalog (read-only) |
> | Materials | Id, Name, Type, E, Poisson, G, Unit weight, Unit mass, Alpha, Fy, Fu. A new material's blank G is E/(2(1+ν)), its blank mass the weight / g |
> | Releases | Named presets of the 12 end DOFs (P V2 V3 T M2 M3 at I and J): `yes`, a partial-fixity stiffness, or blank. Built in: FIXED, PIN-PIN, PIN-I, PIN-J, SLIP-J; an imported pattern with no match becomes R1, R2 … |
> | Modifiers | Named presets of the 8 property modifiers; imported sets become M1, M2 … |
> | Links, Link props | Joints (blank J = grounded), property, angle; a property's type then one row per DOF (Fixed, NonLinear, KE, CE, K, C, CExp, Open, Mu slow / fast, Rate, Radius) |
> | Springs | Joint, CoordSys, U1–R3 |
> | Load patterns | Name, Type, Self weight |
> | Joint loads, Ground displacements | Joint, Pattern, CoordSys, six values |
> | Point loads, Distributed loads, Temperature | Frames (a selector), Pattern, CoordSys, Type, Dir, Rel / Abs, where, values. A row naming several frames gives one load per frame |
> | Load cases | Case, Type, Initial, then one row per load (Load type, Load name, SF) |
> | Combinations | Combination, Type, then one row per item (a case or a combination, SF; SAP's case type is worked out) |
> | Groups | SAP's groups, one row per member; the `PL_KIND_` / `PL_LINE_` groups are Frames' Kind and Line |
> | Carried | Read-only: each table the sheets do not show, with its row count: it rides along unchanged |
>
> **Reading rules:**
> - Rows tie to the model by their Id (J12, F7, S3, PL21 …: the object's uid). The model the workbook was written from is
>   stored whole in the hidden `_pluto` sheet, so an unedited workbook gives the source `.s2k` byte for byte and a
>   value not edited keeps its text.
> - A blank Id is a new object; a copied Id is read as new, with a warning.
> - A new label in an Id-ed row renames the object everywhere; references still naming the old label follow.
> - A row gone deletes its object. A deletion something still needs is refused, naming the rows (a joint still on a
>   frame). A deleted frame's or joint's loads and group rows go with it, with a warning; a label that never existed is
>   an error.
> - Frame selectors (`FrameSelect`): a label, `C1:C9` (Frames' order), `group:`, `line:`, `kind:`, `section:`, `all`;
>   several separated by commas.
> - A header in another unit (`X (ft)`, `E (ksi)`) converts its column (`FrameUnits`: lb kip N kN kgf tonf, in ft mm
>   cm m, F C, ksi psi ksf psf pcf MPa …). Refresh keeps the header's unit with its cells.
> - Errors name their cell (`Frames!I12: no section W99X1 on Sections`). The build refuses while there are any.
> - A table SAP has not confirmed yet (`FrameS2kSchema`) gets new rows anyway, with an `s2k-unconfirmed` notice in the
>   runner's output and the viewer's Checks. The probe's results move tables into the confirmed list.
>
> **Live checks** (Start sheet, red / amber cells, as in the CAESAR workbook): frames with a joint, section, release or
> modifiers preset no sheet has; frames from a joint to itself; labels used twice; links with an unknown joint or
> property; loads naming an unknown pattern; joints nothing uses (amber); copied Ids (amber). Above 5,000 frames the
> quadratic counts are off ("light").
>
> **Refresh** writes the workbook in this version's layout from its own base, every typed cell back where it was, so
> the next read gives the same model.

> [!info]- The runner: `scripts/frame/Build-Frame.ps1`
> - `-In <x.s2k | x.$2k | x.xlsx | x.frame.json>`
>   - An `.s2k` is read losslessly and checked: written back, it must be the source byte for byte. The report says so.
>   - An `.xlsx` is a frame workbook, applied to the model it was written from.
>   - Prints the model's counts and units, and the writer's notices.
> - `-S2k`: writes `<base>.s2k`. It never writes over the input. An existing file is replaced only when this runner
>   wrote it (its stamp, `<base>.build.json`), when it is the model's own source, or when nothing would change;
>   otherwise its first differing lines are listed and `-Force` is needed.
> - `-Xlsx`: writes `<base>.xlsx` and its launchers, `<base> - preview.cmd` (`-Preview -Watch`) and `<base> - s2k.cmd`
>   (`-S2k -Preview`). The Start sheet links them.
> - `-Json`: writes `<base>.frame.json`.
> - `-Preview`: writes `<base>.bin` and `<base>.features.json` for the viewer, drawn from the very document `-S2k`
>   writes (`SapToPluto.Export(S2kDoc, …, FramePreview)`). The build's notes and the workbook's warnings go to the
>   Checks panel; `model.source` names the input and the model hash. `-ModelId` names the model (default `sap/<name>`).
> - `-Watch` (with an `.xlsx`): every save rebuilds the outputs (`-Preview` when none is given). A save that does not
>   build leaves the last good outputs.
> - `-NewWorkbook -Out <name>.xlsx [-Units "Kip, in, F"] [-SapVersion 22.0.0]`: an empty model's workbook and launchers.
> - `-Refresh -In <x>.xlsx`: the workbook in this version's layout over itself, edits kept; the previous file is
>   `<x>.xlsx.bak`.
> - `-Out <base>`: as in `Build-Pipe.ps1`. A workbook beside an `.s2k` of its name that this runner did not write (the
>   SAP export it came from) writes `<name>_edit.*`. Outputs inside the repo are refused.
> - `-Probe <folder>` and `-ProbeCheck <export>`: see the SAP probe below.
> - The last line is `RESULT {json}`.
> - **Development only:** `PLUTO_DLL=<a prebuilt pluto.dll>` loads that assembly instead of compiling. This is how the
>   runner is exercised under pwsh 7 on Linux.
>
> **Checked (2026-11-05, mono and pwsh 7 with the mono-built assembly):**
> - `rack.s2k`, the probe and the tank golden (1,371 joints) each go to a workbook and back with the same model hash,
>   and `-S2k` from the workbook is the source byte for byte. The tank reads in 0.8 s.
> - An openpyxl edit table on the rack: a joint moved, the X header switched to ft (every value converted), a formula
>   with no saved value, a section dimension, a release preset swapped, a frame deleted, a frame added by DX, a point
>   load on `group:PIPE`. Each changed only its own `.s2k` lines; the deleted frame's group row went with it.
> - Refused, naming the cell: a joint still used by three frames, an unknown section, a load on a frame that never
>   existed, a formula with no value. A copied row reads as new with a warning.
> - A model built in an empty workbook (`-NewWorkbook`): materials, sections, joints, frames by delta, a gap link,
>   patterns, loads by `line:` and `kind:`, staged cases, a Linear Add combination, groups, the PL_ groups.
> - LibreOffice recalculation: the Start checks read 0 on clean workbooks and count a broken one's rows; Length works.
> - Refresh: the same model after, for the rack, the tank and an edited, unbuilt workbook.
> - `-S2k` guard (a hand-edited `.s2k` refused with its differing line; `-Force`), `-Preview` from the workbook (the
>   same geometry hash as from the `.s2k`), `-Watch` (a save rebuilt the `.s2k`).
> - Still to do on the production machine: the PS 5.1 compile; Excel opening the workbook clean; `-Watch` with Excel's
>   own saves; the sidecar merge (.NET Framework's serializer).

> [!info]- The SAP probe (Phase 0a)
> SAP silently ignores a table or field name it does not know, so Pluto writes a field only once SAP has exported it
> again. `FrameProbe` (`scripts/arms/frame/FrameProbe.cs`) writes one or more of everything not yet confirmed, each
> with a distinctive value:
> - **Sections:** every shape (Pipe, Box/Tube, I with unequal flanges, Channel, Angle, Tee, Double Angle, General); a
>   catalog reference; section modifiers; notes on a section and a link property.
> - **Frames:** releases (pin-pin, pin at I, axial plus torsion at J, shears at I, partial fixity); a 30° local axis;
>   insertion offsets; end offsets along the length; property modifiers; text joint and frame labels.
> - **Joints:** springs and joint local axes.
> - **Links:** every link type (linear, gap, hook, multilinear elastic, friction isolator, damper); two-joint links; a
>   zero-length link between coincident joints; one-joint links in two forms; link local axes.
> - **Loads:** load patterns of four types; joint forces; ground displacement; frame point loads (relative,
>   absolute, a moment, local); distributed loads (full, partial trapezoidal, projected, local); temperature loads.
> - **Cases and combinations:** linear and nonlinear static cases (one starting from another); nonlinear parameters;
>   a Linear Add combination of nonlinear cases; an Envelope that holds a combination.
>
> Steps:
> 1. `Build-Frame.ps1 -Probe <folder>` writes `probe-v22.s2k` and `probe-v26.s2k`.
> 2. Import the one for your SAP (File > Import > SAP2000 .s2k). If Help > About shows another version, set the
>    `Version` on the PROGRAM CONTROL line to it.
> 3. Export it again with every table (File > Export > SAP2000 .s2k) as `probe.sap-export.s2k`.
> 4. `Build-Frame.ps1 -ProbeCheck <that export>` writes a field-by-field report: kept, changed, dropped, what SAP adds,
>    the tables it writes that the probe did not, and SAP's own field order.
>
> Where a link-type table name is a guess, the export still shows the right one: the type comes from the general
> table.
>
> **Status:** the probe was sent to the user on 2026-11-05; results are pending.
