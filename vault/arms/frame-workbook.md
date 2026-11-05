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
  3b: `-Open` in SAP done (production check to do); the 3D View **done 2026-11-05** (below).
- 4: the pipe-stress layer (support presets, contents, insulation, thermal, equipment).
- 5: the review.
- 6: unit changes.
- 7: dry runs on complex models.

> [!info]- The workbook (`FrameWorkbook`, `scripts/arms/frame/FrameWorkbook.cs`)
> One sheet per kind of object, in the model's units (headers show them):
>
> | Sheet | Rows |
> |---|---|
> | Joints | Id, Joint, X Y Z (global; formulas welcome), Restraint (`fixed`, `pin`, or the DOFs held: `U1 U2 U3 R3`), Local A B C |
> | Frames | Id, Frame, Joint I, Joint J (both on Joints), Length (live, read-only), Section, Kind, Line, Release (a preset), Angle, Insertion (cardinal point), Modifiers (a preset), Offset I / Offset J (joint offsets along local 1 2 3: `0 0 6`; one SAP holds in another system shows as `GLOBAL: 0 6 0` and is kept), End length I / J and Rigid zone (end length offsets, SAP's `FRAME OFFSET ALONG LENGTH`; a Type other than User shows its type and is kept) |
> | Sections | Id, Name, Shape, Material, t3 t2 tf tw t2b tfb dis, Area I33 I22 J (General sections), Catalog and section modifiers (read-only). Each shape's dimensions are required (a new I's blank t2b / tfb are t2 / tf); a new section from a catalog is refused (give its dimensions) |
> | Materials | Id, Name, Type, E, Poisson, G, Unit weight, Unit mass, Alpha, Fy, Fu. A new material's blank G is E/(2(1+ν)), its blank mass the weight / g |
> | Releases | Named presets of the 12 end DOFs (P V2 V3 T M2 M3 at I and J): `yes`, a partial-fixity stiffness, or blank. Built in: FIXED, PIN-PIN, PIN-I, PIN-J, SLIP-J; an imported pattern with no match becomes R1, R2 … |
> | Modifiers | Named presets of the 8 property modifiers; imported sets become M1, M2 … |
> | Links, Link props | Joints (blank J = grounded), property, angle; a property's type then one row per DOF (Fixed, NonLinear, KE, CE, K, C, CExp, Open, Mu slow / fast, Rate, Radius, Points: a multilinear curve `force, displacement; …`, at least three, displacements increasing). A changed type moves the DOF rows to its table |
> | Springs | Joint, CoordSys, U1–R3 |
> | Load patterns | Name, Type, Self weight |
> | Joint loads, Ground displacements | Joint, Pattern, CoordSys, six values |
> | Point loads, Distributed loads, Temperature | Frames (a selector), Pattern, CoordSys, Type, Dir, Rel / Abs, where, values. A row naming several frames gives one load per frame. Headers carry two units where the row decides: `Value (kip \| kip-in)` (a force row, a moment row), `Value A (kip/in \| kip-in/in)`; `Distance (in)`, `Start`, `End` convert on Abs rows (a Rel row is a fraction) |
> | Load cases | Case, Type, Initial (Zero, or a NonStatic case: SAP continues only from one; a loop is refused), then one row per load (Load type, Load name, SF); Geometric nonlinearity on a NonStatic case's first row (None, P-Delta, P-Delta plus Large Displacements: `CASE - STATIC 4`) |
> | Combinations | Combination, Type, then one row per item (a case or a combination, SF; SAP's case type is worked out) |
> | Groups | SAP's groups, one row per member; the `PL_KIND_` / `PL_LINE_` groups are Frames' Kind and Line |
> | Carried | Read-only: each table no object owns, with its row count, and each table objects keep that no sheet shows (offsets along the length, nonlinear case parameters, auto mesh, design data …): they ride along unchanged and follow renames and deletions |
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
> - Errors name their cell and suggest the closest name (`Frames!F12: no section W10X3 on Sections (W10X33?)`). The
>   build refuses while there are any. A unit on a column that takes none is an error, never ignored.
> - Where a frame's or link's joints moved, the `Length` and centroid SAP's export gives it are worked out again.
> - A table SAP has not confirmed yet (`FrameS2kSchema`) gets new rows anyway, with an `s2k-unconfirmed` notice in the
>   runner's output and the viewer's Checks. The probe's results move tables into the confirmed list.
>
> **Live checks** (Start sheet, red / amber cells, as in the CAESAR workbook): frames with a joint, section, release or
> modifiers preset no sheet has; frames from a joint to itself; labels used twice; links with an unknown joint or
> property; loads naming an unknown pattern; loads on a frame no sheet has; joints nothing uses (amber); copied Ids
> (amber). Above 5,000 frames the quadratic counts are off
> ("light"). The run block's **Name check** says when the workbook was saved under another name (its launchers still
> build the old one: run Refresh on it).
>
> **Refresh** writes the workbook in this version's layout from its own base, every typed cell back where it was, so
> the next read gives the same model.

> [!info]- The first production import of an edited rack (2026-11-05)
> A workbook edit of the rack went to SAP 24.2 with `-Open`. The import log showed 13 errors and 3 warnings:
> - **Errors 1–10:** SAP's own design tables, as in the probe.
> - **The edit's fault: an unstable release.** A user preset freed P, V2 and V3 at both ends of C1. SAP refuses that
>   and removes every release of such a frame. The workbook only warned. Now:
>   - a frame using an unstable preset is refused;
>   - the preset's name is red on Releases.
>
>   The rule is CSI's: P, V2, V3 or T at both ends; M2 at both ends with V3 at either; M3 at both ends with V2. A
>   partly fixed DOF counts as held.
> - **The fixture's fault (`rack.s2k`, written by hand, not by SAP):**
>   - its `SteelCode=AISC360-16` is not SAP's name (`"AISC 360-16"`);
>   - its cylindrical joint 20 gave the angle in `Y` where SAP reads `T`, so SAP skipped the joint;
>   - its wall area named a section, WALL6, it never defined.
>
>   The fixture is corrected. The model reads a cylindrical joint's angle from `T`, and so does the viewer's reader
>   (`SapReader`, which now prefers SAP's `GlobalX/Y/Z` for any non-cartesian joint).

> [!info]- Renamed objects and the Groups sheet (2026-11-05)
> - **The Groups sheet's Label column is checked live** against the sheet of its row's Object type (Joints, Frames,
>   Links).
> - **Red or amber, everywhere a label is named:** the hidden Lists sheet holds the joint, frame and link labels the
>   workbook was written with. A label no sheet has is:
>   - **red** when it never existed (the build refuses it);
>   - **amber** when it is an old label: a rename the build follows, or a deleted object whose row the build drops.
>
>   The same split now applies to the joints on Frames, Links, Springs and Joint loads, and to the frames on the
>   frame-load sheets, where a renamed object used to show red. A Start check counts each kind.
> - **`-Refresh` works on a workbook whose sheets do not build:**
>   - it needs only the hidden base and the typed cells (`FrameWorkbook.ReadBase`);
>   - it says the problems and refreshes, so the new checks can then show where they are;
>   - a user's workbook with an unstable release refreshed to the same model and `.s2k`.

> [!info]- Ids (2026-11-05)
> The Id column is each row's link to its object in the model, not a row counter.
> - **A new row shows `new`** by formula as soon as its next column is filled. Nothing is typed, and `new` reads as a
>   new object.
> - **A row-number formula was asked for and declined:**
>   - inserting or deleting a row would shift every Id below it;
>   - on named objects that reads as a chain of renames, which re-point everything that names them;
>   - on CAESAR's Elements, the blocks carried with each element would move to the wrong one.

> [!info]- Dropdowns and tab colours (2026-11-05)
> - **Dropdowns that take only a listed value:** section, release and modifier presets, material, link property,
>   pattern, Rel / Abs, geometric nonlinearity, DOF.
> - **Dropdowns that also take typed text:**
>   - joints (on Frames, Links, Springs, Joint loads, Ground displacements);
>   - frames (on the frame-load sheets, which also take selectors);
>   - Kind (PIPE, DUCT, STEEL or your own);
>   - Load name (the patterns; an Accel load takes U1 U2 U3);
>   - Initial and a combination's Item (the load cases; another combination's name is typed);
>   - every type list.
> - **Tabs are coloured like the column headers:**
>   - geometry blue;
>   - properties teal;
>   - supports and links orange;
>   - loads, cases and combinations rust;
>   - Settings and Groups slate;
>   - the 3D View purple;
>   - Carried grey.

> [!info]- What an import fills (2026-11-05)
> An `.s2k` fills every sheet whose objects it holds:
> - joints, frames, sections, materials, release and modifier presets, links and their properties (with multilinear
>   curves), springs;
> - patterns, every load table, cases (with geometric nonlinearity), combinations, groups;
> - insertion points with joint offsets, and end length offsets.
>
> What stays on **Carried** applies to no frame model edit:
> - SAP's analysis and design settings and preferences, grid lines, mass source, project information;
> - area objects, which a frame workbook does not edit;
> - per-frame SAP defaults (auto mesh, design procedures, output stations, end skew, load transfer);
> - the nonlinear cases' solver controls beyond geometric nonlinearity.
>
> A table SAP exports with data that is not listed above shows on Carried by name, with its row count.

> [!info]- The 3D View (`FrameView`, 2026-11-05)
> The **3D View** sheet (after Frames) draws the model as an Excel XY chart that follows Joints, Frames, Releases,
> Sections and Links as they are edited, on the kit's `WbPlot` ([[vault/arms/workbook-kit|workbook-kit]]). Z is up.
> - **Drawn:** steel and pipe frames joint to joint (pipe: a Pipe section, or Kind PIPE / DUCT); the highlighted
>   frame (by label); pinned ends (a red dot 8 % in from an end whose release preset frees M2 or M3); fixed supports
>   (squares) and other restraints (triangles) from Joints' Restraint; links (dashed) and one-joint links (diamonds);
>   joint labels; the axis triad.
> - **Controls:** Direction (Iso, Plan, Front along +Y, Side along -X, Custom with Azimuth / Elevation), Highlight
>   frame, Joint labels, Supports, Pinned ends, Links.
> - **Figures:** the highlighted frame (joints, length, section), frames drawn, rows not drawn (a joint not on Joints,
>   or a frame from a joint to itself; amber), steel and pipe length, extents X Y Z.
> - **Not shown:** insertion points and offsets. Room for 200 more frames, joints and links than the model had;
>   Refresh follows a model that grew. Above 8,000 frames the sheet is a note only (the viewer shows the model).
> - Hidden `PlotCalc` (a frame block on the Frames rows, a joint block on the Joints rows, the `pl_` parameters) and
>   `PlotPts` (the series). Each frame is drawn J to I with a gap row after it, WbPlot's rule for LibreOffice.
> - LibreOffice draws a marker series under the line series, Excel over them, so the pinned-end dots are solid and
>   wider than a line.
>
> **Checked (LibreOffice recalculation):** each frame's length equals its joints' distance (rack, round-2 export);
> every joint's projection matches a direct computation to 5e-13; the pins sit on PIN-PIN and PIN-I ends only (a
> partial fixity is not drawn as a pin); supports, links and the one-joint link land on their joints; the rendered
> rack shows its portals, pinned beams, supports and the pipe with its link. The workbook round trips stay byte for
> byte, and a Refresh of an older workbook adds the view with the same model.

> [!info]- Frames go between joints (2026-11-05)
> Every element, steel or pipe, is a Frames row between two joints on Joints. That is SAP's own form: its exports are
> joint-based, steel needs named shared joints, and a frame meeting another simply names the same joint. A delta entry
> (DX DY DZ on Frames, making joint J) was tried and dropped the same day: two forms in one sheet doubled every check.
> The CAESAR workbook keeps its delta entry: it models CAESAR's own form, From to To by DX DY DZ.
>
> - **A pipe run** is entered on Joints with formulas: the next joint's X is `=C12+24` (row 12's X plus 24), its Y
>   `=D12`. Refresh keeps the formulas; Excel and LibreOffice save their values; a file saved by a script library
>   with no values is still read (`WbFormula`: references and arithmetic).
> - **A workbook from before** still holding a DX, DY or DZ column with values is refused, naming the first cell, so
>   its deltas are never silently ignored.
> - **Checked:** a three-joint run from joint 1 typed as chained formulas reads to the right coordinates from the
>   openpyxl file (no saved values) and after LibreOffice recalculation (the same model hash); Length reads 24, 36,
>   12; a frame naming `P33` is refused (`P3?`); Refresh keeps every formula; the rack, the probe, the round-2 export
>   and the tank go to a workbook and back byte for byte.

> [!info]- The dry run (Phase 3c, 2026-11-05)
> An agent used the workbook and runner only, through five scenarios:
> 1. a new pipe rack from scratch;
> 2. the rack export round-tripped with typical edits;
> 3. deliberate mistakes;
> 4. Refresh of an edited workbook;
> 5. the probe file.
>
> Scenario 1 built on the first try. Scenario 2's edits each changed only their own lines. Refresh gave the same
> model. It found 13 issues; fixed the same day:
> - **Wrong result:** a unit typed on a load column was ignored; now it converts, and a unit on a column that takes
>   none is an error.
> - **Wrong result:** a section with missing dimensions was accepted; now each shape's dimensions are required, and a
>   new catalog section is refused.
> - **Wrong result:** a case continuing from a linear case, and loops, were written; now refused.
> - **Crash:** a duplicate joint label crashed the read with no cell named; now an error naming it.
> - **Wrong check:** links to joints a delta makes counted red; now accepted (the delta entry has since gone).
> - **Gap:** no live check for a load on a missing frame; added.
> - **Silent:** joints a delta made were never reported; now a notice per joint (the delta entry has since gone).
> - **Hidden data:** kept-but-hidden data was invisible; now listed on Carried, with Double Angle `dis` and section
>   modifiers on Sections.
> - **Messages:** errors pointed at the Id cell; now they name the label cell and suggest a name.
> - **Save As:** a renamed workbook built the old file; the Name check now says so.
> - **Stale values:** lengths and centroids went stale; now recomputed.
> - **Small items:** an empty `GUID=` was written as `GUID=""`; the Link props row count; imported release presets
>   now named by what they release (`R1 I M3~ J M3~`); a hint on link local axes.
>
> Still open:
> - catalog sections for new rows;
> - the deleted-frame warning repeating until its Groups row is removed;
> - the `.bak` name.

> [!info]- The runner: `scripts/frame/Build-Frame.ps1`
> - `-In <x.s2k | x.$2k | x.xlsx | x.frame.json>`
>   - An `.s2k` is read losslessly and checked: written back, it must be the source byte for byte. The report says so.
>   - An `.xlsx` is a frame workbook, applied to the model it was written from.
>   - Prints the model's counts and units, and the writer's notices.
> - `-S2k`: writes `<base>.s2k`. It never writes over the input. An existing file is replaced only when this runner
>   wrote it (its stamp, `<base>.build.json`), when it is the model's own source, or when nothing would change;
>   otherwise its first differing lines are listed and `-Force` is needed.
> - `-Xlsx`: writes `<base>.xlsx` and its launchers, `<base> - preview.cmd` (`-Preview -Watch`), `<base> - s2k.cmd`
>   (`-S2k -Preview`) and `<base> - open in SAP.cmd` (`-S2k -Preview -Open`). The Start sheet links them.
> - `-Json`: writes `<base>.frame.json`.
> - `-Preview`: writes `<base>.bin` and `<base>.features.json` for the viewer, drawn from the very document `-S2k`
>   writes (`SapToPluto.Export(S2kDoc, …, FramePreview)`). The build's notes and the workbook's warnings go to the
>   Checks panel; `model.source` names the input and the model hash. `-ModelId` names the model (default `sap/<name>`).
> - `-Open` (production machine; implies `-S2k`; not with `-Watch`): imports the `.s2k` in a SAP2000 of its own,
>   through the SAP controller in a separate PowerShell (`Run-Sap.ps1 -Open <s2k> -Stamp -NewInstance -KeepOpen`):
>   stamped for that SAP's version (`SapSession.OpenStamped`), saved beside it as `.sdb`. SAP's joint, frame and link
>   counts are compared with the model's; a difference is a warning (a table SAP did not read).
> - `-Watch` (with an `.xlsx`): every save rebuilds the outputs (`-Preview` when none is given). A save that does not
>   build leaves the last good outputs. Every input is opened shared: Excel keeps an open workbook locked for writing,
>   and the first production run's live preview failed on it ("being used by another process", 2026-11-05) because
>   the input's fingerprint used a plain `OpenRead`.
> - `-NewWorkbook -Out <name>.xlsx [-Units "Kip, in, F"] [-SapVersion 22.0.0]`: an empty model's workbook and launchers.
> - `-Refresh -In <x>.xlsx`: the workbook in this version's layout over itself, edits kept; the previous file is
>   `<x>.xlsx.bak`.
> - `-Out <base>`: as in `Build-Pipe.ps1`. A workbook beside an `.s2k` of its name that this runner did not write (the
>   SAP export it came from) writes `<name>_edit.*`. Outputs inside the repo are refused.
> - `-Probe <folder>` and `-ProbeCheck <export>`: see the SAP probe below.
> - The last line is `RESULT {json}`.
> - **Development only:** `PLUTO_DLL=<a prebuilt pluto.dll>` loads that assembly instead of compiling. This is how the
>   runner is exercised under pwsh 7 on Linux. `-Preview` there needs mono's `System.Web.Extensions` loaded first (the
>   sidecar merge uses .NET Framework's serializer); a failed preview leaves no temporary files.
>
> **Checked (2026-11-05, mono and pwsh 7 with the mono-built assembly):**
> - `rack.s2k`, the probe and the tank golden (1,371 joints) each go to a workbook and back with the same model hash,
>   and `-S2k` from the workbook is the source byte for byte. The tank reads in 0.8 s.
> - An openpyxl edit table on the rack: a joint moved, the X header switched to ft (every value converted), a formula
>   with no saved value, a section dimension, a release preset swapped, a frame deleted, a frame added by DX (then; now by joints), a point
>   load on `group:PIPE`. Each changed only its own `.s2k` lines; the deleted frame's group row went with it.
> - Refused, naming the cell: a joint still used by three frames, an unknown section, a load on a frame that never
>   existed, a formula with no value. A copied row reads as new with a warning.
> - A model built in an empty workbook (`-NewWorkbook`): materials, sections, joints, frames by delta (then; now by joints), a gap link,
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
> **Status:**
> - Round 1 was imported in SAP 24.2.0 and exported again on 2026-11-05. The results, SAP's own names and what is
>   still open are in [[vault/format/sap-s2k-tables|sap-s2k-tables]].
> - The fixes are in the model, the workbook, the sidecar and `FrameS2kSchema`. SAP's export is now a fixture
>   (`scripts/frame/testdata/probe.sap-export.s2k`).
> - Round 2 was imported and exported on 2026-11-05: the multilinear, damper and sliding-isolator tables and the
>   insertion offsets are confirmed, and its export is now the fixture. The probe writes SAP's names throughout; a
>   further round only re-checks them.
