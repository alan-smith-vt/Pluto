---
title: A SAP frame model as a STAAD.Pro .std (Build-Frame -Std)
status: draft
created: 2026-11-05
---

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

# Frame model → STAAD.Pro `.std`

`Build-Frame.ps1 -Std` writes `<out>.std` from any frame input: a workbook, an `.s2k` or a `.frame.json`.

**Code:**
- `FrameStd` is the writer.
- `FrameStdSections` gives a section's properties.
- `FrameStdProbe` writes the STAAD probe.

**The rule:** exact or refused ([[vault/decisions/2026-11-05-frame-staad|2026-11-05-frame-staad]]).
- Every refusal comes at once on `[refuse]` lines. Refusals with the same reason are one line naming the objects (12
  at most, then a count).
- What leaving out cannot change in the structure is a `[notice]`, also listed in the file's header comment.

**Status: draft.** The forms below come from excerpts of STAAD's Technical Reference, not the pages themselves. STAAD
has not yet read a file from this writer. The STAAD probe (end of this note) confirms the open points.

> [!info]- The file, in order
> 1. `STAAD SPACE`, then comments:
>    - the source;
>    - every SAP label that is not its own STAAD number (text labels, plates renumbered after members, links as
>      truss members);
>    - the load numbers of the SAP cases and combinations;
>    - every notice.
> 2. `INPUT WIDTH 79`, then `UNIT` from SAP's unit set. Length: `INCHES FEET MMS CM METER`. Force: `KIP POUND KNS
>    NEWTON KG MTON`.
> 3. `JOINT COORDINATES`, `MEMBER INCIDENCES` (frames, then truss links), `ELEMENT INCIDENCES SHELL` (areas).
> 4. `START GROUP DEFINITION`.
> 5. `DEFINE MATERIAL START` … `END DEFINE MATERIAL`.
> 6. `MEMBER PROPERTY AMERICAN`, `ELEMENT PROPERTY`.
> 7. `CONSTANTS`: `MATERIAL`, `BETA`.
> 8. `MEMBER OFFSET`, `MEMBER RELEASE`, `MEMBER TRUSS`, `SUPPORTS`.
> 9. `DEFINE REFERENCE LOADS`, the primary loads, `LOAD COMBINATION`.
> 10. `PERFORM ANALYSIS` (or `PDELTA 30 ANALYSIS SMALLDELTA`), then:
>     - `PRINT JOINT DISPLACEMENTS LIST ALL`;
>     - `PRINT MEMBER FORCES ALL`;
>     - `PRINT SUPPORT REACTIONS`;
>     - `PRINT ELEMENT JOINT STRESSES LIST ALL` with plates;
>     - `FINISH`.
>
> Lines longer than 76 characters continue with ` -`. Id lists use `a TO b` for runs.

> [!info]- Geometry and ids
> - **Axes.** STAAD's Y is up. A SAP point (X, Y, Z) is written (X, Z, −Y), a proper rotation, so forces and moments
>   turn the same way.
> - **Ids** (`SapLabels`):
>   - An integer label keeps its number; any other label gets the next free one.
>   - STAAD members and plates share one sequence. Areas keep their numbers unless one is a frame's; then all areas
>     follow the frames.
>   - Axial links follow as truss members.
> - **Member axes.** SAP's local 1 / 2 / 3 are STAAD's x / y / z. STAAD's BETA-0 axes (TR G.4.3 [V]):
>   - z is horizontal (x × Y) and y points up;
>   - a vertical member (x along ±Y) has z along +Z, so y = z × x.
>
>   SAP's local 2 is the vertical projected off the member (+X for a vertical one), turned by the frame's angle. So:
>   - a non-vertical member's BETA is SAP's angle;
>   - a vertical member pointing up gets 180° more;
>   - one pointing down gets the angle.
>
>   `FrameStd.Beta` works this out for every member; BETA is right-handed about x **[U]**. SAP's advanced local axes
>   are refused.

> [!info]- Sections and materials
> - **Table.** `TABLE ST <name>` for W shapes (`W12X26`) and channels of a whole weight (`C10X30`) [V].
>   - Other families' STAAD names differ from SAP's and are not confirmed: HSS `HSST…`, pipes `PIPS` / `PIPX`, angles
>     `L40404` in sixteenths, tees by their parent W.
>   - A table section with property modifiers is written as numbers instead, with a notice.
> - **Numbers.** `PRIS AX IX IY IZ AY AZ YD ZD` [V], from:
>   - SAP's own computed properties when the section row has them;
>   - else the dimensions, worked out as SAP does (`FrameStdSections`, below).
>
>   The frame's modifiers multiply A, J, I22, I33, AS2 and AS3. A zero shear area is left out (shear deformation
>   ignored, as in SAP). YD / ZD = t3 / t2 (temperature gradients need them).
> - **Refused:** a section with a product of inertia I23 (angles, and General sections that carry one). STAAD's
>   prismatic property has none.
> - **Materials.** `ISOTROPIC` with E, POISSON (when 0.01–0.499), G, DENSITY (SAP's unit weight), ALPHA [V]. Steel adds
>   `TYPE STEEL` and `STRENGTH FY FU RY RT` (RY = EffFy / Fy) [V example].
>   - A frame whose weight modifier differs from its area modifier gets a copy of its material (`<name>_W2`) with
>     unit weight × WeightMod / AMod. STAAD's self weight is DENSITY × AX, and AX carries AMod.
>   - A non-isotropic material is refused.
>
> **Properties from dimensions** (SAP 24.2's own values for every shape of the probe, all equal):
>
> | Shape | A, I | J | AS2 / AS3 |
> |---|---|---|---|
> | Rectangular | b d, b d³/12 | rectangle series | 5/6 A, 5/6 A |
> | Circle | π d²/4, π d⁴/64 | 2 I | 0.9 A |
> | Pipe | annulus | 2 I | A/2 (0.1 % off SAP) |
> | Box/Tube | hollow rectangle | Bredt 2 tf tw (b−tw)²(d−tf)² / (b tw + d tf − tw² − tf²) | 2 d tw, 2 b tf |
> | I (unequal flanges) | three rectangles | Σ b t³ [1/3 − 0.21 (t/b)(1 − (t/b)⁴/12)], web between the flanges | d tw, 5/6 (b tf + b2 tf2) |
> | Channel | three rectangles | as I | d tw, 2 b tf |
> | Tee, Double angle | rectangles | as I, **estimated** (SAP adds a junction term: 1.7 %, 2.9 %) | d tw, 5/6 b tf; 2 d tw, 2 w tf |

> [!info]- Releases, offsets, links, supports
> - **Releases.** SAP's P V2 V3 T M2 M3 at I / J are STAAD's `FX FY FZ MX MY MZ` at START / END, or BOTH when the
>   same [V]. A partly fixed DOF is its spring, `KMZ k` (rotations per degree [V]: SAP's k-in/rad × π/180).
> - **Offsets** (`MEMBER OFFSET`, global, from the joint to the member end; a rigid link [V]):
>   - Cardinal points: bottom / top center of sections symmetric about local 3 (±d/2 along local 2), and middle center
>     / centroid / shear center of doubly symmetric ones. Others are refused.
>   - Joint offsets: local or global, equal at both ends and across the axis only. Others are refused.
>   - SAP's `Transform=No` writes none (SAP then ignores them).
>   - End lengths of type User and rigid-zone factor 1 add `LengthI` along x at START and −`LengthJ` at END. Any other
>     factor is refused.
>
>   **Loads on rigid end zones.** STAAD loads only the clear length; positions are measured from the offset end [V].
>   The part of a load inside a zone, self weight included, goes to that end's joint as a force and its moment. This
>   assumes SAP loads the full joint-to-joint length **[U: the probe's part 5]**.
> - **Links.**
>   - Any nonlinear link refuses the model (each type with its links).
>   - A grounded linear link adds its DOFs (fixed or KE) to its joint's support, in the link's axes.
>   - A two-joint link with only U1 (KE > 0) is a truss member: material `PL_LINK` (E 1, density 0, α 0), `PRIS AX` =
>     KE × L. Any other two-joint or zero-length link is refused. A link with a weight is refused when a pattern has
>     self weight.
> - **Supports** gather restraints (in the joint's local axes), springs (row CoordSys GLOBAL or Local) and grounded
>   links:
>   - In global axes when every part reads there: `FIXED`, `PINNED`, or `FIXED BUT <free> K… <springs>` (releases
>     before springs, rotational springs per degree [V]).
>   - Else `INCLINED f1 f2 f3`, the joint-to-reference offset along one of the parts' axes. The inclined y / z are
>     those of a BETA-0 member along it [V].
>   - Parts no STAAD frame holds together are refused.
>   - A plane where both DOFs act alike may turn freely.

> [!info]- Loads, cases, combinations
> - **One reference load per pattern** (`LOAD Rn LOADTYPE … TITLE <pattern>` [V]). Patterns with nothing to load are
>   left out.
>   - `SELFWEIGHT Y −k` for a self-weight multiplier k.
>   - `JOINT LOAD` (global, rotated).
>   - `MEMBER LOAD`:
>
>     | SAP | STAAD |
>     |---|---|
>     | Gravity | `GY` −w |
>     | X / Y / Z | `GX` w, `GZ` −w, `GY` w |
>     | local 1 / 2 / 3 | `X` / `Y` / `Z` |
>     | projected | `PX` / `PZ` / `PY` |
>     | uniform | `UNI w d1 d2` |
>     | varying | `TRAP w1 w2 d1 d2` |
>     | point | `CON P d` |
>     | moments | `CMOM` / `UMOM` |
>
>     Varying or projected distributed moments are refused.
>   - `TEMPERATURE LOAD`: `TEMP f1 f2 f4` (sic: f3 is the strain form [V]). Uniform → f1; gradient 2 × t3 → f2
>     (top +y minus bottom); gradient 3 × t2 → f4 (+z side minus −z **[U: sign]**).
>   - A load by joint pattern, in another coordinate system, or from SAP's automatic lateral loads is refused.
> - **Ground displacements.**
>   - They go in the primary loads, as `SUPPORT DISPLACEMENT LOAD` with each case's factor. Rotations are in degrees
>     [V]. The excerpt did not show whether reference loads take them.
>   - Only restrained DOFs move (SAP ignores a free one). Through a spring, or at an inclined support, they are
>     refused.
>   - STAAD takes them in 4 load cases at most [V].
> - **One primary load per static case** (`LoadPattern` items only; `Accel` is refused): `LOAD n LOADTYPE …` with
>   `REFERENCE LOAD R1 f …` [V].
>   - A nonlinear case continuing another (its `InitialCond`) holds the whole chain's patterns.
>   - A linear case on another's stiffness writes its own loads (the same stiffness here). On a P-Delta stiffness it
>     is refused.
>   - P-Delta in some cases and not others is refused: STAAD's analysis takes every case. Large displacements are
>     refused.
>   - Modal and other cases are listed, not written.
> - **Combinations** follow the primary loads, numbered after them [V]:
>   - `LOAD COMBINATION n name` for Linear Add; nested Linear Adds are expanded.
>   - `LOAD COMBINATION SRSS n name` and `ABS` for SRSS and Abs Add, of cases only.
>   - Envelope, Range Add, and anything holding an unwritten case or combination, are listed.
> - **LOADTYPE** from SAP's design type: Dead, Live, Roof Live, Seismic-H, Wind, Snow, Temperature, otherwise None [V].

> [!info]- Refused or listed tables
> **Read** (handled above):
> - PROGRAM CONTROL and ACTIVE DEGREES OF FREEDOM (all six, else refused);
> - joints, frames, sections 01, materials 01 / 02 / 03A;
> - links, areas, groups, patterns, the five load tables, cases 1 / 2 / 4, combinations;
> - FRAME AUTO MESH ASSIGNMENTS.
>
> **Automatic mesh.** A frame meshed at joints or frames is refused when a joint lies inside it, or a frame crosses it.
>
> **Refused when not empty**, as STAAD would analyse another structure:
> - constraints;
> - solids, cables, tendons;
> - frame hinges and tension/compression limits;
> - coupled springs, advanced local axes;
> - area, solid, cable and link loads, and other joint / frame load tables (gravity, strain, deformation …);
> - area thickness overwrites, springs, edge constraints, offsets, stiffness modifiers, auto mesh;
> - line springs, panel zones, staged construction, auto wind / seismic.
>
> **Silent:** design, output, display, mass, functions, other analysis cases, bridge, rebar.
>
> **Listed** in a notice: anything else non-empty.

> [!info]- The STAAD probe (`-StdProbe <folder>`)
> `staad-probe.s2k` and `staad-probe.std`: one model, every part analysable, for SAP2000 and STAAD.Pro alike. Each
> part is its own cantilever or short frame, 100 in apart:
>
> | Part | Checks |
> |---|---|
> | 1 | the BETA sign (unequal I at angle 30) |
> | 2, 3, 21 | vertical members up and down |
> | 4 | an inclined channel with local, projected and partial loads |
> | 5 | rigid end zones with loads inside them |
> | 6, 7 | cardinal point 8 and joint offsets |
> | 8, 19 | partial fixity and a hinge |
> | 9 | springs |
> | 10, 11 | temperature (uniform and both gradients; fixed-fixed and cantilever) |
> | 12 | an inclined support |
> | 13, 14 | a grounded link and an axial link |
> | 15 | a settlement with a rotation |
> | 16, 17 | a tip moment and property modifiers |
> | 18 | a plate |
>
> Import the `.s2k` in SAP, run every case, and export joint displacements and reactions. Open and run the `.std`
> in STAAD. The joint displacements and reactions should agree (the plate by its element only).
>
> **Checked in development:**
> - every joint rotated back equals SAP's;
> - every member's STAAD axes, BETA applied right-handed, equal SAP's local 2 / 3 to 1e-9;
> - per pattern, the written joint and member loads (and self weight) give SAP's total force and moment about the
>   origin.
>
> This held for the probe (95 checks), an adjusted SAP export probe (105) and the rack with linear links (44).
> **Not yet run in STAAD.**
