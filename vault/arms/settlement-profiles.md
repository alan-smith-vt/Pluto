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
profile       = "trench"   # "none" | "trench"
depth         = 0.5        # ft at the centreline (down)
width         = 10.0       # ft, zero at +/- width/2
direction_deg = 0.0        # trench axis in plan, from +X
offset        = 0.0        # ft, axis offset from the tank centre (+90 deg side)
```

`trench`: parabolic cross-section, w(d) = −depth · (1 − (2d / width)²) for |d| ≤ width/2,
zero outside, d = perpendicular distance from the axis. Planned next: plane tilt (the
validation case, must give ~zero stress), parabolic dish, half-plane linear slope aligned
with a baseplate spoke.

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

TANK-A, 2026-09-04: trench 0.5 ft deep × 10 ft wide along +X through the centre; 259 of
865 ground joints moved. **SAP 26 run:** `JOINT LOADS - GROUND DISPLACEMENT` accepted; every
ground joint's `NL_SETTLE` U3 equals the CSV to 5e-7 ft. The plate followed the trench
(centre joint U3 −0.5255 ft = ground −0.5 plus the ~0.02 ft soil compression it already had
under NL_HYDRO; outside the trench −0.0185 ft, unchanged), i.e. the thin baseplate under
2.5 kip/ft² of product simply sags into a 10 ft trench — the gaps close again.

![[vault/arms/assets/settlement-example.svg|900]]

The tracked example config (`configs/example.toml`): 0.25 ft × 12 ft.
