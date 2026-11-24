---
title: STAAD.Pro frame → viewer (usage)
status: draft
created: 2026-11-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# STAAD.Pro frame → viewer

A STAAD.Pro frame model's output file (`.anl`) becomes a Pluto viewer model, `<base>.bin` + `<base>.features.json`,
on the production machine. It needs Windows PowerShell 5.1 and C# 5 through `Config.ps1`; STAAD.Pro and Python are not
needed. `StaadToPluto` (`scripts/arms/StaadToPluto.cs`) reads the file with the frame `.anl` reader
(`scripts/lib/readers/AnlFrameReader.cs` and `AnlFramePrints.cs`).

The viewer shows:
- the members, with STAAD's local axes, sections, releases and offsets;
- the supports, as 3D symbols;
- per load case, the member end forces, joint displacements (the deformed shape), support reactions and code check
  ratios.

Verified on the probe's `.anl` (`scripts/lib/probes/`) in the session: every printed number checked through the viewer's
own reader. The PS 5.1 compile and a real model on the production machine are still to do, so this note stays `draft`.

How it fits with the frame workbook, SAP and STAAD: [[vault/arms/pipe-and-frame-workflow|pipe-and-frame-workflow]].
Writing a frame model to STAAD is the other direction, `Build-Frame -Std` ([[vault/format/frame-to-staad|frame-to-staad]]).

## Run

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts\staad\Run-StaadFrame.ps1 `
  -Anl "<project dir>\frame.anl" -Out "<project dir>\viewer\frame"
```

- **`-Out <base>`** writes `<base>.bin` and `<base>.features.json`.
  - The default is beside the `.anl`, with spaces turned to underscores.
  - A folder (an existing one, or any path ending in `\`) means `<folder>\<name>`.
  - An output inside this repo is refused.
- **`-SapLibrary <folder>`** is SAP2000's section library (`...\Property Libraries\Sections`). The default is the
  newest SAP2000 installed. Without one, sections come from STAAD's printed member properties.
- **`-ModelId`** is written to the binary and the sidecar. The default is `staad/<name>`.

Drop the `.bin` and the `.features.json` on the viewer together. The groups and the support symbols live in the
sidecar. A previous `.features.json` is merged, so colour and visibility edits, hand-made groups and section cuts
survive a re-export.

> [!info]- What to print in the `.std`
> The arm shows what the file printed, and nothing else:
>
> | Print | Gives |
> |---|---|
> | `PRINT MEMBER FORCES` | the member end forces |
> | `PRINT JOINT DISPLACEMENTS` | the displacements and the deformed shape |
> | `PRINT SUPPORT REACTIONS` | the reactions, which colour the support symbols |
> | `CHECK CODE` | the code ratios (below) |
> | `PRINT MEMBER PROPERTIES` | the sections, when SAP's library is not on the machine |
>
> Put the prints before `FINISH`, after the analysis. Each one takes the `LOAD LIST` in force.

> [!info]- Code checks per load case
> STAAD's `CHECK CODE` prints one row per member: its governing ratio and case over the cases of the `LOAD LIST` in
> force. So a ratio per member **per case** needs one check per case:
>
> ```
> LOAD LIST 101
> CHECK CODE ALL
> LOAD LIST 102
> CHECK CODE ALL
> ...
> ```
>
> How each check is placed:
> - **A `LOAD LIST` of one case:** the check's ratios go into that case, beside its forces. Pick a member, or colour
>   by `Code ratio`, in any case. The first such check of a case goes there. A later one of the same case is an entry
>   of its own, with a notice.
> - **Any other check** (several cases, or no `LOAD LIST`: every case) is an entry of its own in the load case list:
>   `Check <n>: <code>, worst of LOAD LIST <list>`. It holds each member's governing ratio and its case.
> - **Where one code was checked case by case in two cases or more**, one more entry holds each member's largest ratio
>   over them, and its case: `Code ratio <code>, largest over cases ...`. It is what one check over all of them would
>   print. The viewer's own envelopes are for shells only.
>
> `LOAD LIST ENV <envelopes>` (TR.40) is the cases of those envelopes, from the file's `DEFINE ENVELOPE`. A load in
> two envelopes is in the last one only, as STAAD reads it. An envelope of one case puts its check into that case.
>
> Components (kind `code`): `Code ratio` and `Code case`. Each holds one value per member, the same at both ends.
> A member that was not checked or was NOT DESIGNED has none and draws grey. In the check's own entries the forces
> draw grey too; they belong to the cases.
>
> Sidecar: one group per code of the members that FAIL, and one of those NOT DESIGNED. They are unticked; ticked,
> they paint over the sections.

> [!info]- What is drawn, and where each value comes from
> - **Geometry.** STAAD's axes are kept (Y up), and joints are in inches. Only joints a member uses are drawn.
>   A model the reader marks with an error is refused, every problem named: `SET Z UP`, generated joints or members,
>   a list it cannot expand. Its warnings are passed on.
> - **Local axes.** Each member's own axes, BETA turned. The viewer's y is minus STAAD's z, and its z is STAAD's y
>   (the depth). A BETA that is not a number (`ANGLE`, `RANGLE`, `REFERENCE`) draws as 0, with a warning.
> - **Releases** become release bits: STAAD's FX FY FZ MX MY MZ in SAP's P V2 V3 T M2 M3 slots. A spring (`K..`)
>   or a partial release (`MP`) is drawn as partly fixed.
> - **Offsets** across the member become the section's offset. Along the member they are not drawn, with a notice.
> - **Sections**, in this order:
>   1. **SAP's section library entry of that name** (`FrameSapLibrary`), drawn as `SapToPluto` draws the SAP section.
>      - STAAD's older AMERICAN names are tried as AISC's: `L40404` as `L4X4X1/4`, `HSST6X6X0.375` as `HSS6X6X3/8`,
>        `PIPX20` as `Pipe2XS`.
>      - `TABLE T` is SAP's WT; `LD` / `SD` the 2L of that spacing.
>      - A single angle is turned to STAAD's principal axes (`FrameStd.AngleTurn`, as `StdToFrame` reads it into SAP).
>   2. **PRIS `YD` / `ZD`:** the rectangle, or a circle for `YD` alone, in the unit where the property stands.
>   3. **The family's shape fitted to `PRINT MEMBER PROPERTIES`:** an I for W, S, M and HP; a tube; a pipe. Any
>      other family is a rectangle of the same radii of gyration. On the probe the fitted W10X49 has a 0.359 in web
>      against 0.340.
>   4. **Else a 4 in square**, with a warning.
> - **Forces** are the force inside the member at each end: end A's printed force negated, end B's as printed. So a
>   member's two ends read alike: tension is positive, and a fixed-ended beam's two end moments have the same sign.
>   Components: `Axial Fx`, `Shear Fy`, `Shear Fz`, `Shear |V|`, `Torsion Mx`, `Bending My`, `Bending Mz`,
>   `Bending |M|`. Units as printed.
> - **Displacements:** `DX DY DZ |D|` (inches; converted when printed in another unit) and `RX RY RZ` (radians),
>   global. A case that leaves out a member joint is not written, with a warning; a missing joint would draw
>   undeformed.
> - **Reactions** are as printed, global: `Reaction FX FY FZ |F| MX MY MZ |M|` (kind `restraint`), on every member
>   end at the support.
> - **Load cases** are named `<number>: <title>`. Only cases with a result are listed.
> - **Sidecar:**
>   - one beam group per section, ticked;
>   - one per member kind (`MEMBER TRUSS` ...), unticked;
>   - the code groups above;
>   - one ticked node group per support type;
>   - one `supports` item per support joint (anchor, or each restrained or sprung DOF). `INCLINED` supports are drawn
>     along the global axes, with a warning.

> [!info]- Checks
> - `node viewer/tests/readback_staad.js <outBase>` checks any export:
>   - one beam domain, Y up, inches;
>   - the component kinds in runs, and each magnitude the hypot of its parts;
>   - one displacement and one reaction value per joint, reactions only at supports;
>   - a code ratio the same at both ends of a member;
>   - the sidecar bound to the binary, naming only nodes and beams that exist.
> - `--probe` adds the probe's printed numbers:
>   - member 101's axial force and moments in case 101, end A negated;
>   - member 201's end moments the same sign;
>   - joint 1's reaction and joint 11's displacement;
>   - the code ratios of check 1 (1.071 in case 104; 1.787; NOT DESIGNED with none);
>   - the PRIS 6 in square, and the angle turned 45°.
>
>   Run it on `Run-StaadFrame.ps1 -Anl scripts\lib\probes\anl-prints-probe.anl -Out <outside the repo>`.
> - In the session, a copy of the probe's `.anl` with one-case `LOAD LIST`s before its extra checks placed their
>   ratios in cases 101, 102 and 301, made the repeated check of 101 an entry of its own, and gave the largest-over-cases
>   entry. A real run of one check per case is still to do: the output layout is the same, as the reader reads each
>   `CHECK CODE` alone.
