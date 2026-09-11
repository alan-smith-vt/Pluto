#!/usr/bin/env python3
"""Ring wall soil sweep: the wall foot, the plate over the concrete edge and the load split
per model, from results.s2k, as one table and one figure.

    python ringwall_sweep.py <config.toml> [<config.toml> ...] [--case NL_HYDRO] [--out <dir>]

Per model (config beside its results.s2k), for the case: wall base M11 (corner mean over the
base ring), wall M11 peak above the base and its height, wall F22 peak, foot rotation (base
ring R1 in the joint local axes = about the tangent, degrees, sign flipped so + = wall top outward), plate U3 at the pad ring
nearest the concrete and at the ring wall axis with their differential, plate M11 corner
means at the pad ring, the concrete edge, the ring over the concrete and the shell line,
plate V13 at the concrete edge, and the load into the ring wall soil links and into the last
pad ring (k x closure, gap links, compression only). Units kip, ft; settlements in inches.
Writes ringwall-sweep.svg to --out. Markdown table on stdout.
"""

from __future__ import annotations

import argparse
import math
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from doc_figures import GREEN, INK, MUTED, RUST, STEEL, Fig  # noqa: E402
from tankbuilder import TankModel, load_config, parse_s2k  # noqa: E402

FIELDS = ("F11", "F22", "M11", "M22", "V13")


def ring_means(rows, key, areas):
    acc: dict[float, dict[str, list[float]]] = defaultdict(lambda: defaultdict(list))
    for r in rows:
        if int(r["AREA"]) not in areas:
            continue
        k = key(int(r["JOINT"]))
        for f in FIELDS:
            acc[k][f].append(float(r[f]))
    return {k: {f: sum(v) / len(v) for f, v in d.items()} for k, d in acc.items()}


def nearest(d: dict, x: float):
    return d[min(d, key=lambda k: abs(k - x))]


def sweep_row(cfg: Path, case: str) -> dict:
    spec, s2k = load_config(cfg)
    m = TankModel(spec)
    tables = parse_s2k((s2k.parent / "results.s2k").read_text())
    shell = [r for r in tables["ELEMENT FORCES - AREA SHELLS"] if r["OUTPUTCASE"] == case]
    disp = {int(r["JOINT"]): r for r in tables["JOINT DISPLACEMENTS"] if r["OUTPUTCASE"] == case}
    if not shell or not disp:
        raise SystemExit(f"{s2k.parent}: no rows for {case}")
    z_of = {j: xyz[2] for j, xyz in m.joints.items()}
    r_of = {j: math.hypot(xyz[0], xyz[1]) for j, xyz in m.joints.items()}
    R, C = spec.radius, spec.ringwall_width
    wall = ring_means(shell, lambda j: round(z_of[j], 4), set(m.ids.block("area", "wall")))
    plate = ring_means(shell, lambda j: round(r_of[j], 4), set(m.baseplate_areas))
    zs = sorted(wall)
    base = wall[zs[0]]
    above = [(z, wall[z]["M11"]) for z in zs if 0.05 < z <= 4.0]
    sgn = 1.0 if base["M11"] >= 0 else -1.0
    zpk, mpk = min(above, key=lambda t: sgn * t[1])          # the reversed lobe above the base
    f22pk = max(((z, wall[z]["F22"]) for z in zs if z <= 4.0), key=lambda t: t[1])
    # foot rotation: base ring joints, local R1 (about the tangent), degrees
    rot = [float(disp[j]["R1"]) for j in m.base_joints if j in disp]
    rot_deg = -math.degrees(sum(rot) / len(rot))             # sign flipped: + = wall top outward, as the study note tabulates
    # settlements
    radii = m.cap_radii_of["baseplate"]
    ring_of = m.cap_ring["baseplate"]
    pad_rings = sorted({ring_of[j] for j in m.baseplate_joints if j not in set(m.bearing_joints) and j not in set(m.base_joints)})
    k_last = pad_rings[-1]                              # last ring still on pad springs
    r_last = radii[k_last]
    u3 = lambda js: sum(float(disp[j]["U3"]) for j in js) / len(js)
    pad_last = u3([j for j in m.baseplate_joints if ring_of[j] == k_last])
    k_ref = min(pad_rings, key=lambda k: abs(radii[k] - (R - 1.2)))   # the ring nearest r = R - 1.2 ft (32.4), comparable across meshes
    pad_ref = u3([j for j in m.baseplate_joints if ring_of[j] == k_ref])
    r_ref = radii[k_ref]
    pad_int = u3([j for j in m.baseplate_joints if ring_of[j] == max(1, k_last - 3)])
    rw = u3([j for j in m.ringwall_joints if j in disp])
    # loads: gap links, compression only, k x closure (ground is fixed)
    k_rw = m.link_props["GAP_SOIL"]["k"]
    soil_js = m.soil_face_joints or m.ringwall_joints          # faces: two links per spoke at k/2 each
    rw_load = sum(max(0.0, -float(disp[j]["U3"])) for j in soil_js) * k_rw
    name_last = f"GAP_R{k_last:02d}"
    k_pad = m.link_props[name_last]["k"]
    pad_load = sum(max(0.0, -float(disp[j]["U3"])) for j in m.baseplate_joints if ring_of[j] == k_last) * k_pad
    ks_rw = spec.ringwall_subgrade or spec.subgrade_modulus
    return {
        "name": s2k.stem, "zone": spec.pad_zone, "ks_rw": ks_rw, "ratio": ks_rw / spec.subgrade_modulus,
        "M11_base": base["M11"], "M11_peak": mpk, "z_peak": zpk, "F22_peak": f22pk[1], "z_f22": f22pk[0],
        "rot_deg": rot_deg,
        "r_last": r_last, "u_pad_int": pad_int * 12, "u_pad_last": pad_last * 12, "u_rw": rw * 12,
        "diff": (pad_last - rw) * 12, "r_ref": r_ref, "u_pad_ref": pad_ref * 12, "diff_ref": (pad_ref - rw) * 12,
        "M11_pad": nearest(plate, r_last)["M11"], "M11_edge": nearest(plate, R - C / 2)["M11"],
        "M11_over": nearest(plate, R - C / 2 + 0.375)["M11"], "M11_shell": nearest(plate, R)["M11"],
        "V13_edge": nearest(plate, R - C / 2)["V13"],
        "rw_load": rw_load, "pad_load": pad_load,
        "t1": spec.plate_courses[0].thickness, "tp": spec.baseplate_thickness,
    }


def face_ksi(M: float, t: float) -> float:
    return 6.0 * M / t / t / 144.0


def table(rows: list[dict]) -> str:
    hdr = ["", *[r["name"].replace("TANK-A-", "") for r in rows]]
    lines = ["| " + " | ".join(hdr) + " |", "|" + "---|" * len(hdr)]
    def line(label, fn, fmt="{:.3f}"):
        lines.append("| " + label + " | " + " | ".join(fmt.format(fn(r)) for r in rows) + " |")
    line("pad zoning", lambda r: r["zone"], "{}")
    line("ring wall soil / pad ks", lambda r: r["ratio"], "{:.2f}")
    line("wall base M11, kip-ft/ft → face ksi", lambda r: f"{r['M11_base']:.3f} → {face_ksi(r['M11_base'], r['t1']):.1f}", "{}")
    line("wall M11 peak above, kip-ft/ft at z ft", lambda r: f"{r['M11_peak']:.3f} at {r['z_peak']:.2f}", "{}")
    line("wall F22 peak, kip/ft at z ft", lambda r: f"{r['F22_peak']:.1f} at {r['z_f22']:.2f}", "{}")
    line("foot rotation, °", lambda r: r["rot_deg"], "{:.3f}")
    line("pad U3 interior / last pad ring / ring wall, in", lambda r: f"{r['u_pad_int']:.3f} / {r['u_pad_last']:.3f} (r = {r['r_last']:.2f}) / {r['u_rw']:.3f}", "{}")
    line("differential, pad below ring wall, in", lambda r: r["diff"], "{:+.3f}")
    line("pad U3 at r ≈ 32.4 ft / differential there, in", lambda r: f"{r['u_pad_ref']:.3f} / {r['diff_ref']:+.3f} (r = {r['r_ref']:.2f})", "{}")
    line("plate M11 at last pad ring → face ksi", lambda r: f"{r['M11_pad']:+.3f} → {face_ksi(r['M11_pad'], r['tp']):.1f}", "{}")
    line("plate M11 at concrete edge → face ksi", lambda r: f"{r['M11_edge']:+.3f} → {face_ksi(r['M11_edge'], r['tp']):.1f}", "{}")
    line("plate M11 over concrete (33.375) → face ksi", lambda r: f"{r['M11_over']:+.3f} → {face_ksi(r['M11_over'], r['tp']):.1f}", "{}")
    line("plate M11 at shell line → face ksi", lambda r: f"{r['M11_shell']:+.3f} → {face_ksi(r['M11_shell'], r['tp']):.1f}", "{}")
    line("plate V13 at concrete edge, kip/ft", lambda r: r["V13_edge"], "{:.2f}")
    line("ring wall soil links, kip", lambda r: r["rw_load"], "{:.0f}")
    line("last pad ring springs, kip", lambda r: r["pad_load"], "{:.0f}")
    return "\n".join(lines)


def fig(rows: list[dict]) -> str:
    rows = [r for r in rows if r["zone"] == "boussinesq"]
    f = Fig(960, 420, "RING WALL SOIL SWEEP — graded pad, NL_HYDRO, against the ring wall / pad modulus ratio",
            "Four small panels against the ring wall to pad subgrade ratio on a log axis: plate bending at the concrete edge, differential settlement of the pad below the ring wall, wall foot rotation, and the load into the ring wall soil.")
    panels = [
        ("plate M11 at concrete edge, kip-ft/ft", "M11_edge", "{:+.2f}"),
        ("pad below ring wall, in", "diff", "{:+.3f}"),
        ("foot rotation, °", "rot_deg", "{:.2f}"),
        ("ring wall soil load, kip", "rw_load", "{:.0f}"),
    ]
    xs = [math.log10(r["ratio"]) for r in rows]
    for i, (title, key, fmt) in enumerate(panels):
        px = 60 + i * 230
        x0, x1, y0, y1 = px + 40, px + 200, 340, 90
        ys = [r[key] for r in rows]
        lo, hi = min(ys + [0.0]), max(ys + [0.0])
        pad = (hi - lo) * 0.15 or 1.0
        f.map(0.0, math.log10(30.0), lo - pad, hi + pad, x0, x1, y0, y1)
        f.line(x0, y0, x1, y0, stroke=MUTED, w=.8); f.line(x0, y0, x0, y1, stroke=MUTED, w=.8)
        if lo < 0 < hi:
            f.line(x0, f.Y(0), x1, f.Y(0), stroke=MUTED, w=.6, dash="4 3")
        for v in (1, 2, 5, 10, 25):
            f.line(f.X(math.log10(v)), y0, f.X(math.log10(v)), y0 + 4, stroke=MUTED, w=.8)
            f.text(f.X(math.log10(v)), y0 + 16, str(v), anchor="middle", size=10, fill=MUTED)
        f.text((x0 + x1) / 2, y0 + 32, "ring wall soil / pad ks", anchor="middle", size=10, fill=MUTED)
        f.text(px, y1 - 14, title, size=10, bold=True)
        f.path("M" + " L".join(f"{f.X(x):.1f} {f.Y(y):.1f}" for x, y in zip(xs, ys)), stroke=RUST, w=1.8)
        for x, y in zip(xs, ys):
            f.dot(f.X(x), f.Y(y), 3.5, fill=RUST)
            f.text(f.X(x), f.Y(y) - 8, fmt.format(y), anchor="middle", size=9, fill=INK)
    f.text(16, 390, "TANK-A-bq family: overhang model, [foundation] zone = boussinesq (pad 170 kcf graded 1.0 → 1.57 at the rim), ring wall soil 170 to 4250 kcf.", fill=MUTED, size=10)
    f.text(16, 404, "NL_HYDRO, corner means per ring. Plate M11: + = sagging, − = hogging over the concrete edge. Rotation + = wall top outward.", fill=MUTED, size=10)
    return f.done()


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("configs", nargs="+", type=Path)
    p.add_argument("--case", default="NL_HYDRO")
    p.add_argument("--out", type=Path)
    a = p.parse_args(argv)
    rows = [sweep_row(c, a.case) for c in a.configs]
    print(f"case {a.case}\n")
    print(table(rows))
    if a.out and any(r["zone"] == "boussinesq" for r in rows):
        a.out.mkdir(parents=True, exist_ok=True)
        path = a.out / "ringwall-sweep.svg"
        path.write_bytes(fig(rows).encode("utf-8"))
        print("\nwrote", path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
