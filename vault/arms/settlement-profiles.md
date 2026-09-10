---
title: Settlement profiles — ground displacement on the ground joints
status: current
created: 2026-09-04
---

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

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
  the applied curve and every joint at its own distance. `run_sap.py` regenerates both
  beside the model on every build; the vault copy `vault/arms/assets/settlement-<name>.svg`
  is on demand only, `settlement_audit.py <config> --vault-svg` (2026-09-10: runs used to
  overwrite it), as are the model diagrams, `run_sap.py --figures` or `doc_figures.py <config>`.

Project runs on the real tank (slope, open-top variant, trench; findings and figures) are recorded in the
Notes vault: [Tank Settlement Runs](obsidian://open?vault=Notes&file=<project notes>/Tank%20Settlement%20Runs). Project configs and models live at `<project dir>`.

![[vault/arms/assets/settlement-example.svg|900]]

The tracked example config (`configs/example.toml`): 0.25 ft × 12 ft.
