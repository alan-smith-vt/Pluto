---
title: CAESAR II's error checker, caught before CAESAR
status: current
created: 2026-10-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

# CAESAR II's error checker, caught before CAESAR

CAESAR II checks a model before it analyses it. Its error checker lists:
- **errors** (red): the analysis will not run;
- **warnings** (green): unusual input it accepts;
- **notes** (blue): information.

Pluto runs the same rules while the model is still being built, so a model reaches CAESAR clean:
- **The viewer's Checks tab** (`-Preview`; sidecar section `review`), with click-to-locate.
- **The direct path:** `Build-Pipe.ps1 -Cii` prints the errors and warnings as `[checks ]` lines.
- **The workbook:** red and amber cells as you type, counted on the Start sheet. This covers what a sheet can see.

The rules are `PipeReview.CaesarRules`, plus the model checks Pluto had before. Their limits are CAESAR's configuration defaults (Geometry Directives); a job whose configuration changes them can differ. Codes are the `review` item codes ([[vault/format/features-sidecar|features-sidecar]]).

**Sources:**
- **CAESAR's own messages** on jobs Pluto wrote. The message number is given where one was seen; most were seen on the combined probe job (2026-10-26, CAESAR 15.01), which tripped one rule per piece.
- **CAESAR II's user guide and quick reference**, through web search summaries, for the rest. The documentation hosts are blocked in the build environment.

Every rule is summarised in our own words.

> [!info]- Errors: CAESAR stops
> | CAESAR's rule | Pluto | Workbook |
> |---|---|---|
> | A node on the curvature of two bends, or a bend node that is also an element end (48E) | `node-twice` | Elements, Bend nodes red; Start |
> | Bends from 5 to 95 deg (5E; Minimum / Maximum Allowed Bend Angle) | `bend-angle` | Bend radius red; Start |
> | A node on a bend within 5 deg of the far end (12E), or of another node on it (Minimum Angle to Adjacent Bend Point). The near point itself (angle 0) and M are fine | `bend-node-angle` | Bend nodes red; Start |
> | Corrosion allowance as thick as the wall (3E) | `corrosion-wall` | Sections' Corrosion and Elements' Section / Wall / Corrosion red; Start |
> | A restraint to ground and an imposed displacement at one node: overspecified (57E), whatever their directions (a +Y support with a DX displacement stops it too). CAESAR's advice: give the displacement alone (a direction given holds the node), or give the restraint a connecting node and impose the displacement there | `displacement-restrained` (a restraint with a connecting node does not count) | Displacements, the row red; Start |
> | A loop whose two paths place a node more than 1 in apart (44E; Loop Closure Tolerance). CAESAR prints the miss in X, Y and Z | `loop-closure` | not on the sheet: the preview and `-Cii` |
> | Node numbers 1 to 8,000,000 (since CAESAR 11.00; 32,000 before) | `node-range` | every node column red; Start |
> | A user SIF below 1.0 (1E, once per SIF value) | `sif-below-one` | SIFs red; Start |
> | An element with no length (2E), unless it is a zero-length expansion joint | `element-zero` | DX DY DZ amber |
> | An expansion joint without its axial stiffness, or without both transverse and bending (on a joint of no length, without either): incomplete (19E) | `expansion-joint` | EJ columns red; Start |
> | A WRC 297 nozzle whose stiffener or head distances are under the nozzle's radius, or not given (118E) | `nozzle-stiffener` | Nozzles, the distances red; Start |
> | A custom nozzle without a vessel material (79E, a material / temperature error) | `nozzle-material` (a WRC 297 one without: a warning) | Nozzles, Vessel material red (amber); Start |
> | A PD 5500 nozzle in the documentation's layout: an invalid vessel type (135E) | `nozzle-pd5500`, on a nozzle Pluto writes (one CAESAR wrote is carried as written). A PD 5500 nozzle's values are not checked: they are not where the documentation puts them | Nozzles, Type amber; Start |
> | An element from a node to itself | `element-self` | From / To red |
> | Nothing holding a piece to ground (unstable) | `unsupported` | — |
> | A nozzle whose vessel centreline runs along the nozzle (from the documentation; the probe's nozzle stopped on 118E first) | `nozzle-axis` (a missing centreline: a warning) | — |

> [!info]- Warnings: CAESAR accepts, and says so
> | CAESAR's rule | Pluto | Workbook |
> |---|---|---|
> | A change of direction that is neither a bend nor an intersection (21E). CAESAR compares each element with the one before it in the input, so it sees a corner only where the second element starts at the first one's end | `corner-no-bend` (above 1 deg); `corner-unseen`: the same between elements apart in the input, which CAESAR does not report | Bend radius amber; Start |
> | A geometric intersection with no intersection type: pipe flexibilities and SIFs of 1.0 there (33E). CAESAR 15.01 read SIF types 14-17 this way | `tee-no-sif` (no entry, a joint type, or 14-17; a user SIF entry gives the SIFs) | SIFs, Type 14-17 amber; Start |
> | An intersection type where no geometric intersection is found: the SIFs come from the smallest wall and largest diameter there (39E). With four or more pipes CAESAR computes no SIF (1.0) | `intersection` (tee types 1-6; 7-13 are joints) | SIFs, Node amber; Start |
> | D/t over 100 at a bend or intersection: the code's flexibilities and SIFs are not valid for it, and CAESAR computes them anyway (160E) | `thin-wall` (and reducers) | Sections amber; Start |
> | A reducer outside B31J's limits (282E); its cone angle over 60 deg is the reducer SIF's limit (CAESAR computes it from the length when not given) | `reducer-angle` | — |
> | A rigid with no weight: zero assumed (18E). CAESAR adds no fluid or insulation weight to it | `rigid-weightless` | Elements, Weight amber; Start |
> | A temperature under 0.05 in magnitude is read as a thermal expansion (Alpha Tolerance). CAESAR then finds no allowable or yield for the material at it (134E, 121E) | `temperature-alpha` | T1-T9 amber (Processes, Elements); Start |
> | A restraint or an imposed displacement at a node whose hanger CAESAR designs (72E) | `hanger-restrained` | Hangers, Node amber; Start |
> | A nozzle node that a restraint, a displacement or a hanger also uses (266E) | `nozzle-restrained` | Nozzles, Node amber; Start |
> | An API 650 nozzle on a tank under 120 ft: outside Appendix P's scope (123E) | `nozzle-api-scope` | Nozzles, Vessel OD amber; Start |
> | An API 650 nozzle's growth put in a displacement vector that imposed displacements also use (244E) | `nozzle-vector` | Nozzles, Displacement vector amber; Start |
> | Uniform loads mixing g's and force per length (246E: on one element; 247E: one vector both ways). It reads every vector's flag, used or not | `uniform-mixed` | Uniform, In g's amber; Start |
> | A wave's added-mass coefficient above 1.0, which it typically does not exceed (151E) | `wave-added-mass` | Wind, Added mass amber; Start |
> | A user SIF at a bend: used only when the configuration allows user SIFs at bends | `sif-at-bend` | — |
> | Expansion joints: the torsion stiffness given; on a joint with length, one of transverse / bending blank (CAESAR computes it) | `expansion-joint-stiffness` | EJ columns amber; Start |
> | Pluto's own: a node on a bend within 5 deg of its near end. The configuration's limit names both ends, but 15.01 let a node at 3 deg pass on the bend whose 88 deg node it stopped on | `bend-node-near` | Bend nodes amber; Start |
> | Pluto's own: a CNODE tied to nothing, a bend with no room | `cnode-off-model`, `bend-room`, `bend-corner`, `bend-code` | — |
> | Pluto's own: a reinforced fabricated tee without its pad thickness | `sif-pad` | SIFs, Pad amber |
> | Pluto's own, nozzles: a WRC 297 nozzle without a vessel material | `nozzle-material` | Nozzles, Vessel material amber; Start |
> | Pluto's own, hangers: two entries at one node; a rate without its installed load; an anchor to free that nothing holds | `hanger-twice`, `hanger-rate-only`, `hanger-free` | Hangers, Node amber; Rate / Cold load and Free columns red; Start |

> [!info]- B31J: warnings CAESAR 15.01 gives where it takes a default
> CAESAR 15.01 computes B31J flexibilities and SIFs at intersections and reducers. Where an input it uses is not given, it warns and takes a default. Pluto lists them as one `b31j` info item, since they are usually fine:
> - **293E:** no surface node at an intersection; CAESAR generates one. It came at every intersection of the probe, until the message's display limit (5).
> - **283E:** a tee's geometric parameters not given or outside the tee's requirements, so the SIFs and flexibilities are not reduced. Seen at welding tees and a sweepolet; not at the extruded tee given its crotch radius, the fabricated tees or the weldolet.
> - **285E:** a weldolet without the fitting's outside radius (rp).
> - **286E, 287E:** a reducer without its radius r2 or its length L2.

> [!info]- Notes: information
> | CAESAR's note | Pluto |
> |---|---|
> | Four or more consecutive elements define the same uniform load: a load carries on (166E) | `uniform-repeated`, on an imported job's own blocks. Pluto writes a block only where a load changes |
> | The job's hangers: how many, how many to design and how many pre-designed (69E); the load cases to define for the hanger design (70E) | `hangers` (the ones it designs); `hangers-given` (springs and constant supports as given). The probe's counts matched: 7, 5 and 2 |
> | WRC 297 and API 650 nozzles: their inputs and the stiffnesses CAESAR computed | `nozzles` |
> | An expansion joint with no effective ID gets no pressure thrust | `expansion-joint-thrust` |

> [!info]- What Pluto does not check, and why
> - **Material limits:** a temperature outside the material's table, the minimum wall for the pressure (104E for pipe, 210E for a bent pipe's bend), allowables (121E, 134E). They need CAESAR's material database, which Pluto only names by number.
> - **Stress ratios** (al / hl): carried as written, not modelled.
> - **Hanger design:** CAESAR's. Pluto holds the hangers and their design data ([[vault/format/caesar-neutral-hangers|caesar-neutral-hangers]]) but does not size springs.
> - **CAESAR's configuration.** Pluto assumes the defaults. A job that raises the maximum bend angle, say, gets an error here that CAESAR would accept.
> - **26E**, a reinforced fabricated tee's pad improperly given, came on the probe's element between its type 16 and type 17 entries, where neither is a reinforced tee by the documentation's list. It waits on CAESAR's SIF listing of those nodes.
>
> **To extend this list:** CAESAR's Diagnostics > Error Review describes every message. Send the ones a job raised, or let the build environment reach `docs.hexagonppm.com` (its error code pages).

> [!info]- What the build itself changed
> - **The `.cii` writer** takes node numbers up to 8,000,000, CAESAR's limit; it stopped at 999,999 before.
> - **The TOML builder** numbers `g_load` vectors after the most any run has per length. A vector then holds one kind across the model; before, a run with g's only put them on U1 while U1 was per length elsewhere.
> - **The TOML example** modelled a moving anchor as an anchor plus a displacement at the same node: overspecified. It now gives the displacement in all six directions, which is the anchor.
> - **A new displacement on a connecting node** (CAESAR's way to move a restraint) is written with the element at the restraint's node; the writer refused it before, as on no element.
> - **A branch's `tee`** takes the intersection types 1-6 only (2026-10-26): CAESAR 15.01 read 14-17 as no intersection type. A `[[sifs]]` entry still writes them.

> [!info]- Verification
> - **Against CAESAR (2026-10-26):** on the combined probe, every error CAESAR 15.01 raised is a Pluto error at the same node (44E, 5E, 12E, 3E, 1E, 19E, 2E, 135E, 79E, 118E twice), and Pluto adds one, `nozzle-axis`, on a nozzle CAESAR stopped on for its stiffeners first. Every warning CAESAR raised that Pluto checks is a Pluto warning or the `b31j` info item. Pluto's own warnings on top: the bend node at 3 deg (`bend-node-near`), the corner CAESAR does not see (`corner-unseen`), and the two WRC 297 nozzles without a material.
> - **Earlier probes:** 57E at both anchored nodes with displacements, 151E on both wave elements; 57E for a displacement and a restraint in different directions.
> - **The review:**
>   - a test model trips each rule on its own;
>   - the clean model trips none;
>   - the CAESAR fixture, every public sample and the probe re-exports raise only what applies (s4's weldolets with two pipes, s11's 0.01 wall and two zero-length elements).
> - **The workbook:** each test model, written as a workbook and recalculated by LibreOffice, counts its rule on Start. The clean one counts none. Every sample workbook stays at 0 on the red checks.
> - **Excel:**
>   - every rule's conditional-format formula stays short; the long ones are worked out per row on the hidden PlotCalc sheet;
>   - Open XML SDK schema valid;
>   - no formula Excel refuses.
