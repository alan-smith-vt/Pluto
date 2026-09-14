---
title: SAP tank builder — features, plans, checks
status: current
created: 2026-09-03
---

# SAP tank builder — features, plans, checks

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

`pythonTools/sap/tankbuilder` generates a SAP2000 tank model from a TOML config
(kip, ft, F), `run_sap.py` runs it and hands the results to
[[vault/arms/sap-s2k-to-viewer|SapToPluto]]. This note is the design record for the
features: what each one is in the model, what is still a plan, and how the results are
meant to be checked. Real project numbers live only in the gitignored `configs/*.toml`.

> [!info]- Built (2026-09-03)
> - **Shell local axes** (2026-09-04) — every shell is rotated so local 1 runs along a
>   meridian (up the wall, radially out on the baseplate, up the slope on the roof) and
>   local 2 circumferentially: `AREA LOCAL AXES ASSIGNMENTS 1 - TYPICAL`, angle 90 on wall
>   and roof (SAP's default 2 = projection of +Z), the element's azimuth on the flat plate
>   (default 1 = +X). So `F11 / S11 / M11` = meridional and `F22 / S22 / M22` =
>   circumferential on every element, and `run_sap.py` reads the axes back over the OAPI
>   after import (`[axes] local 1 = meridional on all 4104 shells`, TANK-A on SAP 26;
>   `GetTransformationMatrix` is column-major, local 1 = elements 0, 3, 6).
> - **Wall courses** `[[courses]]` — height ranges at one thickness each; a mesh ring on
>   every boundary; one shell section per thickness `WALL_Tn`; `COURSE_nn` groups.
> - **Baseplate** `[baseplate]` — polar cap on the base ring (quad rings + centre fan),
>   local 3 up, full head on face `Top`.
> - **Roof** `[roof]` — spherical cap on the top ring from a crown radius; eave ring
>   `ROOF_RING` = one equal angle on the wall top (horizontal leg outward, other leg down
>   flush outside the shell; the single bottom L, 2026-09-14). `ring_bar = true` adds a flat bar
>   on the horizontal leg, plate 2t thick, the section of every model before 2026-09-14 (the
>   second angle's upstanding lip had been dropped 2026-09-04); those configs carry it explicitly: SAP `General`
>   section from `section.py`, viewer outline via `<model>.outlines.txt`.
> - **Support layer** `[foundation] mode = "gap"` — fixed `GROUND` joint coincident with
>   every baseplate joint, zero-length compression-only Gap link (I = ground, J = tank),
>   k = subgrade modulus × tributary plan area, one property per ring `GAP_Rnn`; the tank
>   side keeps the rim's tangential restraint only; nonlinear cases `NL_DEAD → NL_HYDRO`
>   (linear `DEAD` / `HYDRO` kept; they see the gap as a two-way spring).
> - `TankModel.ids` (`IdAllocator`) gives every part its own id block per object kind
>   (joint / area / frame / link); the wall claims first, so wall numbering never moves.

> [!info]- Ring wall (plan → built the same day, see below)
> **Model.** A closed polygon of concrete frames on the shell radius, one per spoke. Load
> path (2026-09-04): rim joint → `GAP_CONTACT` link (compression only, k = E_c × width × arc
> / depth, the concrete column under the joint) → wall-top joint (the rim's former ground
> joint, `RINGWALL_TOP`) → `RINGWALL` frames → `GAP_SOIL` link (compression only, k =
> subgrade × width × arc) → fixed ground joint (`RINGWALL_GROUND`, where settlements go).
> Pictures: [[vault/arms/ring-wall-load-path|ring-wall-load-path]].
> The tank mates with the wall as it settles and the two move down together (verified on
> SAP 26: rim U3 −0.02743 ft, wall −0.02740 ft under NL_HYDRO). **Joints** (`joints`,
> 2026-09-08): `"elevations"` (default) puts the rim joint at the wall top, the wall
> frame joint at the centroid (z = −A/2) and the ground joint at the base (z = −A), the two
> links with length A/2 (I below J, local 1 = +Z), cardinal point 10 and no insertion
> table — nothing coincident, group `RINGWALL_AXIS`. `"top"` is the earlier layout: all
> three joints at the wall top, insertion point `8 (top center)` drawing the section
> hanging below, `transform = false | true` for SAP's `Transform` (rigid arms joint →
> centroid; the column was misspelt `StiffTransform` until 2026-09-08, so those runs had
> Yes), group `RINGWALL_TOP`. Same vertical answers either way:
> [[vault/arms/beam-offset-study|beam-offset-study]].
> **Section.** Rectangular, width C × depth A, concrete `CONC` with E = 57 000 √f'c,
> ν = 0.2, 0.150 kcf (self-weight in `DEAD`).
> **Supports.** `gap` (default; `springs` still accepted): the soil gap link above, wall-top
> joints held tangentially only (local axes like the rim, so the ring can expand and
> settle); or `fixed`: wall top pinned in U3, no soil link. Settlement profiles later
> apply to `RINGWALL_GROUND` and to the plate's ground joints, one soil surface.
> **Why P ≈ 0 in the ring wall (2026-09-04).** Nothing loads it radially: the gap links
> carry vertical only and the earth pressure is not modelled (user: not at this time). The
> restraint half is done (tangential only). What it shows is self-weight bending between
> the soil links (V2 ≈ 1 kip, M3 ≈ 0.76 kip-ft on TANK-A). Hoop tension arrives with the
> lateral earth-pressure line load below.
> **Not yet.** Lateral earth pressure on the ring wall from the sand pad + product
> surcharge (needs a K_a from the user) — this is the API 650 hoop-tension load.
>
> **Checking the section / rebar** — three levels, the first two are the real ones:
> 1. *Geometry in the viewer.* The section draws at true width × depth. Bars and hoops
>    would need multi-outline POLY sections in the format (one outline per section today)
>    — a small format extension if wanted.
> 2. *Hand check from exported frame forces* (`pythonTools/sap/ringwall_check.py`, to
>    write): hoop tension vs φ f_y A_s of the longitudinal bars; bending vs the bars per
>    face; shear vs the hoops. Reads the SAP frame-force table; prints demand / capacity
>    per member, flags the governing one. Python because it runs on the development side and reads SAP
>    output. Frame forces are not exported to the viewer yet (SapToPluto open thread).
> 3. *SAP concrete frame design* — the section can carry the rebar (ACI 318 beam
>    design), least work, but SAP designs beams/columns, not rings in tension, so it
>    does not replace 2.

> [!info]- Edge refinement (built 2026-09-10)
> `[mesh] edge_size / edge_length / edge_growth / refine` grade the mesh **meridionally**
> at the shell edges: rows from `edge_size` at the edge growing by `edge_growth` per
> element until the coarse size is reached (the band is at most `edge_length`), then the
> uniform rows; `refine` names the edges (`wall_base`, `wall_top`, `roof_rim`,
> `plate_rim`, default all four), `edge_length = 0` (default) is the old uniform mesh and
> keeps the golden byte-identical. Circumferential size stays `n_theta`: the edge
> response is axisymmetric, so 3 in × 35 in quads are fine and no transition triangles
> are needed (`graded_sizes` in `model.py`; cap rings come from `cap_radii`, and
> `baseplate_tributary_area` uses the real ring radii, so the gap-link stiffnesses
> follow). TANK-A at 0.25 ft / ×1.5 / 4 ft: 37 wall rows, 16 cap rings, 4968 shells,
> SAP 26 runs the five cases in 112 s.
>
> **What it showed** (`pythonTools/sap/edge_profile.py <config> [--case] [--band]`:
> corner means per mesh ring at each edge, with the closed-form N_z beside the wall F11):
> - Corner F11 at the wall base and top is a recovery artefact either way: the element
>   means match N_z to 0.5 % on both meshes (fine base element −0.754 vs −0.754), while
>   the corners swing with ν × the hoop gradient through the boundary layer. Read
>   element means or link sums for a force, never a corner at an edge.
> - The **plate-on-soil edge band** (λ = (4D/k)^¼ ≈ 0.7 ft) is invisible to 2.8 ft plate
>   rings and resolved by 0.25 ft ones. With it resolved, under `NL_DEAD` the shell
>   weight splits: 74.7 kip through the rim contact links into the ring wall and 97 kip
>   into the plate's outer three rings of soil springs (coarse mesh: 159.8 kip all into
>   the ring wall). The rim settles 0.0050 ft (coarse 0.0069), the plate bends down to
>   it over ~1.2 ft and rotates the wall foot: base M11 −0.138 kip-ft/ft (±3.3 ksi face
>   stress in the 1/2 in plate) and a hoop tension band of +3.5 kip/ft at 0.6 ft. The
>   split is governed by the equal 170 kcf subgrade under ring wall and pad and by the
>   linear springs, so it is a modelling assumption to settle, not a mesh question.
> - Wall top and eave: converged. F11 at the top −0.295 vs −0.287; ring P 5.23 kip
>   (coarse 5.07); roof rim F22 7.2 kip/ft.
> - `NL_HYDRO` base: M11 +0.58 at the base and −0.32 at 1.2 ft, i.e. the resolved plate
>   restrains the foot rotation more than the coarse model's 0.148 (pinned 0, fixed 1.33).
> Link forces are still OAPI-only (`Results.LinkForce`, recipe in `offset_study.py`).
>
> **Plate bearing on the ring wall** (`[ringwall] plate_bearing = true`, 2026-09-10, needs
> `joints = "elevations"`). The plate joints over the ring wall width (r ≥ R − C/2, rim
> excluded) lose their `GAP_Rnn` pad link and ground joint and get: an arm joint under
> them on the centroid line (z = −A/2), a `RINGWALL_ARM` frame of the ring wall section
> from the spoke's axis joint, chained outer → inner so arms never overlap, and a
> `GAP_BEARING` contact link (I = arm, J = plate, k = E_c × tributary area / A). Groups
> `PLATE_BEARING` (joints) and `RINGWALL_ARM` (joints + frames). Run as `TANK-A-bearing`
> (fine mesh, settlement off, SAP 26, 37 s): every `GAP_BEARING` reads 0 under `NL_DEAD`,
> the plate lifts off the concrete and bridges from the pad ring at r = 32.44 to the wall
> foot; rim → ring wall 132.6 kip, pad 40 kip through that one ring. Study and figures:
> the Notes vault, `<project notes>/Tank Base Support Study.md`
> (`base_support_figures.py` draws them from a results dump + `Results.LinkForce`).
> **Baseplate overhang** (`[baseplate] overhang`, ft beyond the shell mid-surface, 2026-09-10):
> one ring of quads from the rim out to R + overhang, joints at z = 0 as ring n_r + 1 of the
> plate (radius appended to `cap_radii_of`, tributary areas re-cut to the plate edge), section
> BASEPLATE, no fluid face, group `PLATE_OVERHANG`. They are plate joints like any other, so
> the foundation gives them a link and `plate_bearing` puts them on the concrete; the arm
> chain now runs per side of the axis joint (inward rings and the outward ring each start
> from it). Validation: overhang must not pass the ring wall's outer face. TANK-A: the
> drawings give 9 in from the ring wall inner face (r = 33.0 ft) to the plate edge, i.e.
> 0.125 ft beyond the shell, run as `TANK-A-overhang` (bearing + overhang).
> **Sand cushion** (`[ringwall] cushion_modulus` ksf + `cushion_thickness` ft, needs
> `plate_bearing`, 2026-09-10): the plate joints over the ring wall bear through the
> cushion instead of the concrete column, k = E / t × the joint's own tributary area, one
> link property per plate ring (`GAP_BEAR_Rnn`), and the shell-line `GAP_CONTACT` gets the
> same treatment. TANK-A at E = 1500 ksf / 2 in: 1.7 to 12 kip/ft per joint against 0.34
> for the pad spring beside them (5 to 36 ×, was 2000 ×). Run as `TANK-A-cushion`
> (overhang + cushion). Motivation and results: the Notes base support study.
> **Ring wall subgrade** (`[ringwall] subgrade_modulus` kcf, 2026-09-10; 0 = the foundation
> value): the soil gap link under the ring wall takes its own modulus, the pad springs keep
> `[foundation] subgrade_modulus`. Motivation: the sensitivity runs (`-ks85`, `-ks42`) showed a
> single value cannot settle the pad and the ring wall differently under product. Run as
> `TANK-A-rw340` (overhang + ring wall soil at 340 against the pad's 170).
> **Pad spring zoning** (`[foundation] zone = "none" | "step" | "boussinesq"`, `zone_width` ft,
> `zone_factor`, 2026-09-11): scales the pad subgrade by plate ring. `step` = Bowles' doubled
> edge springs (Foundation Analysis and Design 5e, 10-5 and 10-12): rings at r >= R - width at
> factor x ks. `boussinesq` = the inverse of the flexible-circle half-space dish, pi / (2 E(r/R))
> (`model.ellipe`, `boussinesq_dish`), 1.0 at the centre to 1.571 at the rim, so a uniform pressure
> settles the plate in the elastic dish (edge 0.64 x centre) instead of flat. Pad springs only;
> the ring wall soil link keeps `[ringwall] subgrade_modulus`. Factor stored per `GAP_Rnn` as
> `zone_factor`. Calibration runs on a stripped base (stub wall 0.25 x 0.001 ft, no roof,
> weightless ring wall, fluid weight scaled to 3.14 ksf on the plate): `TANK-A-cal-uniform`,
> `-cal-step2` (3.4 ft band at 2 x, ring wall 340), `-cal-bouss` (ring wall 267);
> `pad_zone_study.py <configs> --out <dir>` prints the dish per ring against Boussinesq and draws
> `pad-zone-calibration.svg`. Findings: the Notes base support study. Live models with it:
> `TANK-A-bq` (overhang + graded pad, ring wall 170) and `-bq-rw267 / -rw340 / -rw850 /
> -rw1700 / -rw4250` (ring wall soil 1.57 to 25 × the pad); `ringwall_sweep.py <configs>
> [--out <dir>]` tabulates the wall foot, the plate over the concrete edge and the load split per
> model and draws `ringwall-sweep.svg`. `foot_section_figure.py <config>[=label] ... --out <dir>` draws the
> wall foot section (sand, ring wall, wall to scale) with the deflected plate, V13 and M11 of
> several models overlaid on one radius axis (`foot-section-<case>.svg`, `--name` to override).
> Series colours in the study figures come from `study_palette.py` (one colour per run by model
> name: original blue, graded teal, 16 × rim amber, pad 42.5 purple, sweeps on gradients between),
> so a run keeps its colour across `pad_zone_study`, `dish_figure`, `foot_section_figure` and
> `foot_springs_figure`.
> **Ring wall face links** (`[ringwall] soil_links = "faces"`, 2026-09-11; needs `joints =
> "elevations"`, `plate_bearing`, `support = "gap"`): the one `GAP_SOIL` link under the axis joint
> becomes two at the inner and outer faces (r = R ∓ C/2, z = −A/2), k/2 each, on joints that
> continue the `RINGWALL_ARM` chain on each side (an arm joint already at a face is reused; the
> new frames are block `soil_face_arm`, group `RINGWALL_SOIL_FACES` holds the face joints, the
> ground joints join `RINGWALL_GROUND`). Same vertical stiffness, rotational bearing stiffness
> k C²/4 per spoke; the point spring let the ring roll freely (0.028° at 25 × pad, halved with
> faces). **Concrete-edge refinement** (`[mesh] refine` entry `"concrete_edge"`, needs a ring
> wall): the plate rings are graded inward from the concrete's inner face (edge_size, growth,
> edge_length as for the rim) and the plate over the concrete is meshed at about edge_size.
> Runs: `TANK-A-bq-rw4250-f` (both), `-f-ks85`, `-f-ks42` (pad softened, ring wall held):
> concrete-edge plate stress 12 / 22 / 36 ksi. Notes base support study. The graded pad needs the ring wall soil at ≥ 1.57 × pad
> with it, or the rim band out-stiffens the concrete's support.
> **Centre tributary area** (2026-09-11): the centre joint's spring uses the consistent share of
> its triangle fan (a third of each triangle) instead of the bisector disc, ring 1 gives up the
> difference, the areas still sum to pi R². The disc had left the centre spring 1.33 × too soft
> (one joint; a dimple at the centre of every settlement plot). Golden regenerated (two link
> values).
> First `run_sap.py` attempt failed at `OpenFile` (returned 1) and SAP went down; the arms
> were overlapping collinear frames then and the user's GUI session held the instance —
> the chained rebuild in a fresh instance imported cleanly, cause not isolated.

> [!info]- Ports (plan)
> A nozzle is geometry: a hole in the shell, a reinforcing pad, a stub. The regular
> cylindrical mesher cannot do it; it needs a local unstructured step: remove the shells
> inside an opening (centre angle, elevation, diameter), rebuild the ring of elements
> around it so the hole is round, optionally a pad as a thicker ring of shells and a stub
> as a short cylinder. Config `[[ports]]` with elevation, angle, diameter, pad thickness,
> pad width and a local element size (peak stress around the hole needs refinement).
> Groups `PORT_nn`. A day or two; worth it because nozzle stresses and settlement
> interact at the bottom course.

> [!info]- Dents and imperfections (plan → built the same day)
> A dent is an imperfection, not geometry: the mesh stays, the joint coordinates move.
> Radial offset = depth × f(ρ), ρ = elliptical distance from the dent centre in (arc,
> height), f = cos²(π ρ / 2) inside ρ ≤ 1, zero outside — C¹ at the edge, no kink.
> Config `[[dents]]` (angle_deg, elevation, depth, width, height); group `DENT_nn` =
> wall shells with a corner inside the footprint. The same offset mechanism is the
> path for a **measured out-of-roundness survey** (radius by angle and elevation) and
> for a tank already distorted by settlement.
>
> **Picking the dent location.** Options: (a) a single-point picker in the viewer that
> turns the pinned readout (world xyz → angle, elevation on the shell) into a
> `[[dents]]` snippet on the clipboard; (b) a predicate (Phase A) selecting a region of
> shells, then the dent centred on the region's centroid; (c) type the numbers. Dents
> and ports are *inputs* to the generator, predicates select *results*, so (a) is the
> right tool: one click, one snippet, no round trip through the sidecar. (b) is right
> for "dent everything the survey flagged". Neither is built; the sample dent is typed.

> [!info]- Table families still unverified against the SAP2000 importer
> Frame connectivity / section (incl. `General`), link property / gap / assignment,
> nonlinear case rows, joint springs, concrete material. They were written from the
> SAP2000 table set without a sample file to copy; the first real `run_sap.py` import
> of a model that has them is the test. If one is rejected, the field names are the
> suspect, not the values.
