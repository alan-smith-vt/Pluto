"""Buckling factors and mode shapes of a tank model with a [buckling] case.

    python buckling_modes.py <config.toml> [<config.toml> ...]

Per config: buckling.csv (factors) and, per mode, from results.s2k ("BUCKLE.<n>" joint
displacements): the part holding the largest displacement (wall / roof / plate), where it is
(azimuth, elevation or roof radius), the circumferential full-wave count on the ring through the
peak (sign changes of the normal displacement / 2), the meridional half-waves along the wall
line through the peak, and the share of the mode's |u| within the dent footprint, if any.
Read-only; prints a table.
"""
from __future__ import annotations

import csv
import math
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from tankbuilder import TankModel, load_config  # noqa: E402

PAIR = re.compile(r'([A-Za-z0-9_#]+)=("([^"]*)"|\S*)')


def mode_disp(res: Path, case: str) -> dict[int, dict[int, tuple[float, float, float]]]:
    """step -> joint -> (U1, U2, U3) for the joint rows of '<case>.<step>'."""
    out: dict[int, dict[int, tuple[float, float, float]]] = defaultdict(dict)
    tag = f"OutputCase={case}."
    table = None
    with res.open("r", errors="replace") as fh:
        for line in fh:
            if line.startswith("TABLE:"):
                table = line.split('"')[1]
                if table != "JOINT DISPLACEMENTS" and out:
                    break
                continue
            if table != "JOINT DISPLACEMENTS" or tag not in line:
                continue
            d = {k: (q if q else v) for k, v, q in PAIR.findall(line)}
            step = int(float(d["OutputCase"].split(".", 1)[1]))
            out[step][int(d["Joint"])] = (float(d["U1"]), float(d["U2"]), float(d["U3"]))
    return out


def sign_changes(vals: list[float], floor: float) -> int:
    s = [v for v in vals if abs(v) > floor]
    return sum(1 for a, b in zip(s, s[1:] + s[:1]) if (a > 0) != (b > 0))


def report(cfg: Path) -> None:
    spec, s2k = load_config(cfg)
    m = TankModel(spec)
    folder = s2k.parent
    print(f"\n== {folder.name}  (n_theta {spec.n_theta}, n_z {spec.n_z}, roof n_r {spec.roof_n_r})")
    fac = {}
    with (folder / "buckling.csv").open() as fh:
        for row in csv.DictReader(fh):
            fac[int(float(row["Mode"]))] = float(row["Factor"])
    print("factors:", ", ".join(f"{k}: {v:.4g}" for k, v in sorted(fac.items())))
    modes = mode_disp(folder / "results.s2k", "BUCKLE")
    wall = set(m._wall_joints)
    roof = {j for a in m.roof_areas for j in m.areas[a]} - wall
    plate = set(m.baseplate_joints) - wall
    dent = set().union(*m.dent_joints.values()) if getattr(m, "dent_joints", None) else set()
    R = spec.radius
    for k in sorted(modes):
        u = modes[k]
        def normal(j):
            x, y, z = m.joints[j]
            u1, u2, u3 = u.get(j, (0.0, 0.0, 0.0))
            if j in wall:
                r = math.hypot(x, y)
                return (u1 * x + u2 * y) / r
            if j in roof:   # spherical cap: normal from the sphere centre
                zc = spec.height + m.roof_rise - spec.roof_crown_radius
                n = (x, y, z - zc)
                L = math.sqrt(sum(c * c for c in n))
                return (u1 * n[0] + u2 * n[1] + u3 * n[2]) / L
            return u3
        mag = {j: math.sqrt(sum(c * c for c in v)) for j, v in u.items()}
        jp = max(mag, key=mag.get)
        part = "wall" if jp in wall else "roof" if jp in roof else "plate" if jp in plate else "other"
        x, y, z = m.joints[jp]
        az = math.degrees(math.atan2(y, x))
        rr = math.hypot(x, y)
        peak = mag[jp]
        floor = 0.05 * peak
        ring = sorted((j for j in (wall if part == "wall" else roof if part == "roof" else plate)
                       if abs(m.joints[j][2] - z) < 1e-6 and abs(math.hypot(*m.joints[j][:2]) - rr) < 1e-6),
                      key=lambda j: math.atan2(m.joints[j][1], m.joints[j][0]))
        waves = sign_changes([normal(j) for j in ring], floor) / 2.0
        line = ""
        if part == "wall":
            col = sorted((j for j in wall if abs(math.atan2(m.joints[j][1], m.joints[j][0]) - math.atan2(y, x)) < 1e-6),
                         key=lambda j: m.joints[j][2])
            s = [normal(j) for j in col if abs(normal(j)) > floor]
            half = 1 + sum(1 for a, b in zip(s, s[1:]) if (a > 0) != (b > 0)) if s else 0
            zs = [m.joints[j][2] for j in col if abs(normal(j)) > 0.5 * peak]
            line = f"  meridional half-waves {half}, |u|>50% over z {min(zs):.2f}..{max(zs):.2f} ft"
            where = f"az {az:7.2f}  z {z:6.2f} ft"
        else:
            where = f"az {az:7.2f}  r {rr:6.2f} ft  z {z:6.2f} ft"
        share = ""
        if dent:
            tot = sum(mag.values())
            share = f"  dent share of sum|u| {sum(mag.get(j, 0.0) for j in dent) / tot:.2f}"
        arc = 2 * math.pi * rr / spec.n_theta if spec.n_theta else 0
        print(f"mode {k:2d}  factor {fac.get(k, float('nan')):9.4g}  {part:5s} {where}  "
              f"circ waves {waves:4.1f} on {len(ring)} joints (half-wave {math.pi * rr / max(waves, 0.5):.2f} ft, "
              f"element arc {arc:.2f} ft){line}{share}")


if __name__ == "__main__":
    for a in sys.argv[1:]:
        report(Path(a))
