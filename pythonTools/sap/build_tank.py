#!/usr/bin/env python3
"""Build a cylindrical steel tank shell model as a SAP2000 .s2k text file.

Scope of this first step
------------------------
  * cylindrical wall meshed by radial (circumferential) and vertical divisions
  * thin-shell elements, one steel material, one wall thickness
  * base ring of joints fully pinned (U1/U2/U3 fixed, rotations free)
  * hydrostatic pressure from water filled 100% to the top of the wall,
    applied as a joint-pattern-driven surface pressure on the inside face

Next step (deliberately not done here, but the hooks exist)
-----------------------------------------------------------
The base joints get their local axes rotated so local X is tangential to the
tank and local Y is radial (perpendicular to the wall), then local Y is
released so the base can breathe radially and the wall picks up hoop stress.
Pass ``base_local_axes=True`` to emit the rotated local axes now; pass
``release_radial=True`` to also drop the radial restraint. Both default off so
the base file stays a plain pinned-base model until we get there.

Units are kip, ft, F throughout.

Geometry / numbering conventions (the viewer relies on these)
------------------------------------------------------------
  * origin at the centre of the tank base, +Z up
  * joint id = 1 + ring_index * n_theta + theta_index, ring 0 at the base
  * area joints are ordered (theta_i, z_k), (theta_i+1, z_k), (theta_i+1, z_k+1),
    (theta_i, z_k+1) so the shell local 3 axis points radially OUTWARD.
    Water is therefore on the local -3 side = shell face 5 = "Bottom", and a
    positive pressure on that face pushes outward, which is what we want.
"""

from __future__ import annotations

import argparse
import math
import tomllib
from dataclasses import dataclass
from pathlib import Path

# --- constants (kip, ft, F) -------------------------------------------------

GAMMA_WATER = 0.0624  # kip/ft^3
G_ACCEL = 32.174  # ft/s^2
STEEL = dict(
    name="A36",
    E=4176000.0,  # kip/ft^2  (29000 ksi)
    poisson=0.3,
    unit_weight=0.490,  # kip/ft^3
    alpha=6.5e-06,
    fy=5184.0,  # kip/ft^2 (36 ksi)
)


@dataclass
class TankSpec:
    # [geometry]
    radius: float = 30.0  # ft, to the shell mid-surface
    height: float = 40.0  # ft, wall height
    thickness: float = 0.0208333  # ft (0.25 in)
    # [mesh]
    n_theta: int = 36  # radial (circumferential) divisions
    n_z: int = 20  # vertical divisions
    # [fluid]
    fill_fraction: float = 1.0  # 1.0 = filled to the top of the wall
    fluid_weight: float = GAMMA_WATER
    load_mode: str = "uniform"  # "uniform" | "joints"
    # [supports]
    base_local_axes: bool = False
    release_radial: bool = False
    # [material]
    mat_name: str = STEEL["name"]
    mat_e: float = STEEL["E"]
    mat_poisson: float = STEEL["poisson"]
    mat_unit_weight: float = STEEL["unit_weight"]
    mat_alpha: float = STEEL["alpha"]

    @property
    def fill_height(self) -> float:
        return self.height * self.fill_fraction

    def validate(self) -> None:
        if self.n_theta < 3:
            raise ValueError("mesh.n_theta must be at least 3")
        if self.n_z < 1:
            raise ValueError("mesh.n_z must be at least 1")
        if self.radius <= 0 or self.height <= 0 or self.thickness <= 0:
            raise ValueError("geometry.radius/height/thickness must be positive")
        if not 0.0 <= self.fill_fraction <= 1.0:
            raise ValueError("fluid.fill_fraction must be between 0 and 1")
        if self.load_mode not in ("uniform", "joints"):
            raise ValueError(f"fluid.load_mode {self.load_mode!r} not recognised")
        if self.release_radial and not self.base_local_axes:
            raise ValueError(
                "supports.release_radial needs supports.base_local_axes = true, "
                "otherwise the released direction is global Y, not radial"
            )


# --- config ------------------------------------------------------------------

# TOML section -> key -> TankSpec field.  Anything not listed is rejected, so a
# typo in a config is an error instead of a silently ignored setting.
CONFIG_MAP = {
    "geometry": {"radius": "radius", "height": "height", "thickness": "thickness"},
    "mesh": {"n_theta": "n_theta", "n_z": "n_z"},
    "fluid": {
        "fill_fraction": "fill_fraction",
        "unit_weight": "fluid_weight",
        "load_mode": "load_mode",
    },
    "supports": {
        "base_local_axes": "base_local_axes",
        "release_radial": "release_radial",
    },
    "material": {
        "name": "mat_name",
        "e": "mat_e",
        "poisson": "mat_poisson",
        "unit_weight": "mat_unit_weight",
        "alpha": "mat_alpha",
    },
}


def load_config(path: Path) -> tuple[TankSpec, Path]:
    """Read a .toml config into a TankSpec plus its optional output path."""
    with path.open("rb") as fh:
        raw = tomllib.load(fh)

    out_dir_raw = "../models"
    out_name = path.stem
    fields: dict[str, object] = {}
    for section, values in raw.items():
        if section == "output":
            unknown = set(values) - {"dir", "name"}
            if unknown:
                raise ValueError(f"unknown key(s) in [output]: {sorted(unknown)}")
            out_dir_raw = values.get("dir", out_dir_raw)
            out_name = values.get("name", out_name)
            continue
        if section not in CONFIG_MAP:
            raise ValueError(f"unknown config section [{section}]")
        if not isinstance(values, dict):
            raise ValueError(f"[{section}] must be a table of key = value entries")
        for key, value in values.items():
            if key not in CONFIG_MAP[section]:
                raise ValueError(f"unknown key {key!r} in [{section}]")
            fields[CONFIG_MAP[section][key]] = value

    # Each model gets its own folder: SAP scatters a dozen working files
    # (.sdb, .msh, .Y_*, .LOG, ...) beside whatever it opens.
    # Relative paths resolve against the config's own folder.
    out = (path.parent / out_dir_raw / out_name / f"{out_name}.s2k").resolve()

    spec = TankSpec(**fields)
    spec.validate()
    return spec, out


# --- model assembly ---------------------------------------------------------


class TankModel:
    """Joints, areas and hydrostatic pattern values for one tank."""

    def __init__(self, spec: TankSpec):
        spec.validate()
        self.spec = spec
        self.joints: dict[int, tuple[float, float, float]] = {}
        self.thetas: dict[int, float] = {}  # joint id -> circumferential angle
        self.areas: dict[int, tuple[int, int, int, int]] = {}
        self._build()

    def joint_id(self, ring: int, i: int) -> int:
        return 1 + ring * self.spec.n_theta + (i % self.spec.n_theta)

    def _build(self) -> None:
        s = self.spec
        dz = s.height / s.n_z
        dtheta = 2.0 * math.pi / s.n_theta

        for ring in range(s.n_z + 1):
            z = ring * dz
            for i in range(s.n_theta):
                theta = i * dtheta
                jid = self.joint_id(ring, i)
                self.joints[jid] = (
                    s.radius * math.cos(theta),
                    s.radius * math.sin(theta),
                    z,
                )
                self.thetas[jid] = theta

        aid = 0
        for ring in range(s.n_z):
            for i in range(s.n_theta):
                aid += 1
                self.areas[aid] = (
                    self.joint_id(ring, i),
                    self.joint_id(ring, i + 1),
                    self.joint_id(ring + 1, i + 1),
                    self.joint_id(ring + 1, i),
                )

    @property
    def base_joints(self) -> list[int]:
        return [self.joint_id(0, i) for i in range(self.spec.n_theta)]

    def _pressure_at(self, z: float) -> float:
        head = self.spec.fill_height - z
        return self.spec.fluid_weight * head if head > 0.0 else 0.0

    def hydro_value(self, jid: int) -> float:
        """Fluid pressure at a joint, kip/ft^2. Zero above the surface."""
        return self._pressure_at(self.joints[jid][2])

    def area_pressure(self, aid: int) -> float:
        """Fluid pressure at the centroid of one shell, kip/ft^2.

        Constant over the element, so the total force is exact for the elements
        fully below the surface; only the element straddling a partial-fill
        surface is approximate, and at full fill there is no such element.
        """
        zs = [self.joints[j][2] for j in self.areas[aid]]
        return self._pressure_at(sum(zs) / 4.0)

    def joint_radial_forces(self) -> dict[int, float]:
        """Outward radial force per joint, kip, from tributary area x pressure.

        Fallback to the surface-pressure tables: statically equivalent at the
        joints, so global response matches, but the shells no longer carry a
        true distributed load.
        """
        s = self.spec
        dz = s.height / s.n_z
        arc = 2.0 * math.pi * s.radius / s.n_theta
        forces: dict[int, float] = {}
        for ring in range(s.n_z + 1):
            # end rings get half the vertical tributary height
            trib_h = dz if 0 < ring < s.n_z else dz / 2.0
            for i in range(s.n_theta):
                jid = self.joint_id(ring, i)
                forces[jid] = self.hydro_value(jid) * arc * trib_h
        return forces


# --- .s2k writer ------------------------------------------------------------


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


def write_s2k(model: TankModel, path: Path) -> None:
    s = model.spec
    w = S2KWriter()

    w.lines.append("File generated by build_tank.py (SapViewer)")
    w.lines.append("")

    w.table(
        "PROGRAM CONTROL",
        [
            _row(
                ProgramName="SAP2000",
                Version="24.0.0",
                CurrUnits="Kip, ft, F",
                MergeTol=0.001,
            )
        ],
    )

    # -- materials
    w.table(
        "MATERIAL PROPERTIES 01 - GENERAL",
        [
            _row(
                Material=s.mat_name,
                Type="Steel",
                SymType="Isotropic",
                TempDepend=False,
                Color="Cyan",
            )
        ],
    )
    g = s.mat_e / (2.0 * (1.0 + s.mat_poisson))
    w.table(
        "MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES",
        [
            _row(
                Material=s.mat_name,
                UnitWeight=s.mat_unit_weight,
                UnitMass=s.mat_unit_weight / G_ACCEL,
                E1=s.mat_e,
                G12=g,
                U12=s.mat_poisson,
                A1=s.mat_alpha,
            )
        ],
    )

    # -- section
    w.table(
        "AREA SECTION PROPERTIES",
        [
            _row(
                Section="TANKWALL",
                Material=s.mat_name,
                MatAngle=0,
                AreaType="Shell",
                Type="Shell-Thin",
                Thickness=s.thickness,
                BendThick=s.thickness,
                Color="Gray8Dark",
            )
        ],
    )

    # -- geometry
    w.table(
        "JOINT COORDINATES",
        [
            _row(Joint=j, CoordSys="GLOBAL", CoordType="Cartesian",
                 XorR=x, Y=y, Z=z)
            for j, (x, y, z) in sorted(model.joints.items())
        ],
    )
    w.table(
        "CONNECTIVITY - AREA",
        [
            _row(Area=a, NumJoints=4, Joint1=j1, Joint2=j2, Joint3=j3, Joint4=j4)
            for a, (j1, j2, j3, j4) in sorted(model.areas.items())
        ],
    )
    w.table(
        "AREA SECTION ASSIGNMENTS",
        [_row(Area=a, Section="TANKWALL") for a in sorted(model.areas)],
    )

    # -- supports.  Today: pinned.  With release_radial, the radial (local 2)
    #    direction is freed so the base can expand and the wall goes into hoop.
    restraints = []
    for j in model.base_joints:
        restraints.append(
            _row(
                Joint=j,
                U1=True,
                U2=not s.release_radial,
                U3=True,
                R1=False,
                R2=False,
                R3=False,
            )
        )
    w.table("JOINT RESTRAINT ASSIGNMENTS", restraints)

    if s.base_local_axes:
        # AngleA rotates the joint local axes about global Z (local 3 stays
        # vertical).  theta-90 puts local X tangential (along -theta) and local
        # Y radial pointing OUTWARD, so a positive local-2 displacement is the
        # base expanding -- which is the sign we want to read in the results.
        # theta+90 also gives a tangential X, but leaves local Y pointing
        # inward, which inverts every radial number in the output tables.
        w.table(
            "JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL",
            [
                _row(
                    Joint=j,
                    AngleA=math.degrees(model.thetas[j]) - 90.0,
                    AngleB=0,
                    AngleC=0,
                )
                for j in model.base_joints
            ],
        )

    # -- loads
    w.table(
        "LOAD PATTERN DEFINITIONS",
        [
            _row(LoadPat="DEAD", DesignType="Dead", SelfWtMult=1),
            _row(LoadPat="HYDRO", DesignType="Live", SelfWtMult=0),
        ],
    )
    w.table("JOINT PATTERN DEFINITIONS", [_row(Pattern="HYDRO")])
    w.table(
        "JOINT PATTERN ASSIGNMENTS",
        [
            _row(Joint=j, Pattern="HYDRO", Value=model.hydro_value(j))
            for j in sorted(model.joints)
        ],
    )
    # Face "Bottom" is shell face 5 (the local -3 side, i.e. the wet inside);
    # a positive pressure there acts along +3, outward.  See module docstring.
    #
    # NOTE: the by-joint-pattern form of this table was rejected by the v25
    # importer (every record read but "no surface pressure loads are specified"
    # -- the pattern/multiplier field names it wants are not documented in the
    # .s2k we can see).  A constant Pressure per element takes one unambiguous
    # value field and avoids the guess.  The joint pattern above is still
    # written, so switching back is a one-line change once the names are known.
    if s.load_mode == "uniform":
        w.table(
            "AREA LOADS - SURFACE PRESSURE",
            [
                _row(Area=a, LoadPat="HYDRO", Face="Bottom",
                     Pressure=model.area_pressure(a))
                for a in sorted(model.areas)
            ],
        )
    elif s.load_mode == "joints":
        rows = []
        for j, f in sorted(model.joint_radial_forces().items()):
            if f == 0.0:
                continue
            x, y, _ = model.joints[j]
            r = math.hypot(x, y)
            rows.append(
                _row(Joint=j, LoadPat="HYDRO", CoordSys="GLOBAL",
                     F1=f * x / r, F2=f * y / r, F3=0, M1=0, M2=0, M3=0)
            )
        w.table("JOINT LOADS - FORCE", rows)
    else:
        raise ValueError(f"unknown load_mode {s.load_mode!r}")

    w.table(
        "LOAD CASE DEFINITIONS",
        [
            _row(Case="DEAD", Type="LinStatic", InitialCond="Zero"),
            _row(Case="HYDRO", Type="LinStatic", InitialCond="Zero"),
        ],
    )
    w.table(
        "CASE - STATIC 1 - LOAD ASSIGNMENTS",
        [
            _row(Case="DEAD", LoadType="Load pattern", LoadName="DEAD", LoadSF=1),
            _row(Case="HYDRO", LoadType="Load pattern", LoadName="HYDRO", LoadSF=1),
        ],
    )

    path.write_text(w.text())


# --- cli --------------------------------------------------------------------


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("config", type=Path, help="path to a .toml tank config")
    p.add_argument("-o", "--out", type=Path,
                   help="output .s2k (overrides [output].file in the config)")
    a = p.parse_args(argv)

    try:
        spec, cfg_out = load_config(a.config)
    except (ValueError, tomllib.TOMLDecodeError) as exc:
        p.error(f"{a.config}: {exc}")

    out = a.out or cfg_out
    model = TankModel(spec)
    out.parent.mkdir(parents=True, exist_ok=True)
    write_s2k(model, out)

    base = "released radially" if spec.release_radial else "pinned"
    print(
        f"{out}: {len(model.joints)} joints, {len(model.areas)} shells, "
        f"base {base}, base pressure "
        f"{spec.fluid_weight * spec.fill_height:.4f} kip/ft^2"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
