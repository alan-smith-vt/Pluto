"""Synthetic HVAC demo inputs: Navisworks duct run, Revit connectors run, Box run, Revit mesh run, utility rack
steel CSV, cut points, drop list and a demo duct-config.psd1, in the layout of the production work folder.

    python pythonTools/ducts/make_demo_data.py --out D:/TetherWork/hvac-demo

Formats are the ones the Pluto readers take (scripts/arms/DuctsToPluto.cs, DuctsRevitOverlay.cs,
DuctsManualDrop.cs, SteelToPluto.cs; writers scripts/navis/DuctsButton.cs, BoxButton.cs, scripts/revit/
ExportConnectors.cs, ExportMesh.cs, SapSession.WritePoints). Generic names only (ROOM-0n, SYSA..SYSC).
Deterministic: same output for the same arguments.

Plant frame (Navisworks): X = E, Y = N, Z = EL, feet. Revit frame: plant = Rz(REVIT_ROT_DEG) * revit + REVIT_T.
"""
import argparse
import math
import os
import random
import shutil
import struct
import uuid

import numpy as np

REVIT_ROT_DEG = 30.0
REVIT_T = np.array([812.5, 431.25, 92.0])   # Revit -> plant translation (ft)
RUN = "20261030-120000"
BLDG = "B1"
TOP_OF_STEEL = 115.0                          # rack beam top (ft)
FT = 1 / 12.0
X = np.array([1.0, 0, 0]); Y = np.array([0, 1.0, 0]); Z = np.array([0, 0, 1.0])

rng = random.Random(7)
GUID_ALPHA = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$"


def new_guid():
    return rng.choice("0123") + "".join(rng.choice(GUID_ALPHA) for _ in range(21))


def ifc_guid_from_unique_id(uid):
    """Port of DuctsToPluto.IfcGuidFromUniqueId (the IFC exporters' rule)."""
    eid = int(uid[37:45], 16)
    last = int(uid[28:36], 16) ^ eid
    hexs = uid[0:8] + uid[9:13] + uid[14:18] + uid[19:23] + uid[24:28] + "%08x" % last
    b = bytes.fromhex(hexs)
    bits = "".join(format(x, "08b") for x in b)
    out = [GUID_ALPHA[int(bits[0:2], 2)]]
    for n in range(21):
        out.append(GUID_ALPHA[int(bits[2 + 6 * n: 8 + 6 * n], 2)])
    return "".join(out)


def unit(v):
    v = np.asarray(v, float)
    return v / np.linalg.norm(v)


def room_of(x):
    return "ROOM-01" if x < 1060 else "ROOM-02" if x < 1120 else "ROOM-03"


# ---------------------------------------------------------------- duct model
class Size:
    def __init__(self, w=None, h=None, d=None):
        self.w, self.h, self.d = w, h, d

    @property
    def round(self):
        return self.d is not None

    def half(self, along_w):   # half extent (ft) in the width / height direction
        if self.round:
            return self.d / 24
        return (self.w if along_w else self.h) / 24

    def text(self):          # Navisworks Name style
        return '%g"ø' % self.d if self.round else "%gx%g" % (self.w, self.h)

    def revit(self):         # Revit Size parameter style
        return '%g"ø' % self.d if self.round else '%g"x%g"' % (self.w, self.h)


class Conn:
    def __init__(self, part, pos, d, size, wdir, ctype="End"):
        self.part, self.pos, self.d, self.size, self.wdir, self.type = part, np.array(pos, float), unit(d), size, wdir, ctype
        self.to = []
        self.cid = len(part.conns) + 1
        part.conns.append(self)

    def key(self):
        return "%d:%d" % (self.part.eid, self.cid)


def connect(a, b):
    a.to.append(b); b.to.append(a)


class Part:
    eid_next = 400001

    def __init__(self, sysname, cat, family, ptype, name_kind, size, fab, material, thk, insul=None,
                 run_sys=None, navis=True, revit=True):
        self.eid = Part.eid_next; Part.eid_next += 1
        self.guid = new_guid()
        self.uid = str(uuid.UUID(int=rng.getrandbits(128))) + "-%08x" % self.eid
        self.revit_guid_param = self.guid          # "" = Revit param blank (GUID computed from UniqueId)
        self.navis_guid = self.guid
        self.sys = sysname                         # system code (SYSA ...)
        self.run_sys = run_sys or sysname          # system written in RunName / SystemName (mislabels differ)
        self.cat, self.family, self.ptype, self.kind = cat, family, ptype, name_kind
        self.size, self.fab, self.material, self.thk, self.insul = size, fab, material, thk, insul
        self.navis, self.revit = navis, revit
        self.conns, self.boxes = [], []            # boxes: (p0, p1, half_w, half_h, wdir)
        self.fit = None                            # straights: (e1, e2) fitted ends
        self.room = None
        self.flange = False
        self.navis_id = uuid.UUID(int=rng.getrandbits(128)).hex
        self.note = ""
        PARTS.append(self)

    def centre_bbox(self):
        lo, hi = bbox_of(self.mesh())
        return (lo + hi) / 2

    def name(self):
        return "%s %s" % (self.size.text(), self.kind)

    def mesh(self):
        tris = []
        for (p0, p1, hw, hh, wdir) in self.boxes:
            tris += cyl_tris(p0, p1, hw, wdir) if self.size.round and self.kind != "Transition" else box_tris(p0, p1, hw, hh, wdir, caps=False)
            if self.flange:
                fl = 3.95 * FT; th = 1.5 * FT; ax = unit(p1 - p0)
                for e in (p0, p1):
                    q = e + ax * (th if e is p0 else -th)
                    tris += box_tris(e, q, hw + fl, hh + fl, wdir, caps=False)
        return tris


PARTS = []


def box_tris(p0, p1, hw, hh, wdir, caps=True):
    ax = unit(p1 - p0)
    w = unit(wdir - ax * np.dot(wdir, ax))
    h = np.cross(ax, w)
    c = []
    for e in (p0, p1):
        for sw, sh in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            c.append(e + w * hw * sw + h * hh * sh)
    q = [(0, 1, 2), (0, 2, 3), (4, 6, 5), (4, 7, 6), (0, 4, 5), (0, 5, 1), (1, 5, 6), (1, 6, 2),
         (2, 6, 7), (2, 7, 3), (3, 7, 4), (3, 4, 0)]
    return [(c[a], c[b], c[d]) for a, b, d in (q if caps else q[4:])]


def cyl_tris(p0, p1, r, wdir, n=16):
    ax = unit(p1 - p0)
    u = unit(wdir - ax * np.dot(wdir, ax)); v = np.cross(ax, u)
    ring = [u * r * math.cos(2 * math.pi * k / n) + v * r * math.sin(2 * math.pi * k / n) for k in range(n)]
    out = []
    for k in range(n):
        a, b = ring[k], ring[(k + 1) % n]
        out += [(p0 + a, p0 + b, p1 + b), (p0 + a, p1 + b, p1 + a)]   # open tube, as a fabrication part
    return out


def bbox_of(tris):
    pts = np.array([p for t in tris for p in t])
    return pts.min(0), pts.max(0)


def horiz_w(d, prev=None):
    """width direction: horizontal, perpendicular to the axis; vertical axis keeps prev."""
    if abs(d[2]) > 0.99:
        return prev if prev is not None else Y.copy()
    return unit(np.cross(Z, d))


class Runner:
    """Walks a duct run part by part, connecting each new part to the previous open connector."""

    def __init__(self, sysname, pos, d, size, style, material, thk, insul=None, last=None):
        self.sys, self.p, self.d, self.size = sysname, np.array(pos, float), unit(d), size
        self.style, self.material, self.thk, self.insul = style, material, thk, insul   # style: "fab" | "revit"
        self.w = horiz_w(self.d)
        self.last = last
        self.run_sys = None

    def _part(self, kind, size=None, cat_override=None, **kw):
        size = size or self.size
        fab = self.style == "fab"
        fam = {"Straight": "Straight", "Bend": "Radius Bend", "Transition": "Transition", "Round Tap": "Round Tap",
               "Shoe Tap": "Shoe Tap", "Damper": "Volume Damper", "End Cap": "End Cap"}[kind]
        if fab:
            cat = "MEP Fabrication Ductwork"; ptype = "Default"
        else:
            cat = {"Straight": "Ducts", "Damper": "Duct Accessories"}.get(kind, "Duct Fittings")
            fam = {"Straight": "Rectangular Duct" if not size.round else "Round Duct",
                   "Bend": "Rectangular Elbow - Mitered", "Transition": "Rectangular Transition - Angle",
                   "Shoe Tap": "Rectangular Takeoff", "Round Tap": "Round Takeoff", "Damper": "Volume Damper",
                   "End Cap": "Rectangular Endcap"}[kind]
            ptype = "Mitered Elbows / Taps"
        p = Part(self.sys, cat_override or cat, fam, ptype, kind, size, fab, self.material, self.thk, self.insul,
                 run_sys=self.run_sys, **kw)
        return p

    def _link(self, c):
        if self.last is not None:
            connect(self.last, c)

    def straight(self, length, piece=8.0, **kw):
        out = []
        n = max(1, int(math.ceil(length / piece - 1e-9)))
        for i in range(n):
            L = length / n
            p = self._part("Straight", **kw)
            a, b = self.p, self.p + self.d * L
            c1 = Conn(p, a, -self.d, self.size, self.w); self._link(c1)
            c2 = Conn(p, b, self.d, self.size, self.w)
            p.boxes.append((a, b, self.size.half(True), self.size.half(False), self.w))
            p.fit = (a, b)
            p.flange = p.fab and not self.size.round and self.size.w >= 60
            self.p, self.last = b, c2
            out.append(p)
        return out

    def bend(self, d2, carry=False):
        """carry: the width direction turns with the elbow (rotation d -> d2), as a real elbow does, instead of
        being reset to horizontal."""
        d2 = unit(d2)
        p = self._part("Bend")
        a = max(self.size.half(True), self.size.half(False)) + 3 * FT
        corner = self.p + self.d * a
        if carry:
            k = np.cross(self.d, d2); s = np.linalg.norm(k); c = float(np.dot(self.d, d2))
            k = k / s if s > 1e-9 else k
            w2 = unit(self.w * c + np.cross(k, self.w) * s + k * np.dot(k, self.w) * (1 - c))
        else:
            w2 = horiz_w(d2, self.w)
        c1 = Conn(p, self.p, -self.d, self.size, self.w); self._link(c1)
        end = corner + d2 * a
        c2 = Conn(p, end, d2, self.size, w2)
        hw, hh = self.size.half(True), self.size.half(False)
        p.boxes.append((self.p, corner + self.d * hh, hw, hh, self.w))
        p.boxes.append((corner - d2 * hh, end, hw, hh, w2))
        self.p, self.d, self.w, self.last = end, d2, w2, c2
        return p

    def transition(self, size2, length=2.0, keep_bottom=True):
        p = self._part("Transition"); p.size2 = size2
        dz = 0.0
        if keep_bottom and abs(self.d[2]) < 0.1:
            h1 = self.size.d if self.size.round else self.size.h
            h2 = size2.d if size2.round else size2.h
            dz = (h2 - h1) / 24
        c1 = Conn(p, self.p, -self.d, self.size, self.w); self._link(c1)
        end = self.p + self.d * length + Z * dz
        c2 = Conn(p, end, self.d, size2, self.w)
        big = lambda s: (max(s.half(True), 0), max(s.half(False), 0))
        hw = max(big(self.size)[0], big(size2)[0]); hh = max(big(self.size)[1], big(size2)[1])
        p.boxes.append((self.p, end, hw, hh, self.w))
        p.note = "%s -> %s" % (self.size.text(), size2.text())
        self.p, self.size, self.last = end, size2, c2
        return p

    def inline(self, kind, length=1.0, **kw):
        p = self._part(kind, **kw)
        c1 = Conn(p, self.p, -self.d, self.size, self.w); self._link(c1)
        end = self.p + self.d * length
        c2 = Conn(p, end, self.d, self.size, self.w)
        p.boxes.append((self.p, end, self.size.half(True) + FT, self.size.half(False) + FT, self.w))
        self.p, self.last = end, c2
        return p

    def gap(self, inches):
        self.p = self.p + self.d * inches * FT
        self.last = None

    def tap(self, main, frac, side, size, style=None, kind=None):
        """branch off straight `main` at fraction `frac` of its length toward unit `side` (perp. to the main)."""
        side = unit(side)
        a, b = main.fit
        q = a + (b - a) * frac
        mw = main.boxes[0][4]
        along_w = abs(np.dot(side, mw)) > 0.5
        P = q + side * main.size.half(along_w)
        r = Runner(self.sys, P, side, size, style or self.style, self.material, self.thk, self.insul)
        r.run_sys = self.run_sys
        kind = kind or ("Round Tap" if size.round else "Shoe Tap")
        t = r._part(kind)
        cm = Conn(main, P, side, size, r.w, ctype="Curve")
        c1 = Conn(t, P, -side, size, r.w); connect(cm, c1)
        L = 1.0 if kind == "Shoe Tap" else 0.5
        end = P + side * L
        c2 = Conn(t, end, side, size, r.w)
        t.boxes.append((P, end, size.half(True), size.half(False), r.w))
        r.p, r.last = end, c2
        return r


def z_on_steel(top, gap_in, size):
    h = size.d if size.round else size.h
    return top + gap_in * FT + h / 24


def build_ducts():
    S, CS = "Stainless Steel", "Carbon Steel"
    # ---- SYSA: fabrication rectangular main 74x28 (flanged 82x36), stainless, bottom 0.5 in above the rack beams
    A = Size(74, 28); ya = 516.0
    ra = Runner("SYSA", (1002, ya, z_on_steel(TOP_OF_STEEL, 0.5, A)), X, A, "fab", S, 0.0625)
    first = ra.straight(8)
    shared = ra.straight(15, piece=5)                  # 3 parts of one fabrication assembly: one IfcGUID
    g = shared[0].guid
    for p in shared:
        p.guid = p.navis_guid = p.revit_guid_param = g
    main_tap1 = ra.straight(8)[0]                      # shoe tap off this one
    ra.straight(10)
    navonly = ra.straight(8)[0]                        # Navisworks row with another IfcGUID than Revit's
    navonly.navis_guid = new_guid(); navonly.note = "same part, other IfcGUID in Navisworks"
    calc = ra.straight(8)[0]                           # Revit IfcGUID parameter blank: computed from UniqueId
    calc.revit_guid_param = ""; calc.guid = calc.navis_guid = ifc_guid_from_unique_id(calc.uid)
    ra.straight(24)
    ra.transition(Size(60, 24))                        # concentric, flat bottom: gap stays 0.5 in
    ra.straight(16)
    main_tap2 = ra.straight(8)[0]                      # round tap off this one
    ra.straight(36)
    ra.straight(8)[0].thk = None                       # missing thickness
    # shoe tap -> 24x12 branch to the south, off the rack (unsupported), then down: a vertical path
    rb = ra.tap(main_tap1, 0.5, -Y, Size(24, 12))
    rb.straight(6)
    rb.bend(-Z)
    rb.straight(7)
    # round tap 14 dia to the south; one piece mislabelled as SYSB; a cut point on it drops the far end
    rr = ra.tap(main_tap2, 0.5, -Y, Size(d=14), kind="Round Tap")
    rr.straight(3)
    rr.run_sys = "SYSB"; mis = rr.straight(3)[0]; mis.note = "mislabelled: RunName says SYSB"; rr.run_sys = None
    rr.straight(4)
    global CUT_AT
    CUT_AT = rr.p.copy() - Y * 1.0
    rr.straight(6)
    # SYSA fragment: one floating piece in ROOM-03, not connected to the main
    rf = Runner("SYSA", (1158, 505.0, 110.0), X, Size(30, 18), "fab", S, 0.0625)
    rf.straight(8)

    # ---- SYSB: Revit (non-fabrication) 48x24 carbon steel ducts, insulated, 2 in above the beams, S-offset up to
    # the hung beams (bottom 1.5 in above them), a Revit-only damper
    B = Size(48, 24); yb = 525.5
    rs = Runner("SYSB", (1002, yb, z_on_steel(TOP_OF_STEEL, 2.0, B)), X, B, "revit", CS, 0.0478, insul=1.5)
    first_b = rs.straight(30, piece=10)
    rs.inline("Damper", 1.0, navis=False)              # Revit only
    mt = rs.straight(10)[0]
    rs.straight(56, piece=10)
    # S-offset: two 45 deg bends, rise to the hung beams (top EL 122)
    up = unit(X + Z)
    rise = z_on_steel(122.0, 1.5, B) - rs.p[2]
    rs.bend(up)
    a = max(B.half(True), B.half(False)) + 3 * FT
    # rise = (a + L + a) sin45 along the 45 deg leg: solve the straight length L
    run45 = (rise - 2 * a * math.sin(math.radians(45))) / math.sin(math.radians(45))
    rs.straight(max(run45, 0.5))
    rs.bend(X)
    rs.straight(60, piece=10)
    rs.straight(8)
    rs.inline("End Cap", 0.25)
    # round tap off the SYSB main going north into the wall zone (other insulation thickness)
    rbn = rs.tap(mt, 0.5, Y, Size(d=12), style="revit", kind="Round Tap")
    tap_b = PARTS[-1]
    rbn.insul = 1.0
    # the drop list names every part of that branch's clean member (tap + straight)
    drop_ids = [tap_b.eid] + [p.eid for p in rbn.straight(6)]

    # ---- SYSC: fabrication round 24 dia, blank material, missing thickness on some, 4 in above the beams; a 10 in
    # gap; a round-to-rect transition into an 18x36 duct (larger dimension vertical)
    C = Size(d=24); yc = 521.2
    rc = Runner("SYSC", (1004, yc, z_on_steel(TOP_OF_STEEL, 4.0, C)), X, C, "fab", "", 0.0396)
    rc.straight(25, piece=5)
    for p in rc.straight(10, piece=5):
        p.thk = None
    rc.straight(26, piece=5)
    rc.gap(10)                                          # 10 in gap between two open ends, lined up
    rc.straight(20, piece=5)
    rc.transition(Size(18, 36))
    tail = rc.straight(30, piece=5)

    # ---- other system: a short exhaust run in ROOM-02 (not a primary system)
    ro = Runner("EXH", (1070, 534.0, 117.0), X, Size(d=10), "fab", "Carbon Steel", 0.0276)
    ro.straight(12, piece=6)

    # ---- orientation case (appended last, own random stream, so the parts above keep their ids and GUIDs): the
    # SYSC 18x36 (larger dimension vertical) gets a rect riser at its end that carries its width direction up, a
    # round riser tapped off its top, and a rect branch off its side that turns down and hangs
    global rng
    main_rng, rng = rng, random.Random(11)
    rc.bend(Z)
    rc.straight(8, piece=4)
    rc.bend(X)
    rc.straight(4)
    rup = rc.tap(tail[4], 0.5, Z, Size(d=10), kind="Round Tap")
    rup.straight(5)
    rdn = rc.tap(tail[2], 0.5, Y, Size(12, 8))
    rdn.straight(3)
    rdn.bend(-Z)
    rdn.straight(6, piece=3)
    # rotation conflict: one same-size 20x30 run off SYSB going north, larger side vertical, a riser, then an elbow
    # that carries the width direction round so the last leg has its larger side horizontal
    rcf = rs.tap(first_b[1], 0.5, Y, Size(20, 30))
    rcf.straight(3)
    rcf.bend(Z, carry=True)
    rcf.straight(4)
    rcf.bend(X, carry=True)
    rcf.straight(6, piece=3)
    # rotation stops at a transition: a 16x28 run off SYSA going south (larger side vertical), then a transition to
    # 28x16 (larger side horizontal)
    rtr = ra.tap(first[0], 0.5, -Y, Size(16, 28))
    rtr.straight(3)
    rtr.transition(Size(28, 16))
    rtr.straight(4)
    rng = main_rng
    return drop_ids


# ---------------------------------------------------------------- writers
def fmt(v):
    if v is None or (isinstance(v, float) and math.isnan(v)):
        return ""
    if isinstance(v, (float, np.floating)):
        return ("%.6f" % v).rstrip("0").rstrip(".")
    return str(v)


def csv_cell(s):
    s = fmt(s)
    return '"' + s.replace('"', '""') + '"' if any(c in s for c in ',"\n') else s


def write_csv(path, header, rows):
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(",".join(header) + "\r\n")
        for r in rows:
            f.write(",".join(csv_cell(c) for c in r) + "\r\n")


def run_name(p):
    return "A-%s-%s-DUCT-%d" % (BLDG, p.run_sys, 1 if p.run_sys == p.sys else 3)


def write_navis(root):
    d = os.path.join(root, "ducts", RUN); os.makedirs(d, exist_ok=True)
    rows, tri = [], []
    navis_parts = [p for p in PARTS if p.navis]
    for p in navis_parts:
        m = p.mesh(); lo, hi = bbox_of(m)
        c = (lo + hi) / 2
        room = room_of(c[0])
        if p.sys == "SYSB" and room == "ROOM-02" and c[0] < 1075:
            room = "ROOM-02A"                            # a room variant: "ROOM-02" takes it
        s = p.size
        fab = p.fab
        e1 = e2 = None; fitlen = fa = fb = None
        if p.fit is not None:
            e1, e2 = p.fit; fitlen = float(np.linalg.norm(e2 - e1))
            fa = s.d if s.round else s.w; fb = s.d if s.round else s.h
        rn = run_name(p)
        rows.append([
            p.navis_guid, p.navis_id, p.cat, p.family, p.ptype, p.name(), "%s Supply" % p.run_sys if p.sys != "EXH" else "EXH Exhaust",
            "Supply Air" if p.sys != "EXH" else "Exhaust Air", "Round" if s.round else "Rectangular", p.material,
            None if (fab or s.round) else float(s.w), None if (fab or s.round) else float(s.h), None if (fab or not s.round) else float(s.d),
            p.thk, p.insul, fitlen, "" if fab else s.text(), "",
            lo[0], lo[1], lo[2], hi[0], hi[1], hi[2], len(p.boxes), 0, len(m),
            *(list(e1) + list(e2) if e1 is not None else [None] * 6), fitlen, fa, fb,
            None, None, None, "", "", 0,
            rn, p.run_sys, p.run_sys, room, "Level 1", "OID-%06d" % (p.eid - 400000), "CWP-01"])
        tri.append(m)
    # fabrication hangers: Navisworks only (the Revit export has no hanger category)
    for hx in (1012.0, 1032.0, 1052.0):
        lo = np.array([hx - 0.1, 512.5, TOP_OF_STEEL + 3.4]); hi = np.array([hx + 0.1, 519.5, 125.0])
        rows.append([new_guid(), uuid.UUID(int=rng.getrandbits(128)).hex, "MEP Fabrication Hangers", "Rod Hanger", "Default",
                     "Rod Hanger", "SYSA Supply", "Supply Air", "", "", None, None, None, None, None, None, "", "",
                     *lo, *hi, 1, 0, 12, *([None] * 9), None, None, None, "", "", 0,
                     "A-%s-SYSA-DUCT-1" % BLDG, "SYSA", "SYSA", room_of(hx), "Level 1", "", "CWP-01"])
        tri.append(box_tris((lo + hi) / 2 - Z * (hi[2] - lo[2]) / 2, (lo + hi) / 2 + Z * (hi[2] - lo[2]) / 2, 3.5, 0.1, X))
    header = ["IfcGUID", "NavisId", "Category", "Family", "Type", "Name", "SystemName", "SystemType", "Shape", "Material",
              "Width_in", "Height_in", "Diameter_in", "WallThk_in", "InsulThk_in", "Length_ft", "Size", "Location",
              "MinX", "MinY", "MinZ", "MaxX", "MaxY", "MaxZ", "OwnGeom", "SkippedGeom", "Triangles",
              "X1", "Y1", "Z1", "X2", "Y2", "Z2", "FitLength_ft", "FitA_in", "FitB_in",
              "LenErr_ft", "SizeErr_in", "TriVsBbox_ft", "Flag", "Hidden", "HiddenGeom",
              "RunName", "ServiceAbbrev", "SystemAbbrev", "Room", "RefLevel", "OID", "CWP"]
    write_csv(os.path.join(d, "ducts.csv"), header, rows)
    with open(os.path.join(d, "ducts_tri.bin"), "wb") as f:
        for i, m in enumerate(tri):
            f.write(struct.pack("<ii", i + 1, len(m)))
            f.write(np.array([[q for pt in t for q in pt] for t in m], dtype="<f4").tobytes())
    # Revit centrelines for the non-fabrication ducts / fittings (the button's linework; -Centrelines, plain export)
    nodes, segs = {}, []

    def nid(pt):
        k = tuple(np.round(pt, 3))
        if k not in nodes:
            nodes[k] = [len(nodes) + 1, 0, set()]
        return nodes[k]
    for i, p in enumerate(navis_parts):
        if p.fab or p.cat not in ("Ducts", "Duct Fittings"):
            continue
        ends = [c.pos for c in p.conns if c.type == "End"]
        pts = [ends[0], ends[1]] if p.cat == "Ducts" or p.kind != "Bend" else [ends[0], p.boxes[0][1] - unit(p.boxes[0][1] - p.boxes[0][0]) * p.boxes[0][3], ends[1]]
        for a, b in zip(pts[:-1], pts[1:]):
            na, nb = nid(a), nid(b)
            for n in (na, nb):
                n[1] += 1; n[2].add(p.cat)
            segs.append([len(segs) + 1, i + 1, p.navis_guid, p.cat, na[0], nb[0], float(np.linalg.norm(b - a))])
    write_csv(os.path.join(d, "cl_segments.csv"), ["Seg", "Row", "IfcGUID", "Category", "Node1", "Node2", "Length_ft"], segs)
    write_csv(os.path.join(d, "cl_nodes.csv"), ["Node", "X", "Y", "Z", "Degree", "Categories"],
              [[v[0], k[0], k[1], k[2], v[1], ";".join(sorted(v[2]))] for k, v in sorted(nodes.items(), key=lambda kv: kv[1][0])])
    return d, rows, tri


def to_revit(p):
    th = math.radians(-REVIT_ROT_DEG); c, s = math.cos(th), math.sin(th)
    q = np.asarray(p, float) - REVIT_T
    return np.array([c * q[0] - s * q[1], s * q[0] + c * q[1], q[2]])


def rot_revit(v):
    th = math.radians(-REVIT_ROT_DEG); c, s = math.cos(th), math.sin(th)
    return np.array([c * v[0] - s * v[1], s * v[0] + c * v[1], v[2]])


def write_revit(root):
    d = os.path.join(root, "revit", RUN); os.makedirs(d, exist_ok=True)
    er, cr = [], []
    for p in PARTS:
        if not p.revit:
            continue
        rn = run_name(p)
        er.append([p.eid, p.uid, p.revit_guid_param, p.cat, p.family, p.ptype,
                   p.size.revit() + ("-" + p.size2.revit() if p.kind == "Transition" else ""),
                   "%s Supply" % p.run_sys if p.sys != "EXH" else "EXH Exhaust",
                   "Supply Air" if p.sys != "EXH" else "Exhaust Air", "Level 1", 1 if p.fab else 0, len(p.conns), rn, "RunName"])
        for k in p.conns:
            P = to_revit(k.pos); D = rot_revit(k.d); W = rot_revit(k.wdir)
            s = k.size
            cr.append([p.eid, k.cid, k.type, "DomainHvac", "Round" if s.round else "Rectangular", *P, *D, *W,
                       None if s.round else float(s.w), None if s.round else float(s.h), float(s.d) if s.round else None,
                       ";".join(o.key() for o in k.to)])
    write_csv(os.path.join(d, "elements.csv"), ["ElementId", "UniqueId", "IfcGUID", "Category", "Family", "Type", "Size", "SystemName",
                                                "SystemType", "Level", "IsFabrication", "Connectors", "RunName", "RunNameParam"], er)
    write_csv(os.path.join(d, "connectors.csv"), ["ElementId", "ConnectorId", "ConnType", "Domain", "Shape", "X_ft", "Y_ft", "Z_ft",
                                                  "DirX", "DirY", "DirZ", "WidthDirX", "WidthDirY", "WidthDirZ", "Width_in",
                                                  "Height_in", "Diameter_in", "ConnectedTo"], cr)
    with open(os.path.join(d, "summary.txt"), "w") as f:
        f.write("synthetic Pluto Connectors run (make_demo_data.py)\nRevit -> plant: rotate %g deg about Z, then translate %s ft\n"
                % (REVIT_ROT_DEG, list(REVIT_T)))
    return d


def write_box(root, nav_rows, tri):
    """Pluto Box run: every Navisworks item inside a box around ROOM-02 (one geometry item per row)."""
    d = os.path.join(root, "box", RUN); os.makedirs(d, exist_ok=True)
    items, recs = [], []
    for r, m in zip(nav_rows, tri):
        lo, hi = bbox_of(m)
        if not (1060 <= (lo[0] + hi[0]) / 2 < 1120):
            continue
        items.append([len(items) + 1, uuid.UUID(int=rng.getrandbits(128)).hex, "Solid", "Geometry", "LcOaPolyShell", 0, 0, "", "ROOM-02",
                      r[0], r[2], r[3], r[4], r[16], r[5], r[1], 1, r[42], r[6], "Model>Level 1>" + r[2], len(m), len(m), 0,
                      *lo, *hi, r[45]])
        recs.append(m)
    write_csv(os.path.join(d, "box_items.csv"),
              "Row,NavisId,DisplayName,ClassDisplayName,ClassName,HiddenSelf,Hidden,HiddenBy,Box,AncIfcGUID,AncCategory,AncFamily,AncType,AncSize,AncName,AncNavisId,AncLevelsUp,AncRunName,AncSystem,Path,TrianglesTotal,TrianglesKept,Lines,MinX,MinY,MinZ,MaxX,MaxY,MaxZ,AncRoom".split(","),
              items)
    with open(os.path.join(d, "box_tri.bin"), "wb") as f:
        for i, m in enumerate(recs):
            f.write(struct.pack("<ii", i + 1, len(m)))
            f.write(np.array([[q for pt in t for q in pt] for t in m], dtype="<f4").tobytes())
    return d


def write_revit_mesh(root):
    """Revit Pluto Mesh run: Revit solids in the Revit frame + the fit line (mesh-boxes.txt) to the plant frame."""
    d = os.path.join(root, "revit-mesh", RUN); os.makedirs(d, exist_ok=True)
    items, recs = [], []
    for p in PARTS:
        if not p.revit or room_of(p.centre_bbox()[0]) != "ROOM-02":
            continue
        items.append([p.eid, p.cat, p.family, p.ptype, run_name(p), "%s Supply" % p.run_sys, 1 if p.fab else 0, p.guid, ""])
        recs.append([tuple(to_revit(q) for q in t) for t in p.mesh()])
        if p.insul:   # insulation covering the duct (no RunName of its own: takes the covered duct's)
            items.append([Part.eid_next + len(items), "Duct Insulations", "Duct Insulation", "Fiberglass", "", "", 0, "", str(p.eid)])
            recs.append([tuple(to_revit(q) for q in t) for (a, b, hw, hh, w) in p.boxes
                         for t in box_tris(a, b, hw + p.insul * FT, hh + p.insul * FT, w)])
    write_csv(os.path.join(d, "mesh_items.csv"), ["ElementId", "Category", "Family", "Type", "RunName", "SystemName", "IsFabrication", "IfcGUID", "Host"], items)
    with open(os.path.join(d, "mesh_tri.bin"), "wb") as f:
        for i, m in enumerate(recs):
            f.write(struct.pack("<ii", i + 1, len(m)))
            f.write(np.array([[q for pt in t for q in pt] for t in m], dtype="<f4").tobytes())
    th = math.radians(REVIT_ROT_DEG)
    with open(os.path.join(d, "mesh-boxes.txt"), "w") as f:
        f.write("fit %.9f %.9f %.6f %.6f %.6f\n" % (math.cos(th), math.sin(th), *REVIT_T))
    return d


# ---------------------------------------------------------------- steel
W = {   # name: D, Bf, Tf, Tw (in)
    "W10X49": (9.98, 10.0, 0.56, 0.34), "W12X26": (12.2, 6.49, 0.38, 0.23), "W14X30": (13.8, 6.73, 0.385, 0.27),
    "W8X31": (8.0, 8.0, 0.435, 0.285), "W10X33": (9.73, 7.96, 0.435, 0.29), "W6X15": (5.99, 5.99, 0.26, 0.23)}


def build_steel():
    M = []

    def mem(p0, p1, sec, cp, web=None, note=""):
        p0, p1 = np.array(p0, float), np.array(p1, float)
        ax = unit(p1 - p0)
        if web is None:
            web = Z if abs(ax[2]) < 0.99 else X
            web = unit(web - ax * np.dot(web, ax))
        M.append((p0, p1, sec, cp, np.asarray(web, float), note))

    T = TOP_OF_STEEL
    cols = [1000 + 20 * i for i in range(10)]
    # columns (centred), fixed bases, top at the beam line
    for x in cols:
        for y in (510.0, 530.0):
            mem((x, y, 100.0), (x, y, T), "W10X49", "", web=X)
    # longitudinal beams, CP 8 (top-centre), spliced end to end at X 1045 / 1100 / 1145 (collinear splices)
    for y in (510.0, 530.0):
        xs = [1000, 1045, 1100, 1145, 1180] if y == 510.0 else [1000, 1045, 1060, 1100, 1145, 1180]
        for a, b in zip(xs[:-1], xs[1:]):
            mem((a, y, T), (b, y, T), "W14X30", 8)
    # cross beams at every column line and mid-bay, CP 8; mid-bays 1080-1100 and 1140-1160 left out (20 ft spans)
    for x in cols + [c + 10 for c in cols[:-1] if c + 10 not in (1090, 1150)]:
        mem((x, 510.0, T), (x, 530.0, T), "W12X26", 8)
    # vertical braces (sloped, framed both ends) in the two end bays of both lines
    for y in (510.0, 530.0):
        mem((1000, y, 100.0), (1010, y, T), "W8X31", "")
        mem((1180, y, 100.0), (1170, y, T), "W8X31", "")
    # cantilevers into the walls: north wall Y 540 from the X 1060 column top (4-way node with the two longitudinal
    # beams and the cross beam), south wall Y 500 from X 1140
    mem((1060, 530.0, T), (1060, 540.0, T), "W12X26", 8)
    mem((1140, 510.0, T), (1140, 500.0, T), "W12X26", 8)
    # hung frames from the ceiling (EL 130): two hangers + a beam at EL 122 (CP 8) under the raised SYSB duct
    for x in (1114, 1122, 1130, 1140, 1150, 1160, 1170):
        mem((x, 521.5, 130.0), (x, 521.5, 122.0), "W6X15", "", web=X)
        mem((x, 529.5, 130.0), (x, 529.5, 122.0), "W6X15", "", web=X)
        mem((x, 521.5, 122.0), (x, 529.5, 122.0), "W10X33", 8)
    # a stray beam far from the rack: two plain open ends
    mem((1186, 520.0, 121.0), (1192, 520.0, 121.0), "W6X15", 8)
    # a near miss: a strut on the rack's outer line stopping 12 in short of the X 1180 column (beyond the 6 in snap,
    # within 24 in): its west end is an open end flagged OPEN_NEAR; clear of every duct
    mem((1190, 510.0, 108.0), (1181, 510.0, 108.0), "W6X15", "")
    return M


def write_steel(root, M):
    d = os.path.join(root, "steel"); os.makedirs(d, exist_ok=True)
    m = 0.3048
    rows = []
    for i, (p0, p1, sec, cp, web, note) in enumerate(M):
        D, Bf, Tf, Tw = W[sec]
        rows.append(["M%04d" % (i + 1), *(p0 * m), *(p1 * m), sec, D * 0.0254, Bf * 0.0254, Tf * 0.0254, Tw * 0.0254,
                     *web, room_of((p0[0] + p1[0]) / 2), "RACK-1", cp])
    write_csv(os.path.join(d, "steel.csv"), ["MemberOid", "X0", "Y0", "Z0", "X1", "Y1", "Z1", "SectionName", "D", "Bf", "Tf", "Tw",
                                             "YDirX", "YDirY", "YDirZ", "Room", "RunName", "CP"], rows)
    return os.path.join(d, "steel.csv")


# ---------------------------------------------------------------- cuts, drop, config
def write_cuts(root, drop_ids):
    d = os.path.join(root, "cuts"); os.makedirs(d, exist_ok=True)
    c = CUT_AT * 12
    write_csv(os.path.join(d, "cuts.csv"), ["Name", "Found", "X_in", "Y_in", "Z_in", "Frames", "FrameNames"],
              [["CUT1", 1, *c, 2, ""], ["CUT2", 0, None, None, None, 0, ""]])
    with open(os.path.join(d, "drop.txt"), "w") as f:
        f.write("# hand-picked drops (viewer Cut picker): one Revit ElementId or N:<NavisId> per line\n")
        for e in drop_ids:
            f.write("%d   # SYSB north round branch (tap + straight)\n" % e)
        f.write("999999   # not in the model: reported as unused\n")
    return d


CONFIG = r"""# Demo duct-config.psd1 (synthetic data, make_demo_data.py). Copy to scripts\duct\duct-config.psd1 to run the
# pipeline on the demo data. Same keys as duct-config.example.psd1, plus DuctSteel.
@{{
    WorkDir = '{root}'
    # files shared with the add-ins (room boxes, service codes): the demo's own root, never the production folder
    AddinDir = '{root}'
    SapDir  = ''
    # SYSA / SYSB / SYSC: the RunName service field ("A-B1-<service>-DUCT-<n>") has no hyphens, so no 'SYS-A'
    DuctSystems = @('SYSA=#4f8cff', 'SYSB=#ff8a3d', 'SYSC')
    DuctRooms = @('ROOM-01', 'ROOM-02', 'ROOM-03')
    DuctMains = @()
    DuctCuts = '{root}\cuts\cuts.csv'
    RevitMeshNoRunExclude = @()
    DuctDrop = @('{root}\cuts\drop.txt')
    # utility rack steel (SteelToPluto CSV: meters, plant frame)
    DuctSteel = '{root}\steel\steel.csv'
    Group = 'DUCT_ALL'
    Cases = @{{
        Dead    = '1 DEAD'
        Steel   = 'Steel_Loading'
        Seismic = @('3 Seismic X', '4 Seismic Z', '5 Seismic Y - vert')
        Combo18 = '18 BLC 7B'
        Combo19 = '19 BLC 7C'
        Combo20 = '20 BLC 7D'
    }}
    Dcr = @{{
        ComboLS = 'C'; Material = '304L'; CarbonSections = @(); EndsOnly = $true; NoShear = $true; StiffenerSpacingIn = 24; Limit = 1.0
    }}
    Release = @{{ Candidates = @('1'); StiffnessFactor = 1000 }}
    Optimize = @{{ Candidates = 'auto'; MaxCandidates = 20; MaxJoints = 10 }}
}}
"""


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--out", required=True, help="demo root (layout of the production work folder)")
    a = ap.parse_args()
    root = os.path.abspath(a.out)
    os.makedirs(root, exist_ok=True)
    drop_ids = build_ducts()
    nd, rows, tri = write_navis(root)
    rd = write_revit(root)
    write_box(root, rows, tri)
    write_revit_mesh(root)
    steel = write_steel(root, build_steel())
    write_cuts(root, drop_ids)
    with open(os.path.join(root, "duct-config.psd1"), "w", encoding="utf-8") as f:
        f.write(CONFIG.format(root=root))
    shutil.copyfile(os.path.join(os.path.dirname(os.path.abspath(__file__)), "demo-README.md"), os.path.join(root, "README.md"))
    print("ducts  %s: %d rows" % (nd, len(rows)))
    print("revit  %s: %d elements" % (rd, sum(1 for p in PARTS if p.revit)))
    print("steel  %s" % steel)
    print("config %s" % os.path.join(root, "duct-config.psd1"))


if __name__ == "__main__":
    main()
