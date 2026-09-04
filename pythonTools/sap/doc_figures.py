"""Model diagrams for the vault, generated from a tank config (not hand-drawn).

    python doc_figures.py configs/example.toml            # -> vault/arms/assets/tank-*.svg

Writes three SVGs, a half-section of the tank each, single-word labels:
    tank-loads.svg          load patterns / cases: DEAD, HYDRO, SETTLE and the NL chain
    tank-local-axes.svg     shell local axes on wall, baseplate, roof (1 merid, 2 circ, 3 normal)
    tank-beam-nodes.svg     node connectivity of the eave ring and the ring wall
Geometry (radius, height, crown radius, ring wall size) comes from the config so the
pictures match the model the note describes.
"""

from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from tankbuilder import TankModel, load_config  # noqa: E402

ASSETS = HERE.parent.parent / "vault" / "arms" / "assets"

INK, MUTED, STEEL, RUST, SOIL, CONC, GREEN = "#1f2328", "#6b7380", "#2f6b9a", "#b4472b", "#d9cbb0", "#e3e1dc", "#2e7d4f"
MARKERS = {"ink": INK, "muted": MUTED, "steel": STEEL, "rust": RUST, "green": GREEN}   # one arrowhead per colour
MARKER_OF = {v: k for k, v in MARKERS.items()}


class Fig:
    """Tiny SVG builder with a half-section coordinate map: model (r, z) ft -> px."""

    def __init__(self, W, H, title, aria):
        self.W, self.H = W, H
        self.o = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" font-family="Consolas, Menlo, monospace" '
                  f'font-size="12" role="img" aria-label="{aria}">',
                  f'<rect width="{W}" height="{H}" fill="#ffffff"/>',
                  '<defs>' + ''.join(
                      f'<marker id="ah-{k}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">'
                      f'<path d="M0 0L10 5L0 10z" fill="{c}"/></marker>' for k, c in MARKERS.items()) +
                  '<pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">'
                  f'<line x1="0" y1="0" x2="0" y2="6" stroke="{INK}" stroke-width=".8" stroke-opacity=".45"/></pattern></defs>',
                  f'<text x="16" y="24" font-size="13" font-weight="bold" fill="{INK}">{title}</text>']

    def map(self, r0, r1, z0, z1, x0, x1, y0, y1):
        """model r in [r0, r1] -> px x in [x0, x1]; model z in [z0, z1] -> px y in [y0, y1] (y0 = bottom)."""
        self.sx = (x1 - x0) / (r1 - r0); self.sz = (y1 - y0) / (z1 - z0)
        self.r0, self.z0, self.x0, self.y0 = r0, z0, x0, y0

    def X(self, r): return self.x0 + (r - self.r0) * self.sx
    def Y(self, z): return self.y0 + (z - self.z0) * self.sz

    def add(self, s): self.o.append(s)

    def text(self, x, y, s, fill=INK, anchor="start", size=12, bold=False):
        self.add(f'<text x="{x:.1f}" y="{y:.1f}" fill="{fill}" text-anchor="{anchor}" font-size="{size}"'
                 + (' font-weight="bold"' if bold else '') + f'>{s}</text>')

    def line(self, x1, y1, x2, y2, stroke=INK, w=1.2, dash=None, arrow=False, arrow_start=False):
        d = f' stroke-dasharray="{dash}"' if dash else ''
        mk = MARKER_OF.get(stroke, 'ink')
        m = f' marker-end="url(#ah-{mk})"' if arrow else ''
        m += f' marker-start="url(#ah-{mk})"' if arrow_start else ''
        self.add(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{w}"{d}{m}/>')

    def dot(self, x, y, r=4, fill=INK, stroke=None):
        st = f' stroke="{stroke}" stroke-width="1.5"' if stroke else ''
        self.add(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{r}" fill="{fill}"{st}/>')

    def path(self, d, stroke=INK, w=1.2, fill="none", dash=None):
        dd = f' stroke-dasharray="{dash}"' if dash else ''
        self.add(f'<path d="{d}" fill="{fill}" stroke="{stroke}" stroke-width="{w}"{dd}/>')

    def done(self):
        self.add('</svg>')
        return "\n".join(self.o) + "\n"


def half_section(f: Fig, m: TankModel, wall_w=3.0, roof_w=3.0, show_ringwall=True):
    """Draw the tank half-section: baseplate, wall, spherical roof, ring wall + ground."""
    s = m.spec
    R, H = s.radius, s.height
    # baseplate
    f.line(f.X(0), f.Y(0), f.X(R), f.Y(0), w=wall_w)
    # wall
    f.line(f.X(R), f.Y(0), f.X(R), f.Y(H), w=wall_w)
    # roof arc: sample z(r)
    pts = [(f.X(r), f.Y(m.roof_z(r))) for r in [R * i / 40 for i in range(41)]]
    f.path("M" + " L".join(f"{x:.1f} {y:.1f}" for x, y in pts), w=roof_w)
    # centreline
    f.line(f.X(0), f.Y(-0.4 * (s.ringwall_depth if s.ringwall else 2)), f.X(0), f.Y(m.roof_z(0) + 2), stroke=MUTED, w=.8, dash="8 4 2 4")
    if show_ringwall and s.ringwall:
        c, a = s.ringwall_width, s.ringwall_depth
        f.add(f'<rect x="{f.X(R - c / 2):.1f}" y="{f.Y(0):.1f}" width="{c * f.sx:.1f}" height="{-a * f.sz:.1f}" fill="{CONC}" stroke="{INK}" stroke-width="1"/>')
        # ground hatch outside the wall, sand pad inside
        f.add(f'<rect x="{f.X(R + c / 2):.1f}" y="{f.Y(0):.1f}" width="{(f.W - 20) - f.X(R + c / 2):.1f}" height="{-a * f.sz:.1f}" fill="url(#hatch)"/>')
        f.add(f'<rect x="{f.X(0):.1f}" y="{f.Y(0):.1f}" width="{(R - c / 2) * f.sx:.1f}" height="{-a * f.sz * 0.6:.1f}" fill="{SOIL}"/>')


def fig_loads(m: TankModel) -> str:
    s = m.spec
    R, H, zc = s.radius, s.height, m.roof_z(0)
    f = Fig(860, 520, "LOADS", "Tank half-section with the load patterns: DEAD self weight, HYDRO pressure on the wall and baseplate, SETTLE ground displacement under the plate and ring wall, and the nonlinear case chain")
    f.map(0, R * 1.35, -6, zc + 4, 80, 700, 470, 60)
    half_section(f, m)
    # DEAD: gravity arrows on roof and wall
    for r in [R * 0.25, R * 0.55, R * 0.85]:
        z = m.roof_z(r)
        f.line(f.X(r), f.Y(z + 3.5), f.X(r), f.Y(z + 0.4), stroke=INK, w=1.4, arrow=True)
    f.text(f.X(R * 0.55), f.Y(zc + 3.9) - 6, "DEAD", anchor="middle", bold=True)
    # HYDRO: triangular pressure on the inside of the wall + uniform on the plate
    hf = s.fill_height
    n = 7
    for i in range(1, n + 1):
        z = hf * (1 - i / n)
        L = (hf - z) / hf * R * 0.28
        f.line(f.X(R) - L * f.sx, f.Y(z), f.X(R) - 4, f.Y(z), stroke=STEEL, w=1.3, arrow=True)
    f.path(f"M{f.X(R) - R * 0.28 * f.sx:.1f} {f.Y(0):.1f} L{f.X(R):.1f} {f.Y(hf):.1f}", stroke=STEEL, w=1)
    f.line(f.X(R * 0.02), f.Y(hf), f.X(R * 0.98), f.Y(hf), stroke=STEEL, w=.8, dash="6 4")
    f.text(f.X(R * 0.5), f.Y(hf) - 5, "fill", fill=STEEL, anchor="middle")
    for r in [R * 0.15, R * 0.35, R * 0.55]:
        f.line(f.X(r), f.Y(hf * 0.35), f.X(r), f.Y(0.6), stroke=STEEL, w=1.3, arrow=True)
    f.text(f.X(R * 0.35), f.Y(hf * 0.35) - 6, "HYDRO", fill=STEEL, anchor="middle", bold=True)
    # SETTLE: ground joints move
    for r in [R * 0.2, R * 0.45, R * 0.7, R]:
        f.dot(f.X(r), f.Y(0), 3.5, fill=RUST)
        f.line(f.X(r), f.Y(0) + 6, f.X(r), f.Y(0) + 26, stroke=RUST, w=1.3, arrow=True)
    f.text(f.X(R * 0.45), f.Y(0) + 42, "SETTLE", fill=RUST, anchor="middle", bold=True)
    f.text(f.X(R * 0.45), f.Y(0) + 56, "w(x, y)", fill=RUST, anchor="middle")
    # case chain
    x, y = 720, 120
    f.text(x, y, "CASES", bold=True)
    chain = [("DEAD", INK), ("HYDRO", STEEL), ("NL_DEAD", INK), ("NL_HYDRO", STEEL), ("NL_SETTLE", RUST)]
    for i, (name, col) in enumerate(chain):
        yy = y + 26 + i * 26
        f.text(x, yy, name, fill=col)
        if i >= 3:
            f.line(x - 12, yy - 20, x - 12, yy - 6, stroke=MUTED, w=1, arrow=True)
    f.text(x, y + 26 * len(chain) + 30, "linear", fill=MUTED, size=11)
    f.text(x, y + 26 * len(chain) + 44, "then gaps", fill=MUTED, size=11)
    return f.done()


def fig_axes(m: TankModel) -> str:
    s = m.spec
    R, H, zc = s.radius, s.height, m.roof_z(0)
    f = Fig(860, 520, "SHELL LOCAL AXES", "Tank half-section with the shell local axes drawn on the wall, baseplate and roof: 1 meridional, 2 circumferential, 3 normal")
    f.map(0, R * 1.35, -6, zc + 4, 80, 700, 470, 60)
    half_section(f, m, show_ringwall=False)
    L = 30  # px arrow length

    def triad(x, y, d1, d3, label_side=1):
        # d1, d3: unit px directions for local 1 and 3 in the section plane; 2 is out of plane
        f.line(x, y, x + d1[0] * L, y + d1[1] * L, stroke=RUST, w=2, arrow=True)
        f.text(x + d1[0] * (L + 12), y + d1[1] * (L + 12) + 4, "1", fill=RUST, anchor="middle", bold=True)
        f.line(x, y, x + d3[0] * L, y + d3[1] * L, stroke=STEEL, w=2, arrow=True)
        f.text(x + d3[0] * (L + 12), y + d3[1] * (L + 12) + 4, "3", fill=STEEL, anchor="middle", bold=True)
        f.add(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="6" fill="none" stroke="{GREEN}" stroke-width="2"/>')
        f.dot(x, y, 2, fill=GREEN)
        f.text(x + 10 * label_side, y - 9, "2", fill=GREEN, anchor="middle", bold=True)

    # wall: 1 up, 3 outward (+r), 2 circumferential (out of page)
    triad(f.X(R), f.Y(H * 0.5), (0, -1), (1, 0))
    f.text(f.X(R) + 44, f.Y(H * 0.5) + 4, "wall", fill=MUTED)
    # baseplate: 1 radial outward, 3 up
    triad(f.X(R * 0.45), f.Y(0), (1, 0), (0, -1))
    f.text(f.X(R * 0.45), f.Y(0) + 22, "baseplate", fill=MUTED, anchor="middle")
    # roof: 1 up the slope toward the crown, 3 normal (outward/up)
    r = R * 0.55
    z = m.roof_z(r)
    dr = 0.01 * R
    dz = m.roof_z(r - dr) - m.roof_z(r + dr)
    tvec = (-2 * dr * f.sx, -dz * abs(f.sz))            # toward the crown, in px
    tl = math.hypot(*tvec); t = (tvec[0] / tl, tvec[1] / tl)
    nrm = (-t[1], t[0])                                   # rotate: outward normal (up-right)
    if nrm[1] > 0: nrm = (-nrm[0], -nrm[1])
    triad(f.X(r), f.Y(z), t, nrm)
    f.text(f.X(r) + 8, f.Y(z) + 40, "roof", fill=MUTED)
    # legend
    x, y = 720, 120
    f.text(x, y, "1", fill=RUST, bold=True); f.text(x + 18, y, "merid")
    f.text(x, y + 22, "2", fill=GREEN, bold=True); f.text(x + 18, y + 22, "circ")
    f.text(x, y + 44, "3", fill=STEEL, bold=True); f.text(x + 18, y + 44, "normal")
    f.text(x, y + 78, "F11 M11 S11 : 1", fill=MUTED, size=11)
    f.text(x, y + 94, "F22 M22 S22 : 2", fill=MUTED, size=11)
    f.text(x, y + 110, "M11 : bars along 1", fill=MUTED, size=11)
    f.text(x, y + 140, "2 out of page", fill=GREEN, size=11)
    return f.done()


def fig_beam_nodes(m: TankModel) -> str:
    s = m.spec
    R, H = s.radius, s.height
    f = Fig(860, 560, "BEAM NODES", "Node connectivity: the eave ring shares the wall-top joints with the wall and roof; the ring wall's top joints hang under the tank rim joints through contact gap links and sit on soil gap links to fixed ground joints")
    # two zoomed insets: eave (top) and rim (bottom), drawn schematically
    # ---- eave inset ----
    ex, ey = 110, 60
    f.text(ex, ey, "EAVE", bold=True)
    wx, wy = ex + 160, ey + 150            # wall-top joint px
    f.line(wx, wy, wx, wy + 110, w=3)      # wall
    f.text(wx + 8, wy + 100, "wall", fill=MUTED)
    f.path(f"M{wx} {wy} Q {wx - 70} {wy - 40} {wx - 150} {wy - 70}", w=3)   # roof
    f.text(wx - 150, wy - 80, "roof", fill=MUTED)
    # eave ring section (L) at the joint, small outline
    f.path(f"M{wx} {wy} l 22 0 l 0 5 l -22 0 z", fill=CONC, w=1)
    f.path(f"M{wx} {wy} l 0 18 l 5 0 l 0 -18 z", fill=CONC, w=1)
    f.text(wx + 32, wy + 8, "ROOF_RING", fill=STEEL)
    f.text(wx + 32, wy + 22, "frame", fill=MUTED, size=11)
    f.dot(wx, wy, 5, fill=STEEL)
    f.text(wx - 8, wy - 12, "joint", fill=STEEL, anchor="end")
    f.text(wx - 8, wy + 2, "TOP_RING", fill=STEEL, anchor="end", size=11)
    f.text(ex, ey + 240, "one joint : wall + roof + ring", fill=MUTED, size=11)
    # ---- rim inset ----
    rx, ry = 470, 60
    f.text(rx, ry, "RIM", bold=True)
    jx, jy = rx + 160, ry + 130             # rim joint (tank)
    f.line(jx, jy, jx, jy - 110, w=3)       # wall
    f.text(jx + 8, jy - 100, "wall", fill=MUTED)
    f.line(jx, jy, jx - 150, jy, w=3)       # baseplate
    f.text(jx - 150, jy - 8, "baseplate", fill=MUTED)
    f.dot(jx, jy, 5, fill=INK)
    f.text(jx + 12, jy + 4, "rim", bold=True)
    f.text(jx + 12, jy + 18, "BASE_RING", size=11, fill=MUTED)
    # contact gap link (drawn with length; zero-length in SAP)
    g1 = jy + 40
    f.line(jx, jy + 5, jx, g1 - 5, stroke=STEEL, w=2)
    f.path(f"M{jx - 8} {jy + 20} h16 M{jx - 8} {jy + 26} h16", stroke=STEEL, w=2)
    f.text(jx + 12, jy + 40, "GAP_CONTACT", fill=STEEL, size=11)
    # wall-top joint + ring wall
    f.dot(jx, g1, 5, fill="#ffffff", stroke=STEEL)
    f.text(jx + 12, g1 + 4, "wall top", fill=STEEL, bold=True)
    f.text(jx + 12, g1 + 18, "RINGWALL_TOP", fill=STEEL, size=11)
    cw, ch = 46, 120
    f.add(f'<rect x="{jx - cw / 2}" y="{g1}" width="{cw}" height="{ch}" fill="{CONC}" stroke="{INK}" stroke-width="1"/>')
    f.text(jx, g1 + ch / 2 + 4, "RINGWALL", anchor="middle", size=11)
    f.text(jx, g1 + ch / 2 + 18, "frame", anchor="middle", size=11, fill=MUTED)
    f.line(jx, g1, jx, g1 + ch / 2, stroke=STEEL, w=1.2, dash="2 4")
    f.dot(jx, g1 + ch / 2, 3, fill="none", stroke=STEEL)
    f.text(jx + cw / 2 + 6, g1 + ch / 2 + 4, "centroid", fill=MUTED, size=11)
    # soil gap link below the base
    g2 = g1 + ch + 40
    f.line(jx, g1 + ch, jx, g2 - 5, stroke=STEEL, w=2)
    f.path(f"M{jx - 8} {g1 + ch + 14} h16 M{jx - 8} {g1 + ch + 20} h16", stroke=STEEL, w=2)
    f.add(f'<polyline points="{jx},{g1 + ch + 24} {jx - 7},{g1 + ch + 27} {jx + 7},{g1 + ch + 32} {jx - 7},{g1 + ch + 37} {jx},{g1 + ch + 40}" fill="none" stroke="{STEEL}" stroke-width="2"/>')
    f.text(jx + 12, g1 + ch + 30, "GAP_SOIL", fill=STEEL, size=11)
    f.dot(jx, g2, 5, fill=STEEL)
    f.text(jx + 12, g2 + 4, "ground", bold=True)
    f.text(jx + 12, g2 + 18, "RINGWALL_GROUND", size=11, fill=MUTED)
    f.line(jx - 22, g2 + 8, jx + 22, g2 + 8, w=2)
    for k in range(5):
        f.line(jx - 18 + k * 9, g2 + 8, jx - 24 + k * 9, g2 + 15, w=1)
    f.text(jx + 40, g2 + 14, "fixed", fill=MUTED, size=11)
    f.text(rx, g2 + 50, "three joints coincident in SAP", fill=MUTED, size=11)
    # plate ground, for comparison, small
    px_, py_ = rx - 240, g1 + 60
    f.text(px_, py_ - 40, "PLATE", bold=True)
    f.line(px_ - 60, py_, px_ + 60, py_, w=3)
    f.dot(px_, py_, 4, fill=INK)
    f.text(px_ + 10, py_ - 6, "plate", size=11)
    f.line(px_, py_ + 5, px_, py_ + 30, stroke=STEEL, w=2)
    f.path(f"M{px_ - 8} {py_ + 14} h16 M{px_ - 8} {py_ + 20} h16", stroke=STEEL, w=2)
    f.text(px_ + 10, py_ + 22, "GAP_Rnn", fill=STEEL, size=11)
    f.dot(px_, py_ + 36, 4, fill=STEEL)
    f.text(px_ + 10, py_ + 40, "GROUND", size=11, fill=MUTED)
    f.line(px_ - 16, py_ + 43, px_ + 16, py_ + 43, w=2)
    return f.done()


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("config", type=Path)
    p.add_argument("-o", "--out-dir", type=Path, default=ASSETS)
    a = p.parse_args(argv)
    spec, _ = load_config(a.config)
    m = TankModel(spec)
    a.out_dir.mkdir(parents=True, exist_ok=True)
    for name, fn in (("tank-loads", fig_loads), ("tank-local-axes", fig_axes), ("tank-beam-nodes", fig_beam_nodes)):
        path = a.out_dir / f"{name}.svg"
        path.write_bytes(fn(m).replace("\n", "\r\n").encode("utf-8"))
        print(f"[figure]  {path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
