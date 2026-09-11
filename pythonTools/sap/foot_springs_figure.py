#!/usr/bin/env python3
"""Two sections through the wall foot, side by side: the soil springs as first modelled
(flat pad, one soil spring under the ring wall centroid, plate on rigid contact rings) and
the final configuration (Boussinesq-graded pad, two face springs at k/2 on arms, graded plate
mesh at the concrete edge).

    python foot_springs_figure.py <first.toml> <final.toml> --out <dir> [--name foot-springs]
"""

from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from base_support_figures import spring  # noqa: E402
from doc_figures import CONC, GREEN, INK, MUTED, RUST, SOIL, STEEL, Fig  # noqa: E402
from tankbuilder import TankModel, load_config, parse_s2k  # noqa: E402

DEF_SCALE = 40.0   # displacement exaggeration for the deformed-shape overlay


def deformed_shape(m: TankModel, s2k: Path, case: str = "NL_HYDRO"):
    """(r, z) of the theta = 0 spoke after loading, relative to the ring wall axis settlement:
    plate rings from r = 30 ft to the toe, then the wall from the base up to 2.4 ft. Base ring
    joints report in local axes (U2 = radial outward); everything else in global (U1 = x = radial
    at theta = 0). Returns (plate_pts, wall_pts, ring wall settlement ft)."""
    tables = parse_s2k((s2k.parent / "results.s2k").read_text())
    d = {int(r["JOINT"]): r for r in tables["JOINT DISPLACEMENTS"] if r["OUTPUTCASE"] == case}
    s = m.spec
    rw = sum(float(d[j]["U3"]) for j in m.ringwall_joints) / len(m.ringwall_joints)
    base = set(m.base_joints)
    radii = m.cap_radii_of["baseplate"]
    plate = []
    for j in m.baseplate_joints:
        if abs(m.thetas[j]) > 1e-9:
            continue
        r = math.hypot(*m.joints[j][:2])
        if r < 30.0:
            continue
        u = float(d[j]["U2"]) if j in base else float(d[j]["U1"])
        plate.append((r + u * DEF_SCALE, (float(d[j]["U3"]) - rw) * DEF_SCALE))
    plate.sort()
    wall = []
    for ring, z in enumerate(m.z_levels):
        if z > 1.9:
            break
        j = m.joint_id(ring, 0)
        u = float(d[j]["U2"]) if j in base else float(d[j]["U1"])
        wall.append((s.radius + u * DEF_SCALE, z + (float(d[j]["U3"]) - rw) * DEF_SCALE))
    return plate, wall, rw


def panel(f: Fig, m: TankModel, x0: float, x1: float, title: str, final: bool, shape=None) -> None:
    s = m.spec
    R, C, A = s.radius, s.ringwall_width, s.ringwall_depth
    r0 = 30.6
    f.map(r0, R + C / 2 + 0.35, -A - 1.5, 2.6, x0, x1, 400, 60)
    y0 = f.Y(0)
    f.text(x0, 50, title, size=12, bold=True)
    # ground, concrete, plate, wall
    f.add(f'<rect x="{f.X(R - C / 2):.1f}" y="{y0:.1f}" width="{C * f.sx:.1f}" height="{-A * f.sz:.1f}" fill="{CONC}" stroke="{INK}" stroke-width="1"/>')
    f.add(f'<rect x="{f.X(r0):.1f}" y="{y0:.1f}" width="{(R - C / 2 - r0) * f.sx:.1f}" height="{-0.5 * f.sz:.1f}" fill="{SOIL}"/>')
    f.text(f.X(r0) + 4, y0 + 14, "sand pad", fill=MUTED, size=10)
    f.text(f.X(R + C / 2) - 4, y0 + 16, "ring wall", anchor="end", size=10)
    f.line(f.X(r0), y0, f.X(R + s.baseplate_overhang), y0, w=3)
    f.line(f.X(R), y0, f.X(R), f.Y(2.4), w=3)
    f.text(f.X(R) + 6, f.Y(2.1), "wall", size=10)
    f.text(f.X(r0) + 4, y0 - 7, "baseplate", size=10)
    radii = m.cap_radii_of["baseplate"]
    yg = f.Y(-1.15)
    yc = f.Y(-A / 2)
    yb = f.Y(-A)
    # pad springs and bearing chain per plate ring
    for k, r in enumerate(radii):
        if r < r0 + 0.2:
            continue
        over = r >= R - C / 2 - 1e-6
        if over:
            f.line(f.X(r), yc, f.X(r), y0 + 3, stroke=GREEN, w=2)
            f.dot(f.X(r), yc, 3, fill=GREEN)
            f.dot(f.X(r), y0, 3, fill=GREEN)
        else:
            fac = m.pad_zone_factor(k)
            f.dot(f.X(r), y0, 3, fill=STEEL)
            spring(f, f.X(r), y0 + 3, yg, color=STEEL, n=4, amp=3.5)
            f.text(f.X(r), yg + 12, f"{r:.2f}", anchor="middle", size=8, fill=STEEL)
            if final:
                f.text(f.X(r), yg + 22, f"{fac:.2f}×", anchor="middle", size=8, fill=STEEL)
    # arms: axis joint to the outermost arm joint each side, chained
    arm_r = sorted({round(math.hypot(*m.joints[j][:2]), 3) for j in m.bearing_arm_joints})
    if arm_r:
        f.line(f.X(min(arm_r)), yc, f.X(max(arm_r)), yc, stroke=GREEN, w=4)
    f.dot(f.X(R), yc, 4.5, fill=GREEN)
    f.line(f.X(R), y0, f.X(R), yc, stroke=GREEN, w=2)
    # soil springs
    if final:
        for r in (R - C / 2, R + C / 2):
            f.dot(f.X(r), yc, 3.5, fill=GREEN)
            spring(f, f.X(r), yc + 3, yb, color=GREEN, n=4, amp=3.5)
            f.text(f.X(r), yb + 12, f"{r:.2f}", anchor="middle", size=8, fill=GREEN)
        f.text(f.X(R), yb + 24, "GAP_SOIL × 2 at the faces, k/2 each", anchor="middle", size=9, fill=GREEN)
        f.text(f.X(R), yb + 36, "→ rotational stiffness k C²/4 per spoke", anchor="middle", size=9, fill=GREEN)
    else:
        spring(f, f.X(R), yc + 3, yb, color=GREEN, n=4, amp=3.5)
        f.text(f.X(R), yb + 12, "GAP_SOIL, one per spoke at the centroid", anchor="middle", size=9, fill=GREEN)
        f.text(f.X(R), yb + 24, "→ the ring rolls freely on a point", anchor="middle", size=9, fill=GREEN)
    # concrete inner edge marker
    f.text(f.X(R - C / 2), y0 - 16, "33.0", anchor="middle", size=8, fill=INK)
    # deformed shape overlay, NL_HYDRO, relative to the ring wall settlement
    if shape:
        plate, wall, rw = shape
        f.path("M" + " L".join(f"{f.X(r):.1f} {f.Y(z):.1f}" for r, z in plate), stroke=RUST, w=2.2, dash="6 3")
        f.path("M" + " L".join(f"{f.X(r):.1f} {f.Y(z):.1f}" for r, z in wall), stroke=RUST, w=2.2, dash="6 3")
        f.text(f.X(s.radius) - 10, f.Y(1.9), f"deformed, × {DEF_SCALE:g}", fill=RUST, size=9, anchor="end")
        f.text(f.X(s.radius) - 10, f.Y(1.9) + 12, "NL_HYDRO, relative to the", fill=RUST, size=8, anchor="end")
        f.text(f.X(s.radius) - 10, f.Y(1.9) + 22, f"ring wall (settles {-rw * 12:.2f} in)", fill=RUST, size=8, anchor="end")


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("first", type=Path)
    p.add_argument("final", type=Path)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--name", default="foot-springs")
    a = p.parse_args(argv)
    spec0, s2k0 = load_config(a.first)
    spec1, s2k1 = load_config(a.final)
    m0, m1 = TankModel(spec0), TankModel(spec1)
    sh0 = deformed_shape(m0, s2k0) if (s2k0.parent / "results.s2k").exists() else None
    sh1 = deformed_shape(m1, s2k1) if (s2k1.parent / "results.s2k").exists() else None
    f = Fig(1000, 560, "SOIL SPRINGS AT THE WALL FOOT — as first modelled (left) and the final configuration (right)",
            "Two sections through the wall foot side by side. Left: flat pad springs, one soil spring under the ring wall centroid, the plate over the concrete on rigid contact rings. Right: pad springs graded by the Boussinesq factor toward the rim, a graded plate mesh at the concrete edge, and two soil springs at the ring wall faces at half stiffness each.")
    panel(f, m0, 40, 470, "AS FIRST MODELLED: flat pad, point spring under the ring wall", False, sh0)
    panel(f, m1, 540, 970, "FINAL: graded pad (× k_s at each ring), face springs, refined edge", True, sh1)
    f.text(40, 470, "Both: pad springs k = k_s × tributary area, compression only, to fixed ground; plate joints over the concrete on GAP_BEARING contact links", fill=MUTED, size=10)
    f.text(40, 484, "(E_c × area / depth, ≈ 2000 × a pad spring) carried on rigid RINGWALL_ARM frames from the ring wall beam at its centroid; the shell line on GAP_CONTACT.", fill=MUTED, size=10)
    f.text(40, 504, "Left: dead load lifts the plate off the concrete between the rings; product settles pad and ring wall equally on equal springs, and the ring rolls toward the load.", fill=MUTED, size=10)
    f.text(40, 518, f"Right: pad k_s rises from 1.0× at the centre to {math.pi / 2:.2f}× at the rim; ring wall soil ≥ 1.57 × pad (the rim of the dish); plate rings at 32.38, 32.75, 33.0 at the concrete edge.", fill=MUTED, size=10)
    f.text(40, 538, f"Section at one spoke; springs at the plate rings, radii in ft. Dashed: deformed plate and wall under product, × {DEF_SCALE:g}, relative to the ring wall; the foot rolls outward.", fill=MUTED, size=10)
    a.out.mkdir(parents=True, exist_ok=True)
    out = a.out / f"{a.name}.svg"
    out.write_bytes(f.done().encode("utf-8"))
    print("wrote", out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
