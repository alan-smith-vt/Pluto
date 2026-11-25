---
title: Pluto handoff, current state
status: current
created: 2026-11-18
---

*↑ [[vault/Pluto Home|Home]] › [[vault/handoffs/Handoffs map|Handoffs map]]*

# Pluto handoff: current state

**Where it stands (2026-11-24).** Branch `pipestress` holds:
- **The pipe workflow.** An Excel workbook or a TOML becomes one pipe model, reviewed in the viewer with CAESAR's error
  checks, and written as a CAESAR II `.cii`. An imported job comes back byte for byte.
- **The frame side.** A lossless SAP2000 `.s2k` model, edited in the frame workbook (3D View, live checks) and written
  to SAP and to STAAD.Pro (`-Std`, exact or refused). A `.std` reads into a frame model (`-In x.std`): one it wrote,
  STAAD's edits too, or a model made in STAAD. Later units are converted to the first, and envelopes become SAP
  Envelope combinations. The commands after the analysis (load lists, prints, design parameters) and STAAD's load
  numbers ride on the workbook's two STAAD sheets: `-Std` writes them again, `-S2k` leaves them out.
- **STAAD's design** is in the frame workbook itself: each member's parameters on its own Frames row, and the
  parameter sets, rows by list and run on three STAAD sheets ([[vault/arms/staad-design|staad-design]]). The separate
  `<model>.design.xlsx` of 2026-11-24 is gone.
- **SAP <-> workbook <-> STAAD.** The workbook is the master. `-In <SAP export | .std> -Merge <workbook>` reads either
  program's file back and keeps what that program has no place for
  ([[vault/arms/pipe-and-frame-workflow|pipe-and-frame-workflow]]).
- **STAAD results in the viewer.** A frame model's `.anl` becomes a viewer model (`scripts/staad/Run-StaadFrame.ps1`,
  [[vault/arms/staad-frame-to-viewer|staad-frame-to-viewer]]): forces, displacements, reactions and code ratios per
  load case (one `LOAD LIST <case>` + `CHECK CODE` per case).
- **The CAESAR → SAP check model.** `Build-Frame -In <CAESAR job>` seeds the frame model from the pipe, for member
  forces and support reactions:
  - frames, bends, rigids, expansion joints, hangers and nozzles;
  - linear and nonlinear supports;
  - CAESAR's cases, each OPE continuing from SUS when a support is nonlinear.

  It is verified against CAESAR 15.01 and SAP 24.2 on two probes. The linear model agrees within 5% away from tees.
  The nonlinear one agrees except on friction-sized loads, as SAP's friction follows the load path.

**Read first:** [[vault/format/pipe-to-frame|pipe-to-frame]],
[[vault/decisions/2026-11-12-caesar-to-sap|2026-11-12-caesar-to-sap]],
[[vault/format/sap-s2k-tables|sap-s2k-tables]], [[vault/arms/frame-workbook|frame-workbook]].

**Gaps in the check model:**
- **B31J's tee flexibility.** Tees are rigid joints, with a notice. The formulas are needed: B31J Table 1-1, or
  docs.hexagonppm.com allowed in the cloud environment.
- **Not yet run by a probe:**
  - an OPE with its own pressure set;
  - reducer stepping;
  - a finite expansion joint's weight;
  - k with flanged ends;
  - rotational gaps;
  - the hanger count.
- **Element forces** have not been compared with CAESAR's yet. Only restraint loads and displacements have.

**For the user to decide:** a personal Windows path is in the pushed history (fixed in the file on 2026-11-18).
Purging it means rewriting the branch's history.

**Waiting on the production machine:**
- one thing left from STAAD round 4: at the next SAP import of a `.s2k` made from a `.std`, whether SAP takes
  `DesTypeOpt="User Spec"` (its import log names a value it refuses). The rest of round 4 is settled: units, the
  envelope's place after `PERFORM ANALYSIS`, the triangle, per-case checks into the viewer. Also found: a load in two
  envelopes stays in both, as STAAD lists it (see
  [[vault/format/frame-to-staad|frame-to-staad]]);
- the user's STAAD model again with `-In <x>.std -Xlsx`: the remaining refusals, if any, to read next (see
  [[vault/handoffs/handoff-2026-11-24b|handoff-2026-11-24b]]);
- `StaadToPluto`'s probe run: `Run-StaadFrame.ps1 -Anl scripts\lib\probes\anl-prints-probe.anl
  -Out <outside the repo>`, then `node viewer/tests/readback_staad.js <out> --probe`. Then a real model with a
  `CHECK CODE` per case;
- the STAAD design in the workbook, by hand: type values on Frames rows of a real model, and check the `.std`;
- the tidy console report of `Build-Frame.ps1` and `Build-Pipe.ps1` (`scripts/lib/Console.ps1`) in Windows PowerShell
  5.1's own console (checked under pwsh 7 only);
- the user's workbook: `-In <it>.xlsx -Xlsx -Out <new>` splits its frames (insertion points kept). Then `-Std` from
  the new workbook should have no auto mesh refusals;
- optional: whether `RA` gives SAP's leg arrangement (STAAD's `ST` is its mirror image), if drawings or STAAD's angle
  design matter.

Done there since 2026-11-23: catalog sections both ways in STAAD 2025's database form (W, channels, tees, HSS, double
and single angles); single angles confirmed end to end both ways; STAAD's short PRIS forms into SAP.

Done there on 2026-11-25: the batch with the STAAD design in the workbook builds and runs (it is one compile, so
`FrameDesign`, `FrameMesh` and `StaadToPluto` with it). The round 4 probe written straight to `-Std`, and through its
workbook, gives the same `.std` there as in the session, command for command, with `DFF 240 MEMB 5 TO 7 11` from the
four Frames rows.

**For the user to look at:** a workbook is now written split where SAP's auto mesh joins frames to joints on their span
(and `-Std` splits what is left). A warning names any split joint still held to ground, and the user's workbook had
such frames at seeded support points. Whether those
points are meant to carry the steel or to stay held is the model's question, in SAP as in STAAD.

**Seen, not settled:** SAP's own rows carry `IncludeSCAn=Yes` (shear center in the analysis) on its channel only, and
the angle probe showed SAP does not twist single angles. The `.std` notice says SAP twists tees and double angles too,
which was taken from the channel; no probe has run a tee or a double angle in SAP.

**Next:**
1. **Round 3, the sample job, in SAP only.** CAESAR's outputs are already in `scripts/caesar/testdata`: restraint loads,
   displacements and local element forces for all six cases. Seed it, run it in SAP and export.
   - The comparison adds element forces for the first time.
   - It exercises reducer stepping, expansion-joint weight, rotational gaps and two tees at full size.
   - Materials are Pluto's defaults (A53 B) unless the job's CAESAR workbook has a Materials sheet.
2. **B31J tee flexibility,** once its formulas are in hand.

**Later, not planned:** SAP back to CAESAR: results by CAESAR node and element, the support steel's stiffness into
CAESAR's restraints, re-seeding that keeps SAP-side additions, and writing a seeded model back as a `.cii`. See
"Possible later" in [[vault/decisions/2026-11-12-caesar-to-sap|2026-11-12-caesar-to-sap]].

**Branch `CalcFlow`** (0 behind `pipestress`; 2026-11-25). Steel connection calcs set up and run from one STAAD
`.anl` ([[vault/calcflow/CalcFlow map|CalcFlow map]]):
- **Built:** harvest and the calc library; the connection register and `plan`; `run` (Excel or LibreOffice, a record
  per check per revision) and `summary`; and the front door: `CalcFlow.xlsx` (Start links, Model Config per beam, Connection
  Lib marks, a schedule tab per template, Check and Run calcs) and the generated `Model_Data.xlsx`
  ([[vault/calcflow/calcflow-workbook|calcflow-workbook]]). Since 2026-11-24 also:
  - a template that takes a table of load rows: one calc per mark, its rows from only the beam ends that call it;
  - a template's input and result cells given by hand, and a block (anchor coordinates) typed as one table in a cell;
  - **Refresh config:** the workbook rebuilt around a changed model or templates folder, everything filled in carried
    over, the old file kept; post-process and check say when it is due;
  - a live check of the force units on Start; statuses in the soft colours of the pipe-review workbooks;
  - **Forces** per template (2026-11-25): the member end forces, or the support reaction at the beam end's joint in
    the same axes, for an anchorage where a brace lands on the base.
- **Decided 2026-11-24** ([[vault/decisions/2026-11-24-calcflow-workbook|2026-11-24-calcflow-workbook]]): link cells,
  two files, a connection name is a mark with every geometry input on one schedule row, parser work on `pipestress`.
- **The frame `.anl` reader lives on `pipestress`** since 2026-11-24. It reads every usual output command: the model,
  member end forces, support reactions, joint and section displacements, the code check (three layouts, AISC 360-05
  to 360-22 and the older ASD and LRFD codes) and the printed member tables, checked on the probe's real `.anl`
  (STAAD.Pro 2025). Model_Data.xlsx tabulates all of it.
- **Waiting on the production machine:** `CalcFlow.ps1 new` with the real templates; the link cells, the refresh
  waiting for Excel to close the workbook, and the Force units formulas in Excel;
  `scripts\calcflow\tests\Run-Tests.ps1` under Windows PowerShell 5.1 (the Excel stage's first run); a template built
  on an Excel data table (it needs the Excel engine).
- **Run end to end on 2026-11-25** on a made-up frame (8 marks, 26 calcs, LibreOffice): the DCRs agree with the
  templates' reference code to 5e-13. Found and fixed: harvest chose several governing results; sign-led text was
  read as formulas. Found and open: the circular weld tab needs Excel 365; harvest lists helper cells off the print
  area as inputs.
- **First real output (2026-11-25):** refused for a member list on `PRINT MEMBER FORCES` (the echoed command line was
  read as a force row); fixed on `pipestress` and merged. The user's file was not seen: to be confirmed on their run.
- **Excel repaired the first real workbook's tables (2026-11-25):** headings now keep Excel's rules for a table (a
  label over 255 characters was the one way found to break them), and a check of those rules runs over every
  workbook the tests write. The cause on the user's file is not confirmed: `CalcFlow.ps1 excel-check -Workbook <it>`
  on their machine says what its tables break.
- **Since the first real workbook (2026-11-25):** heading rows and key columns frozen on every tab; a note beside
  every cell saying what it takes; a workbook's calcs found from which sheet reads which, so a tab holds every input
  of its calc, and a Template Sheets tab says what became of each sheet.
- **Inputs from the coloured cells (2026-11-25):** a template with yellow cells gets its tab from them, whatever its
  layout (`cs/TemplateMap.cs`): cells typed per mark, tables to type, tables of rows the tool writes with values
  typed per beam end, results along the rows. The user confirmed the reading on 133 template tabs with `map`; the
  tabs built from it have run only on made-up templates. A large library gets tabs for the templates in use.
  **To learn from the user's run:** `new` on their library, a few marks, Refresh config, and what the tabs look like.
- **Next:** the same pass on the production machine with Excel; the first real project through the workbook; section
  dimensions looked up from the shape.

Last sessions: [[vault/handoffs/handoff-2026-11-25|handoff-2026-11-25]] (`CalcFlow`), [[vault/handoffs/handoff-2026-11-24b|handoff-2026-11-24b]] (STAAD results in the viewer), [[vault/handoffs/handoff-2026-11-24|handoff-2026-11-24]] (`CalcFlow`), [[vault/handoffs/handoff-2026-11-23|handoff-2026-11-23]] (`pipestress`).
