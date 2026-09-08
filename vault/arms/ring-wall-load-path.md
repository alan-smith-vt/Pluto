---
title: Ring wall — restraints and load path
status: current
created: 2026-09-04
---

*↑ [[vault/Home|Home]] › [[vault/arms/Arms map|Arms map]]*

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

One chain per spoke, all three joints coincident in SAP (drawn apart):

1. tank rim joint (`BASE_RING`): U1 tangential held, radial released
2. `GAP_CONTACT` link, compression only, vertical; k = E_c × width × arc / depth (the
   concrete column under the joint)
3. wall-top joint (`RINGWALL_TOP`): the `RINGWALL` frame axis; tangential restraint only
4. `GAP_SOIL` link, compression only, vertical; k = subgrade modulus × width × arc
5. ground joint (`RINGWALL_GROUND`): fully fixed; settlement profiles go here later

![[vault/arms/assets/ring-wall-section.svg|800]]

The `RINGWALL` frame node is the wall-top joint ③. The concrete section is drawn hanging
below it (SAP insertion point 8, top centre) so its centroid sits A/2 under the node; the
dotted line is that offset. The intent was `Transform = No` (analysis on the node, offset
drawing only, in SAP and in the viewer) — but the builder wrote the column as
`StiffTransform`, which SAP ignores, so every run to 2026-09-08 had `Transform = Yes`:
rigid arms from the node down to the centroid. Same answer under vertical load; not the
same once a horizontal push acts at the top. Fixed in the builder, `[ringwall] transform`;
the study is [[vault/arms/beam-offset-study|beam-offset-study]].

Verified on SAP2000 26.3.0, TANK-A, `NL_HYDRO`:

| joint | U3 |
|---|---|
| rim | −0.02743 ft |
| wall top | −0.02740 ft |
| ground | 0 |

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
