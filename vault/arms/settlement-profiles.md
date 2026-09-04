---
title: Settlement profiles — ground displacement on the ground joints
status: current
created: 2026-09-04
---

*↑ [[vault/Home|Home]] › [[vault/arms/Arms map|Arms map]]*

Settlement is a **ground displacement** on the fixed ground joints (plate `GROUND` +
`RINGWALL_GROUND`), load pattern `SETTLE`, nonlinear case `NL_SETTLE` continuing from
`NL_HYDRO`. The gap links above the joints open where the ground drops away, the plate
and ring wall span, and the soil springs engage again wherever the structure catches up.
Load path pictures: [[vault/arms/ring-wall-load-path|ring-wall-load-path]].

## Config

```toml
[settlement]
profile       = "trench"   # "none" | "trench" | "slope"
depth         = 0.5        # ft at the centreline (down)
width         = 10.0       # ft, zero at +/- width/2
direction_deg = 0.0        # trench axis in plan, from +X
offset        = 0.0        # ft, axis offset from the tank centre (+90 deg side)
```

d = signed distance from the line at `direction_deg` through the point at `offset`.

`trench`: parabolic cross-section, w(d) = −depth · (1 − (2d / width)²) for |d| ≤ width/2,
zero outside.

`slope` (2026-09-04): half-plane linear settlement hinged on that line: w = 0 for d ≤ 0,
w = −depth · d / R beyond it, so the full depth is reached at the tank edge and the ring
wall just outside gets a little more. `width` unused. Planned next: plane tilt (the
validation case, must give ~zero stress), parabolic dish.

## Audit record (generated, not hand-drawn)

`python pythonTools/sap/settlement_audit.py configs/<name>.toml --vault-svg` writes, from
the model itself:

- `<model>/<name>.settlement.csv` — `node, x, y, z, dx, dy, dz` for every ground joint
  (dx = dy = 0 today; dz in ft, negative = down). This is what the .s2k asks SAP for;
  what SAP did with it is `results.s2k` (`JOINT DISPLACEMENTS`, case `NL_SETTLE`).
- `<model>/<name>.settlement.svg` — plan heatmap of dz on the ground joints with the
  tank and ring wall outline and the profile axis, plus an elevation across the profile:
  the applied curve and every joint at its own distance. Copied to
  `vault/arms/assets/settlement-<name>.svg`. `run_sap.py` regenerates both on every build.

![[vault/arms/assets/settlement-TANK-A.svg|900]]

TANK-A, 2026-09-04, **slope** 0.5 ft at the edge, hinge along +X (the figure above; 432 of
865 ground joints moved). SAP 26, `NL_SETTLE`: ground joints match the CSV to 5e-7 ft. The
plate follows the ground everywhere. The **ring wall does not**: it bridges the hinge as a
stiff ring — wall top −0.139 ft at the hinge azimuths (soil there compressed 1.7 in, ~87
kip per joint at the linear 624 kip/ft), soil gap open on 38 of 72 spokes on the flat
side, `RINGWALL` M3 up to ±217 kip-ft, V2 ±58 kip, P −36…+29 kip. The **tank rim lifts off
the ring wall on 54 of 72 spokes**, by up to 0.08 ft (1 in) at 90° and 270°, bearing only
near the hinge. Two things to weigh before trusting the magnitudes: the soil is a linear
spring (no bearing limit), and the concrete's vertical compliance lives in `GAP_CONTACT`.

Earlier the same day, **trench** 0.5 ft × 10 ft then × 30 ft along +X: 259 / 583 joints
moved; the plate sagged fully into the trench, every gap closed again. **SAP 26 run:** `JOINT LOADS - GROUND DISPLACEMENT` accepted; every
ground joint's `NL_SETTLE` U3 equals the CSV to 5e-7 ft. The plate followed the trench
(centre joint U3 −0.5255 ft = ground −0.5 plus the ~0.02 ft soil compression it already had
under NL_HYDRO; outside the trench −0.0185 ft, unchanged), i.e. the thin baseplate under
2.5 kip/ft² of product simply sags into a 10 ft trench — the gaps close again.

![[vault/arms/assets/settlement-example.svg|900]]

The tracked example config (`configs/example.toml`): 0.25 ft × 12 ft.
