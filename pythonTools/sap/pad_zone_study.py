#!/usr/bin/env python3
"""Pad spring zoning calibration (Bowles 5e 10-5 / 10-12): the stripped base models'
settlement dish against the Boussinesq flexible-circle solution.

    python pad_zone_study.py <config.toml> [<config.toml> ...] [--case NL_HYDRO] --out <dir>

For each model (config beside its results.s2k): plate U3 by mesh ring, averaged around
the circumference, normalised to the flat Winkler value (p + plate weight) / ks
(the centre joint alone settles ~1.3 x too much because its spring uses the
bisector tributary area, 6.3 ft^2, while the triangle fan delivers a consistent 8.5 ft^2
share), beside the half-space dish w(r)/w(0) =
(2/pi) E(r/R), and the pad spring factor per ring. Prints the table and writes
pad-zone-calibration.svg to --out. Units kip, ft; settlement printed in inches.
"""

from __future__ import annotations

import argparse
import math
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from doc_figures import CONC, INK, MUTED, Fig  # noqa: E402
from study_palette import profile_colour  # noqa: E402
from tankbuilder import TankModel, load_config, parse_s2k  # noqa: E402
from tankbuilder.model import boussinesq_dish  # noqa: E402



def plate_profile(cfg: Path, case: str) -> dict:
    """{name, R, rings: [(r, U3 mean, ks factor)], ringwall: U3 mean of the ring wall axis joints}."""
    spec, s2k = load_config(cfg)
    m = TankModel(spec)
    tables = parse_s2k((s2k.parent / "results.s2k").read_text())
    u3 = {}
    for row in tables["JOINT DISPLACEMENTS"]:
        if row["OUTPUTCASE"] == case:
            u3[int(row["JOINT"])] = float(row["U3"])
    if not u3:
        raise SystemExit(f"{s2k.parent / 'results.s2k'}: no rows for case {case}")
    radii = m.cap_radii_of["baseplate"]
    ring_of = m.cap_ring["baseplate"]
    acc: dict[int, list[float]] = defaultdict(list)
    for j in m.baseplate_joints:
        if j in u3:
            acc[ring_of[j]].append(u3[j])
    rings = []
    for k in sorted(acc):
        r = radii[k]
        fac = m.pad_zone_factor(k) if k < len(radii) - (1 if m.overhang_joints else 0) else float("nan")
        bearing = any(j in set(m.bearing_joints) or j in set(m.base_joints) for j in m.baseplate_joints if ring_of[j] == k)
        rings.append((r, sum(acc[k]) / len(acc[k]), fac, bearing))
    rw = [u3[j] for j in m.ringwall_joints if j in u3]
    return {"name": s2k.stem, "R": spec.radius, "ks": spec.subgrade_modulus, "ks_rw": spec.ringwall_subgrade or spec.subgrade_modulus,
            "zone": spec.pad_zone, "rings": rings, "ringwall": sum(rw) / len(rw) if rw else float("nan"),
            "p": spec.fluid_weight * spec.fill_height, "plate_w": spec.baseplate_thickness * spec.mat_unit_weight}


def plateau(p: dict) -> float:
    """Reference settlement: the flat Winkler value (pressure + plate weight) / pad ks, which
    is also the centre of the ideal dish. Negative = down, like U3."""
    return -(p["p"] + p["plate_w"]) / p["ks"]


def print_table(profiles: list[dict]) -> None:
    for p in profiles:
        w0 = plateau(p)
        print(f"\n=== {p['name']}  zone {p['zone']}  pad ks {p['ks']:g}  ring wall ks {p['ks_rw']:g}  "
              f"p {p['p']:.4f} ksf  reference (p + plate)/ks {w0 * 12:.4f} in  (centre joint {p['rings'][0][1] * 12:.4f} in, spring artefact)")
        print(f"{'r ft':>8} {'r/R':>6} {'U3 in':>9} {'U3/w_ref':>9} {'Bouss.':>7} {'ks fac':>7}  support")
        for r, u, fac, bearing in p["rings"]:
            rho = r / p["R"]
            print(f"{r:8.3f} {rho:6.3f} {u * 12:9.4f} {u / w0:9.3f} {boussinesq_dish(rho):7.3f} "
                  f"{fac:7.3f}  {'ring wall' if bearing else 'pad'}")
        print(f"ring wall axis U3 {p['ringwall'] * 12:.4f} in = {p['ringwall'] / w0:.3f} x reference")


def fig_dish(profiles: list[dict]) -> str:
    R = profiles[0]["R"]
    f = Fig(960, 520, "PAD SPRING ZONING — settlement dish of the stripped base under uniform pressure vs the Boussinesq flexible circle",
            "Left: plate settlement normalised to the centre against r/R for each pad zoning, with the elastic half-space dish for a flexible uniformly loaded circle. Right: the pad subgrade factor per plate ring for each zoning.")
    # --- left: normalised dish
    x0, x1, y0, y1 = 70, 560, 430, 70
    f.map(0.0, 1.0, 1.05, 0.3, x0, x1, y0, y1)          # settlement down the page, like the dish itself
    f.line(x0, y1, x1, y1, stroke=MUTED, w=.8); f.line(x0, y0, x0, y1, stroke=MUTED, w=.8)
    for v in (0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0):
        f.line(x0 - 4, f.Y(v), x1, f.Y(v), stroke=MUTED, w=.4, dash="2 4")
        f.text(x0 - 8, f.Y(v) + 4, f"{v:.1f}", anchor="end", size=10, fill=MUTED)
    for v in (0, 0.2, 0.4, 0.6, 0.8, 1.0):
        f.line(f.X(v), y1 - 4, f.X(v), y1, stroke=MUTED, w=.8)
        f.text(f.X(v), y1 - 8, f"{v:.1f}", anchor="middle", size=10, fill=MUTED)
    f.text((x0 + x1) / 2, y1 - 22, "r / R  (R = shell radius; ring wall shaded)", anchor="middle", size=11, fill=MUTED)
    f.text(x0 - 60, y0 + 16, "w(r) / w_ref, down", size=11, fill=MUTED)
    rw_in = (R - 0.625) / R
    f.add(f'<rect x="{f.X(rw_in):.1f}" y="{y1:.1f}" width="{(x1 - f.X(rw_in)):.1f}" height="{y0 - y1:.1f}" fill="{CONC}" fill-opacity=".5"/>')
    # Boussinesq
    pts = [(k / 60.0, boussinesq_dish(k / 60.0)) for k in range(61)]
    f.path("M" + " L".join(f"{f.X(r):.1f} {f.Y(w):.1f}" for r, w in pts), stroke=INK, w=2.2, dash="7 4")
    f.text(f.X(0.05), f.Y(0.40), "dashed: half-space, flexible circle, (2/π) E(r/R), 0.64 at the edge", size=10, fill=INK)
    f.text(f.X(0.05), f.Y(0.43), f"w_ref = (p + plate) / ks = {-plateau(profiles[0]) * 12:.3f} in", size=10, fill=MUTED)
    for p in profiles:
        col = profile_colour(p)
        w0 = plateau(p)
        pts = [(r / R, u / w0) for r, u, _, _ in p["rings"] if r > 0.0]
        f.path("M" + " L".join(f"{f.X(r):.1f} {f.Y(max(0.3, min(1.05, w))):.1f}" for r, w in pts), stroke=col, w=1.8)
        for r, w in pts:
            f.dot(f.X(r), f.Y(max(0.3, min(1.05, w))), 3, fill=col)
        f.dot(f.X(1.0), f.Y(max(0.3, min(1.05, p["ringwall"] / w0))), 5, fill="none", stroke=col)
    ly = f.Y(0.47)
    for p in profiles:
        col = profile_colour(p)
        w0 = plateau(p)
        edge = [u for r, u, _, _ in p["rings"] if r < R - 0.625][-1] / w0
        f.line(x0 + 14, ly, x0 + 44, ly, stroke=col, w=2)
        f.text(x0 + 50, ly + 4, f"{p['name'][8:]}: last pad ring {edge:.2f}, ring wall {p['ringwall'] / w0:.2f} (open circle)", size=10, fill=col)
        ly += 15
    # --- right: factor per ring
    x0, x1, y0, y1 = 640, 930, 430, 70
    f.map(0.0, 1.0, 0.8, 2.2, x0, x1, y0, y1)
    f.line(x0, y0, x1, y0, stroke=MUTED, w=.8); f.line(x0, y0, x0, y1, stroke=MUTED, w=.8)
    for v in (1.0, 1.2, 1.4, 1.6, 1.8, 2.0):
        f.line(x0 - 4, f.Y(v), x1, f.Y(v), stroke=MUTED, w=.4, dash="2 4")
        f.text(x0 - 8, f.Y(v) + 4, f"{v:.1f}", anchor="end", size=10, fill=MUTED)
    for v in (0, 0.5, 1.0):
        f.text(f.X(v), y0 + 16, f"{v:.1f}", anchor="middle", size=10, fill=MUTED)
    f.text((x0 + x1) / 2, y0 + 32, "r / R", anchor="middle", size=11, fill=MUTED)
    f.text(x0 - 60, y1 - 12, "ks factor", size=11, fill=MUTED)
    pts = [(k / 60.0, 1.0 / boussinesq_dish(k / 60.0)) for k in range(61)]
    f.path("M" + " L".join(f"{f.X(r):.1f} {f.Y(w):.1f}" for r, w in pts), stroke=INK, w=2.2, dash="7 4")
    f.text(x1, f.Y(math.pi / 2) - 8, "π/2 = 1.571", anchor="end", size=10, fill=INK)
    for p in profiles:
        col = profile_colour(p)
        pts = [(r / R, fac) for r, _, fac, bearing in p["rings"] if not bearing and not math.isnan(fac)]
        d = ""
        for i, (r, fac) in enumerate(pts):
            d += ("M" if i == 0 else " L") + f"{f.X(r):.1f} {f.Y(fac):.1f}"
        f.path(d, stroke=col, w=1.8)
        for r, fac in pts:
            f.dot(f.X(r), f.Y(fac), 3, fill=col)
        f.dot(f.X(1.0), f.Y(p["ks_rw"] / p["ks"]), 5, fill="none", stroke=col)
    f.text(x0, y1 - 20, "pad ks factor per plate ring; open circle = ring wall soil / pad ks", size=10, fill=MUTED)
    f.text(16, 500, "Stripped base: plate + pad + ring wall only (0.25 ft x 0.001 ft stub wall, no roof, weightless ring wall), uniform 3.14 ksf on the plate. NL_HYDRO plate U3, joint means per ring, normalised to the flat Winkler value (p + plate weight) / pad ks (centre joint omitted: its spring is 1.3 x too soft, a tributary-area artefact of the fan).", fill=MUTED, size=10)
    return f.done()


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("configs", nargs="+", type=Path)
    p.add_argument("--case", default="NL_HYDRO")
    p.add_argument("--out", type=Path)
    a = p.parse_args(argv)
    profiles = [plate_profile(c, a.case) for c in a.configs]
    print_table(profiles)
    if a.out:
        a.out.mkdir(parents=True, exist_ok=True)
        path = a.out / "pad-zone-calibration.svg"
        path.write_bytes(fig_dish(profiles).encode("utf-8"))
        print("\nwrote", path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
