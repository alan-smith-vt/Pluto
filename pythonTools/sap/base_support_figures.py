#!/usr/bin/env python3
"""Figures for the wall-base support side study: how the shell load reaches the soil at
the rim on the coarse and the fine mesh, and the wall / plate profiles there.

    python base_support_figures.py base_data.json TANK-A-fine=links_fine.json [TANK-A-bearing=links_bearing.json] --out <dir>

base_data.json comes from a results.s2k dump (plate U3 by ring, wall rows F11 / F22 / M11
by z, both models); links_fine.json from Results.LinkForce over the OAPI (fine model).
Writes base-support-model.svg, base-support-loads.svg, base-wall-profiles.svg.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from doc_figures import CONC, GREEN, INK, MUTED, RUST, SOIL, STEEL, Fig  # noqa: E402

COARSE, FINE, BEARING = "TANK-A-ringwallC", "TANK-A-fine", "TANK-A-bearing"
SERIES = ((COARSE, STEEL, "coarse"), (FINE, RUST, "fine"), (BEARING, GREEN, "bearing"))
R, C, A = 33.625, 1.25, 3.75          # shell radius, ring wall width, depth (ft)


def spring(f: Fig, x, y0, y1, color=INK, n=5, amp=4.0):
    """Zigzag spring from (x, y0) down to (x, y1)."""
    seg = (y1 - y0) / (n * 2 + 2)
    d = f"M{x:.1f} {y0:.1f} L{x:.1f} {y0 + seg:.1f}"
    y = y0 + seg
    for i in range(n * 2):
        y += seg
        d += f" L{x + (amp if i % 2 == 0 else -amp):.1f} {y:.1f}"
    d += f" L{x:.1f} {y1:.1f}"
    f.path(d, stroke=color, w=1.2)
    f.line(x - 7, y1, x + 7, y1, stroke=color, w=1.6)


def fig_model(radii_fine) -> str:
    f = Fig(900, 470, "RIM SUPPORT AS MODELLED — section through the wall foot, fine mesh joints",
            "Section through the tank wall foot: baseplate on pad springs, rim joint on a contact link to the concrete ring wall, ring wall on a soil spring; the two plate joints over the inner half of the ring wall are on pad springs")
    f.map(30.4, 35.0, -A - 1.6, 3.2, 70, 860, 420, 50)
    y0 = f.Y(0)
    # ring wall + ground
    f.add(f'<rect x="{f.X(R - C / 2):.1f}" y="{y0:.1f}" width="{C * f.sx:.1f}" height="{-A * f.sz:.1f}" fill="{CONC}" stroke="{INK}" stroke-width="1"/>')
    f.add(f'<rect x="{f.X(30.4):.1f}" y="{y0:.1f}" width="{(R - C / 2 - 30.4) * f.sx:.1f}" height="{-0.5 * f.sz:.1f}" fill="{SOIL}"/>')
    f.text(f.X(31.2), y0 + 14, "sand pad", fill=MUTED, size=11)
    f.text(f.X(R + C / 2) - 6, y0 - A * f.sz + 16, "ring wall", anchor="end", size=11)
    f.text(f.X(R + C / 2) - 6, y0 - A * f.sz + 30, f"{C:g} × {A:g} ft", anchor="end", size=10, fill=MUTED)
    # plate and wall
    f.line(f.X(30.4), y0, f.X(R), y0, w=3)
    f.line(f.X(R), y0, f.X(R), f.Y(3.0), w=3)
    f.text(f.X(R) + 8, f.Y(2.6), "wall, 1/2 in", size=11)
    f.text(f.X(31.0), y0 - 8, "baseplate, 3/8 in", size=11)
    # plate joints (fine mesh) with pad springs, the last two over the concrete
    yg = f.Y(-1.1)
    for r in radii_fine:
        if r < 30.5 or r >= R - 1e-6:
            continue
        over_conc = r >= R - C / 2 - 1e-6
        col = RUST if over_conc else STEEL
        f.dot(f.X(r), y0, 3.5, fill=col)
        spring(f, f.X(r), y0 + 4, yg, color=col)
        f.text(f.X(r), yg + 14, f"r = {r:.2f}", anchor="middle", size=9, fill=col)
    f.text(f.X(31.6), yg + 30, "GAP_Rnn: k = 170 kcf × tributary area, to fixed ground", fill=STEEL, size=10)
    # rim joint: contact link to the ring wall axis joint, soil link below
    f.dot(f.X(R), y0, 4.5, fill=INK)
    f.line(f.X(R), y0, f.X(R), f.Y(-A / 2), stroke=GREEN, w=2.2)
    f.dot(f.X(R), f.Y(-A / 2), 3.5, fill=GREEN)
    f.text(f.X(R) + 8, f.Y(-A / 4) + 4, "GAP_CONTACT (concrete, stiff)", fill=GREEN, size=10)
    spring(f, f.X(R), f.Y(-A / 2) + 4, f.Y(-A) , color=GREEN)
    f.text(f.X(R) + 8, f.Y(-A * 0.8) + 4, "GAP_SOIL: 170 kcf × 1.25 ft × arc = 623 kip/ft", fill=GREEN, size=10)
    # annotation: the mismatch
    xa, ya = f.X(33.19), y0 - 30
    f.add(f'<rect x="{f.X(R - C / 2):.1f}" y="{y0 - 3:.1f}" width="{(C / 2) * f.sx:.1f}" height="6" fill="{RUST}" fill-opacity=".35"/>')
    f.line(xa, ya, xa, y0 - 8, stroke=RUST, w=1.2, arrow=True)
    f.text(xa, ya - 24, "inner half of the ring wall, 0.625 ft:", fill=RUST, anchor="middle", size=11, bold=True)
    f.text(xa, ya - 10, "plate joints here sit on concrete in reality, on pad springs in the model", fill=RUST, anchor="middle", size=11)
    # lambda band
    lam = 0.72
    f.line(f.X(R - 2 * lam), f.Y(2.4), f.X(R), f.Y(2.4), stroke=MUTED, w=1, arrow=True, arrow_start=True)
    f.text(f.X(R - lam) - 30, f.Y(2.4) - 6, "2λ ≈ 1.4 ft, λ = (4D/k)^¼ = 0.72 ft", anchor="middle", fill=MUTED, size=10)
    f.text(f.X(R - lam) - 30, f.Y(2.4) + 14, "plate-on-soil edge band", anchor="middle", fill=MUTED, size=10)
    f.text(16, 450, "Rim joint carries shell + roof + ring (2.22 kip/spoke) into the ring wall; the plate is welded to it. Fine mesh rings: 0.25, 0.375, 0.56, 0.84 ... ft.", fill=MUTED, size=10)
    return f.done()


def fig_model_bearing(radii_fine) -> str:
    f = Fig(900, 470, "RIM SUPPORT, PLATE BEARING ON THE RING WALL — TANK-A-bearing",
            "Section through the tank wall foot with ringwall.plate_bearing: the plate joints over the ring wall width sit on contact links to arm joints on the ring wall centroid line, tied to the ring wall axis joint by rigid arm frames; pad springs only inside the concrete")
    f.map(30.4, 35.0, -A - 1.6, 3.2, 70, 860, 420, 50)
    y0 = f.Y(0)
    f.add(f'<rect x="{f.X(R - C / 2):.1f}" y="{y0:.1f}" width="{C * f.sx:.1f}" height="{-A * f.sz:.1f}" fill="{CONC}" stroke="{INK}" stroke-width="1"/>')
    f.add(f'<rect x="{f.X(30.4):.1f}" y="{y0:.1f}" width="{(R - C / 2 - 30.4) * f.sx:.1f}" height="{-0.5 * f.sz:.1f}" fill="{SOIL}"/>')
    f.text(f.X(31.2), y0 + 14, "sand pad", fill=MUTED, size=11)
    f.text(f.X(R + C / 2) - 6, y0 - A * f.sz + 16, "ring wall", anchor="end", size=11)
    f.text(f.X(R + C / 2) - 6, y0 - A * f.sz + 30, f"{C:g} × {A:g} ft", anchor="end", size=10, fill=MUTED)
    f.line(f.X(30.4), y0, f.X(R), y0, w=3)
    f.line(f.X(R), y0, f.X(R), f.Y(3.0), w=3)
    f.text(f.X(R) + 8, f.Y(2.6), "wall, 1/2 in", size=11)
    f.text(f.X(31.0), y0 - 8, "baseplate, 3/8 in", size=11)
    yg = f.Y(-1.1)
    yc = f.Y(-A / 2)
    for r in radii_fine:
        if r < 30.5 or r >= R - 1e-6:
            continue
        if r >= R - C / 2 - 1e-6:
            # bearing chain: arm joint on the centroid line, arm frame from the axis joint, contact link up
            f.line(f.X(R), yc, f.X(r), yc, stroke=GREEN, w=4)
            f.dot(f.X(r), yc, 3.5, fill=GREEN)
            f.line(f.X(r), yc, f.X(r), y0 + 4, stroke=GREEN, w=2.2)
            f.dot(f.X(r), y0, 3.5, fill=GREEN)
            f.text(f.X(r), yc - 8, f"r = {r:.2f}", anchor="middle", size=9, fill=GREEN)
        else:
            f.dot(f.X(r), y0, 3.5, fill=STEEL)
            spring(f, f.X(r), y0 + 4, yg, color=STEEL)
            f.text(f.X(r), yg + 14, f"r = {r:.2f}", anchor="middle", size=9, fill=STEEL)
    f.text(f.X(30.6), yg + 30, "GAP_Rnn pad springs, unchanged inside r = 33.0 ft", fill=STEEL, size=10)
    f.dot(f.X(R), y0, 4.5, fill=INK)
    f.line(f.X(R), y0, f.X(R), yc, stroke=GREEN, w=2.2)
    f.dot(f.X(R), yc, 4.5, fill=GREEN)
    spring(f, f.X(R), yc + 4, f.Y(-A), color=GREEN)
    f.text(f.X(R) + 8, f.Y(-A * 0.8) + 4, "GAP_SOIL, 623 kip/ft per spoke", fill=GREEN, size=10)
    f.text(f.X(R) + 8, f.Y(-A / 4) + 4, "GAP_CONTACT at the rim", fill=GREEN, size=10)
    # callouts
    f.text(f.X(32.9), y0 - 60, "PLATE_BEARING joints → GAP_BEARING links", fill=GREEN, anchor="end", size=11, bold=True)
    f.text(f.X(32.9), y0 - 46, "k = E_c × tributary area / depth, compression only", fill=GREEN, anchor="end", size=10)
    f.line(f.X(32.92), y0 - 50, f.X(33.15), y0 - 8, stroke=GREEN, w=1.2, arrow=True)
    f.text(f.X(R - C / 2) - 8, yc + 34, "RINGWALL_ARM frames (ring wall section),", fill=GREEN, anchor="end", size=10)
    f.text(f.X(R - C / 2) - 8, yc + 46, "chained from the RINGWALL_AXIS joint radially in", fill=GREEN, anchor="end", size=10)
    f.text(16, 450, "The ring wall section is rigid across its 1.25 ft width, so the arm frames and axis joint act as one body per spoke. Settlement off in this model.", fill=MUTED, size=10)
    return f.done()


def fig_loads(base, links) -> str:
    f = Fig(900, 520, "DEAD LOAD AT THE RIM — settlement profile and where the shell weight goes",
            "Left: baseplate settlement against radius near the rim for the coarse and fine mesh. Right: the dead-load path into the soil per mesh: contact links into the ring wall versus the plate's outer soil springs")
    # --- left: settlement profile
    x0, x1, y0, y1 = 70, 470, 420, 70
    f.map(30.0, R + 0.2, -0.0075, 0.0005, x0, x1, y0, y1)
    f.line(x0, f.Y(0), x1, f.Y(0), stroke=MUTED, w=.8)
    f.line(x0, y0, x0, y1, stroke=MUTED, w=.8)
    for v in (0, -0.002, -0.004, -0.006):
        f.line(x0 - 4, f.Y(v), x0, f.Y(v), stroke=MUTED, w=.8)
        f.text(x0 - 8, f.Y(v) + 4, f"{v * 12:.3g} in".replace("-0 in", "0"), anchor="end", size=10, fill=MUTED)
    for r in (30, 31, 32, 33):
        f.line(f.X(r), y0, f.X(r), y0 + 4, stroke=MUTED, w=.8)
        f.text(f.X(r), y0 + 16, f"{r}", anchor="middle", size=10, fill=MUTED)
    f.text((x0 + x1) / 2, y0 + 32, "radius, ft", anchor="middle", size=11, fill=MUTED)
    f.text(x0 - 60, y1 - 10, "plate U3", size=11, fill=MUTED)
    # ring wall footprint
    f.add(f'<rect x="{f.X(R - C / 2):.1f}" y="{y1:.1f}" width="{(C / 2 + 0.2) * f.sx:.1f}" height="{y0 - y1:.1f}" fill="{CONC}" fill-opacity=".6"/>')
    f.text(f.X(R - C / 4), y1 + 14, "ring wall", anchor="middle", size=10, fill=MUTED)
    for name, col, lab in SERIES:
        if name not in base:
            continue
        pts = [(p["r"], p["U3"]) for p in base[name]["NL_DEAD"]["plate_U3"] if p["r"] >= 29.5]
        f.path("M" + " L".join(f"{f.X(r):.1f} {f.Y(u):.1f}" for r, u in pts), stroke=col, w=2)
        for r, u in pts:
            f.dot(f.X(r), f.Y(u), 3, fill=col)
    ty = y0 - 26 - 14 * (len([1 for n, _, _ in SERIES if n in base]) - 1)
    for name, col, lab in SERIES:
        if name in base:
            rim = base[name]["NL_DEAD"]["plate_U3"][-1]["U3"]
            f.text(x0 + 10, ty, f"{lab}: rim {rim:.4f} ft", fill=col, size=10)
            ty += 14
    # --- right: load path bars
    bx0, bw, gap = 530, 90, 30
    top, bot = 90, 420
    scale = (bot - top) / 175.0
    cols = [("coarse", [("ring wall via contact links", 159.8, GREEN), ("pad springs, outer rings", 0.0, RUST)])]
    fine_contact = fine_outer = fine_over_conc = 0.0
    for name, _, lab in SERIES[1:]:
        if name not in links:
            continue
        ld = links[name]["NL_DEAD"]
        contact = -ld["GAP_CONTACT"] - ld.get("GAP_BEARING", 0.0)
        outer = -sum(v for k, v in ld.items() if k in ("R13", "R14", "R15"))
        if name == FINE:
            fine_contact, fine_outer, fine_over_conc = contact, outer, -(ld["R14"] + ld["R15"])
        cols.append((lab, [("ring wall via contact links", contact, GREEN), ("pad springs, outer 3 rings", outer, RUST)]))
    for i, (lab, parts) in enumerate(cols):
        x = bx0 + i * (bw + gap)
        y = bot
        for pl, v, c in parts:
            h = v * scale
            f.add(f'<rect x="{x}" y="{y - h:.1f}" width="{bw}" height="{h:.1f}" fill="{c}" fill-opacity=".85" stroke="{INK}" stroke-width=".6"/>')
            if v > 0:
                f.text(x + bw / 2, y - h / 2 + 4, f"{v:.1f}", anchor="middle", size=11, fill="#ffffff", bold=True)
            y -= h
        f.text(x + bw / 2, bot + 16, lab, anchor="middle", size=11, bold=True)
    f.text(bx0 + (len(cols) * (bw + gap) - gap) / 2, bot + 30, "shell + roof + ring = 159.8 kip in every model", anchor="middle", size=9, fill=MUTED)
    f.line(bx0 - 20, bot, bx0 + len(cols) * (bw + gap) - gap + 20, bot, stroke=MUTED, w=.8)
    xf = bx0 + len(cols) * (bw + gap) - gap - bw
    if fine_outer:
        xfine = bx0 + (bw + gap) * [c[0] for c in cols].index("fine")
        yy = bot - (fine_contact + fine_outer / 2) * scale
        f.text(xfine + bw / 2, yy - 14, f"{fine_over_conc:.0f} of {fine_outer:.0f}", anchor="middle", size=9, fill="#ffffff")
        f.text(xfine + bw / 2, yy + 20, "over concrete", anchor="middle", size=9, fill="#ffffff")
    f.text(bx0 - 20, top - 30, "NL_DEAD: what carries the shell weight (kip)", size=11, bold=True)
    f.text(bx0 - 20, top - 16, "green: rim → GAP_CONTACT → ring wall (+ GAP_BEARING, which reads 0)", size=10, fill=MUTED)
    f.text(bx0 - 20, top - 4, "red: plate → pad springs, outer 3 rings (incl. plate weight)", size=10, fill=MUTED)
    f.text(16, 500, "Both meshes close on the total (362.7 kip on the soil incl. plate 54.4 and ring wall 148.5). The split is what moved.", fill=MUTED, size=10)
    return f.done()


def fig_profiles(base) -> str:
    f = Fig(900, 560, "WALL FOOT PROFILES — bottom 4 ft, corner means around the circumference",
            "Four panels of wall profiles against height above the base: dead-load F11 corners and element means with the statics line, dead-load hoop force F22, dead-load meridional moment M11, and hydrostatic M11; coarse and fine mesh")
    panels = [
        ("F11 dead (kip/ft): corners vs element means", "F11", "NL_DEAD", (-1.3, -0.4)),
        ("F22 dead, hoop (kip/ft)", "F22", "NL_DEAD", (-1.0, 4.0)),
        ("M11 dead (kip-ft/ft)", "M11", "NL_DEAD", (-0.16, 0.04)),
        ("M11 hydro (kip-ft/ft): pinned 0 / fixed 1.33 at the base", "M11", "NL_HYDRO", (-0.5, 0.7)),
    ]
    W, Hh = 380, 200
    for i, (title, field, case, (v0, v1)) in enumerate(panels):
        px, py = 60 + (i % 2) * 440, 60 + (i // 2) * 245
        x0, x1, y0, y1 = px + 40, px + W, py + Hh, py + 20
        f.map(v0, v1, 0.0, 4.0, x0, x1, y0, y1)
        f.text(px, py + 8, title, size=11, bold=True)
        f.line(x0, y0, x1, y0, stroke=MUTED, w=.8); f.line(x0, y0, x0, y1, stroke=MUTED, w=.8)
        if v0 < 0 < v1:
            f.line(f.X(0), y0, f.X(0), y1, stroke=MUTED, w=.6, dash="4 3")
        for z in (0, 1, 2, 3, 4):
            f.line(x0 - 4, f.Y(z), x0, f.Y(z), stroke=MUTED, w=.8)
            f.text(x0 - 7, f.Y(z) + 4, f"{z}", anchor="end", size=9, fill=MUTED)
        f.text(px + 4, y1 + 4, "z ft", size=9, fill=MUTED)
        for k in range(5):
            v = v0 + (v1 - v0) * k / 4
            f.line(f.X(v), y0, f.X(v), y0 + 4, stroke=MUTED, w=.8)
            f.text(f.X(v), y0 + 15, f"{v:.2g}", anchor="middle", size=9, fill=MUTED)
        for name, col, _ in SERIES:
            if name not in base:
                continue
            rows = base[name][case]["wall_corner"]
            pts = [(p[field], p["z"]) for p in rows if p["z"] <= 4.0]
            pts = [(max(v0, min(v1, v)), z) for v, z in pts]
            f.path("M" + " L".join(f"{f.X(v):.1f} {f.Y(z):.1f}" for v, z in pts), stroke=col, w=1.8)
            for v, z in pts:
                f.dot(f.X(v), f.Y(z), 2.5, fill=col)
            if field == "F11" and case == "NL_DEAD":
                em = [(p["F11"], p["z"]) for p in base[name][case]["wall_elem_F11"] if p["z"] <= 4.0]
                for v, z in em:
                    f.add(f'<rect x="{f.X(v) - 3:.1f}" y="{f.Y(z) - 3:.1f}" width="6" height="6" fill="none" stroke="{col}" stroke-width="1.4"/>')
        if field == "F11" and case == "NL_DEAD":
            # statics: N_z = -(0.2871 + 0.49 * 0.041667 * (9 - z)) for z in course 1
            # roof + ring 0.2871, courses 2 + 3 above 9 ft 0.2858, course 1 below at 0.0204 per ft
            pts = [(-(0.2871 + 0.2858 + 0.49 * 0.0416667 * (9.0 - z)), z) for z in (0.0, 4.0)]
            f.path("M" + " L".join(f"{f.X(v):.1f} {f.Y(z):.1f}" for v, z in pts), stroke=INK, w=1.2, dash="6 3")
            f.text(x1 - 4, y1 + 14, "dashed: N_z statics; squares: element means", anchor="end", size=9, fill=MUTED)
    f.text(60, 545, "coarse = 1 ft rows; fine = 0.25 ft rows; bearing = fine + plate on the ring wall. Corner means per ring; F11 spike = recovery, F22 / M11 = foot rotation.", fill=MUTED, size=10)
    lx = 560
    for name, col, lab in SERIES:
        if name in base:
            f.line(lx, 24, lx + 30, 24, stroke=col, w=2); f.text(lx + 36, 28, lab, size=10)
            lx += 110
    return f.done()


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("base_json", type=Path)
    p.add_argument("links_json", type=Path, nargs="+", help="<model name>=<json> per model")
    p.add_argument("--out", type=Path, required=True)
    a = p.parse_args(argv)
    base = json.loads(a.base_json.read_text())
    links = {}
    for item in a.links_json:
        name, _, path = str(item).partition("=")
        links[name] = json.loads(Path(path).read_text())
    a.out.mkdir(parents=True, exist_ok=True)
    figs = {
        "base-support-model.svg": fig_model(base[FINE]["plate_radii"]),
        "base-support-loads.svg": fig_loads(base, links),
        "base-wall-profiles.svg": fig_profiles(base),
    }
    if BEARING in base:
        figs["base-support-bearing.svg"] = fig_model_bearing(base[BEARING]["plate_radii"])
    for name, svg in figs.items():
        (a.out / name).write_bytes(svg.encode("utf-8"))
        print("wrote", a.out / name)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
