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
- 0b: the shared workbook kit.
- 2: the viewer (releases, supports, links, loads by pattern / case).
- 3: the workbook.
- 4: the pipe-stress layer (support presets, contents, insulation, thermal, equipment).
- 5: the review.
- 6: unit changes.
- 7: dry runs on complex models.

> [!info]- The runner: `scripts/frame/Build-Frame.ps1` (today)
> - `-In <x.s2k | x.$2k | x.frame.json>`
>   - An `.s2k` is read losslessly and checked: written back, it must be the source byte for byte. The report says so.
>   - Prints the model's counts and units.
> - `-S2k`: writes `<base>.s2k`. It never writes over the input. An existing file with other contents needs `-Force`.
> - `-Json`: writes `<base>.frame.json`.
> - `-Preview`: writes `<base>.bin` and `<base>.features.json` for the viewer, drawn from the very document `-S2k`
>   writes (`SapToPluto.Export(S2kDoc, …, FramePreview)`). The build's notes go to the Checks panel; `model.source` names
>   the input and the model hash. `-ModelId` names the model (default `sap/<name>`).
> - `-Out <base>`: as in `Build-Pipe.ps1`. Outputs inside the repo are refused.
> - `-Probe <folder>` and `-ProbeCheck <export>`: see the SAP probe below.
> - The last line is `RESULT {json}`.
> - **Development only:** `PLUTO_DLL=<a prebuilt pluto.dll>` loads that assembly instead of compiling. This is how the
>   runner is exercised under pwsh 7 on Linux.
>
> **Checked (2026-11-05, pwsh 7 with the mono-built assembly):**
> - `-Preview` from `rack.s2k` and from its `.frame.json` gives the same geometry hash as a plain SAP export of the
>   file (the sidecar merge needs .NET Framework's serializer: on Linux only a first run, with no previous sidecar,
>   gets that far);
> - `.s2k` → `.frame.json` → `.s2k` is byte-identical to the source;
> - the `-Force` guard, the repo guard, `-Probe` and `-ProbeCheck` all work;
> - the PS 5.1 compile probe on the production machine is still to do.

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
