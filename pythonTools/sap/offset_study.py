#!/usr/bin/env python3
"""Beam or ring on the tank's three-joint gap chain, built four ways, compared.

    python offset_study.py                 # beam:  models/offset-study/offset-beam.s2k
    python offset_study.py --ring          # ring:  models/offset-study/offset-ring.s2k
    python offset_study.py [--ring] --run  # + SAP2000 (attach or start): import, run, compare

The chain at every frame joint is the ring wall's: TANK joint (the shell rim) -> GAP_CONTACT
link -> WALL joint (the frame node) -> GAP_SOIL link -> fixed GROUND joint. Both links are
compression-only in U1 (= global +Z, I below J). The links also carry LINEAR shear and
rocking terms (contact U2/U3 = k_shear, soil U2/U3 = k_lat, soil R2/R3 = k_rot) so the
chains are held laterally without joint restraints; under vertical load they carry
nothing. The tank model has none of them.

Schemes (one per beam / ring, same section, loads and stiffnesses):

  A  COINCIDENT   all three joints at the section centroid; cardinal 10; zero-length links.
  B  TANK         all three joints at the TOP of the section; cardinal 8, Transform=No
                  (section drawn hanging below, analysis on the joint line). What the
                  builder meant.
  X  XFORM        as B but Transform=Yes: SAP puts rigid arms from the joint down to the
                  centroid, so a horizontal force at the joint becomes a couple. What the
                  tank actually had until 2026-09-08 (the column was misspelt).
  C  TRUE         real geometry: TANK joint at the top (z = 0), WALL joint at the centroid
                  (z = -A/2, cardinal 10), GROUND joint at the base (z = -A); the links have
                  length. Easiest to audit in the GUI: nothing is coincident.

Cases, staged nonlinear: NL_DEAD (self weight) -> NL_VERT (tank weight on the TANK joints)
-> NL_SETTLE (ground displacement on the GROUND joints: the tank's half-plane slope on
the ring, the middle joint dropping on the beam). Vertical load only: the point is that
the four schemes are equivalent for it. --run prints per case and scheme the frame
force envelopes, the open soil gaps, the wall rolling and the tank sway, and writes the
same to CSV.
"""

from __future__ import annotations

import argparse
import csv
import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from tankbuilder.s2k import S2KWriter, _row, program_control  # noqa: E402
from tankbuilder.spec import G_ACCEL  # noqa: E402

# --- section, soil, loads (kip, ft): the TANK-A ring wall numbers ---------------------
WIDTH = 1.25          # ft, C
DEPTH = 3.75          # ft, A
FC_PSI = 3000.0
UNIT_WEIGHT = 0.150   # kip/ft^3
SUBGRADE = 170.0      # kip/ft^3, soil under the wall
K_SHEAR = 1.0e4       # kip/ft per ft of wall: shell base -> wall top shear (contact U2/U3)
K_LAT = 50.0          # kip/ft per ft of wall: soil lateral under the base (soil U2/U3)
VERT_W = 3.0          # kip/ft, tank weight on the rim

RING_R = 33.625       # ft, TANK-A
RING_N = 72
RING_SETTLE = 0.5     # ft at the tank edge, half-plane slope rising toward +Y (hinge on X)

BEAM_SPAN = 20.0      # ft, two spans, joints every 10 ft
BEAM_SETTLE = 0.1     # ft, middle ground joint drops

SCHEMES = [
    # key, label, cardinal, stiff_transform, z_wall, z_ground   (tank joint at z = 0)
    ("A", "COINCIDENT", 10, False, 0.0, 0.0),
    ("B", "TANK", 8, False, 0.0, 0.0),
    ("X", "XFORM", 8, True, 0.0, 0.0),
    ("C", "TRUE", 10, False, -DEPTH / 2.0, -DEPTH),
]
_CARDINAL = {8: "8 (top center)", 10: "10 (centroid)"}


def concrete() -> dict:
    e_ksi = 57.0 * math.sqrt(FC_PSI)
    return {"name": "CONC", "E": e_ksi * 144.0, "poisson": 0.2,
            "unit_weight": UNIT_WEIGHT, "alpha": 5.5e-06}


class Study:
    """joints: tank 100n+k, wall 1000+100n+k, ground 2000+100n+k (n = scheme 1..4,
    k = 1..N <= 72); frames and contact links 100n+k, soil links 1000+100n+k."""

    def __init__(self, ring: bool):
        self.ring = ring
        self.joints: dict[int, tuple[float, float, float]] = {}
        self.frames: dict[int, tuple[int, int]] = {}
        self.cardinal: dict[int, tuple[int, bool]] = {}
        self.links: dict[int, tuple[int, int, str]] = {}
        self.tank: dict[str, list[int]] = {}
        self.wall: dict[str, list[int]] = {}
        self.ground: dict[str, list[int]] = {}
        self.frames_of: dict[str, list[int]] = {}
        self.soil_links: dict[str, list[int]] = {}
        self.settle: dict[int, float] = {}                  # ground joint -> dz
        self.trib: dict[int, float] = {}                    # tank joint -> tributary length
        n_pts = RING_N if ring else 5
        for n, (key, _, card, xform, zw, zg) in enumerate(SCHEMES, start=1):
            ts, ws, gs, fs, sl = [], [], [], [], []
            for k in range(1, n_pts + 1):
                if ring:
                    th = 2.0 * math.pi * (k - 1) / RING_N
                    cx = (n - 1) * 3.0 * RING_R
                    x, y = cx + RING_R * math.cos(th), RING_R * math.sin(th)
                    trib = 2.0 * math.pi * RING_R / RING_N
                    dz = -RING_SETTLE * y / RING_R if y > 0.0 else 0.0
                else:
                    x, y = (k - 1) * BEAM_SPAN / 2.0, (n - 1) * 20.0
                    trib = BEAM_SPAN / 2.0 if 1 < k < n_pts else BEAM_SPAN / 4.0
                    dz = -BEAM_SETTLE if k == 3 else 0.0
                t, w, g = 100 * n + k, 1000 + 100 * n + k, 2000 + 100 * n + k
                self.joints[t] = (x, y, 0.0)
                self.joints[w] = (x, y, zw)
                self.joints[g] = (x, y, zg)
                self.trib[t] = trib
                self.settle[g] = dz
                ts.append(t); ws.append(w); gs.append(g)
                self.links[100 * n + k] = (w, t, "GAP_CONTACT")          # I below, J above
                self.links[1000 + 100 * n + k] = (g, w, "GAP_SOIL")
                sl.append(1000 + 100 * n + k)
            n_frames = n_pts if ring else n_pts - 1
            for k in range(1, n_frames + 1):
                f = 100 * n + k
                self.frames[f] = (ws[k - 1], ws[k % n_pts])
                if card != 10 or xform:
                    self.cardinal[f] = (card, xform)
                fs.append(f)
            self.tank[key], self.wall[key], self.ground[key] = ts, ws, gs
            self.frames_of[key], self.soil_links[key] = fs, sl

    # per-unit-length stiffnesses x tributary length (uniform: ring arc / beam 10 ft)
    @property
    def trib_unit(self) -> float:
        return 2.0 * math.pi * RING_R / RING_N if self.ring else BEAM_SPAN / 2.0

    def link_props(self) -> dict[str, dict]:
        L = self.trib_unit
        return {
            "GAP_CONTACT": {"U1": concrete()["E"] * WIDTH * L / DEPTH,
                            "U2": K_SHEAR * L, "U3": K_SHEAR * L, "R2": 0.0, "R3": 0.0},
            "GAP_SOIL": {"U1": SUBGRADE * WIDTH * L, "U2": K_LAT * L, "U3": K_LAT * L,
                         "R2": SUBGRADE * WIDTH ** 3 / 12.0 * L, "R3": SUBGRADE * WIDTH ** 3 / 12.0 * L},
        }

    def tables(self) -> list[tuple[str, list[str]]]:
        c = concrete()
        props = self.link_props()
        gap_rows = []
        for name, p in props.items():
            gap_rows.append(_row(Link=name, DOF="U1", Fixed=False, NonLinear=True, TransKE=p["U1"],
                                 TransCE=0, TransK=p["U1"], Open=0))
            for dof in ("U2", "U3"):
                gap_rows.append(_row(Link=name, DOF=dof, Fixed=False, NonLinear=False,
                                     TransKE=p[dof], TransCE=0))
            for dof in ("R2", "R3"):
                if p[dof]:
                    gap_rows.append(_row(Link=name, DOF=dof, Fixed=False, NonLinear=False,
                                         RotKE=p[dof], RotCE=0))
        out = [
            ("MATERIAL PROPERTIES 01 - GENERAL", [
                _row(Material=c["name"], Type="Concrete", SymType="Isotropic",
                     TempDepend=False, Color="Gray4")]),
            ("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES", [
                _row(Material=c["name"], UnitWeight=c["unit_weight"],
                     UnitMass=c["unit_weight"] / G_ACCEL, E1=c["E"],
                     G12=c["E"] / (2.0 * (1.0 + c["poisson"])), U12=c["poisson"], A1=c["alpha"])]),
            ("FRAME SECTION PROPERTIES 01 - GENERAL", [
                _row(SectionName="RINGWALL", Material=c["name"], Shape="Rectangular",
                     t3=DEPTH, t2=WIDTH, Color="Yellow")]),
            ("LINK PROPERTY DEFINITIONS 01 - GENERAL", [
                _row(Link=name, LinkType="Gap", Mass=0, Weight=0, RotInert1=0, RotInert2=0,
                     RotInert3=0, DefLength=1, DefArea=1, PDM2I=0, PDM2J=0, PDM3I=0, PDM3J=0,
                     Color="Magenta") for name in props]),
            ("LINK PROPERTY DEFINITIONS 05 - GAP", gap_rows),
            ("JOINT COORDINATES", [
                _row(Joint=j, CoordSys="GLOBAL", CoordType="Cartesian", XorR=x, Y=y, Z=z)
                for j, (x, y, z) in sorted(self.joints.items())]),
            ("CONNECTIVITY - FRAME", [
                _row(Frame=f, JointI=i, JointJ=j, IsCurved=False)
                for f, (i, j) in sorted(self.frames.items())]),
            ("CONNECTIVITY - LINK", [
                _row(Link=l, JointI=i, JointJ=j) for l, (i, j, _) in sorted(self.links.items())]),
            ("FRAME SECTION ASSIGNMENTS", [
                _row(Frame=f, SectionType="Rectangular", AutoSelect="N.A.", AnalSect="RINGWALL",
                     DesignSect="RINGWALL", MatProp="Default")
                for f in sorted(self.frames)]),
            ("FRAME INSERTION POINT ASSIGNMENTS", [
                _row(Frame=f, CardinalPt=_CARDINAL[card], Mirror2=False, Mirror3=False,
                     Transform=xform, CoordSys="Local",
                     Offset1I=0, Offset2I=0, Offset3I=0, Offset1J=0, Offset2J=0, Offset3J=0)
                for f, (card, xform) in sorted(self.cardinal.items())]),
            ("LINK PROPERTY ASSIGNMENTS", [
                _row(Link=l, LinkProp=p, LinkFDProp="None") for l, (_, _, p) in sorted(self.links.items())]),
        ]
        defs, assigns = [], []
        for key, label, *_ in SCHEMES:
            name = f"{key}_{label}"
            defs.append(_row(GroupName=name, Selection=True, SectionCut=True, Steel=True,
                             Concrete=True, Aluminum=True, ColdFormed=True, Stage=True,
                             Bridge=True, AutoSeismic=False, AutoWind=False, SelDesSteel=False,
                             SelDesAlum=False, SelDesCold=False, MassWeight=True, Color="Green"))
            assigns += [_row(GroupName=name, ObjectType="Frame", ObjectLabel=f) for f in self.frames_of[key]]
            assigns += [_row(GroupName=name, ObjectType="Joint", ObjectLabel=j)
                        for j in self.tank[key] + self.wall[key] + self.ground[key]]
            assigns += [_row(GroupName=name, ObjectType="Link", ObjectLabel=l)
                        for l, (i, _, _) in self.links.items() if i in self.wall[key] or i in self.ground[key]]
        out += [("GROUPS 1 - DEFINITIONS", defs), ("GROUPS 2 - ASSIGNMENTS", assigns)]
        # restraints: ground fixed; tank joints have no rotational stiffness of their own
        rest = []
        for key, *_ in SCHEMES:
            rest += [_row(Joint=g, U1=True, U2=True, U3=True, R1=True, R2=True, R3=True) for g in self.ground[key]]
            rest += [_row(Joint=t, U1=False, U2=False, U3=False, R1=True, R2=True, R3=True) for t in self.tank[key]]
            if not self.ring:
                # beam: hold the wall line longitudinally at one end (links give lateral + vertical)
                rest.append(_row(Joint=self.wall[key][0], U1=True, U2=False, U3=False, R1=False, R2=False, R3=False))
        out.append(("JOINT RESTRAINT ASSIGNMENTS", rest))
        # loads on the tank joints and the ground joints
        vert, settle = [], []
        for t, L in sorted(self.trib.items()):
            vert.append(_row(Joint=t, LoadPat="VERT", CoordSys="GLOBAL", F1=0, F2=0, F3=-VERT_W * L, M1=0, M2=0, M3=0))
        for g, dz in sorted(self.settle.items()):
            if dz:
                settle.append(_row(Joint=g, LoadPat="SETTLE", CoordSys="GLOBAL", U1=0, U2=0, U3=dz, R1=0, R2=0, R3=0))
        cases = ["NL_DEAD", "NL_VERT", "NL_SETTLE"]
        pats = ["DEAD", "VERT", "SETTLE"]
        out += [
            ("LOAD PATTERN DEFINITIONS", [
                _row(LoadPat="DEAD", DesignType="Dead", SelfWtMult=1),
                _row(LoadPat="VERT", DesignType="Live", SelfWtMult=0),
                _row(LoadPat="SETTLE", DesignType="Other", SelfWtMult=0)]),
            ("JOINT LOADS - FORCE", vert),
            ("JOINT LOADS - GROUND DISPLACEMENT", settle),
            ("LOAD CASE DEFINITIONS", [
                _row(Case=c_, Type="NonStatic", InitialCond=(cases[i - 1] if i else "Zero"))
                for i, c_ in enumerate(cases)]),
            ("CASE - STATIC 1 - LOAD ASSIGNMENTS", [
                _row(Case=c_, LoadType="Load pattern", LoadName=p, LoadSF=1) for c_, p in zip(cases, pats)]),
            ("CASE - STATIC 2 - NONLINEAR LOAD APPLICATION", [
                _row(Case=c_, LoadApp="Full Load", MonitorDOF="U3", MonitorJt=self.wall["A"][0]) for c_ in cases]),
        ]
        return out

    def s2k_text(self) -> str:
        w = S2KWriter()
        w.lines.append("File generated by offset_study.py (Pluto)")
        w.lines.append("")
        w.table("PROGRAM CONTROL", program_control())
        for name, rows in self.tables():
            w.table(name, rows)
        return w.text()


# --- SAP run + comparison -----------------------------------------------------------

_LINK_FIELDS = ["Obj", "Elm", "PointElm", "LoadCase", "StepType", "StepNum",
                "P", "V2", "V3", "T", "M2", "M3"]


def link_forces(sap) -> dict[tuple[str, str], float]:
    """(link, case) -> P along local 1 (negative = compression)."""
    sap._select_all_cases()
    r = sap.model.Results.LinkForce("ALL", 2)
    n = r[0]
    cols = r[1:1 + len(_LINK_FIELDS)]
    out = {}
    for i in range(n):
        row = {f: cols[k][i] for k, f in enumerate(_LINK_FIELDS)}
        out[(str(row["Obj"]), row["LoadCase"])] = row["P"]
    return out


def check_insertion(study: Study, sap) -> None:
    """Enforce and echo the insertion point per scheme over the OAPI (SetInsertionPoint /
    GetInsertionPoint): the .s2k column is `Transform`, and a misspelt column silently
    leaves SAP's default, Transform = Yes (found 2026-09-08)."""
    for key, label, card, xform, *_ in SCHEMES:
        for f in study.frames_of[key]:
            sap.model.FrameObj.SetInsertionPoint(str(f), card, False, xform, [0.0] * 3, [0.0] * 3, "Local")
        r = sap.model.FrameObj.GetInsertionPoint(str(study.frames_of[key][0]))
        print(f"[insert]  {key}_{label}: cardinal {r[0]}, transform {r[2]}   (asked {card}, {xform})")


def check_link_props(sap) -> None:
    """Print what SAP made of the GAP tables (PropLink.GetGap): DOF flags and stiffnesses."""
    for name in ("GAP_CONTACT", "GAP_SOIL"):
        r = sap.model.PropLink.GetGap(name)
        dof, fixed, nonlin, ke, ce, k, dis = r[0], r[1], r[2], r[3], r[4], r[5], r[6]
        active = [f"{d}{'*' if nonlin[i] else ''}={ke[i]:g}" for i, d in enumerate(("U1", "U2", "U3", "R1", "R2", "R3")) if dof[i]]
        print(f"[props]   {name}: " + ", ".join(active) + "   (* = nonlinear gap)")


def compare(study: Study, sap) -> list[dict]:
    disp = {}
    for d in sap.joint_displacements():
        disp[(str(d["Joint"]), d["OutputCase"])] = d
    frames = sap.frame_forces()
    link_p = link_forces(sap)
    rows = []
    for case in sap.load_cases():
        for key, label, *_ in SCHEMES:
            fs = {str(f) for f in study.frames_of[key]}
            ff = [r for r in frames if r["OutputCase"] == case and str(r["Frame"]) in fs]
            wall = [disp[(str(j), case)] for j in study.wall[key]]
            tank = [disp[(str(j), case)] for j in study.tank[key]]
            soil = [link_p[(str(l), case)] for l in study.soil_links[key]]
            row = {"case": case, "scheme": f"{key}_{label}"}
            for c in ("P", "V2", "V3", "T", "M2", "M3"):
                row[f"{c}_max_abs"] = max(abs(r[c]) for r in ff)
            row["wall_U3_min"] = min(d["U3"] for d in wall)
            row["wall_roll_max"] = max(math.hypot(d["R1"], d["R2"]) for d in wall)
            row["tank_sway_max"] = max(math.hypot(d["U1"], d["U2"]) for d in tank)
            row["soil_P_min"] = min(soil)
            row["soil_open"] = sum(1 for p in soil if p == 0.0)
            rows.append(row)
    return rows


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ring", action="store_true", help="rings (default: two-span beams)")
    ap.add_argument("--run", action="store_true", help="import into SAP2000, run, compare")
    ap.add_argument("--out", default=None)
    a = ap.parse_args(argv)
    kind = "ring" if a.ring else "beam"
    out = Path(a.out) if a.out else HERE / "models" / "offset-study" / f"offset-{kind}.s2k"
    out.parent.mkdir(parents=True, exist_ok=True)
    study = Study(ring=a.ring)
    out.write_text(study.s2k_text(), newline="\n")
    lp = study.link_props()
    print(f"[build]   {out}  ({len(study.joints)} joints, {len(study.frames)} frames, {len(study.links)} links; "
          f"per joint: contact U1 {lp['GAP_CONTACT']['U1']:.4g}, shear {lp['GAP_CONTACT']['U2']:.4g}; "
          f"soil U1 {lp['GAP_SOIL']['U1']:.4g}, lat {lp['GAP_SOIL']['U2']:.4g}, rot {lp['GAP_SOIL']['R2']:.4g})")
    if not a.run:
        return 0
    from tankbuilder.sap_api import SapSession
    sap = SapSession.attach_or_start()
    print(f"[sap]     SAP2000 {sap.version}")
    sap.open(out)
    j, _, f = sap.counts()
    print(f"[open]    {j} joints, {f} frames, groups: {', '.join(sap.groups())}")
    check_link_props(sap)
    check_insertion(study, sap)
    secs = sap.run()
    print(f"[run]     {', '.join(sap.load_cases())}  ({secs:.1f}s)")
    rows = compare(study, sap)
    csv_path = out.with_suffix(".csv")
    with csv_path.open("w", newline="") as fh:
        wr = csv.DictWriter(fh, fieldnames=list(rows[0].keys()))
        wr.writeheader()
        wr.writerows(rows)
    print(f"[compare] {csv_path}")
    cols = [c for c in rows[0] if c not in ("case", "scheme")]
    print(f"{'case':10} {'scheme':13} " + " ".join(f"{c:>13}" for c in cols))
    for r in rows:
        print(f"{r['case']:10} {r['scheme']:13} " + " ".join(
            f"{r[c]:13.5g}" if isinstance(r[c], float) else f"{r[c]:>13}" for c in cols))
    return 0


if __name__ == "__main__":
    sys.exit(main())
