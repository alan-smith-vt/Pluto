---
title: Ring wall — restraints and load path
status: current
created: 2026-09-04
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

Why the concrete ring wall showed no hoop force, what was rebuilt on 2026-09-04, and how
the rim load path now runs. Model details live in
[[vault/arms/sap-tank-builder|sap-tank-builder]]; this note is the picture. Figures are
`.svg` files in `vault/arms/assets/`, embedded with `![[…|width]]` (the files carry no
fixed size, so a click-to-zoom popout scales to the window).

## Restraints in plan

Before, every ring wall joint was held in global X and Y. A ring pinned at every point in
plan cannot grow, so hoop tension was impossible whatever pushed on it. Now each wall-top
joint carries joint local axes copied from its rim joint (X tangential, Y radial outward)
and is restrained in U1 only. Tangential restraints all round still stop rigid-body drift
and spin; uniform expansion is free.

![[vault/arms/assets/ring-wall-plan-before.svg|400]]

![[vault/arms/assets/ring-wall-plan-now.svg|400]]

## Section at the rim — load path now

One chain per spoke. Since 2026-09-08 (`[ringwall] joints = "elevations"`, the default)
the three joints sit at three elevations and the links have length; before, all three
were coincident at the wall top (`joints = "top"`, still available):

1. tank rim joint (`BASE_RING`, z = 0): U1 tangential held, radial released
2. `GAP_CONTACT` link, compression only, vertical, length A/2; k = E_c × width × arc /
   depth (the concrete column under the joint)
3. wall joint (`RINGWALL_AXIS`, z = −A/2, the section centroid): the `RINGWALL` frame
   axis; tangential restraint only
4. `GAP_SOIL` link, compression only, vertical, length A/2; k = subgrade modulus × width
   × arc
5. ground joint (`RINGWALL_GROUND`, z = −A, the wall base): fully fixed; settlement
   profiles go here

![[vault/arms/assets/ring-wall-section.svg|800]]

The `RINGWALL` frame node is the wall joint ③ at the centroid, so the section is drawn
where its stiffness is, with no insertion point, in SAP and in the viewer. The figure
above still shows the earlier layout (all three joints at the wall top, section hung
below on insertion point 8); the generated `tank-beam-nodes.svg` in
[[vault/arms/sap-tank-model-diagrams|sap-tank-model-diagrams]] shows the current one.
The four possible layouts give the same vertical answers —
[[vault/arms/beam-offset-study|beam-offset-study]]; the elevations layout was chosen
because nothing is coincident, so each joint and link can be picked in the GUI. (The
"top" layout's builder flag was also misspelt `StiffTransform` until 2026-09-08, so those
runs had SAP's default `Transform = Yes`, rigid arms node → centroid; within 0.4 % and
now written correctly as `Transform` from `[ringwall] transform`.)

Verified on SAP2000 26.3.0, `NL_HYDRO`, spoke 1 (joints 1 / 4828 / 4900), the old
coincident layout (`models/TANK-A`, 2026-09-04) against the elevations layout
(`models/TANK-A-ringwallC`, 2026-09-08):

| joint | U3, coincident | U3, elevations |
|---|---|---|
| rim | −0.0274341 ft | −0.0274341 ft |
| wall (top / centroid) | −0.0273999 ft | −0.0273999 ft |
| ground | 0 | 0 |

Under `NL_SETTLE` (slope) the rim reads −0.13941 vs −0.13918 ft, the 0.16 % being the old
layout's rigid arm (`Transform = Yes`) dragging the wall joint radially (U2 −0.0022 ft
there, 0 now).

The contact link closes by 0.00003 ft: the tank mates with the wall and the two settle
together. Nothing loads the ring radially yet (earth pressure deferred at the user's
request), so hoop P ≈ 0 and the frames show self-weight bending only (V2 ≈ 1 kip, M3 ≈
0.76 kip-ft).

## Joint restraint summary

| joint | before | now |
|---|---|---|
| tank rim | U1 tangential, U2 released, U3 via gap link to the wall | unchanged; the link is `GAP_CONTACT` (concrete stiffness) |
| wall top (`RINGWALL_TOP`) | U1 = U2 = Yes in global, U3 linear spring | local axes, U1 tangential only, U3 via `GAP_SOIL` |
| `RINGWALL_GROUND` | did not exist | fully fixed; settlement target |
| plate ground joints | fully fixed | unchanged |

## Deferred

Lateral earth pressure on the inner face, p = Ka · (γ_sand · z + q) over the wall depth as
a radial line load on the frames — the API 650 ring wall hoop-tension load. Needs Ka,
γ_sand and the pad height. Freeing the restraints alone gives P ≈ 0, which is the current,
expected state; the settlement tilt validation needs that freedom anyway, since a rigidly
pinned ring cannot follow a settling plate.
