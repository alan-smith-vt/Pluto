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

> [!info]- The mapping (phase 1: the linear model)
> | CAESAR | SAP |
> |---|---|
> | Axes | A Y-up job is turned to SAP's Z up: (x, y, z) → (x, −z, y). A Z-up job is written as it is. |
> | Node | A joint labelled with the node number. Synthetic points have their own labels:<br>• bend near / far points `<to>N`, `<to>F`;<br>• chord points `<to>A1` …;<br>• mitre cuts `<to>M1` …;<br>• reducer steps `<to>R1` …<br>Zero-length pieces join their ends into one joint, with a note. |
> | Geometry | CaesarGeometry on the model's own `.cii` text: CAESAR's bend convention (the tangent intersection point) and CNODE placement, the same geometry the viewer draws. |
> | Pipe | A frame per piece, SAP's `Pipe` section (OD × wall) of the element's material: `P8.625X0.322`. Labels are `<from>-<to>`, with `.1`, `.2` … where an element is several frames. |
> | Bend | Chords of at most 22.5°, through every bend node. I22 and I33 modifiers are 1 / k:<br>• k is CAESAR's when given;<br>• else 1.65 / h, with h = t R / r²;<br>• times h^(1/6) for one flanged end, h^(1/3) for two;<br>• at least 1.<br>Torsion is not modified. Weight and mass modifiers carry the arc over the chords. A mitred bend needs its k. |
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
> | To a CNODE | A linear link (Fixed DOFs, or KE):<br>• **coincident:** zero length, along SAP's default axes turned about Z;<br>• **apart:** along the line between the joints only. |
> | Hanger | **Spring:** KE plus its installed load up in H (down on the CNODE). **Constant:** the load in H. **Count:** rate and load times the count. |
> | Nozzle | **Custom:** springs along the nozzle: axial, torsion, in-plane bending about the normal to nozzle and vessel axes, out-of-plane bending about the vessel axis; a blank stiffness is rigid. |
> | Load cases | CAESAR's recommended set:<br>• OPE per thermal set: W + Dn + Tn + Pn (or P1) + F1 + H;<br>• SUS: W + P1 + F1 + H;<br>• EXP = OPE − SUS, a Linear Add combination.<br>A pattern in none of them is a case of its own. |
> | Groups | `PL_KIND_PIPE`, `PL_LINE_<line>`, `CAESAR_BENDS`, `CAESAR_RIGIDS`, `CAESAR_REDUCERS`, `CAESAR_EXPJOINTS`, `CAESAR_TIES`. |
>
> **Units.** The SAP set follows the job: `lb, in, F` for an English job, `N, mm, C` for a metric one. CAESAR's
> rotational stiffness per degree becomes SAP's per radian.

> [!info]- Not written, refused, and the open items
> **Listed and not written:**
> - SIFs, allowables, code stresses;
> - snubbers (dynamic cases only);
> - wind and wave (CAESAR keeps the profile with its load cases).
>
> **Refused, every item named** (the model is not written):
> - one-way supports, gaps and friction: **phase 2**, nonlinear links and NonStatic cases;
> - hangers CAESAR designs (give the rate and installed load it chose);
> - WRC 297, API 650 and PD 5500 nozzles (give CAESAR's stiffnesses as custom);
> - rods, bilinear and spring restraint types;
> - cold-spring elements;
> - supports at a joint in directions not square to each other;
> - a tie neither vertical nor horizontal;
> - an imposed displacement at a joint with skewed supports.
>
> **Every test model but e13 is refused today:** each has a one-way support or a gap.
>
> **Open until the probe** (`scripts/caesar/testdata/models/sap-probe-1.toml`, all two-way supports, thermal strains):
> - CAESAR's rigid stiffness (10 × wall);
> - its reducer stepping;
> - k with flanged ends;
> - the expansion joint's weight;
> - the custom nozzle's axes;
> - the hanger count.
