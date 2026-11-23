---
title: A SAP frame model as a STAAD.Pro .std (Build-Frame -Std)
status: current
created: 2026-11-05
---

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

# Frame model → STAAD.Pro `.std`

`Build-Frame.ps1 -Std` writes `<out>.std` from any frame input: a workbook, an `.s2k`, a `.frame.json`, or a CAESAR
model it seeds ([[vault/format/pipe-to-frame|pipe-to-frame]]). A seed is refused when it has a tie to a CNODE at its
node or a zero-length expansion joint (zero-length links), or any nonlinear support (nonlinear links): see Links below.

**And back:** `Build-Frame.ps1 -In <x>.std` reads a `.std` the writer made, after edits in STAAD too, into a frame
model again (2026-11-23; "Reading a .std back", below).

**Code:**
- `FrameStd` is the writer.
- `FrameStdSections` gives a section's properties.
- `FrameStdProbe` writes the STAAD probe.
- `StdToFrame` is the reader. It works over `StaadInput` in `scripts/lib/readers/ANLReader.cs`: the `.anl` reader's own
  parsers, extended to read a `.std`'s input commands.

**The rule:** exact or refused ([[vault/decisions/2026-11-05-frame-staad|2026-11-05-frame-staad]]).
- Every refusal comes at once on `[refuse]` lines. Refusals with the same reason are one line naming the objects (12
  at most, then a count).
- What leaving out cannot change in the structure is a `[notice]`, also listed in the file's header comment.

**Status: current** (STAAD.Pro 2025 and SAP 24.2, three probe rounds, 2026-11-07). The forms came from excerpts of
STAAD's Technical Reference ([V] below means the excerpt). STAAD.Pro 2025 has read every command the writer uses, and
the STAAD probe (end of this note) confirmed the conventions against SAP's own results. Two differences remain, both
written with a notice: plates (element formulation, large on coarse meshes) and channels, tees and double angles
(SAP's shear-center twist).

> [!info]- The file, in order
> 1. `STAAD SPACE`, then comments:
>    - the source;
>    - every SAP label that is not its own STAAD number (text labels, plates renumbered after members, links as
>      truss members), and every material or group whose STAAD name is not its SAP label;
>    - the load numbers of the SAP cases and combinations;
>    - every notice.
> 2. `INPUT WIDTH 79`, then `UNIT` from SAP's unit set. Length: `INCHES FEET MMS CM METER`. Force: `KIP POUND KNS
>    NEWTON KG MTON`.
> 3. `JOINT COORDINATES`, `MEMBER INCIDENCES` (frames, then truss links), `ELEMENT INCIDENCES SHELL` (areas).
> 4. `START GROUP DEFINITION`.
> 5. `DEFINE MATERIAL START` … `END DEFINE MATERIAL`.
> 6. `MEMBER PROPERTY AMERICAN`, `ELEMENT PROPERTY`: a line per SAP section, its name in a comment above it
>    (`* SAP section <name>`, `* SAP area section <name>`). A `TABLE ST` line's comment also holds the section's SAP
>    row, which SAP needs to rebuild it.
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
>   `FrameStd.Beta` works this out for every member; BETA is right-handed about x (confirmed by STAAD round 2, part 1). SAP's advanced local axes
>   are refused.

> [!info]- Sections and materials
> - **Table.** `TABLE ST <name>` for W shapes (`W12X26`) and channels of a whole weight (`C10X30`) [V], only for
>   a section SAP took from its own database (`FromFile=Yes`, named by its `SectInFile`). SAP rebuilds any other
>   shape from its dimensions on import, without fillets: the rack fixture's hand-written W10X33 (`FromFile=No`,
>   Area 9.71 in its row) was 9.49 in² in SAP, while STAAD's table gave 9.71. Such a section is `PRIS` with the
>   dimensions' values; its row's are used only where they agree with them (SAP's own export), and a row that
>   disagrees gets a notice.
>   - **Since (2026-11-23):** more families go by name, in the form STAAD.Pro 2025's own save writes them (the
>     names probe). It writes a `MEMBER PROPERTY` block per database, and the database's own table names:
>
>     | SAP catalog section | STAAD 2025 |
>     |---|---|
>     | channel of a fractional weight `C15X33.9` | `MEMBER PROPERTY 'US (AISC 2023).db3'`, `TABLE 'Standard Channel' ST 'C15X33.9'` |
>     | HSS box `HSS24X12X3/4` | the same database, `TABLE 'HSS Rectangle' ST 'HSS24X12X.750'` (the EDI name, from SAP's library file) |
>     | HSS round `HSS5.500X0.375` | `TABLE 'HSS Round' ST 'HSS5.5X.375'` |
>     | tee `WT22X131` | `TABLE 'W' T 'W44X262'`, the W it is cut from |
>     | double angle `2L2-1/2X2-1/2X5/16`, equal legs, no spacing | `MEMBER PROPERTY 'US (AISC V16.0).db3'`, `TABLE 'Angle' LD 'L2-1/2X2-1/2X5/16'` |
>
>     W shapes and whole-weight channels stay in `MEMBER PROPERTY AMERICAN`. A single angle is refused (its I23).
>     Unequal legs, a spacing, MC channels and pipes by another name are written as numbers: STAAD's form for them
>     has not been seen. Bentley's help gives older names (`L505010`, `PIPX20`), which STAAD 2025's save does not use.
>   - A table section with property modifiers is written as numbers instead, with a notice.
> - **Numbers.** `PRIS AX IX IY IZ AY AZ YD ZD` [V], from:
>   - SAP's own computed properties when the section row has them;
>   - else the dimensions, worked out as SAP does (`FrameStdSections`, below).
>
>   The frame's modifiers multiply A, J, I22, I33, AS2 and AS3. A zero shear area is left out (shear deformation
>   ignored, as in SAP). YD / ZD = t3 / t2 (temperature gradients need them).
> - **Refused:**
>   - a section with a product of inertia I23 (angles, and General sections that carry one). STAAD's prismatic
>     property has none. An I23 below 1e-9 of I33 + I22 is round-off and counts as 0: SAP's own row for a library
>     W10X33 carries 2.8E-14 (2026-11-23).
>   - a section with modifiers of its own (its row's `AMod` … `WMod` other than 1, 2026-11-23). SAP applies them as
>     well as the frame's, and only the frame's are folded in. Give them to its frames instead.
> - **Channels, tees and double angles are written centroidal, with a notice and a banner** (the user's choice,
>   2026-11-07). SAP 24 twists them about their shear center; STAAD does not (round 2). The banner is a starred
>   block just above `MEMBER PROPERTY AMERICAN`, listing the members: "SHEAR CENTER NOT MODELED …". Bending and axial
>   results match SAP's; the twist and the joint movement it causes do not.
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
>   assumes SAP loads the full joint-to-joint length (confirmed by round 2, part 5: equal to 0.00002 in). The zones'
>   self weight is worked out from the DENSITY and AX the file gives (STAAD's own self weight is their product), so a
>   read takes it off again to the digit.
> - **Links.**
>   - Any nonlinear link refuses the model (each type with its links).
>   - A grounded linear link adds its DOFs (fixed or KE) to its joint's support, in the link's axes.
>   - A two-joint link with only U1 (KE > 0) is a truss member: material `PL_LINK` (the model's largest E, so STAAD
>     does not warn of a soft material; density 0, α 0), `PRIS AX` = KE × L / E. Any other two-joint or zero-length link is refused. A link with a weight is refused when a pattern has
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
>     (top +y minus bottom); gradient 3 × t2 → f4 (+z side minus −z). Confirmed against SAP in round 3: SAP's `Gradient2` 3 and `Gradient3` −2 on a 100 in cantilever
>     moved its tip U2 −0.065, U3 −0.0975, R2 0.00195, R3 −0.0013 in both programs. SAP's gradients are
>     `Type=Gradient2` / `Gradient3` with their value in `TempGrad2` / `TempGrad3`; any other type name is refused
>     (SAP reads it as a uniform temperature, or drops it).
>   - A load by joint pattern, in another coordinate system, or from SAP's automatic lateral loads is refused.
> - **Ground displacements.**
>   - They go in the primary loads, as `SUPPORT DISPLACEMENT LOAD` with each case's factor. Rotations are in degrees
>     [V]. The excerpt did not show whether reference loads take them.
>   - Only restrained DOFs move (SAP ignores a free one).
>   - The joint is an `ENFORCED` support (`ENFORCED BUT <free DOFs>`). STAAD 2025 stops a model with plates
>     otherwise ("not valid on models with finite elements, unless used in conjunction with ENFORCED support
>     definitions", round 1), and without ENFORCED it takes them in 4 load cases at most.
>   - Refused: a displacement at an inclined support, or at a joint with springs or a grounded link (ENFORCED takes
>     no springs).
> - **Joints nothing connects** (no frame, area or axial link) are left out with their supports and loads, in a
>   notice. STAAD warns of them; in SAP they carry nothing.
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

> [!info]- Reading a .std back (`Build-Frame -In <x>.std`, 2026-11-23)
> `StdToFrame` reads a `.std` this writer made back into a frame model, after edits in STAAD too. STAAD's commands are
> the model, so an edit made there comes back; the comments the writer adds give back what STAAD has no place for:
> SAP's labels; section, area section, material and group names; a table section's SAP row; the SAP version and units.
> Outputs default to `<name>_std.*`, never over the model the `.std` came from.
>
> **The parsing is the `.anl` reader's.** `StaadInput` (in `scripts/lib/readers/ANLReader.cs`) indexes the file with the
> `.anl` indexer and reads joints, incidences, groups and plate thicknesses with its parsers, made able to:
> - take the commands only a `.std` needs marked (`MEMBER TRUSS`, `DEFINE REFERENCE LOADS`, `SELFWEIGHT`, `PDELTA` …);
> - run without progress bars;
> - keep coordinates and thicknesses as written, in doubles;
> - record each group's kind.
>
> Text parsers there, on the same section index, read what an `.anl` never needed:
> - materials, member properties, constants, offsets, releases, trusses and supports;
> - reference loads, primary loads and combinations.
>
> An `.anl` is read as before: the old and new reader give the same output on random number lines, the probe and a
> numbered echo of it.
>
> | STAAD | Frame model |
> |---|---|
> | joint (X, Y, Z) | SAP (X, −Z, Y) |
> | BETA | the frame's angle (SAP's local 2 / 3 are STAAD's y / z) |
> | `PRIS AX IX IY IZ [AY AZ] YD ZD` | a General section (shear areas 0 when absent) |
> | the same SAP section with other numbers | the first one's section, with frame modifiers (mass and weight with AMod) |
> | `TABLE ST`, or STAAD 2025's `TABLE '<table>' ST '<name>'` under `MEMBER PROPERTY '<database>'` | the SAP row in its comment when `-Std` writes this property from it; otherwise (a shape picked in STAAD) SAP's catalog section from its library, the newest AISC file's: an I section, channel, HSS box or round, by SAP's label or EDI name. A single angle is refused, and so is a name SAP's library does not have |
> | `TABLE '<table>' T '<W>'` | the tee cut from it, SAP's WT of half its depth and weight (`W44X262` → `WT22X131`) |
> | `TABLE '<table>' LD / SD '<angle>' [SP s]` | SAP's 2L of that angle whose vertical legs are the long (LD) or short (SD) ones and whose spacing is s; Pluto knows SAP's labels for no spacing only |
> | `ISOTROPIC` | a material, Steel with `TYPE STEEL`, else Other; G must agree with E and POISSON (SAP's isotropic rule) |
> | `MEMBER OFFSET` | end lengths (rigid) along it; across it, equal at both ends, joint offsets (local) |
> | `MEMBER RELEASE` | releases; `K…` partial fixity (rotations per degree back to per radian) |
> | `MEMBER TRUSS` | linear links, U1 = E AX / L (the header's EA / L when it agrees) |
> | plates, `ELEMENT PLANE STRESS` | areas, Shell-Thin or Membrane |
> | `SUPPORTS` | restraints and springs; `INCLINED`: the joint's local axes; `ENFORCED`: restraints |
> | reference loads | load patterns, the zones' self weight taken off the joint loads |
> | primary loads | linear static cases; nonlinear P-Delta from zero under `PDELTA` |
> | a primary load's own loads, support displacements | a load pattern of its own each |
> | `LOAD COMBINATION [SRSS, ABS]` | Linear Add, SRSS, Abs Add |
>
> A number worked out on the way back (a spring per radian, a modifier, a gradient) is the shortest that writes back
> STAAD's digits: 1000, not 999.99999999752.
>
> **Numbers to the file's digits.** Pluto writes 12 significant digits; STAAD.Pro's own save keeps 6. Every check that
> compares a number with one worked out allows half a unit in the last digit the file gives, never more than 1e-5 of
> it. That covers:
> - G against E and POISSON;
> - a truss's EA / L from the header against its AX;
> - the end zones' weight against the joint loads;
> - offsets, and load positions on the clear length.
>
> **Exact or refused.** Any command the writer does not write is refused by name, with where it stood. That covers:
> - another `UNIT` along the file;
> - `CUT OFF`, `ELEMENT LOAD`, `LIN` member loads, `MP` releases, `CDAMP`;
> - a `TABLE ST` with no SAP row when SAP's library is not on the machine or lacks the shape, or the shape is not an I
>   section.
>
> `START JOB INFORMATION`, a record only, is passed over with a notice.
>
> **The check.** The model is written back with `-Std`, and both files are read as STAAD reads them, then compared
> object by object, numbers to the file's digits:
> - each joint, member, plate, property, constant, offset, release, support and group by its id;
> - each material by its name;
> - each primary load through its reference loads and their factors;
> - each combination by its number.
>
> Order, layout, keyword order, comments and prints do not count. A load given in a primary load itself comes back as a
> reference load of its own, and counts as the same. The run says whether the models are the same, or lists the first
> differences.
>
> **Tested here:**
> - The models below come back with every command the same:
>   - the STAAD probe;
>   - the linear rack, also with a catalog W10X33, P-Delta cases and kN / m;
>   - a CAESAR seed with linear supports;
>   - the probe with its comments stripped, which comes back with STAAD's numbers as labels;
>   - the probe in STAAD's layout (four joints a line, job information);
>   - a hand edit: a moved joint, an added load, a released support.
> - The read model's workbook writes the same `.std`.
>
> **STAAD.Pro's own save** (STAAD.Pro 2025, on the production machine, 2026-11-23). A `.std` Pluto wrote, opened in
> STAAD.Pro and saved from its graphical editor:
> - keeps every comment in place, the SAP row above `TABLE ST` too;
> - writes numbers to 6 significant digits (`G 11153.8`, `PRIS AX 3.51084`);
> - reorders PRIS (`AX AY AZ IX IY IZ`), writes `LOAD COMB`, several joints and members a line, and id lists split
>   over lines;
> - rewords the prints and adds `START JOB INFORMATION`;
> - puts a joint load added in a load case directly in its primary load.
>
> The first reader refused both files, holding G to 12 digits. Now an unedited save reads exactly. An edited one (a
> moved joint, a support made fixed, a joint load added) reads with those three changes and nothing else. The probe,
> rounded to 6 digits the same way, reads exactly as well, every converted number clean.
>
> **In SAP2000 24.2** (the same day):
> - **The read-back probe.** It imported with only SAP's own ten design-table errors, the same as the original probe's.
>   Its one warning, a Wind pattern's blank `AutoLoad`, is gone: Wind and Quake patterns now get `AutoLoad=None`. Run,
>   it agrees with STAAD's run of the same `.std` at every joint, 480 displacements and 310 reactions; joints with
>   local axes are compared in those axes, which SAP reports in. The channels agree too: read back they are General
>   sections, which SAP does not twist about a shear center.
> - **Catalog sections.** The read-back rack's W10X33 got SAP's default section, a General one with every property 1,
>   and its columns shortened eight times more than in STAAD (the pipes on it grew the same to every digit). The cause
>   was the row, a test variant naming `AISC16.xml`, which SAP 24 does not have; SAP's own export names its library
>   file by full path, and reads back. A restored row's library file is now checked when the `.s2k` is written
>   ([[vault/format/sap-s2k-tables|sap-s2k-tables]]). A `TABLE ST` shape picked in STAAD, with no SAP row in its
>   comment, was still refused then.
> - **SAP's own W10X33 export, through Pluto both ways** (later the same day): `-Std`, then `-In <it>.std -S2k`,
>   imported into SAP with no error, warning or dialog, the shape still SAP's catalog section. On the way, `-Std` had
>   refused SAP's own row for its `I23=2.8E-14`, round-off now counted as 0.
> - **A shape picked in STAAD** now comes from SAP's library (`AISC15.xml`'s W10X33 entry, from the user). The row
>   Pluto builds is SAP's own to every digit SAP writes, its noise aside
>   ([[vault/format/sap-s2k-tables|sap-s2k-tables]], "The library's entries"). On the production machine, a W12X26
>   picked in STAAD (with no comments, and under W10X33's old comment) read exactly. It imported into SAP as its AISC15
>   catalog section with no error, warning or dialog. SAP's export of it equals Pluto's row in all 38 shared fields,
>   Cw to its 15 digits. A channel was refused, named: the library's element for it is `STEEL_CHANNEL`.
> - **The other steel families** (the same day: an entry of each kind from the user, then two probes).
>   - **SAP's library probe:** SAP 24.2 imported a frame of each kind from its reference alone, and its export gives
>     SAP's own row of each kind. Pluto's rows equal them in every field Pluto writes. SAP also works out a channel's,
>     tee's and double angle's shear center and Cw, which Pluto leaves to it, and a single angle's I23 about its legs
>     (0.4241 on L3X2-1/2X3/16).
>   - **STAAD's names probe:** STAAD.Pro 2025 saved the same shapes in its database form. The names are AISC's: HSS
>     by their EDI names, tees cut from a W, double angles as `LD` of the single angle.
>     - `MEMBER PROPERTY 'US (AISC 2023).db3'`, `TABLE 'W' ST 'W12X26'`;
>     - `TABLE 'Standard Channel' ST 'C15X33.9'`;
>     - `TABLE 'HSS Rectangle' ST 'HSS24X12X.750'`, `TABLE 'HSS Round' ST 'HSS5.5X.375'`;
>     - `TABLE 'W' T 'W44X262'`;
>     - under `'US (AISC V16.0).db3'`, `TABLE 'Angle' ST 'L3X2-1/2X3/16'` and `TABLE 'Angle' LD 'L2-1/2X2-1/2X5/16'`.
>
>     The reader had refused all of it: it read one table per file, and split quoted names.
>   - **Now:**
>     - the reader takes either form, a table per block;
>     - `-Std` writes these families in STAAD's form (Sections and materials);
>     - the read-back check compares a table section by its shape, whichever STAAD table it came from;
>     - a material with no `G` counts as STAAD's E / 2 (1 + ν).
>
>     STAAD's save, its single angle aside, reads exactly. So does SAP's export written by `-Std` (its angle aside).
>     A single angle stays refused: STAAD's angle axes against SAP's are not confirmed.

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
> | 10, 11 | a uniform temperature (fixed-fixed and cantilever) |
> | 22 | gradients 2 and 3 on a cantilever, SAP's `Gradient2` / `TempGrad2` (round 2 tried four guessed spellings on parts 22 – 25) |
> | 12 | an inclined support |
> | 13, 14 | a grounded link and an axial link |
> | 15 | a settlement with a rotation |
> | 16, 17 | a tip moment and property modifiers |
> | 18 | a plate |
>
> Import the `.s2k` in SAP, run every case, and export joint displacements and reactions. Open and run the `.std`
> in STAAD. The joint displacements and reactions should agree (the plate by its element only). The `.s2k` is stamped
> 22.0.0 unless `-SapVersion` gives another; if SAP asks, edit its Version to the one Help > About shows.
>
> **Checked in development:**
> - every joint rotated back equals SAP's;
> - every member's STAAD axes, BETA applied right-handed, equal SAP's local 2 / 3 to 1e-9;
> - per pattern, the written joint and member loads (and self weight) give SAP's total force and moment about the
>   origin.
>
> This held for the probe (95 checks, 111 in round 2), an adjusted SAP export probe and the rack with linear links.

> [!info]- STAAD round 1 (STAAD.Pro 2025 25.00.01 and SAP 24.2, 2026-11-07)
> The rack (its gap and friction links made linear) and the probe ran in both programs. SAP's results came back as its
> Excel export and were compared joint by joint, rotated back.
>
> **STAAD parsed every command without an error:**
> - geometry and properties: `TABLE ST W10X33`, `PRIS`, `MEMBER TRUSS`, `ELEMENT INCIDENCES SHELL`, `ELEMENT
>   PROPERTY … THICKNESS`;
> - materials: `ISOTROPIC` with `TYPE STEEL` / `STRENGTH FY FU RY RT`;
> - `CONSTANTS` (`MATERIAL`, `BETA`), `MEMBER OFFSET`, `MEMBER RELEASE` with `KMY` / `KMZ`;
> - `SUPPORTS` with `K…` springs and `INCLINED f1 f2 f3`;
> - reference loads and `REFERENCE LOAD`, `SELFWEIGHT`, `JOINT LOAD`;
> - `MEMBER LOAD` (`CON`, `CMOM`, `UNI`, `TRAP` in local, global and projected directions), `TEMP f1 f2 f4`,
>   `SUPPORT DISPLACEMENT LOAD`;
> - `LOAD COMBINATION` plain / `SRSS` / `ABS`, the prints.
>
> **The rack agreed where it could be checked:**
> - per case, the total vertical reaction (77.30 vs 77.248 kip, 79.26 vs 79.203, at STAAD's printed 2 decimals);
> - the pipes' thermal growth to every printed digit (0.03754 / 0.41290 / 0.45043 in), so OPE = SUS + T1 and EXP
>   are right.
>
> **Its frames could not be checked.** The wall W1 is one shell element (240 × 180 in, 6 in steel, 73.5 kip, pinned
> at two corners) that every frame ties into, and the two programs' single elements behave very differently:
>
> | (DEAD) | SAP | STAAD |
> |---|---|---|
> | wall thrust at each pin | 2.14 kip | 42.67 kip |
> | top corner sag | 0.00016 in | 0.00171 in |
> | top corner in the wall's plane | 0.00005 in | 0.00096 in |
> | top corner out of it | 0.0080 in | 0.0044 in |
> | column C1 axial | 0.48 kip | 2.93 kip |
>
> A plane-stress solve of the wall alone (in the session, Wilson's incompatible-mode quad) reproduces SAP's one
> element: 2.07 kip thrust, 0.00015 in sag. Refined, the thrust converges to about 21 kip (13 at 2 × 2, 19 at
> 8 × 6). So:
> - SAP's single element, its weight at the corners right above the pins, hardly acts as a deep beam;
> - STAAD's overshoots about 2×;
> - neither is the wall's answer.
>
> Round 2 runs the rack without the wall, and with it meshed 8 × 6, to tell the translation from the mesh.

> [!info]- STAAD round 2 (2026-11-07)
> **The probe ran to the end** with the settled joint `ENFORCED`. Per part, the largest joint-displacement difference
> was at most 0.00002 in for every part but four (STAAD prints 5 decimals):
> - BETA sign (1);
> - vertical members, up and down at angles (2, 21);
> - rigid end zones with loads inside them (5);
> - cardinal point and joint offsets (6, 7);
> - partial fixity and springs per degree (8, 9);
> - uniform temperature (10, 11), the grounded and the axial link (13, 14);
> - settlement with a rotation (15), a tip moment, modifiers with the weight modifier (16, 17);
> - the hinge (19), and every combination (Linear Add, nested, SRSS, Abs Add, OPE from SUS).
>
> The four that differed:
> - **Part 12 (inclined support) agrees too.** SAP reports a joint with local axes in those axes. Projected onto
>   them, STAAD's displacements match to every digit, and the reaction along the support is −1.193 vs −1.188 kip.
> - **Part 13's reaction** is reported differently: SAP leaves the grounded link's fixed DOF out of the joint
>   reaction. Its displacements agree.
> - **Channels (3, 4) twist in SAP and not in STAAD.** SAP 24 puts a channel's torsion axis at its shear center, so
>   a shear along local 2 at the centroid twists it. Part 3's twist matches exactly: V2 0.0964 × e 1.861 in ×
>   L / GJ = 0.004925 rad, SAP 0.004924. The joint's extra movement along local 2 is twist × e. STAAD's `PRIS`
>   member is centroidal and does not twist (R 0). Tees and double angles are monosymmetric the same way.
> - **Gradients (22 – 25):** SAP read `Temperature Gradient 2`, `Temperature Gradient 2-2` and `Temp Gradient 2-2`
>   as `Temperature`, and dropped `Gradient 2` / `3`. SAP's name has to come from its own dialog.
>
> **The rack without its wall** agreed to 1.5 – 3 %. All of it is the W10X33: SAP rebuilt the hand-written section
> from its dimensions (9.49 in², J 0.49), while STAAD's table section is 9.71, J 0.583. The DEAD weights differ by
> exactly that (3.748 vs about 3.807 kip). The section rule above now writes SAP's values.
>
> The rack also has a free X-sway once the wall is gone (pinned column bases). STAAD added a weak spring; SAP's
> nonlinear cases drifted a uniform 0.0033 in, with no force.
>
> **The meshed wall (8 × 6)** brings the plates together:
>
> | | 1 element | 8 × 6 |
> |---|---|---|
> | SAP thrust at each pin | 2.14 kip | 18.54 kip |
> | STAAD thrust at each pin | 42.67 kip | 21.87 kip |
>
> The wall alone in plane stress converges to about 21 kip. Vertical reactions agree (37.54 / 37.55); the pipe
> anchor away from the wall agrees to 0.1 % (17.25 / 17.27 kip-in). Displacements near the wall still differ 5 –
> 10 %: plate formulation and the W10X33 together.
>
> **The probe stopped in STAAD's solver** (support displacement in a model with plates needs `ENFORCED`; now
> written so). STAAD warned of the rack's unconnected joint 20 (now left out) and of the truss links' E of 1 (now
> the model's E).
>
> **SAP read the workbook's gradient names as uniform temperatures.** `Type="Temp Gradient 2"` / `"3"` came back as
> `Temperature`: the probe's cantilever grew by α × 21 × L (20 + 3 − 2) and did not bend. See
> [[vault/format/sap-s2k-tables|sap-s2k-tables]]. Round 2's parts 22 – 25 try four spellings.

> [!info]- STAAD round 3 (2026-11-07)
> - **Gradients, the last open sign, agree.** Part 22 in SAP's own form (`Gradient2` 3, `Gradient3` −2 on a 100 in
>   cantilever) moved its tip U2 −0.065, U3 −0.0975, R2 0.00195, R3 −0.0013 in both programs (the `TEMP` row
>   above).
> - **SAP read all four temperature rows** back as written ([[vault/format/sap-s2k-tables|sap-s2k-tables]]).
> - **The ten "Table key … not found" import errors** came again with SAP's own `TABLES AUTOMATICALLY SAVED AFTER
>   ANALYSIS` row in the file: they are SAP's, not the file's ([[vault/format/sap-s2k-tables|sap-s2k-tables]]).
