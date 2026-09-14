#!/usr/bin/env python3
"""Meridional profiles at the shell edges from results.s2k, averaged around the circumference.

    python edge_profile.py configs/A.toml [configs/B.toml ...] [--case NL_DEAD] [--band 4]

For each model: the wall rows within `band` ft of the base and the top, the roof
and baseplate rings within `band` ft of the rim, and the eave ring axial force.
Every value is the mean over all shell corners on that mesh ring (both elements
sharing the ring, every spoke), i.e. the corner-average the model check note
uses. The wall table carries the closed-form N_z = -(W_roof + W_ring)/(2 pi R)
- gamma * sum(t dz above) beside F11, so the edge overshoot is visible as the
gap between the two columns. Units kip, ft.
"""

from __future__ import annotations

import argparse
import math
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from tankbuilder import TankModel, load_config, parse_s2k  # noqa: E402
from tankbuilder.section import eave_ring_section  # noqa: E402

FIELDS = ("F11", "F22", "M11", "M22")


def ring_means(rows, joint_key, part_areas):
    """{ring key: {field: mean}} over corner rows whose Area is in part_areas."""
    acc: dict[float, dict[str, list[float]]] = defaultdict(lambda: defaultdict(list))
    for r in rows:
        a = int(r["AREA"])
        if a not in part_areas:
            continue
        k = joint_key(int(r["JOINT"]))
        for f in FIELDS:
            acc[k][f].append(float(r[f]))
    return {k: {f: sum(v) / len(v) for f, v in d.items()} for k, d in acc.items()}


def wall_expected(model, z):
    """N_z by statics: everything above z per foot of circumference (negative = compression)."""
    s = model.spec
    circ = 2.0 * math.pi * s.radius
    w_roof = 0.0
    if s.roof:
        rise = model.roof_rise
        w_roof = 2.0 * math.pi * s.roof_crown_radius * rise * s.roof_thickness * s.mat_unit_weight
        if s.roof_ring:
            w_roof += eave_ring_section(s.roof_ring_leg, s.roof_ring_thickness, s.roof_ring_bar)["Area"] * s.mat_unit_weight * circ
    above = 0.0
    z0 = 0.0
    for c in s.plate_courses:
        z1 = z0 + c.height
        if z1 > z:
            above += (z1 - max(z, z0)) * c.thickness
        z0 = z1
    return -(w_roof / circ + s.mat_unit_weight * above)


def profile(cfg: Path, case: str, band: float) -> None:
    spec, s2k = load_config(cfg)
    model = TankModel(spec)
    res = s2k.parent / "results.s2k"
    tables = parse_s2k(res.read_text())
    rows = [r for r in tables["ELEMENT FORCES - AREA SHELLS"] if r["OUTPUTCASE"] == case]
    if not rows:
        raise SystemExit(f"{res}: no rows for case {case}")
    z_of = {j: xyz[2] for j, xyz in model.joints.items()}
    r_of = {j: math.hypot(xyz[0], xyz[1]) for j, xyz in model.joints.items()}
    key = lambda d: (lambda j: round(d[j], 4))

    print(f"\n=== {s2k.stem}  case {case}  ({len(model.areas)} shells, {model.n_rows} wall rows)")
    wall = ring_means(rows, key(z_of), set(model.ids.block("area", "wall")))
    h = spec.height
    print(f"--- wall: rows within {band:g} ft of the base and the top (corner means, kip/ft, kip-ft/ft)")
    print(f"{'z':>8} {'F11':>9} {'N_z calc':>9} {'F22':>9} {'M11':>9}")
    for z in sorted(wall):
        if z <= band or z >= h - band:
            d = wall[z]
            print(f"{z:8.3f} {d['F11']:9.4f} {wall_expected(model, z):9.4f} {d['F22']:9.4f} {d['M11']:9.5f}")
    for name, edge_r in (("roof", spec.radius), ("baseplate", spec.radius)):
        if not getattr(spec, name):
            continue
        cap = ring_means(rows, key(r_of), set(model.ids.block("area", name)))
        print(f"--- {name}: rings within {band:g} ft of the rim")
        print(f"{'r':>8} {'F11':>9} {'F22':>9} {'M11':>9} {'M22':>9}")
        for r in sorted(cap):
            if r >= edge_r - band:
                d = cap[r]
                print(f"{r:8.3f} {d['F11']:9.4f} {d['F22']:9.4f} {d['M11']:9.5f} {d['M22']:9.5f}")
    if spec.roof and spec.roof_ring and "ELEMENT FORCES - FRAMES" in tables:
        ring = set(model.ids.block("frame", "roof_ring"))
        p = [float(r["P"]) for r in tables["ELEMENT FORCES - FRAMES"]
             if r["OUTPUTCASE"] == case and int(r["FRAME"]) in ring]
        if p:
            print(f"--- eave ring: P mean {sum(p) / len(p):.4f} kip over {len(p)} stations")


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("configs", nargs="+", type=Path)
    p.add_argument("--case", default="NL_DEAD")
    p.add_argument("--band", type=float, default=4.0)
    a = p.parse_args(argv)
    for cfg in a.configs:
        profile(cfg, a.case, a.band)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
