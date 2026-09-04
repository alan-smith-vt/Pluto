"""Settlement audit: the ground displacements the builder applies, as data and as a picture.

    python settlement_audit.py configs/TANK-A.toml             # beside the model .s2k
    python settlement_audit.py configs/TANK-A.toml -o some/dir --vault-svg

Writes <dir>/<name>.settlement.csv  --  node, x, y, z, dx, dy, dz for every ground joint
       <dir>/<name>.settlement.svg  --  plan heatmap of dz on the ground joints + an
                                        elevation across the profile (the applied curve
                                        and the joints on it)
With --vault-svg the .svg is also copied to vault/arms/assets/settlement-<name>.svg so
the vault note embeds it. Everything is generated from the model; nothing is hand-drawn.
The audit covers what the .s2k ASKS for (JOINT LOADS - GROUND DISPLACEMENT); what SAP
did with it is in results.s2k.
"""

from __future__ import annotations

import argparse
import csv
import math
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from tankbuilder import TankModel, load_config  # noqa: E402

VAULT_ASSETS = HERE.parent.parent / "vault" / "arms" / "assets"


# ---------------------------------------------------------------- data ----

def settlement_rows(model: TankModel) -> list[dict]:
    """One row per ground joint: node, x, y, z, dx, dy, dz (ft)."""
    rows = []
    for j in model.all_ground_joints:
        x, y, z = model.joints[j]
        rows.append({"node": j, "x": x, "y": y, "z": z, "dx": 0.0, "dy": 0.0,
                     "dz": model.settlements.get(j, 0.0)})
    return rows


def write_csv(rows: list[dict], path: Path) -> None:
    with open(path, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["node", "x", "y", "z", "dx", "dy", "dz"])
        for r in rows:
            w.writerow([r["node"], _num(r["x"]), _num(r["y"]), _num(r["z"]),
                        _num(r["dx"]), _num(r["dy"]), _num(r["dz"])])


def _num(v: float) -> str:
    return "0" if v == 0 else f"{v:.6g}"


# ----------------------------------------------------------------- svg ----

def _lerp(a, b, t):
    return a + (b - a) * t


def _color(t: float) -> str:
    """0 = no settlement (pale) -> 1 = full depth (deep blue); sequential, one hue."""
    t = min(max(t, 0.0), 1.0)
    c0, c1 = (232, 236, 240), (23, 64, 140)
    r, g, b = (int(round(_lerp(c0[i], c1[i], t))) for i in range(3))
    return f"#{r:02x}{g:02x}{b:02x}"


def settlement_svg(model: TankModel, rows: list[dict], title: str) -> str:
    """Plan heatmap of dz over the ground joints, the tank and ring wall outline, the
    profile axis, a colour bar; below it an elevation across the profile: the applied
    curve w(d) and every ground joint plotted at its perpendicular distance d."""
    s = model.spec
    R = s.radius
    rw = s.ringwall_width if s.ringwall else 0.0
    depth = max((abs(r["dz"]) for r in rows), default=0.0) or 1.0

    # ---- layout (all in SVG px) ----
    W, H = 900, 560
    plan_cx, plan_cy, plan_r = 250, 270, 190
    sc = plan_r / (R + rw + 1.0)            # ft -> px in plan
    bar_x, bar_y, bar_w, bar_h = 480, 90, 18, 360
    el_x0, el_x1, el_y0, el_y1 = 540, 880, 90, 450   # elevation box

    def px(x): return plan_cx + x * sc
    def py(y): return plan_cy - y * sc

    o = []
    o.append(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" font-family="Consolas, Menlo, monospace" font-size="11" role="img" aria-label="Applied ground settlement: plan heatmap of dz on the ground joints and the profile in elevation">')
    o.append(f'<rect x="0" y="0" width="{W}" height="{H}" fill="#ffffff"/>')
    o.append(f'<text x="16" y="24" font-size="13" font-weight="bold" fill="#1f2328">{_esc(title)}</text>')
    kind = s.settlement
    desc = (f"{kind}: depth {s.settlement_depth:g} ft, width {s.settlement_width:g} ft, "
            f"direction {s.settlement_direction_deg:g} deg, offset {s.settlement_offset:g} ft; "
            f"{sum(1 for r in rows if r['dz'] != 0)} of {len(rows)} ground joints moved; "
            f"case NL_SETTLE after NL_HYDRO")
    o.append(f'<text x="16" y="42" fill="#6b7380">{_esc(desc)}</text>')

    # ---- plan ----
    o.append(f'<text x="{plan_cx}" y="{plan_cy - plan_r - 18}" text-anchor="middle" font-weight="bold" fill="#1f2328">PLAN  dz on the ground joints (ft)</text>')
    if rw:
        o.append(f'<circle cx="{plan_cx}" cy="{plan_cy}" r="{(R + rw / 2) * sc:.1f}" fill="none" stroke="#9aa0a6" stroke-width="{max(rw * sc, 1):.1f}" stroke-opacity=".5"/>')
    o.append(f'<circle cx="{plan_cx}" cy="{plan_cy}" r="{R * sc:.1f}" fill="none" stroke="#1f2328" stroke-width="1.2"/>')
    # profile axis (trench centreline) and its edges
    th = math.radians(s.settlement_direction_deg)
    ux, uy = math.cos(th), math.sin(th)             # along the axis
    nx, ny = -math.sin(th), math.cos(th)            # perpendicular (+d side)
    L = R + rw + 2.0
    c = s.settlement_offset
    for dd, dash, col in ((0.0, "6 4", "#b4472b"), (s.settlement_width / 2, "2 3", "#b4472b"), (-s.settlement_width / 2, "2 3", "#b4472b")):
        x0, y0 = nx * (c + dd) - ux * L, ny * (c + dd) - uy * L
        x1, y1 = nx * (c + dd) + ux * L, ny * (c + dd) + uy * L
        o.append(f'<line x1="{px(x0):.1f}" y1="{py(y0):.1f}" x2="{px(x1):.1f}" y2="{py(y1):.1f}" stroke="{col}" stroke-width="1" stroke-dasharray="{dash}"/>')
    # joints, small dz first so the deep ones draw on top
    for r in sorted(rows, key=lambda r: abs(r["dz"])):
        t = abs(r["dz"]) / depth
        rad = 2.2 if r["dz"] == 0 else 3.2
        o.append(f'<circle cx="{px(r["x"]):.1f}" cy="{py(r["y"]):.1f}" r="{rad}" fill="{_color(t)}" stroke="#1f2328" stroke-width=".3"/>')
    # axes marks
    o.append(f'<line x1="{px(-L):.1f}" y1="{py(0):.1f}" x2="{px(L):.1f}" y2="{py(0):.1f}" stroke="#c0c4c8" stroke-width=".6"/>')
    o.append(f'<line x1="{px(0):.1f}" y1="{py(-L):.1f}" x2="{px(0):.1f}" y2="{py(L):.1f}" stroke="#c0c4c8" stroke-width=".6"/>')
    o.append(f'<text x="{px(L) + 4:.1f}" y="{py(0) + 4:.1f}" fill="#6b7380">+X</text>')
    o.append(f'<text x="{px(0) - 4:.1f}" y="{py(L) - 4:.1f}" fill="#6b7380" text-anchor="end">+Y</text>')
    o.append(f'<text x="{plan_cx}" y="{plan_cy + plan_r + 26}" text-anchor="middle" fill="#6b7380">dashed: profile axis and edges; ring wall shaded; R = {R:g} ft</text>')

    # ---- colour bar ----
    steps = 24
    for i in range(steps):                    # 0 (pale) at the top, full depth (dark) at the bottom: down is down
        t0, t1 = i / steps, (i + 1) / steps
        yy = bar_y + bar_h * t0
        o.append(f'<rect x="{bar_x}" y="{yy:.1f}" width="{bar_w}" height="{bar_h / steps + .5:.1f}" fill="{_color((t0 + t1) / 2)}"/>')
    o.append(f'<rect x="{bar_x}" y="{bar_y}" width="{bar_w}" height="{bar_h}" fill="none" stroke="#1f2328" stroke-width=".6"/>')
    for k in range(5):
        t = k / 4
        yy = bar_y + bar_h * t
        lab = "0" if t == 0 else f"{-t * depth:.3g}"
        o.append(f'<text x="{bar_x + bar_w + 5}" y="{yy + 4:.1f}" fill="#1f2328">{lab}</text>')
    o.append(f'<text x="{bar_x}" y="{bar_y - 8}" fill="#6b7380">dz ft</text>')

    # ---- elevation across the profile ----
    o.append(f'<text x="{(el_x0 + el_x1) / 2}" y="{el_y0 - 18}" text-anchor="middle" font-weight="bold" fill="#1f2328">ELEVATION  across the profile (d = distance from the axis)</text>')
    dmax = R + rw
    def ex(d): return el_x0 + (d + dmax) / (2 * dmax) * (el_x1 - el_x0)
    def ey(w): return el_y0 + (-w / depth) * (el_y1 - el_y0) * 0.85 + 20
    o.append(f'<rect x="{el_x0}" y="{el_y0}" width="{el_x1 - el_x0}" height="{el_y1 - el_y0}" fill="none" stroke="#c0c4c8" stroke-width=".6"/>')
    # original ground line and the tank footprint
    o.append(f'<line x1="{ex(-dmax):.1f}" y1="{ey(0):.1f}" x2="{ex(dmax):.1f}" y2="{ey(0):.1f}" stroke="#9aa0a6" stroke-width="1" stroke-dasharray="4 3"/>')
    o.append(f'<line x1="{ex(-R):.1f}" y1="{ey(0) - 8:.1f}" x2="{ex(-R):.1f}" y2="{ey(0) + 8:.1f}" stroke="#1f2328" stroke-width="1"/>')
    o.append(f'<line x1="{ex(R):.1f}" y1="{ey(0) - 8:.1f}" x2="{ex(R):.1f}" y2="{ey(0) + 8:.1f}" stroke="#1f2328" stroke-width="1"/>')
    o.append(f'<text x="{ex(-R):.1f}" y="{ey(0) - 12:.1f}" text-anchor="middle" fill="#6b7380">shell</text>')
    o.append(f'<text x="{ex(R):.1f}" y="{ey(0) - 12:.1f}" text-anchor="middle" fill="#6b7380">shell</text>')
    # the applied curve, sampled along d through the centre line of the profile
    pts = []
    n = 160
    for i in range(n + 1):
        d = -dmax + 2 * dmax * i / n
        # a point at perpendicular distance d from the axis (offset included): x = n*(c+d)
        w = model.settlement_dz(nx * (c + d), ny * (c + d))
        pts.append(f"{ex(d):.1f},{ey(w):.1f}")
    o.append(f'<polyline points="{" ".join(pts)}" fill="none" stroke="#b4472b" stroke-width="1.5"/>')
    # every ground joint at its own d
    for r in sorted(rows, key=lambda r: abs(r["dz"])):
        d = nx * r["x"] + ny * r["y"] - c
        t = abs(r["dz"]) / depth
        o.append(f'<circle cx="{ex(d):.1f}" cy="{ey(r["dz"]):.1f}" r="2.4" fill="{_color(t)}" stroke="#1f2328" stroke-width=".3"/>')
    # d axis ticks
    ticks = [-R, 0.0, R]
    hw = s.settlement_width / 2
    if hw < R * 0.85:
        ticks += [-hw, hw]
    for d in sorted(ticks):
        o.append(f'<line x1="{ex(d):.1f}" y1="{el_y1}" x2="{ex(d):.1f}" y2="{el_y1 + 5}" stroke="#1f2328" stroke-width=".6"/>')
        o.append(f'<text x="{ex(d):.1f}" y="{el_y1 + 17}" text-anchor="middle" fill="#6b7380">{d:g}</text>')
    o.append(f'<text x="{(el_x0 + el_x1) / 2}" y="{el_y1 + 34}" text-anchor="middle" fill="#6b7380">d, ft (perpendicular to the axis; + = the {s.settlement_direction_deg + 90:g} deg side)</text>')
    o.append(f'<text x="{el_x0 - 6}" y="{ey(0) + 4:.1f}" text-anchor="end" fill="#6b7380">0</text>')
    o.append(f'<text x="{el_x0 - 6}" y="{ey(-depth) + 4:.1f}" text-anchor="end" fill="#6b7380">{-depth:.3g}</text>')
    o.append(f'<text x="{(el_x0 + el_x1) / 2}" y="{el_y1 + 50}" text-anchor="middle" fill="#6b7380">red: applied w(d); dots: ground joints (plate + ring wall)</text>')
    o.append('</svg>')
    return "\n".join(o) + "\n"


def _esc(t: str) -> str:
    return t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


# --------------------------------------------------------------- driver ----

def write_audit(model: TankModel, out_dir: Path, name: str) -> tuple[Path, Path]:
    out_dir.mkdir(parents=True, exist_ok=True)
    rows = settlement_rows(model)
    csv_path = out_dir / f"{name}.settlement.csv"
    svg_path = out_dir / f"{name}.settlement.svg"
    write_csv(rows, csv_path)
    svg_path.write_text(settlement_svg(model, rows, f"{name} — applied ground settlement"), encoding="utf-8")
    return csv_path, svg_path


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("config", type=Path)
    p.add_argument("-o", "--out-dir", type=Path, help="default: the model's folder")
    p.add_argument("--vault-svg", action="store_true", help="also copy the .svg to vault/arms/assets/settlement-<name>.svg")
    a = p.parse_args(argv)
    spec, s2k = load_config(a.config)
    model = TankModel(spec)
    if spec.settlement == "none":
        print(f"{a.config}: no [settlement] profile; nothing to audit")
        return 1
    name = s2k.stem
    csv_path, svg_path = write_audit(model, a.out_dir or s2k.parent, name)
    moved = sum(1 for v in model.settlements.values() if v != 0.0)
    print(f"[audit]   {csv_path}  ({len(model.settlements)} ground joints, {moved} moved, "
          f"max dz {min(model.settlements.values()):.4g} ft)")
    print(f"[audit]   {svg_path}")
    if a.vault_svg:
        VAULT_ASSETS.mkdir(parents=True, exist_ok=True)
        dst = VAULT_ASSETS / f"settlement-{name}.svg"
        shutil.copyfile(svg_path, dst)
        print(f"[audit]   {dst}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
