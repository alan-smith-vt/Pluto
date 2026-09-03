---
title: SAP tank builder — features, plans, checks
status: current
created: 2026-09-03
---

# SAP tank builder — features, plans, checks

*↑ [[vault/Home|Home]] › [[vault/arms/Arms map|Arms map]]*

`pythonTools/sap/tankbuilder` generates a SAP2000 tank model from a TOML config
(kip, ft, F), `run_sap.py` runs it and hands the results to
[[vault/arms/sap-s2k-to-viewer|SapToPluto]]. This note is the design record for the
features: what each one is in the model, what is still a plan, and how the results are
meant to be checked. Real project numbers live only in the gitignored `configs/*.toml`.

> [!info]- Built (2026-09-03)
> - **Wall courses** `[[courses]]` — height ranges at one thickness each; a mesh ring on
>   every boundary; one shell section per thickness `WALL_Tn`; `COURSE_nn` groups.
> - **Baseplate** `[baseplate]` — polar cap on the base ring (quad rings + centre fan),
>   local 3 up, full head on face `Top`.
> - **Roof** `[roof]` — spherical cap on the top ring from a crown radius; eave ring
>   `ROOF_RING` of (2) equal angles lying as a Z (stacked legs on the wall top, inner leg
>   down flush outside the shell, outer lip up = gutter): SAP `General` section from
>   `section.py`, viewer outline via `<model>.outlines.txt`.
> - **Support layer** `[foundation] mode = "gap"` — fixed `GROUND` joint coincident with
>   every baseplate joint, zero-length compression-only Gap link (I = ground, J = tank),
>   k = subgrade modulus × tributary plan area, one property per ring `GAP_Rnn`; the tank
>   side keeps the rim's tangential restraint only; nonlinear cases `NL_DEAD → NL_HYDRO`
>   (linear `DEAD` / `HYDRO` kept; they see the gap as a two-way spring).
> - `TankModel.ids` (`IdAllocator`) gives every part its own id block per object kind
>   (joint / area / frame / link); the wall claims first, so wall numbering never moves.

> [!info]- Ring wall (plan → built the same day, see below)
> **Model.** A closed polygon of concrete frames on the shell radius, one per spoke. The
> frame joints are the rim's former ground joints, so the gap links now act shell ↔ ring
> wall (compression only; the shell can lift off). Frame axis at the top of the wall, not
> the centroid: loads and supports both sit on the axis, so hoop tension and bending about
> the horizontal axis are unaffected; the vertical eccentricity is ignored.
> **Section.** Rectangular, width C × depth A, concrete `CONC` with E = 57 000 √f'c,
> ν = 0.2, 0.150 kcf (self-weight in `DEAD`).
> **Supports.** `springs` (default): U3 spring k = subgrade modulus × width × arc per
> joint, U1/U2 restrained; or `fixed`. Settlement profiles later apply to these joints
> and to the plate's ground joints, one soil surface.
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
