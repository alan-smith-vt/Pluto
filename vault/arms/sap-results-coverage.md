---
title: SAP results coverage — what the viewer gets versus what SAP2000 has
status: current
created: 2026-09-08
---

*↑ [[vault/Home|Home]] › [[vault/arms/Arms map|Arms map]]*

What `run_sap.py` pulls out of SAP2000 over the OAPI, what `SapToPluto` turns into viewer
components, and what SAP holds that never leaves it. The pipeline is `Results.*` calls in
`pythonTools/sap/tankbuilder/sap_api.py` → `results.s2k` (three tables) →
`scripts/arms/SapToPluto.cs` → the v4 binary. Table-level mapping of the model side is in
[[vault/arms/sap-s2k-to-viewer|sap-s2k-to-viewer]]; this note is the results side only.
Units in the tank runs: kip, ft, with derived stresses reported in ksi.

## Shells

SAP's shell element carries membrane forces, plate moments and transverse shears per unit
length at each element corner, plus a stress recovery at the top and bottom faces.

| SAP2000 quantity | OAPI source | in `results.s2k` | in the viewer | notes |
|---|---|---|---|---|
| F11, F22, F12 membrane forces (force/length) | `AreaForceShell` | yes | kind **force**: `F11 (merid)`, `F22 (circ)`, `F12 (IP shear)` | per corner, in shell local axes; local 1 = meridional, 2 = circumferential on every shell since 2026-09-04 |
| M11, M22, M12 plate moments (force·length/length) | `AreaForceShell` | yes | kind **moment**: `M11 (merid)`, `M22 (circ)`, `M12 (twist)` | SAP's M11 is carried by bars along local 1, i.e. it bends the meridional fibres |
| V13, V23 transverse shears | `AreaForceShell` | yes | force: `V13 (OOP shear 1)`, `V23 (OOP shear 2)` | |
| FMax, FMin, FVM — principal membrane forces, von Mises | `AreaForceShell` | yes | force: `FMax (principal max)`, `FMin (principal min)`, `FVM (von Mises)` (2026-09-08) | FAngle written, not shown |
| MMax, MMin — principal moments | `AreaForceShell` | yes | moment: `MMax`, `MMin` (2026-09-08) | MAngle written, not shown |
| VMax, VAngle — principal transverse shear | `AreaForceShell` | yes (unused) | no | |
| S11, S22, S12 membrane stresses | derived, `F / t` | — | kind **stress**: `S11 (merid membrane)`, `S22 (circ membrane)`, `S12 (IP shear membrane)`, ksi | thickness from `AREA SECTION PROPERTIES` |
| S11, S22, S12, SMax, SMin, SVM at the **top and bottom faces** (membrane ± bending) | `AreaStressShell` | yes, `ELEMENT STRESSES - AREA SHELLS` (2026-09-08) | stress: `S11 top (merid)` … `SVM top (von Mises)`, `S11 bot` … `SVM bot`, ksi | SAP's own recovery; checked: mean of the faces = the membrane value to 1e-7. SAngle per face and S13 / S23 / SMaxAvg written, not shown |
| joint forces of shells (`AreaJointForceShell`) | — | no | no | corner nodal forces; useful for reactions on a cut |
| stress / force **averaging at joints** | viewer | — | nodal smoothing toggle | smoothing hides the real force step at a thickness change; turn it off to read course boundaries |

Every shell row carries `Joint` (the corner), so the viewer paints per corner, not one
value per element. Values are the **final state** of each case: `sap_api` asks for
nonlinear static output **step-by-step** (`Results.Setup.SetOptionNLStatic(2)`), which
with Final State saved is one `Step` row per corner. SAP's default, envelopes, returns a
`Max` and a `Min` row per corner and **zeroes every principal and von Mises field** on
them (found 2026-09-08 when `NL_SETTLE` showed no principals); the linear cases were
never affected.

## Frames (viewer beams)

| SAP2000 quantity | OAPI source | in `results.s2k` | in the viewer | notes |
|---|---|---|---|---|
| P, V2, V3, T, M2, M3 at every output station | `FrameForce` | yes, all stations | first and last station per frame per case: kind **force** `P (circ)`, `V2 (vert shear)`, `V3 (radial shear)`; kind **moment** `M3 (vert moment)`, `M2 (plan moment)`, `T (torsion)` | |
| interior stations (mid-span peaks) | `FrameForce` | yes | kind **envelope** (2026-09-08): `P (circ) env` … `T (torsion) env` = max \|value\| over all stations, same at both ends | the binary holds two stations per beam; the ring wall's self-weight M3 peaks mid-span at 0.76 kip-ft with ≈ 0 at the ends, which only the envelope shows. True multi-station fields are a format change (`nStations` > 2 is allowed by v4 but the writer, reader and beam geometry are two-station) |
| frame stresses | none in SAP | — | kind **stress** (2026-09-08): `Sa (P/A)`, `Sb3 (M3/S33)`, `Sb2 (M2/S22)`, `Smax`, `Smin` in ksi at the two ends | computed in `SapToPluto` from `Area / S33 / S22` on the section row (General sections) or the rectangle `t3 × t2`; other shapes without those columns read NaN |
| frame joint forces (`FrameJointForce`) | — | no | no | |

## Joints

| SAP2000 quantity | OAPI source | in `results.s2k` | in the viewer | notes |
|---|---|---|---|---|
| U1..U3, R1..R3 displacements | `JointDispl` | yes, in **joint local axes** | `Translation X/Y/Z`, `Rotation X/Y/Z` in **global** (rotated by `Rz(A)·Ry(B)·Rx(C)`), plus `Translation R` / `Translation T` in cylindrical mode | fanned to every shell corner on the joint |
| reactions (`JointReact`) | — | no | no | restrained joints only; the plate and ring wall ground joints would give the soil bearing directly |
| base reactions (`BaseReact`) | — | no | no | total force and moment on the model; the one-line sanity check (sum of gap link forces = tank weight) |
| joint velocities / accelerations | — | no | no | dynamic only, not applicable |

## Links (gap elements)

| SAP2000 quantity | OAPI source | in `results.s2k` | in the viewer | notes |
|---|---|---|---|---|
| P, V2, V3, T, M2, M3 per link | `LinkForce` | **no** | no | the compression-only bearing under the plate and the ring wall; GUI only today (`Display › Show Forces/Stresses › Links`); `offset_study.py` pulls it over the OAPI and shows the call works |
| link deformations (gap opening) | `LinkDeformation` | no | no | positive = open; the direct lift-off readout |

## Cases

`run_sap.py` exports every case SAP ran: `DEAD`, `HYDRO` (linear, gaps as two-way springs),
`NL_DEAD`, `NL_HYDRO`, `NL_SETTLE` (staged nonlinear, final state). Only the `NL_*` cases are
meaningful with gaps; the linear pair is there for reference and is easy to pick by
mistake in the viewer dropdown. Not exported: step-by-step nonlinear results (not saved),
combinations (none defined), envelopes (none defined), section cuts defined in SAP (none;
the viewer has its own section cuts on the sidecar).

## What this adds up to

- **Covered** (2026-09-08): the full shell force and moment set per corner with
  principals and von Mises, membrane and face stresses, global displacements, frame end
  forces and moments, frame fibre stresses, station envelopes. The component dropdown
  filters by kind (Force / Moment / Stress / Envelope / Displacement).
- **Still missing**: link forces and gap openings (the bearing and lift-off picture);
  reactions; true multi-station beam fields (the envelope stands in). Each is a single
  OAPI call plus a viewer component, except the stations, which are a format change.
- **Next**, in order of value: (1) `LinkForce` + `LinkDeformation` into `results.s2k`
  and a link component in the viewer; (2) `BaseReact` printed by `run_sap.py` as the
  equilibrium check; (3) `nStations` > 2 through writer, reader and beam geometry.
