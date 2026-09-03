"""SAP2000 .s2k text tables: writer for TankModel, and a small reader.

The output of write_s2k for configs/tank_hoop.toml is byte-identical to the
file SAP2000 v25 imported and ran (tests/golden/tank_hoop.s2k) -- keep it so.
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
    return [
        ("AREA SECTION PROPERTIES", [
            _row(Section="TANKWALL", Material=s.mat_name, MatAngle=0,
                 AreaType="Shell", Type="Shell-Thin", Thickness=s.thickness,
                 BendThick=s.thickness, Color="Gray8Dark")]),
    ]


def geometry_tables(model: TankModel) -> list[tuple[str, list[str]]]:
    return [
        ("JOINT COORDINATES", [
            _row(Joint=j, CoordSys="GLOBAL", CoordType="Cartesian", XorR=x, Y=y, Z=z)
            for j, (x, y, z) in sorted(model.joints.items())]),
        ("CONNECTIVITY - AREA", [
            _row(Area=a, NumJoints=4, Joint1=j1, Joint2=j2, Joint3=j3, Joint4=j4)
            for a, (j1, j2, j3, j4) in sorted(model.areas.items())]),
        ("AREA SECTION ASSIGNMENTS", [
            _row(Area=a, Section="TANKWALL") for a in sorted(model.areas)]),
    ]


def support_tables(model: TankModel) -> list[tuple[str, list[str]]]:
    """Pinned base; with release_radial the radial (local 2) direction is freed
    so the base can expand and the wall goes into hoop."""
    s = model.spec
    out = [
        ("JOINT RESTRAINT ASSIGNMENTS", [
            _row(Joint=j, U1=True, U2=not s.release_radial, U3=True,
                 R1=False, R2=False, R3=False)
            for j in model.base_joints]),
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
    # Face "Bottom" is shell face 5 (the local -3 side, i.e. the wet inside);
    # a positive pressure there acts along +3, outward.
    #
    # NOTE: the by-joint-pattern form of this table was rejected by the v25
    # importer (every record read but "no surface pressure loads are specified"
    # -- the pattern/multiplier field names it wants are not documented in the
    # .s2k we can see).  A constant Pressure per element takes one unambiguous
    # value field and avoids the guess.  The joint pattern above is still
    # written, so switching back is a one-line change once the names are known.
    if s.load_mode == "uniform":
        out.append(("AREA LOADS - SURFACE PRESSURE", [
            _row(Area=a, LoadPat="HYDRO", Face="Bottom", Pressure=model.area_pressure(a))
            for a in sorted(model.areas)]))
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
    out += [
        ("LOAD CASE DEFINITIONS", [
            _row(Case="DEAD", Type="LinStatic", InitialCond="Zero"),
            _row(Case="HYDRO", Type="LinStatic", InitialCond="Zero")]),
        ("CASE - STATIC 1 - LOAD ASSIGNMENTS", [
            _row(Case="DEAD", LoadType="Load pattern", LoadName="DEAD", LoadSF=1),
            _row(Case="HYDRO", LoadType="Load pattern", LoadName="HYDRO", LoadSF=1)]),
    ]
    return out


def s2k_text(model: TankModel) -> str:
    """The whole .s2k file for a model, as text."""
    w = S2KWriter()
    w.lines.append("File generated by build_tank.py (SapViewer)")
    w.lines.append("")
    w.table("PROGRAM CONTROL", program_control())
    for name, rows in (material_tables(model) + section_tables(model)
                       + geometry_tables(model) + support_tables(model)
                       + load_tables(model)):
        w.table(name, rows)
    return w.text()


def write_s2k(model: TankModel, path: Path) -> None:
    path.write_text(s2k_text(model))


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
