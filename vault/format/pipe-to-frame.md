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
> | One-way, gap, friction (phase 2) | Each side a restraint acts on is a two-joint link from a support point to the node, along the side the node may move freely (CAESAR's +Y: the pipe rests on it):<br>• **one-way:** SAP's gap, its opening CAESAR's gap, stiffness CAESAR's when given; when rigid 1e9 lb/in (1e9 in·lb/deg), not CAESAR's 1e12, which stalled SAP's nonlinear solution (round 2b; under 0.1% on the probe's forces);<br>• **two-way with a gap:** a gap each side;<br>• **friction:** SAP's friction isolator (its U1 the compression-only contact, U2 / U3 sliding at mu after CAESAR's friction stiffness, 1e6 lb/in, flat); with a gap too, a gap and an isolator in series through a joint free along the support only;<br>• **rotations:** the same on R1 about the axis;<br>• **support point:** half the pipe's OD behind the node, at least 1 in (the viewer draws the stub), held to ground or tied rigidly to the CNODE. SAP takes a link shorter than its merge tolerance (0.1 in) as zero-length, its local 1 +Z, so none is that short (round 2c);<br>• **a gap and an isolator in series:** the joint between them gets a token 1 lb/in spring along its free DOF, so it is held when both are open. |
| Frames' auto mesh | `AutoMesh=No` on every frame. CAESAR's elements meet only at their ends, and SAP's default would join a frame to any joint on its span, such as a limit stop's support point on the pipe's axis (round 2, below). |
> | Hanger | **Spring:** KE plus its installed load up in H (down on the CNODE). **Constant:** the load in H. **Count:** rate and load times the count. |
> | Nozzle | **Custom:** springs along the nozzle: axial, torsion, in-plane bending about the normal to nozzle and vessel axes, out-of-plane bending about the vessel axis; a blank stiffness is rigid. |
> | Load cases | CAESAR 15.01's recommended set (both probe jobs and the sample job's own output show it):<br>• OPE per thermal set: W + Dn + Tn + Pn (or P1) + H;<br>• SUS: W + P1 + H;<br>• EXP: each OPE − SUS, and each OPE − a later OPE (L6 = L1 − L2), Linear Add combinations.<br>Force vectors and uniform loads are in none: in a linear model each is a case of its own. With any nonlinear support every case is nonlinear static, with SAP's step and event limits raised (2,000 steps, 200 events a step, 100 iterations). SUS runs from zero, and each OPE continues from SUS's end state with its own Dn, Tn and any Pn − P1: the weight is carried cold, then the pipe heats up. SAP did not converge the probe's second OPE from zero (round 2d). Friction then follows that load path where CAESAR's does not (round 2d, below). |
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
> - **An OPE with its own pressure set** (Pn − P1 on top of SUS): written, not yet run by a probe.
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

> [!info]- Round 1b (2026-11-16): SAP2000 24.2 on the corrected seed
> SAP ran the corrected probe 1. Its cases are now CAESAR's own: L1–L3, and L4 = L1−L3, L5 = L2−L3, L6 = L1−L2.
> Compared with round 1's CAESAR results:
> - **The settling support** agrees in SAP itself, now that its tie is a stiffness.
> - **Away from the tees,** every restraint-load component over 200 lb / 200 ft·lb is within 5% in L3–L6 and within 8%
>   in L1–L2. The largest differences are tens of pounds: the guide at 70, the skewed support, the spring.
> - **At the tees:** under the branch's pressure thrust, CAESAR's run is about 10% softer at tee 40 (15,350 vs
>   16,970 lb/in); under thermal load, 1.7%. So the branch and tee sit 0.032 in further along the branch in every
>   weight case, and the branch anchor's axial load differs by the expansion joint's 1,800 lb/in times that. This is
>   local flexibility at the tee: B31J's again. Softening the run at the tee in a session solver closed most of it.

> [!info]- Round 2 (2026-11-16): CAESAR II 15.01 and SAP2000 24.2 on sap-probe-2 (nonlinear supports)
> Both ran CAESAR's recommended cases. A first version of the probe tied two pipe nodes 12 ft apart by a CNODE. CAESAR
> stopped on it (44E): a CNODE is the same point as its node to CAESAR. The probe now rests on a settling frame node, and
> the review flags such ties (`cnode-apart`, [[vault/arms/caesar-checks|caesar-checks]]).
>
> **SAP's run was not the model.** The limit stop at node 90 (0.25 in each way) carried nothing, yet node 90 did not move
> axially. The anchored leg took the full EA × ε, 395 kip, against CAESAR's 234 kip, which is that force after the gap
> closes.
> - The cause: the stop's two support points lie on the pipe's axis, 4.3 in either side of the node. SAP's default auto
>   mesh at intermediate joints joined the pipe frames to them, so two fully held joints sat on the pipe.
> - The coupling's axial-gap support points (0.01 in along the axis) were joined the same way.
> - The seed now writes `AutoMesh=No` on every frame ([[vault/format/sap-s2k-tables|sap-s2k-tables]]).
>
> **Against CAESAR,** the seed was solved by the session's frame solver. Its contact and friction are iterated from zero
> as CAESAR does (it reproduces SAP's linear results to four digits). Restraint loads of 100 lb or 100 ft·lb and more:
>
> | Case | Within 3% | Within 5% |
> |---|---|---|
> | L1 | 22 of 24 | — |
> | L2 | 16 of 20 | 18 of 20 |
> | L3 | 14 of 14 | — |
> | L4 | 16 of 16 | — |
> | L5 | 9 of 13 | 11 of 13 |
> | L6 | 11 of 11 | — |
>
> - The limit stop: 36,453 lb vs CAESAR's 36,462.
> - The rests, the hold-down, the two-way gap, the vertical guide, the coupling's gaps and the settling rest land where
>   CAESAR puts them.
> - The largest differences are friction forces of about 100 lb, up to 13%, and an end-anchor moment of about
>   120 ft·lb.
>
> The solver also showed a singular stiffness. A gap and an isolator in series leave the joint between them free when
> both are open; the token spring holds it.

> [!info]- Round 2b (2026-11-16): SAP2000 24.2 on the corrected probe 2
> - **L3 (weight, no thermal) agrees in SAP itself:** all 14 restraint loads within 3%, most within 0.1%. The rests, the
>   hold-down, the gaps, friction under weight and the settling support are right in SAP too.
> - **L1 and L2 stopped short.** L1 is at about 30% of its load: the limit stop at 90 moved 0.133 in of its 0.25 in gap
>   and never closed, and the anchor shows 75 lb against CAESAR's 234 kip. L2 is at about 0.1%. SAP's nonlinear solution
>   did not converge and kept its last converged step.
> - **The solver's settings, not the model:** the session solver converges on the same seed and matches CAESAR.
> - **The seed now:**
>   - rigid nonlinear supports at 1e9 lb/in instead of CAESAR's 1e12 (the session solver: under 0.1% change);
>   - SAP's limits raised: 2,000 steps, 1,000 null steps, 200 events a step, 40 / 100 iterations.
>
>   A variant on SAP's `Iterative Only` scheme was sent alongside, so one round shows which SAP needs.

> [!info]- Round 2c (2026-11-16): SAP2000 24.2 on both solution schemes
> SAP ran the round-2b seed on its `Iterative Events` scheme and the variant on `Iterative Only`. The two agree to within
> a few pounds.
> - **L3 agrees again:** 14 of 14 within 3%.
> - **L1 converged but differed** at the end anchor 290 (FX 0 against CAESAR's 871 lb; MZ −4,924 against −5,934 ft·lb)
>   and at the riser guide 200 (75 against −784 lb). The run up to the coupling sat 0.46 in further along X than in
>   CAESAR.
>   - **The cause: the coupling's six gap links were 0.01 in long.** SAP takes a two-joint link shorter than its merge
>     tolerance (0.1 in in SAP 24.2's preferences) as zero-length: its local 1 is +Z, whatever the line between its
>     joints.
>   - **The evidence:** all six reported one deformation. The axial gap acted vertically, where the coupling's tie
>     already held it. The coupling opened 0.711 in axially against its 0.25 in gap, and 0.711 − 0.25 is the 0.46 in.
>   - **Confirmed:** the session solver given SAP's rule reproduces SAP's L1: 290 MZ −4,924, 290 FX 0, 200 FX 68.
>   - **The seed now** puts a CNODE's support point half an OD from the node, as for a grounded one, and takes a tie as
>     zero-length under the same tolerance ([[vault/format/sap-s2k-tables|sap-s2k-tables]]). Solved in the session it
>     matches CAESAR as in round 2 (L1 22 of 24, L4 16 of 16 within 3%).
> - **L2 did not converge on either scheme.** SAP's log names the null-step limit (Iterative Events) and the total-step
>   limit (Iterative Only, after 25 minutes). L5 and L6 are built on it.
>   - **The stall is at the start of loading.** It stopped at about 0.2% of the load, where no gap with an opening is
>     near closing. Only the zero-opening contacts are in play.
>   - The session solver converges on the same seed.
>   - **The suspect: friction from zero.** An isolator's friction is mu times its compression, zero when loading starts.
>     SAP's usual way with friction isolators is a weight case first, the other cases starting from its end state.
>   - **Round 2d** runs the corrected seed with two extra cases, `L1_FROM_SUS` and `L2_FROM_SUS`. They continue from
>     L3's end state with only D1 + T1, and T2.

> [!info]- Round 2d (2026-11-18): SAP2000 24.2 on the corrected seed, with OPE continuing from SUS
> SAP ran the corrected seed plus two diagnostic cases, `L1_FROM_SUS` and `L2_FROM_SUS`. They continue from L3's end
> state with only D1 + T1, and T2.
> - **The coupling holds.** L1 from zero now converges, anchor 10 taking 234,236 lb against CAESAR's 234,460, and the
>   end anchor's axial load is gone from the differences.
> - **L2 from zero stalled again** (the null-step limit). **L2 from SUS converged:** the limit stop 36,426 lb against
>   36,462, the anchor 36,859 against 36,886. **The seed's OPE cases now continue from SUS**, as the diagnostic cases did.
> - **Against CAESAR,** with OPE from SUS. Restraint loads of 100 lb or 100 ft·lb and more:
>
>   | Case | Within 3% | Within 5% |
>   |---|---|---|
>   | L1 | 14 of 24 | 16 of 24 |
>   | L2 | 14 of 21 | 14 of 21 |
>   | L3 | 14 of 14 | — |
>   | L4 | 8 of 17 | 8 of 17 |
>   | L5 | 6 of 13 | 6 of 13 |
>   | L6 | 6 of 12 | 8 of 12 |
>
> - **Every difference over 5% is friction.** They sit on the first leg's friction supports (50, 70, 130), or on what
>   balances them: anchor 10's lateral force and moment about the vertical, the end anchor's torsion and the riser
>   guide's lateral load. The largest differences are about 150 lb and 410 ft·lb.
>   - **The direction differs, not the size.** At rest 50, CAESAR's 155 lb friction points along the node's total
>     movement, mostly axial. SAP's points across the pipe. Node 50 is between the anchor and the limit stop: once the
>     stop closes it stops moving axially, while the leg keeps moving sideways. SAP's friction follows the load path, so
>     it ends opposing the last slip. CAESAR solves the final state and points friction along the total movement.
>   - **Confirmed in the session** by two solvers of the same seed that differ only there. The final-state one
>     reproduces CAESAR (L1 22 of 24 within 3%). The load-stepping one, run from SUS as SAP was (friction elastic-plastic,
>     its slip kept from step to step), reproduces SAP: displacements within 0.009 in, every rigid support's load within
>     1% in L1 and within 3% in L2.
>   - **So the translation is right.** What remains is SAP's path-following friction against CAESAR's final state. No
>     SAP setting makes friction path-independent.
