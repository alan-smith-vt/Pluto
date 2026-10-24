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

**Sources** (2026-10-24):
- **CAESAR's own messages** on jobs Pluto wrote; the message number is given where one was seen.
- **CAESAR II's user guide and quick reference**, through web search summaries. The documentation hosts are blocked in the build environment, so the exact message texts and numbers of the rest are not known here.

Every rule is summarised in our own words.

> [!info]- Errors: CAESAR stops
> | CAESAR's rule | Pluto | Workbook |
> |---|---|---|
> | A node on the curvature of two bends, or a bend node that is also an element end (48E, seen) | `node-twice` | Elements, Bend nodes red; Start |
> | Bends from 5 to 95 deg (Minimum / Maximum Allowed Bend Angle) | `bend-angle` | Bend radius red; Start |
> | Nodes on a bend at least 5 deg from each other and from its near and far ends; the near point itself (angle 0) and M are fine (Minimum Angle to Adjacent Bend Point) | `bend-node-angle` | Bend nodes red; Start |
> | Corrosion allowance as thick as the wall | `corrosion-wall` | Sections' Corrosion and Elements' Section / Wall / Corrosion red; Start |
> | A restraint and an imposed displacement in the same direction at a node (overspecified) | `displacement-restrained` | Displacements, that DOF red; Start |
> | A loop whose two paths place a node more than 1 in apart (Loop Closure Tolerance) | `loop-closure` | not on the sheet: the preview and `-Cii` |
> | Node numbers 1 to 8,000,000 (since CAESAR 11.00; 32,000 before) | `node-range` | every node column red; Start |
> | A user SIF below 1.0 | `sif-below-one` | SIFs red; Start |
> | An element with no length, unless it is a zero-length expansion joint | `element-zero` | DX DY DZ amber |
> | An element from a node to itself | `element-self` | From / To red |
> | Nothing holding a piece to ground (unstable) | `unsupported` | — |

> [!info]- Warnings: CAESAR accepts, and says so
> | CAESAR's rule | Pluto | Workbook |
> |---|---|---|
> | A change of direction with no bend or intersection | `corner-no-bend` (above 1 deg) | Bend radius amber; Start |
> | Uniform loads mixing g's and force per length (246E: on one element; 247E: one vector both ways). It reads every vector's flag, used or not | `uniform-mixed` | Uniform, In g's amber; Start |
> | An intersection is three pipes, two in line. With one or two CAESAR assumes a partial intersection; with four or more it computes no SIF (1.0) | `intersection` (tee types 1-6, 14-17 only; 7-13 are joints) | SIFs, Node amber; Start |
> | D/t over 100 at a bend or intersection (the code's SIFs are not proven for it) | `thin-wall` (and reducers) | Sections amber; Start |
> | A reducer's cone angle over 60 deg (the reducer SIF's limit; CAESAR computes it from the length when not given) | `reducer-angle` | — |
> | A user SIF at a bend: used only when the configuration allows user SIFs at bends | `sif-at-bend` | — |
> | Expansion joints: every stiffness given; on a joint with length one of transverse / bending blank (CAESAR computes it) | `expansion-joint` | EJ columns amber; Start |
> | A temperature under 0.05 in magnitude is read as a thermal expansion (Alpha Tolerance) | `temperature-alpha` | T1-T9 amber (Processes, Elements); Start |
> | Pluto's own: a tee with no SIF entry, a CNODE tied to nothing, a bend with no room | `tee-no-sif`, `cnode-off-model`, `bend-room`, `bend-corner`, `bend-code` | — |

> [!info]- Notes: information
> | CAESAR's note | Pluto |
> |---|---|
> | Four or more consecutive elements define the same uniform load: a load carries on (166E, seen) | `uniform-repeated`, on an imported job's own blocks. Pluto writes a block only where a load changes |
> | The job's spring hangers: CAESAR designs them first and asks for its hanger-design load cases (69E, 70E, seen) | `hangers` |
> | A rigid with no weight is a weightless construction element | `rigid-weightless` |
> | An expansion joint with no effective ID gets no pressure thrust | `expansion-joint-thrust` |

> [!info]- What Pluto does not check, and why
> - **Material limits** (a temperature outside the material's table, minimum wall for pressure, allowables). They need CAESAR's material database, which Pluto only names by number.
> - **Nozzles** (vessel and nozzle centrelines collinear), **hanger design**, **stress ratios** (al / hl). These are carried as written and not modelled.
> - **CAESAR's configuration.** Pluto assumes the defaults. A job that raises the maximum bend angle, say, gets an error here that CAESAR would accept.
>
> **To extend this list:** CAESAR's Diagnostics > Error Review describes every message. Send the ones a job raised, or let the build environment reach `docs.hexagonppm.com` (its error code pages).

> [!info]- What the build itself changed (2026-10-24)
> - **The `.cii` writer** takes node numbers up to 8,000,000, CAESAR's limit; it stopped at 999,999 before.
> - **The TOML builder** numbers `g_load` vectors after the most any run has per length. A vector then holds one kind across the model; before, a run with g's only put them on U1 while U1 was per length elsewhere.
> - **The TOML example** modelled a moving anchor as an anchor plus a displacement at the same node: overspecified. It now gives the displacement in all six directions, which is the anchor.

> [!info]- Verification (2026-10-24, Linux)
> - **The review:**
>   - a test model trips each rule on its own;
>   - the clean model trips none;
>   - the CAESAR fixture, every public sample and the probe re-exports raise only what applies (s4's weldolets with two pipes, s11's 0.01 wall and two zero-length elements).
> - **The workbook:** each test model, written as a workbook and recalculated by LibreOffice, counts its rule on Start. The clean one counts none. Every sample workbook stays at 0 on the red checks.
> - **Excel:**
>   - every rule's conditional-format formula stays short; the long ones are worked out per row on the hidden PlotCalc sheet;
>   - Open XML SDK schema valid;
>   - no formula Excel refuses.
