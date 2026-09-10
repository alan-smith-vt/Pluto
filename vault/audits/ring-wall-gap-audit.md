---
title: Ring wall gap elements — GUI audit
status: current
created: 2026-09-08
---

*↑ [[vault/Pluto|Home]] › [[vault/audits/Audits map|Audits map]]*

How to check the two gap-link families around the concrete ring wall inside the SAP2000 26
GUI, what each check should show, and what the model facts are. The load path itself is
drawn in [[vault/arms/ring-wall-load-path|ring-wall-load-path]]; the side-by-side proof
that the insertion-point offset is drawing-only is
[[vault/arms/beam-offset-study|beam-offset-study]]. Model: `pythonTools/sap/models/TANK-A-ringwallC/TANK-A-ringwallC.sdb`
(the 2026-09-08 build with the chain at true elevations; `models/TANK-A` is the earlier
coincident layout, same ids). Open the `.sdb`, or File › Import › SAP2000 .s2k. Units kip, ft.

> [!info]- What is there (from the .s2k, n_theta = 72)
>
> One chain per spoke k = 1…72, three joints at three elevations (`[ringwall] joints =
> "elevations"`, default since 2026-09-08; `"top"` puts all three at z = 0 with insertion
> point 8, the layout of `models/TANK-A`):
>
> | object | ids | z | notes |
> |---|---|---|---|
> | tank rim joint (`BASE_RING`) | `k` | 0 | local axes X tangential / Y radial (`AngleA`); restraint U1 only |
> | `GAP_CONTACT` link | `793 + k` | 0 → −A/2 | I = wall `4827 + k`, J = rim `k`; k = E_c·C·arc/A = **439 730 kip/ft** |
> | wall joint (`RINGWALL_AXIS`) | `4827 + k` | −A/2 = −1.875 | same local axes as its rim joint; restraint U1 only; the `RINGWALL` frame node at the section centroid |
> | `RINGWALL` frames | `73 … 144` | −A/2 | rectangular 1.25 × 3.75 ft, `CONC`; cardinal point 10, no insertion table |
> | `GAP_SOIL` link | `865 + k` | −A/2 → −A | I = ground `4899 + k`, J = wall `4827 + k`; k = ks·C·arc = **623.5 kip/ft** |
> | ground joint (`RINGWALL_GROUND`) | `4899 + k` | −A = −3.75 | fully fixed; `SETTLE` ground displacement goes here |
>
> Both link properties: `LinkType = Gap`, `DOF = U1` only, `NonLinear = Yes`, `Open = 0`,
> `TransKE = TransK` (the linear cases see a two-way spring, so only `NL_*` results
> count). A link's local 1 runs from I to J, so with I the lower joint it is **global +Z**
> (a zero-length link defaults to +Z too); the gap deformation is d = u_J − u_I along
> local 1, so settling of the upper joint is a closing (compressive) deformation. Plate
> gap links `GAP_R00…` are the same construction, zero length, under the baseplate.

## GUI checks, in order

Each numbered step is one thing to look at and the answer it should give. Nothing in the
chain is coincident any more, so any joint or link can be clicked in an elevation view;
groups are still the quickest way to select a whole family.

> [!info]- 1. Find the objects (Select › Select › Groups)
>
> `Select › Select › Groups…` lists `BASE_RING`, `RINGWALL_AXIS`, `RINGWALL_GROUND`,
> `RINGWALL`. Pick one and the status bar bottom-left reports the count: 72 joints (or
> 72 frames). `View › Set Display Options (Ctrl+E)`: tick **Joints › Labels, Restraints,
> Local Axes**, **Links › Labels, Local Axes**, **Frames › Labels**. In an elevation view
> (`View › Set 2D View`, XZ plane) each spoke reads top to bottom: rim joint, contact
> link 1.875 ft long, wall joint, soil link 1.875 ft long, ground joint. A right-click on
> any of them opens its *Information* form (connected links and frames, restraints,
> local axes, property). `Select › Select › Labels…` still picks by name.

> [!info]- 2. Link property definitions (Define › Section Properties › Link/Support Properties)
>
> `GAP_CONTACT` and `GAP_SOIL` › **Modify/Show Property**. Expect: type Gap, mass and
> weight 0, directional properties: **U1 only** ticked, **NonLinear** ticked. Under
> *Modify/Show for U1*: Effective Stiffness 439 730 (contact) / 623.5 (soil) kip/ft,
> Stiffness the same, Opening 0. Anything ticked in U2 / U3 / R1–R3 is wrong: the rim is
> meant to slide radially on the wall (friction not modelled) and the links must not
> carry moment.

> [!info]- 3. Link local axes (Display › Show Misc Assigns › Link/Support › Local Axes)
>
> Also `Assign › Link/Support › Local Axes` with a `GAP_SOIL` link selected. Expect
> **local 1 = global +Z** on every ring wall link (red arrow up, pointing from I to J)
> with no advanced axis assignment. `Display › Show Tables › Model Definition ›
> Connectivity Data › Link Connectivity` shows Joint I / Joint J: I must be the **lower**
> joint of the pair (wall joint for `GAP_CONTACT`, ground for `GAP_SOIL`). If a link
> ever has I on top the gap works in tension and the wall lifts off instead of bearing.

> [!info]- 4. Joint restraints and local axes (Assign › Joint › Restraints / Local Axes)
>
> Select group `RINGWALL_AXIS`, `Assign › Joint › Restraints`: expect **Translation 1
> only**. `Assign › Joint › Local Axes`: an *Advanced* (rotation about Z) assignment,
> angle equal to the rim joint at the same spoke; on screen the joint axis 1 (red) is
> tangential, 2 (white) radial, 3 (cyan) up. `BASE_RING`: the same. `RINGWALL_GROUND`:
> all six ticked. A wall-top joint with U2 or U3 ticked is the pre-2026-09-04 model (a
> pinned ring cannot expand or settle).

> [!info]- 5. Frame insertion point (Assign › Frame › Insertion Point)
>
> Select group `RINGWALL`. Expect Cardinal Point **10 (Centroid)**, Mirror 2/3 off,
> joint offsets 0 — the frame runs through the wall joints at the centroid, and the
> "transform stiffness" box is irrelevant with no offset. `View › Set Display Options ›
> General › Extrude View` shows the concrete 1.875 ft above and below the joint line,
> its top at the rim joints. (In the older `models/TANK-A` the frames are on the rim
> joints with Cardinal Point 8 and, because the builder's `StiffTransform` column was
> misspelt, SAP's default `Transform = Yes`: rigid arms down to the centroid.) `View › Set Display Options › General › Extrude View` shows the concrete
> section hanging below the joint line, its top at the joint. Right-click a frame:
> *Frame Information › Location* lists the insertion point. With the box unticked SAP
> would insert rigid arms from the joint down to the centroid, which couples axial
> force with end rotation and would change the answers; see the offset study for the
> magnitude (zero for a straight beam under gravity, but not for a curved ring).

> [!info]- 6. Load cases (Define › Load Cases)
>
> `NL_DEAD` (initial condition zero) → `NL_HYDRO` (continues from `NL_DEAD`) →
> `NL_SETTLE` (continues from `NL_HYDRO`), all Nonlinear Static, Full Load, results
> Final State. `DEAD` / `HYDRO` are the linear twins and treat every gap as a two-way
> spring; do not report from them. Monitored DOF U3 at joint 1.

> [!info]- 7. Link forces after the run (Display › Show Forces/Stresses › Links)
>
> Case `NL_HYDRO`, component **Axial (P)**, or `Display › Show Tables › Analysis Results ›
> Element Output › Link Output › Element Forces – Links`. P is along local 1 and
> **negative = compression**. Expect at every spoke:
> - `GAP_CONTACT` P ≤ 0, and `GAP_SOIL` P ≤ 0.
> - `GAP_SOIL` P = `GAP_CONTACT` P at the same spoke plus that spoke's share of the wall
>   self-weight (0.150 × 1.25 × 3.75 × arc ≈ 0.70 × arc kip). The 72 soil links sum to
>   the whole tank weight on the rim plus the wall weight.
> - Any P > 0 means a gap in tension: wrong sense (see 3) or the linear case selected.
> - Under `NL_SETTLE` with the slope profile, links on the dropped side should read
>   **exactly 0** where the wall has lifted off the settling ground and larger
>   compression on the hinge side. A link at 0 is the gap open, not an error.
>
> The `.s2k` results export (`results.s2k`) carries no link table yet; the GUI table is
> the only source for these numbers today (the OAPI call is `Results.LinkForce`, used in
> `pythonTools/sap/offset_study.py`).

> [!info]- 8. Joint displacements per spoke (Display › Show Tables › Joint Displacements)
>
> Filter the table to one spoke, e.g. joints `1`, `4828`, `4900` (rim, wall, ground)
> and case `NL_HYDRO`. Expect U3(rim) ≈ U3(wall) (contact gap closed; 2026-09-08
> numbers −0.027434 / −0.027400 ft, identical to the coincident layout) and U3(ground)
> = 0. Under `NL_SETTLE` the ground joint shows the prescribed displacement and the
> difference U3(wall) − U3(ground) is the soil gap: negative = compressed (bearing),
> positive = open (lift-off). The table's U1/U2 are in the **joint local axes** for
> `BASE_RING` / `RINGWALL_AXIS` (U2 = radial).

> [!info]- 9. Deformed shape (Display › Show Deformed Shape)
>
> Case `NL_SETTLE`, scale up. The `RINGWALL` frames should follow the ground on the
> bearing side and hang free above the dropped ground on the other, with the tank rim
> riding on the wall. If the ring stays planar while the ground drops, either the
> tangential restraint got a radial or vertical component (4) or the links are
> two-way (2).

## Findings from the desk audit (2026-09-08)

- **The wall frames were stiffness-transformed all along.** `StiffTransform=No` is not a
  SAP column; the real one is `Transform`, defaulting to Yes. So the notes' "drawing
  only" claim was wrong for every run: the analysis axis sat at the centroid on rigid
  arms from the wall-top joint (scheme X in [[vault/arms/beam-offset-study|beam-offset-study]]).
  Within 0.4 % of the other layouts for everything vertical, which is everything the
  tank sees. Builder fixed, and the wall moved to scheme C (`models/TANK-A-ringwallC`,
  `[ringwall] joints = "elevations"`): rim, wall centroid and base at their own
  elevations, no coincident joints, `NL_HYDRO` rim / wall U3 unchanged to 1e-9 ft.
- **Sense is right by construction**: every link has I below and J above, so local 1
  = +Z and downward motion of the upper joint is a closing gap. Confirmed
  numerically in the offset study (middle link reads 0 when the ground drops away).
- **Radial is free between rim and wall**: `GAP_CONTACT` is U1-only, so no friction or
  shear key ties the shell base to the wall top. Intended while earth pressure is
  deferred; if a radial tie is ever wanted it is a U2 property on the contact link, not
  a restraint.
- **Linear cases mislead**: `DEAD` / `HYDRO` exist only as references; the two-way
  effective stiffness makes them see tension in the gaps. Report `NL_*` only.
- **No link output in `results.s2k`**: link forces are GUI-only today. Adding
  `Results.LinkForce` to `sap_api.results_s2k` is a small step if the audit table is
  wanted per build.
- The concrete column stiffness sits in `GAP_CONTACT` (E·C·arc/A); the wall frames
  themselves carry only the ring bending/hoop. Swapping that compliance into the soil
  link would change nothing in the vertical path and is a matter of preference.
