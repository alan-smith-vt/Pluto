---
title: A CAESAR model as a SAP2000 check model (PipeToFrame)
status: draft
created: 2026-11-12
---

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

# CAESAR → SAP2000: the pipe's structural check model

`Build-Frame -In <job.cii | .toml | .pipe.json | CAESAR workbook>` seeds a SAP2000 frame model from a CAESAR model
(`scripts/arms/frame/PipeToFrame.cs`). It holds the same pipe, loads and supports as frames, links and loads. Its
purpose is member forces and support reactions for structural design. There are **no code checks**: SIFs, allowables
and code stresses stay in CAESAR. The decision: [[vault/decisions/2026-11-12-caesar-to-sap|2026-11-12-caesar-to-sap]].

How the seed is written and used:
- **SAP's own text.** The seed is written as `.s2k` text and read back by the frame model, so every Build-Frame output
  follows:
  - `-S2k` (and `-Open` in SAP);
  - `-Xlsx`, the frame workbook;
  - `-Preview`;
  - `-Std`.
- **Outputs.** They default to `<job>_sap.*`, beside the job and never over it.
- **Seeded once.** Edits then go on in the frame workbook. Re-seeding replaces the model.
- **Round trip.** The frame workbook made from a seed gives its `.s2k` back byte for byte.

> [!info]- The mapping
> | CAESAR | SAP |
> |---|---|
> | Axes | A Y-up job is turned to SAP's Z up: (x, y, z) → (x, −z, y). A Z-up job is written as it is. |
> | Node | A joint labelled with the node number. Synthetic points have their own labels:<br>• bend near / far points `<to>N`, `<to>F`;<br>• chord points `<to>A1` …;<br>• mitre cuts `<to>M1` …;<br>• reducer steps `<to>R1` …<br>Zero-length pieces join their ends into one joint, with a note. |
> | Geometry | CaesarGeometry on the model's own `.cii` text: CAESAR's bend convention (the tangent intersection point) and CNODE placement, the same geometry the viewer draws. |
> | Pipe | A frame per piece, SAP's `Pipe` section (OD × wall) of the element's material: `P8.625X0.322`. Labels are `<from>-<to>`, with `.1`, `.2` … where an element is several frames. |
> | Bend | Chords of at most 22.5°, through every bend node. I22 and I33 modifiers are 1 / k:<br>• k is CAESAR's when given;<br>• else **B31J's 1.3 / h**, with h = t R / r² (what CAESAR 15.01 applies under B31.1-2024; `-AppendixD`: the older 1.65 / h);<br>• times h^(1/6) for one flanged end, h^(1/3) for two;<br>• at least 1.<br>Torsion is not modified. Weight and mass modifiers carry the arc over the chords. A mitred bend needs its k. |
> | Reducer | 10 stepped frames, each with the OD and wall at its middle. |
> | Rigid | The wall 10 × (the bore kept), weightless (weight and mass modifiers 0). The rigid's weight goes in W_PIPE, its contents in W_FLUID, 1.75 × its insulation in W_INSUL. A weightless rigid carries none. |
> | Expansion joint | **With a length:** a General section giving EA / L = axial K, EI / L = bending K and GJ / L = torsion K (bending = transverse × L² / 12 when only that is given); no weight, no shear deformation (AS2 = AS3 = 0).<br>**Zero length:** a linear link of the same stiffnesses.<br>**Thrust:** P × π / 4 × effective ID² at both ends, in each Pn. |
> | Materials | One SAP material per CAESAR material (`MAT106`, Type Other): E, Poisson, density and alpha. They come from `PipeModel.Materials`, the CAESAR workbook's Materials sheet. An element's own E, Poisson or density wins, as in CAESAR. A material not listed takes Pluto's defaults, with a warning ([[vault/arms/pipe-workbook|pipe-workbook]]). |
> | W | Three patterns; CAESAR's W is the three together:<br>• W_PIPE: self weight;<br>• W_FLUID: contents;<br>• W_INSUL: insulation, cladding, refractory. |
> | T1–T9 | Pattern Tn: a temperature load Tn − ambient on every frame, with the material's one alpha. A value under CAESAR's alpha tolerance (0.05) is CAESAR's thermal strain, written as strain / alpha. |
> | P1–P9 | The expansion-joint thrust only. CAESAR's Bourdon and pressure-stiffening options are off by its default. |
> | F, D, U, H | **Fn:** joint forces and moments.<br>**Dn:** ground displacements. The DOF is restrained in every case and moved in Dn, as CAESAR holds it; rotations are given in radians.<br>**Un:** distributed loads, or g's times the frame's whole weight.<br>**H:** hanger installed loads. |
> | Rigid two-way support | ANC, X … RZ, GUI and LIM become joint restraints:<br>• a skewed one goes in joint local axes (local 3 vertical where one is);<br>• GUI and LIM directions come from the pipe at the node, as CAESAR does (a vertical pipe's guide holds both horizontals). |
> | Support with stiffness | A joint spring, in the same axes. A flexible anchor's stiffness goes in all six DOFs. |
> | To a CNODE | A linear link, rigid DOFs at CAESAR's own 1e12 lb/in (1e12 in·lb/deg), not SAP's `Fixed` (round 1, below):<br>• **coincident:** zero length, along SAP's default axes (1 = +Z, 2 = +X, 3 = +Y, confirmed by round 1) turned about Z;<br>• **apart:** along the line between the joints only. |
> | One-way, gap, friction (phase 2) | Each side a restraint acts on is a two-joint link from a support point to the node, along the side the node may move freely (CAESAR's +Y: the pipe rests on it):<br>• **one-way:** SAP's gap, its opening CAESAR's gap, stiffness CAESAR's (1e12 when rigid);<br>• **two-way with a gap:** a gap each side;<br>• **friction:** SAP's friction isolator (its U1 the compression-only contact, U2 / U3 sliding at mu after CAESAR's friction stiffness, 1e6 lb/in, flat); with a gap too, a gap and an isolator in series through a joint free along the support only;<br>• **rotations:** the same on R1 about the axis;<br>• **support point:** held (to ground, half the pipe's OD behind it, so the viewer draws the stub), or tied rigidly to the CNODE. |
> | Hanger | **Spring:** KE plus its installed load up in H (down on the CNODE). **Constant:** the load in H. **Count:** rate and load times the count. |
> | Nozzle | **Custom:** springs along the nozzle: axial, torsion, in-plane bending about the normal to nozzle and vessel axes, out-of-plane bending about the vessel axis; a blank stiffness is rigid. |
> | Load cases | CAESAR 15.01's recommended set (both probe jobs and the sample job's own output show it):<br>• OPE per thermal set: W + Dn + Tn + Pn (or P1) + H;<br>• SUS: W + P1 + H;<br>• EXP: each OPE − SUS, and each OPE − a later OPE (L6 = L1 − L2), Linear Add combinations.<br>Force vectors and uniform loads are in none: in a linear model each is a case of its own. With any nonlinear support every case is nonlinear static from zero, as CAESAR solves them. |
> | Groups | `PL_KIND_PIPE`, `PL_LINE_<line>`, `CAESAR_BENDS`, `CAESAR_RIGIDS`, `CAESAR_REDUCERS`, `CAESAR_EXPJOINTS`, `CAESAR_TIES`, `CAESAR_SUPPORTS`. |
> | Tees | Rigid joints. B31J gives branch connections a flexibility of their own, which CAESAR applies for B31.1-2020 and later: not modelled yet, listed in a notice (round 1, below). |
>
> **Units.** The SAP set follows the job: `lb, in, F` for an English job, `N, mm, C` for a metric one. CAESAR's
> rotational stiffness per degree becomes SAP's per radian.

> [!info]- Not written, refused, and the open items
> **Listed and not written:**
> - SIFs, allowables, code stresses;
> - snubbers (dynamic cases only);
> - wind and wave (CAESAR keeps the profile with its load cases);
> - B31J's tee flexibility (tees are rigid joints).
>
> **Refused, every item named** (the model is not written):
> - hangers CAESAR designs (give the rate and installed load it chose);
> - WRC 297, API 650 and PD 5500 nozzles (give CAESAR's stiffnesses as custom);
> - rods, bilinear and spring restraint types;
> - cold-spring elements;
> - friction on a rotation; a gap with friction on a tie to a CNODE;
> - linear supports at a joint in directions not square to each other;
> - a linear tie neither vertical nor horizontal;
> - an imposed displacement at a joint with skewed supports.
>
> **The test models:** the sample job and e1–e6, e10, e13 and both probes seed; e7, e8, e11, e12 and probe-final are
> refused for their designed hangers and computed nozzles only.
>
> **Open:**
> - **B31J's tee flexibility**: its formulas (B31J Table 1-1) and where CAESAR applies it.
> - **Round 2** (`sap-probe-2.toml`): the nonlinear supports in SAP against CAESAR. This includes whether SAP's
>   nonlinear solution converges with CAESAR's 1e12 stiffness, and the rotational gap's `RotK` / `RotOpen`.
> - **Not exercised by round 1:** reducer stepping, a finite expansion joint's weight, k with flanged ends, the hanger
>   count.

> [!info]- Round 1 (2026-11-16): CAESAR II 15.01 and SAP2000 24.2 on sap-probe-1
> CAESAR ran the `.cii` with its recommended cases: OPE W+D1+T1+P1+H, OPE W+T2+P1+H, SUS W+P1+H, and EXP L1−L3,
> L2−L3, L1−L2. Its case editor would not open, so the seed's own cases (with F1) were not run there. SAP ran the
> seed. The comparison was made in the session, in CAESAR's axes and its sign (loads on the restraint):
> - SAP's joints with local axes report in them;
> - a hanger's load in CAESAR is its installed load plus the spring's.
>
> **Agreed:**
> - **Materials:** CAESAR's E, Poisson and density for A106 B are Pluto's defaults (29.4e6 psi, 0.3, 0.283 lb/in³).
> - **Weight:** the total matched to 3 lb in 6,140.
> - **Axial force:** the thermal force in the anchored leg, 226,034 lb vs 226,038.
> - **Supports and loads:** the hangers (spring and constant), the rotational spring, the skewed support, the
>   expansion joint's thrust, the custom nozzle, the reducer and the rigids, mostly within 1–3%.
>
> **Corrected after round 1:**
> - **Bends.** CAESAR's bends were stiffer than 1.65 / h. A frame solver of the seed (written for the session; it
>   reproduces SAP's results to four digits) fitted a uniform factor of 0.79 of k: B31J's 1.3 / h. CAESAR's echo shows
>   `APPLY B31J SIFS & FLEX = DEFAULT` under B31.1-2024. With 1.3 / h the SR bend's far-end rotation is 0.0371° vs
>   CAESAR's 0.0369° (it was 0.0299°).
> - **The settling support.** A Y support tied to a CNODE that moves down 0.25 in. SAP held the pipe's next joints in
>   place and reported 238 kip where CAESAR has 2.2 kip. The cause: SAP 24.2 gives a ground displacement through a
>   link DOF set `Fixed` as loads that never reach the rest of the model; those joints are out of equilibrium by the
>   load. With the DOF as CAESAR's 1e12 stiffness instead, the solver gives CAESAR's 2,199 lb vs 2,182.
> - **The cases** follow CAESAR's recommended set (no F1; the OPE − OPE range).
>
> **Still different: the tees.** Within 4–15% next to the probe's three tees (the branch anchor, the riser's end,
> the nozzle), SAP being the stiffer. That fits B31J's branch flexibility, which the check model does not have yet.
