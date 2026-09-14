#!/usr/bin/env python3
"""Section through the wall foot with the baseplate response of several models overlaid:
deflected plate (exaggerated) over the sand, ring wall and wall drawn to scale, then the
plate transverse shear V13 and meridional moment M11 on the same radius axis.

    python foot_section_figure.py <config.toml>[=label] ... [--case NL_HYDRO] [--band 5] [--scale 30] --out <dir>

Per model: plate U3 joint means per mesh ring, V13 and M11 corner means per ring over the
baseplate shells, within `band` ft of the shell. Writes foot-section-<case>.svg.
"""

from __future__ import annotations

import argparse
import math
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from doc_figures import CONC, INK, MUTED, SOIL, Fig  # noqa: E402
from study_palette import profile_colour  # noqa: E402
from tankbuilder import TankModel, load_config, parse_s2k  # noqa: E402


def model_profile(cfg: Path, case: str, band: float) -> dict:
    spec, s2k = load_config(cfg)
    m = TankModel(spec)
    tables = parse_s2k((s2k.parent / "results.s2k").read_text())
    r_of = {j: math.hypot(xyz[0], xyz[1]) for j, xyz in m.joints.items()}
    R = spec.radius
    disp = {int(r["JOINT"]): float(r["U3"]) for r in tables["JOINT DISPLACEMENTS"] if r["OUTPUTCASE"] == case}
    plate_areas = set(m.baseplate_areas)
    acc: dict[float, dict[str, list[float]]] = defaultdict(lambda: defaultdict(list))
    for row in tables["ELEMENT FORCES - AREA SHELLS"]:
        if row["OUTPUTCASE"] != case or int(row["AREA"]) not in plate_areas:
            continue
        r = round(r_of[int(row["JOINT"])], 4)
        if r < R - band:
            continue
        acc[r]["V13"].append(float(row["V13"]))
        acc[r]["M11"].append(float(row["M11"]))
    arms: dict[float, list[float]] = defaultdict(list)
    for j in m.bearing_arm_joints:
        if j in disp:
            arms[round(r_of[j], 4)].append(disp[j])
    u: dict[float, list[float]] = defaultdict(list)
    for j in m.baseplate_joints:
        r = round(r_of[j], 4)
        if r >= R - band and j in disp:
            u[r].append(disp[j])
    rings = sorted(acc)
    rw = [disp[j] for j in m.ringwall_joints if j in disp]
    ks_rw = spec.ringwall_subgrade or spec.subgrade_modulus
    return {
        "name": s2k.stem, "ratio": ks_rw / spec.subgrade_modulus, "R": R, "C": spec.ringwall_width,
        "A": spec.ringwall_depth, "tp": spec.baseplate_thickness, "ks": spec.subgrade_modulus,
        "r": rings,
        "U3": [sum(u[r]) / len(u[r]) for r in rings],
        "V13": [sum(acc[r]["V13"]) / len(acc[r]["V13"]) for r in rings],
        "M11": [sum(acc[r]["M11"]) / len(acc[r]["M11"]) for r in rings],
        "rw": sum(rw) / len(rw) if rw else float("nan"),
        "top": sorted((r, sum(v) / len(v)) for r, v in arms.items()),   # concrete top under each bearing joint: axis translation + roll
    }


def fig(profiles: list[dict], labels: list[str], case: str, band: float, scale: float, subtitle: str = "") -> str:
    p0 = profiles[0]
    R, C, A = p0["R"], p0["C"], p0["A"]
    r0, r1 = R - band, R + C / 2 + 0.3
    colours = [profile_colour(p) for p in profiles]      # one colour per run, shared across the study figures
    f = Fig(1000, 900, f"WALL FOOT SECTION — baseplate deflection, transverse shear and moment, {case}",
            "Three stacked panels on one radius axis through the wall foot: the deflected baseplate over the sand pad, ring wall and wall for six ring wall soil stiffnesses, then the plate transverse shear V13 and the meridional moment M11 against radius.")
    X0, X1 = 150, 940
    # --- panel 1: section with deflected plates -----------------------------------
    y_top, y_bot = 70, 380
    deepest = min(min(p["U3"]) for p in profiles) * scale          # ft on the drawing, negative
    zmin, zmax = min(-0.6, deepest * 1.12), 0.45
    f.map(r0, r1, zmin, zmax, X0, X1, y_bot, y_top)
    y0 = f.Y(0.0)
    f.add(f'<rect x="{f.X(r0):.1f}" y="{y0:.1f}" width="{(R - C / 2 - r0) * f.sx:.1f}" height="{-0.5 * f.sz:.1f}" fill="{SOIL}"/>')
    f.add(f'<rect x="{f.X(R - C / 2):.1f}" y="{y0:.1f}" width="{C * f.sx:.1f}" height="{(zmin) * f.sz:.1f}" fill="{CONC}" stroke="{INK}" stroke-width=".8"/>')
    f.text(f.X(r0) + 6, y0 + 16, "sand pad", fill=MUTED, size=10)
    f.text(f.X(R - C / 2) + 6, f.Y(-0.95), f"ring wall {C:g} × {A:g} ft", fill=INK, size=10)
    f.line(f.X(r0), y0, f.X(R + 0.125), y0, stroke=MUTED, w=1, dash="4 3")
    f.line(f.X(R), y0, f.X(R), f.Y(zmax), stroke=INK, w=3)
    f.text(f.X(R) + 6, f.Y(zmax) + 14, "wall", size=10)
    f.text(f.X(R - C / 2) - 6, y0 - 6, "undeformed plate", fill=MUTED, size=9, anchor="end")
    for (p, lab, col) in zip(profiles, labels, colours):
        pts = [(r, u * scale) for r, u in zip(p["r"], p["U3"])]
        f.path("M" + " L".join(f"{f.X(r):.1f} {f.Y(z):.1f}" for r, z in pts), stroke=col, w=2)
        for r, z in pts:
            f.dot(f.X(r), f.Y(z), 2.4, fill=col)
        # concrete top, settled and rolled: through the arm joints (axis + R1 x arm), extended to the faces
        top = p["top"]
        if len(top) >= 2:
            (ra, za), (rb, zb) = top[0], top[-1]
            slope = (zb - za) / (rb - ra)
            zi, zo = za + slope * (R - C / 2 - ra), za + slope * (R + C / 2 - ra)
            f.line(f.X(R - C / 2), f.Y(zi * scale), f.X(R + C / 2), f.Y(zo * scale), stroke=col, w=1.2, dash="2 2")
        else:
            zr = p["rw"] * scale
            f.line(f.X(R - C / 2), f.Y(zr), f.X(R + C / 2), f.Y(zr), stroke=col, w=1.2, dash="2 2")
    # settlement axis, inches, at the left edge of the section: 0 = undeformed plate level
    ax = X0 - 12
    f.line(ax, f.Y(0.0), ax, f.Y(zmin + 0.05), stroke=INK, w=1)
    span_in = -zmin * 12.0 / scale                                   # inches of settlement shown
    step = next(s for s in (0.02, 0.05, 0.1, 0.2, 0.25, 0.5, 1.0) if span_in / s <= 7)
    v = 0.0
    while True:
        z = v * scale / 12.0
        if z < zmin + 0.03:
            break
        f.line(ax - 4, f.Y(z), ax, f.Y(z), stroke=INK, w=1)
        f.text(ax - 7, f.Y(z) + 4, f"{v:+.2f}" if v else "0", anchor="end", size=9)
        v -= step
    f.text(ax - 7, f.Y(0.0) - 12, "plate U3, in", anchor="end", size=9, fill=INK)
    f.text(ax - 7, f.Y(0.0) - 2, "(0 = undeformed level)", anchor="end", size=8, fill=MUTED)
    f.text(X0, y_top - 10, f"deflected baseplate, displacements × {scale:g}; dotted = concrete top, settled and rolled (through the bearing arm joints)", size=11, bold=True)
    if subtitle:
        f.text(X0, y_top - 24, subtitle, size=10, fill=MUTED)
    # legend
    lx, ly = X0 + 20, y_top + 8
    for (p, lab, col) in zip(profiles, labels, colours):
        f.line(lx, ly, lx + 26, ly, stroke=col, w=2.4)
        f.text(lx + 32, ly + 4, lab, size=10, fill=col)
        ly += 14
    # --- panels 2 and 3 -------------------------------------------------------------
    def panel(y_top, y_bot, key, title, fmt_ticks, tp=None, peak_ticks=False):
        vals = [v for p in profiles for v in p[key]]
        lo, hi = min(vals + [0.0]), max(vals + [0.0])
        pad = (hi - lo) * 0.1 or 0.1
        f.map(r0, r1, lo - pad, hi + pad, X0, X1, y_bot, y_top)
        f.add(f'<rect x="{f.X(R - C / 2):.1f}" y="{y_top:.1f}" width="{C * f.sx:.1f}" height="{y_bot - y_top:.1f}" fill="{CONC}" fill-opacity=".45"/>')
        f.line(X0, y_bot, X1, y_bot, stroke=MUTED, w=.8); f.line(X0, y_bot, X0, y_top, stroke=MUTED, w=.8)
        f.line(X0, f.Y(0), X1, f.Y(0), stroke=MUTED, w=.6, dash="4 3")
        f.line(f.X(R), y_top, f.X(R), y_bot, stroke=INK, w=1, dash="2 3")
        for v in fmt_ticks:
            if lo - pad <= v <= hi + pad:
                f.line(X0 - 4, f.Y(v), X0, f.Y(v), stroke=MUTED, w=.8)
                lab = f"{v:g}" + (f"  ({6 * v / tp / tp / 144:.0f} ksi)" if tp and v else "")
                f.text(X0 - 7, f.Y(v) + 4, lab, anchor="end", size=9, fill=MUTED)
        if peak_ticks:
            # one tick per curve at its extreme, with a light dotted lead line out to the peak point
            peaks = []
            for (p, lab, col) in zip(profiles, labels, colours):
                r_pk, v_pk = max(zip(p["r"], p[key]), key=lambda t: abs(t[1]))
                f.line(X0, f.Y(v_pk), f.X(r_pk), f.Y(v_pk), stroke=col, w=.7, dash="1 3")
                f.line(X0 - 4, f.Y(v_pk), X0, f.Y(v_pk), stroke=col, w=1.2)
                txt = f"{v_pk:+.2f}" + (f" ({6 * v_pk / tp / tp / 144:+.0f} ksi)" if tp else "")
                peaks.append([f.Y(v_pk), txt, col])
            # labels: keep at least 10 px apart, pushed down the page in order, ticks stay put
            peaks.sort(key=lambda q: q[0])
            ys = [q[0] for q in peaks]
            for i in range(1, len(ys)):
                ys[i] = max(ys[i], ys[i - 1] + 10.0)
            over = ys[-1] - min(y_bot, peaks[-1][0] + 4)
            if over > 0:                                        # slide the stack up if it ran off the panel
                ys = [y - over for y in ys]
            for (y_true, txt, col), y_lab in zip(peaks, ys):
                if abs(y_lab - y_true) > 0.5:
                    f.line(X0 - 6, y_true, X0 - 12, y_lab, stroke=col, w=.6)
                f.text(X0 - 14, y_lab + 3, txt, anchor="end", size=8, fill=col)
        for (p, lab, col) in zip(profiles, labels, colours):
            pts = list(zip(p["r"], p[key]))
            f.path("M" + " L".join(f"{f.X(r):.1f} {f.Y(v):.1f}" for r, v in pts), stroke=col, w=1.8)
            for r, v in pts:
                f.dot(f.X(r), f.Y(v), 2.2, fill=col)
        f.text(X0, y_top - 10, title, size=11, bold=True)
    panel(430, 600, "V13", "plate transverse shear V13, kip/ft (corner means per ring; shaded = ring wall)", [-1, 0, 1, 2, 3])
    panel(650, 830, "M11", "plate meridional moment M11, kip-ft/ft, + = sagging; a tick per curve at its peak, with the face stress in the 3/8 in plate",
          [0], tp=p0["tp"], peak_ticks=True)
    # shared r axis
    for r in range(int(math.ceil(r0)), int(r1) + 1):
        f.line(f.X(r), 830, f.X(r), 834, stroke=MUTED, w=.8)
        f.text(f.X(r), 848, f"{r}", anchor="middle", size=10, fill=MUTED)
    for r in (R - C / 2, R, R + C / 2):
        f.text(f.X(r), 862, f"{r:.3g}", anchor="middle", size=9, fill=INK)
    f.text((X0 + X1) / 2, 880, "radius, ft   (33.0 concrete inner face, 33.625 shell, 34.25 outer face; plate edge at 33.75)", anchor="middle", size=10, fill=MUTED)
    return f.done()


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("configs", nargs="+", help="<config.toml>[=label]")
    p.add_argument("--case", default="NL_HYDRO")
    p.add_argument("--band", type=float, default=5.0)
    p.add_argument("--scale", type=float, default=None, help="displacement multiplier; default puts the deepest settlement at ~1 ft on the drawing")
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--name", default=None, help="output file stem (default foot-section-<case>)")
    p.add_argument("--subtitle", default="", help="one line under the section title, e.g. what the pad springs are")
    a = p.parse_args(argv)
    profiles, labels = [], []
    for item in a.configs:
        path, _, label = item.partition("=")
        pr = model_profile(Path(path), a.case, a.band)
        profiles.append(pr)
        labels.append(label or f"{pr['name']} (ring wall {pr['ratio']:g} × pad)")
    if a.scale is None:
        deepest = max(-min(pr["U3"]) for pr in profiles)               # ft
        raw = 1.0 / deepest if deepest > 0 else 60.0
        a.scale = next(s for s in (5, 10, 15, 20, 25, 30, 40, 50, 60, 80, 100, 150, 200) if s >= raw) if raw <= 200 else 200
    a.out.mkdir(parents=True, exist_ok=True)
    out = a.out / f"{a.name or 'foot-section-' + a.case.lower()}.svg"
    out.write_bytes(fig(profiles, labels, a.case, a.band, a.scale, a.subtitle).encode("utf-8"))
    for pr, lab in zip(profiles, labels):
        print(f"{lab}: rings {len(pr['r'])}, U3 at shell {pr['U3'][[i for i, r in enumerate(pr['r']) if abs(r - pr['R']) < 1e-3][0]] * 12:+.4f} in, "
              f"V13 max {max(pr['V13'], key=abs):+.2f}, M11 min/max {min(pr['M11']):+.3f}/{max(pr['M11']):+.3f}")
    print("wrote", out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
