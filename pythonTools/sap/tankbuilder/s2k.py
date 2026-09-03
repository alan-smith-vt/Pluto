"""SAP2000 .s2k text tables: writer for TankModel, and a small reader.

The output of write_s2k for configs/example.toml is pinned to
tests/golden/example.s2k (see tests/test_build_tank.py) -- keep it so, or
regenerate the golden on purpose.
"""

from __future__ import annotations

import math
import re
from pathlib import Path

from .model import TankModel

# --- formatting -------------------------------------------------------------


def _fmt(value) -> str:
    if isinstance(value, bool):
        return "Yes" if value else "No"
    if isinstance(value, float):
        return f"{value:.9g}"
    text = str(value)
    return f'"{text}"' if (" " in text or "," in text) else text


def _row(**fields) -> str:
    return "   " + "   ".join(f"{k}={_fmt(v)}" for k, v in fields.items())


class S2KWriter:
    def __init__(self):
        self.lines: list[str] = []

    def table(self, name: str, rows: list[str]) -> None:
        if not rows:
            return
        self.lines.append(f'TABLE:  "{name}"')
        self.lines.extend(rows)
        self.lines.append("")

    def text(self) -> str:
        return "\n".join(self.lines + ["END TABLE DATA", ""])


# --- tables -----------------------------------------------------------------


def program_control() -> list[str]:
    return [_row(ProgramName="SAP2000", Version="24.0.0", CurrUnits="Kip, ft, F",
                 MergeTol=0.001)]


def material_tables(model: TankModel) -> list[tuple[str, list[str]]]:
    s = model.spec
    g = s.mat_e / (2.0 * (1.0 + s.mat_poisson))
    from .spec import G_ACCEL
    return [
        ("MATERIAL PROPERTIES 01 - GENERAL", [
            _row(Material=s.mat_name, Type="Steel", SymType="Isotropic",
                 TempDepend=False, Color="Cyan")]),
        ("MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES", [
            _row(Material=s.mat_name, UnitWeight=s.mat_unit_weight,
                 UnitMass=s.mat_unit_weight / G_ACCEL, E1=s.mat_e, G12=g,
                 U12=s.mat_poisson, A1=s.mat_alpha)]),
    ]


def section_tables(model: TankModel) -> list[tuple[str, list[str]]]:
    s = model.spec
    out = [
        ("AREA SECTION PROPERTIES", [
            _row(Section=name, Material=s.mat_name, MatAngle=0,
                 AreaType="Shell", Type="Shell-Thin", Thickness=t,
                 BendThick=t, Color="Gray8Dark")
            for name, t in model.sections.items()]),
    ]
    if model.frame_sections:
        # Shape + dimensions for SAP's own shapes; Shape=General rows carry the
        # computed properties (section.general_section).
        # NOTE: not yet verified against the v25 importer (first frames in this
        # generator, 2026-09-03) -- if it rejects the table, the field names are
        # the suspect, not the values.
        out.append(("FRAME SECTION PROPERTIES 01 - GENERAL", [
            _row(SectionName=name, Material=s.mat_name, **props, Color="Yellow")
            for name, props in model.frame_sections.items()]))
    return out


def link_tables(model: TankModel) -> list[tuple[str, list[str]]]:
    """Compression-only Gap links (foundation.mode = 'gap'). U1 is the only
    active DOF: nonlinear gap with zero opening; TransKE is the effective
    stiffness the LINEAR cases see (a two-way spring), TransK the nonlinear
    one. NOTE: field names from the SAP2000 table set, unverified against the
    v25 importer like the frame tables."""
    if not model.links:
        return []
    return [
        ("LINK PROPERTY DEFINITIONS 01 - GENERAL", [
            _row(Link=name, LinkType="Gap", Mass=0, Weight=0, RotInert1=0, RotInert2=0,
                 RotInert3=0, DefLength=1, DefArea=1, PDM2I=0, PDM2J=0, PDM3I=0, PDM3J=0,
                 Color="Magenta")
            for name in model.link_props]),
        ("LINK PROPERTY DEFINITIONS 05 - GAP", [
            _row(Link=name, DOF="U1", Fixed=False, NonLinear=True, TransKE=p["k"],
                 TransCE=0, TransK=p["k"], Open=0)
            for name, p in model.link_props.items()]),
        ("CONNECTIVITY - LINK", [
            _row(Link=l, JointI=i, JointJ=j) for l, (i, j) in sorted(model.links.items())]),
        ("LINK PROPERTY ASSIGNMENTS", [
            _row(Link=l, LinkProp=model.link_prop[l], LinkFDProp="None")
            for l in sorted(model.links)]),
    ]


def geometry_tables(model: TankModel) -> list[tuple[str, list[str]]]:
    return [
        ("JOINT COORDINATES", [
            _row(Joint=j, CoordSys="GLOBAL", CoordType="Cartesian", XorR=x, Y=y, Z=z)
            for j, (x, y, z) in sorted(model.joints.items())]),
        ("CONNECTIVITY - AREA", [
            _row(Area=a, NumJoints=len(js), **{f"Joint{k + 1}": j for k, j in enumerate(js)})
            for a, js in sorted(model.areas.items())]),
        ("AREA SECTION ASSIGNMENTS", [
            _row(Area=a, Section=model.area_section[a]) for a in sorted(model.areas)]),
        ("CONNECTIVITY - FRAME", [
            _row(Frame=f, JointI=i, JointJ=j, IsCurved=False)
            for f, (i, j) in sorted(model.frames.items())]),
        ("FRAME SECTION ASSIGNMENTS", [
            _row(Frame=f, SectionType=model.frame_sections[model.frame_section[f]]["Shape"],
                 AutoSelect="N.A.", AnalSect=model.frame_section[f],
                 DesignSect=model.frame_section[f], MatProp="Default")
            for f in sorted(model.frames)]),
    ] + link_tables(model)


def group_tables(model: TankModel) -> list[tuple[str, list[str]]]:
    """SAP GROUPS. Definition flags mirror what SAP2000 v25 wrote for its own
    ALL group; assignments are one row per object (Joint / Area)."""
    groups = model.groups()
    if not groups:
        return []
    defs = []
    assigns = []
    for name, (areas, joints, frames) in groups.items():
        defs.append(_row(GroupName=name, Selection=True, SectionCut=True, Steel=True,
                         Concrete=True, Aluminum=True, ColdFormed=True, Stage=True,
                         Bridge=True, AutoSeismic=False, AutoWind=False, SelDesSteel=False,
                         SelDesAlum=False, SelDesCold=False, MassWeight=True, Color="Green"))
        assigns += [_row(GroupName=name, ObjectType="Area", ObjectLabel=a) for a in areas]
        assigns += [_row(GroupName=name, ObjectType="Joint", ObjectLabel=j) for j in joints]
        assigns += [_row(GroupName=name, ObjectType="Frame", ObjectLabel=f) for f in frames]
    return [("GROUPS 1 - DEFINITIONS", defs), ("GROUPS 2 - ASSIGNMENTS", assigns)]


def support_tables(model: TankModel) -> list[tuple[str, list[str]]]:
    """Base ring: with release_radial the radial (local 2) direction is freed so
    the base can expand and the wall goes into hoop. foundation 'fixed': ring
    pinned, baseplate interior joints held in U3. foundation 'gap': the links
    carry the vertical, so the ring keeps only U1 (tangential, and U2 unless
    released), the interior is free, and the ground joints are fully fixed."""
    s = model.spec
    gap = model.gap
    out = [
        ("JOINT RESTRAINT ASSIGNMENTS", [
            _row(Joint=j, U1=True, U2=not s.release_radial, U3=not gap,
                 R1=False, R2=False, R3=False)
            for j in model.base_joints] + [
            _row(Joint=j, U1=False, U2=False, U3=True, R1=False, R2=False, R3=False)
            for j in ([] if gap else model.baseplate_interior_joints)] + [
            _row(Joint=j, U1=True, U2=True, U3=True, R1=True, R2=True, R3=True)
            for j in model.ground_joints]),
    ]
    if s.base_local_axes:
        # AngleA rotates the joint local axes about global Z (local 3 stays
        # vertical); see TankModel.base_local_angle_deg for the sign choice.
        out.append(("JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL", [
            _row(Joint=j, AngleA=model.base_local_angle_deg(j), AngleB=0, AngleC=0)
            for j in model.base_joints]))
    return out


def load_tables(model: TankModel) -> list[tuple[str, list[str]]]:
    s = model.spec
    out = [
        ("LOAD PATTERN DEFINITIONS", [
            _row(LoadPat="DEAD", DesignType="Dead", SelfWtMult=1),
            _row(LoadPat="HYDRO", DesignType="Live", SelfWtMult=0)]),
        ("JOINT PATTERN DEFINITIONS", [_row(Pattern="HYDRO")]),
        ("JOINT PATTERN ASSIGNMENTS", [
            _row(Joint=j, Pattern="HYDRO", Value=model.hydro_value(j))
            for j in sorted(model.joints)]),
    ]
    # Wall: face "Bottom" is shell face 5 (the local -3 side, i.e. the wet
    # inside); a positive pressure there acts along +3, outward. Baseplate:
    # local 3 is up, the water sits on face "Top" and pushes along -3, down.
    # Areas with no wet face (the roof) get no pressure row.
    #
    # NOTE: the by-joint-pattern form of this table was rejected by the v25
    # importer (every record read but "no surface pressure loads are specified"
    # -- the pattern/multiplier field names it wants are not documented in the
    # .s2k we can see).  A constant Pressure per element takes one unambiguous
    # value field and avoids the guess.  The joint pattern above is still
    # written, so switching back is a one-line change once the names are known.
    if s.load_mode == "uniform":
        out.append(("AREA LOADS - SURFACE PRESSURE", [
            _row(Area=a, LoadPat="HYDRO", Face=model.area_face[a], Pressure=model.area_pressure(a))
            for a in sorted(model.areas) if a in model.area_face]))
    elif s.load_mode == "joints":
        rows = []
        for j, f in sorted(model.joint_radial_forces().items()):
            if f == 0.0:
                continue
            x, y, _ = model.joints[j]
            r = math.hypot(x, y)
            rows.append(_row(Joint=j, LoadPat="HYDRO", CoordSys="GLOBAL",
                             F1=f * x / r, F2=f * y / r, F3=0, M1=0, M2=0, M3=0))
        out.append(("JOINT LOADS - FORCE", rows))
    else:
        raise ValueError(f"unknown load_mode {s.load_mode!r}")
    cases = [
        _row(Case="DEAD", Type="LinStatic", InitialCond="Zero"),
        _row(Case="HYDRO", Type="LinStatic", InitialCond="Zero")]
    assigns = [
        _row(Case="DEAD", LoadType="Load pattern", LoadName="DEAD", LoadSF=1),
        _row(Case="HYDRO", LoadType="Load pattern", LoadName="HYDRO", LoadSF=1)]
    if model.gap:
        # Gap links are nonlinear: the linear cases above see their effective
        # stiffness (a two-way spring, kept for reference); the real answer is
        # the staged nonlinear pair, HYDRO continuing from the DEAD state.
        cases += [
            _row(Case="NL_DEAD", Type="NonStatic", InitialCond="Zero"),
            _row(Case="NL_HYDRO", Type="NonStatic", InitialCond="NL_DEAD")]
        assigns += [
            _row(Case="NL_DEAD", LoadType="Load pattern", LoadName="DEAD", LoadSF=1),
            _row(Case="NL_HYDRO", LoadType="Load pattern", LoadName="HYDRO", LoadSF=1)]
    out += [
        ("LOAD CASE DEFINITIONS", cases),
        ("CASE - STATIC 1 - LOAD ASSIGNMENTS", assigns),
    ]
    if model.gap:
        out.append(("CASE - STATIC 2 - NONLINEAR LOAD APPLICATION", [
            _row(Case=c, LoadApp="Full Load", MonitorDOF="U3", MonitorJt=model.base_joints[0])
            for c in ("NL_DEAD", "NL_HYDRO")]))
    return out


def s2k_text(model: TankModel) -> str:
    """The whole .s2k file for a model, as text."""
    w = S2KWriter()
    w.lines.append("File generated by build_tank.py (SapViewer)")
    w.lines.append("")
    w.table("PROGRAM CONTROL", program_control())
    for name, rows in (material_tables(model) + section_tables(model)
                       + geometry_tables(model) + group_tables(model) + support_tables(model)
                       + load_tables(model)):
        w.table(name, rows)
    return w.text()


def outlines_text(model: TankModel) -> str:
    """Section outlines for the viewer: one line per section,
    NAME: y,z y,z ... (section-local, centroid origin, file length units).
    SapToPluto reads <model>.outlines.txt beside the .s2k when it exists."""
    return "".join(
        name + ": " + " ".join(f"{_fmt(y)},{_fmt(z)}" for y, z in pts) + "\n"
        for name, pts in model.frame_outlines.items())


def write_s2k(model: TankModel, path: Path) -> None:
    path.write_text(s2k_text(model))
    outlines = path.with_suffix(".outlines.txt")
    if model.frame_outlines:
        outlines.write_text(outlines_text(model))
    elif outlines.exists():
        outlines.unlink()


# --- reader -----------------------------------------------------------------

_PAIR = re.compile(r'([A-Za-z0-9_#]+)=("([^"]*)"|\S*)')


def parse_s2k(text: str) -> dict[str, list[dict[str, str]]]:
    """Parse .s2k tables into {TABLE NAME: [row dicts]}, keys upper-cased.

    Quoted values may contain spaces, and a trailing underscore continues a
    row onto the next line -- both are why this scans pairs instead of
    splitting on whitespace. Mirrors the C# Sap2kParser in scripts/lib.
    """
    tables: dict[str, list[dict[str, str]]] = {}
    current: list[dict[str, str]] | None = None
    pending = ""
    for raw in text.splitlines():
        line = raw.strip()
        if not line:
            continue
        if pending:
            line, pending = pending + " " + line, ""
        if line.endswith("_"):
            pending = line[:-1].strip()
            continue
        if line.upper().startswith("END TABLE DATA"):
            break
        m = re.match(r'^TABLE:\s*"?([^"]+)"?\s*$', line, re.I)
        if m:
            current = tables.setdefault(m.group(1).strip().upper(), [])
            continue
        if current is not None and "=" in line:
            current.append({k.upper(): (q if q else v) for k, v, q in _PAIR.findall(line)})
    return tables
