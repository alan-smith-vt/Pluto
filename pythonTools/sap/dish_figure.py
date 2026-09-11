#!/usr/bin/env python3
"""Quick full-width settlement dish: plate U3 against radius from the centre to the ring wall's
outer face for several models, one panel, inches, ring wall and wall drawn behind.

    python dish_figure.py <config.toml>[=label] ... [--case NL_HYDRO] --out <dir> [--name dish]
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from doc_figures import CONC, INK, MUTED, SOIL, Fig  # noqa: E402
from foot_section_figure import PALETTE, model_profile  # noqa: E402


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("configs", nargs="+")
    p.add_argument("--case", default="NL_HYDRO")
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--name", default="dish")
    a = p.parse_args(argv)
    profiles, labels = [], []
    for item in a.configs:
        path, _, label = item.partition("=")
        pr = model_profile(Path(path), a.case, 1e9)
        profiles.append(pr); labels.append(label or pr["name"])
    R, C = profiles[0]["R"], profiles[0]["C"]
    f = Fig(1000, 420, f"FULL-WIDTH SETTLEMENT DISH — plate U3 from the centre to the ring wall, {a.case}",
            "Plate settlement in inches against radius from the tank centre to the ring wall outer face for several models; the ring wall and wall drawn behind.")
    X0, X1, y_top, y_bot = 80, 950, 70, 340
    lo = min(min(pr["U3"]) for pr in profiles) * 12
    f.map(0.0, R + C / 2 + 0.2, lo * 1.1, 0.02, X0, X1, y_bot, y_top)
    f.add(f'<rect x="{f.X(R - C / 2):.1f}" y="{y_top:.1f}" width="{C * f.sx:.1f}" height="{y_bot - y_top:.1f}" fill="{CONC}" fill-opacity=".6"/>')
    f.add(f'<rect x="{X0:.1f}" y="{f.Y(0):.1f}" width="{(R - C / 2) * f.sx:.1f}" height="{y_bot - f.Y(0):.1f}" fill="{SOIL}" fill-opacity=".35"/>')
    f.line(f.X(R), f.Y(0), f.X(R), y_top, stroke=INK, w=3)
    f.line(X0, f.Y(0), X1, f.Y(0), stroke=MUTED, w=1, dash="4 3")
    f.text(f.X(R - C / 2) - 6, f.Y(0) - 6, "undeformed plate", fill=MUTED, size=9, anchor="end")
    f.text(f.X(R) + 6, y_top + 14, "wall", size=10)
    f.text(f.X(R - C / 2) - 6, y_bot - 8, "ring wall", anchor="end", size=10, fill=MUTED)
    step = 0.05 if -lo < 0.4 else 0.1
    v = 0.0
    while v >= lo * 1.1:
        f.line(X0 - 4, f.Y(v), X0, f.Y(v), stroke=INK, w=1)
        f.text(X0 - 7, f.Y(v) + 4, f"{v:+.2f}" if v else "0", anchor="end", size=9)
        v -= step
    f.text(X0 - 7, y_top - 8, "U3, in", anchor="end", size=9)
    for r in range(0, int(R) + 1, 5):
        f.line(f.X(r), y_bot, f.X(r), y_bot + 4, stroke=MUTED, w=.8)
        f.text(f.X(r), y_bot + 16, str(r), anchor="middle", size=10, fill=MUTED)
    f.text((X0 + X1) / 2, y_bot + 32, "radius, ft", anchor="middle", size=10, fill=MUTED)
    ly = y_top + 26
    for pr, lab, col in zip(profiles, labels, PALETTE):
        pts = list(zip(pr["r"], pr["U3"]))
        f.path("M" + " L".join(f"{f.X(r):.1f} {f.Y(u * 12):.1f}" for r, u in pts), stroke=col, w=1.8)
        for r, u in pts:
            f.dot(f.X(r), f.Y(u * 12), 2.2, fill=col)
        f.line(X0 + 20, ly, X0 + 46, ly, stroke=col, w=2.4)
        f.text(X0 + 52, ly + 4, lab, size=10, fill=col)
        ly += 14
    f.text(16, 400, "Joint means per plate ring. The centre joint (r = 0) is included; the axis is true, nothing exaggerated.", fill=MUTED, size=10)
    a.out.mkdir(parents=True, exist_ok=True)
    out = a.out / f"{a.name}.svg"
    out.write_bytes(f.done().encode("utf-8"))
    print("wrote", out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
