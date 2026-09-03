"""TankSpec: the parameters of one tank model, and the TOML config loader."""

from __future__ import annotations

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


@dataclass(frozen=True)
class Course:
    """One plate course of the wall: a height range at one thickness.

    divisions: mesh rows in this course; None = share mesh.n_z out by height.
    """
    height: float
    thickness: float
    divisions: int | None = None


@dataclass(frozen=True)
class Dent:
    """A local inward imperfection of the wall: the joints move, the mesh does not.
    Radial offset = depth * cos^2(pi rho / 2) inside the elliptical footprint
    rho = sqrt((arc / (width/2))^2 + (dz / (height/2))^2) <= 1, zero outside."""
    angle_deg: float
    elevation: float
    depth: float       # ft, positive = inward
    width: float       # ft, footprint along the circumference (arc length)
    height: float      # ft, footprint up the wall


@dataclass
class TankSpec:
    # [geometry]
    radius: float = 30.0  # ft, to the shell mid-surface
    height: float = 40.0  # ft, wall height (= sum of the courses when given)
    thickness: float = 0.0208333  # ft (0.25 in); the single course when [[courses]] is absent
    # [[courses]]  bottom first; overrides geometry.thickness (and fixes height)
    courses: tuple[Course, ...] | None = None
    # [mesh]
    n_theta: int = 36  # radial (circumferential) divisions
    n_z: int = 20  # vertical divisions, in total; shared out to the courses by height
    # [fluid]
    fill_fraction: float = 1.0  # 1.0 = filled to the top of the wall
    fluid_weight: float = GAMMA_WATER
    load_mode: str = "uniform"  # "uniform" | "joints"
    # [baseplate]  flat plate inside the base ring, polar mesh, water on its Top face.
    # Interior joints are held vertically (U3) until the gap-link support layer lands.
    baseplate: bool = False
    baseplate_thickness: float = 0.03125  # ft (3/8 in)
    baseplate_n_r: int = 8                # radial rings of elements (rim .. centre fan)
    # [roof]  spherical cap on the top ring, crown radius >= tank radius, dead load only
    roof: bool = False
    roof_crown_radius: float = 48.0       # ft (0.8 D for the default tank)
    roof_thickness: float = 0.03125       # ft (3/8 in)
    roof_n_r: int = 8
    roof_ring: bool = True                # eave compression ring: (2) equal-leg angles back to back
    roof_ring_leg: float = 0.25           # ft (3 in)
    roof_ring_thickness: float = 0.03125  # ft (3/8 in)
    # [foundation]  what holds the baseplate down/up.
    #   "fixed": interior joints held in U3, base ring pinned (linear stand-in)
    #   "gap":   a coincident fixed GROUND joint under every baseplate joint and a
    #            zero-length compression-only Gap link between them (vertical
    #            stiffness = subgrade modulus x tributary area); the tank side
    #            keeps only the rim's tangential restraint. Adds the nonlinear
    #            static cases NL_DEAD -> NL_HYDRO.
    foundation: str = "fixed"
    subgrade_modulus: float = 170.0       # kip/ft^3 (compacted sand, guess)
    # [ringwall]  concrete ring under the shell (needs foundation "gap": its joints are
    # the rim's ground joints, so the gap links act shell <-> ring wall). Frame axis at
    # the top of the wall; supports on the same joints.
    ringwall: bool = False
    ringwall_width: float = 1.25          # ft, radial (C)
    ringwall_depth: float = 3.75          # ft, vertical (A)
    ringwall_fc: float = 3000.0           # psi; E = 57000 sqrt(f'c)
    ringwall_unit_weight: float = 0.150   # kip/ft^3
    ringwall_support: str = "springs"     # "springs": U3 = subgrade x width x arc | "fixed"
    # [[dents]]  wall imperfections applied to the joint coordinates
    dents: tuple[Dent, ...] = ()
    # [supports]
    base_local_axes: bool = False
    release_radial: bool = False
    # [groups]  SAP GROUPS written into the .s2k; they come back out of the
    # results export and become sidecar groups in SapToPluto (Color by groups).
    groups: bool = True          # WALL (all shells), BASE_RING / TOP_RING (joints)
    course_groups: bool = True   # COURSE_01.. one per horizontal course of shells
    # [material]
    mat_name: str = STEEL["name"]
    mat_e: float = STEEL["E"]
    mat_poisson: float = STEEL["poisson"]
    mat_unit_weight: float = STEEL["unit_weight"]
    mat_alpha: float = STEEL["alpha"]

    @property
    def fill_height(self) -> float:
        return self.height * self.fill_fraction

    @property
    def plate_courses(self) -> tuple[Course, ...]:
        """The courses, bottom first; one course of geometry.thickness by default."""
        if self.courses:
            return self.courses
        return (Course(self.height, self.thickness),)

    def course_divisions(self) -> list[int]:
        """Mesh rows per course: explicit divisions, else mesh.n_z shared out by
        height (at least one row each). Every course boundary is a mesh ring."""
        cs = self.plate_courses
        return [c.divisions if c.divisions is not None
                else max(1, round(self.n_z * c.height / self.height)) for c in cs]

    def validate(self) -> None:
        if self.n_theta < 3:
            raise ValueError("mesh.n_theta must be at least 3")
        if self.n_z < 1:
            raise ValueError("mesh.n_z must be at least 1")
        if self.radius <= 0 or self.height <= 0 or self.thickness <= 0:
            raise ValueError("geometry.radius/height/thickness must be positive")
        if self.courses is not None:
            if not self.courses:
                raise ValueError("[[courses]] given but empty")
            for k, c in enumerate(self.courses, 1):
                if c.height <= 0 or c.thickness <= 0:
                    raise ValueError(f"courses[{k}]: height and thickness must be positive")
                if c.divisions is not None and c.divisions < 1:
                    raise ValueError(f"courses[{k}]: divisions must be at least 1")
            total = sum(c.height for c in self.courses)
            if abs(total - self.height) > 1e-9 * max(1.0, self.height):
                raise ValueError(
                    f"courses sum to {total:g} ft but geometry.height is {self.height:g} ft"
                )
        if self.baseplate:
            if self.baseplate_thickness <= 0 or self.baseplate_n_r < 1:
                raise ValueError("baseplate.thickness must be positive and baseplate.n_r at least 1")
        if self.roof:
            if self.roof_thickness <= 0 or self.roof_n_r < 1:
                raise ValueError("roof.thickness must be positive and roof.n_r at least 1")
            if self.roof_crown_radius < self.radius:
                raise ValueError(
                    f"roof.crown_radius {self.roof_crown_radius:g} is less than the tank radius "
                    f"{self.radius:g}; a spherical cap needs crown_radius >= radius"
                )
            if self.roof_ring and (self.roof_ring_leg <= 0 or self.roof_ring_thickness <= 0):
                raise ValueError("roof.ring_leg and roof.ring_thickness must be positive")
        if self.foundation not in ("fixed", "gap"):
            raise ValueError(f"foundation.mode {self.foundation!r} not recognised (fixed | gap)")
        if self.foundation == "gap":
            if not self.baseplate:
                raise ValueError("foundation.mode = 'gap' needs baseplate.enabled = true")
            if self.subgrade_modulus <= 0:
                raise ValueError("foundation.subgrade_modulus must be positive")
        if self.ringwall:
            if self.foundation != "gap":
                raise ValueError("ringwall.enabled needs foundation.mode = 'gap' (its joints are the rim's ground joints)")
            if self.ringwall_width <= 0 or self.ringwall_depth <= 0 or self.ringwall_fc <= 0:
                raise ValueError("ringwall.width, depth and fc must be positive")
            if self.ringwall_support not in ("springs", "fixed"):
                raise ValueError(f"ringwall.support {self.ringwall_support!r} not recognised (springs | fixed)")
        for k, d in enumerate(self.dents, 1):
            if d.width <= 0 or d.height <= 0:
                raise ValueError(f"dents[{k}]: width and height must be positive")
            if d.depth == 0:
                raise ValueError(f"dents[{k}]: depth must be non-zero")
            if not 0.0 <= d.elevation <= self.height:
                raise ValueError(f"dents[{k}]: elevation {d.elevation:g} is off the wall (0 .. {self.height:g})")
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
    "courses": {"height": "height", "thickness": "thickness", "divisions": "divisions"},
    "mesh": {"n_theta": "n_theta", "n_z": "n_z"},
    "fluid": {
        "fill_fraction": "fill_fraction",
        "unit_weight": "fluid_weight",
        "load_mode": "load_mode",
    },
    "baseplate": {
        "enabled": "baseplate",
        "thickness": "baseplate_thickness",
        "n_r": "baseplate_n_r",
    },
    "roof": {
        "enabled": "roof",
        "crown_radius": "roof_crown_radius",
        "thickness": "roof_thickness",
        "n_r": "roof_n_r",
        "ring": "roof_ring",
        "ring_leg": "roof_ring_leg",
        "ring_thickness": "roof_ring_thickness",
    },
    "foundation": {"mode": "foundation", "subgrade_modulus": "subgrade_modulus"},
    "ringwall": {
        "enabled": "ringwall",
        "width": "ringwall_width",
        "depth": "ringwall_depth",
        "fc": "ringwall_fc",
        "unit_weight": "ringwall_unit_weight",
        "support": "ringwall_support",
    },
    "dents": {"angle_deg": "angle_deg", "elevation": "elevation", "depth": "depth",
              "width": "width", "height": "height"},
    "supports": {
        "base_local_axes": "base_local_axes",
        "release_radial": "release_radial",
    },
    "groups": {"enabled": "groups", "courses": "course_groups"},
    "material": {
        "name": "mat_name",
        "e": "mat_e",
        "poisson": "mat_poisson",
        "unit_weight": "mat_unit_weight",
        "alpha": "mat_alpha",
    },
}


def spec_from_dict(raw: dict) -> tuple[TankSpec, str, str]:
    """Build a validated TankSpec from parsed TOML. Returns (spec, out_dir, out_name)."""
    out_dir_raw = "../models"
    out_name = None
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
        if section == "courses":
            fields["courses"] = _courses_from_list(values)
            continue
        if section == "dents":
            fields["dents"] = _dents_from_list(values)
            continue
        if not isinstance(values, dict):
            raise ValueError(f"[{section}] must be a table of key = value entries")
        for key, value in values.items():
            if key not in CONFIG_MAP[section]:
                raise ValueError(f"unknown key {key!r} in [{section}]")
            fields[CONFIG_MAP[section][key]] = value
    if fields.get("courses"):
        if "thickness" in fields:
            raise ValueError("give either geometry.thickness or [[courses]], not both")
        total = sum(c.height for c in fields["courses"])
        fields.setdefault("height", total)   # geometry.height is optional with courses
        fields["thickness"] = fields["courses"][0].thickness
    spec = TankSpec(**fields)
    spec.validate()
    return spec, out_dir_raw, out_name


def _dents_from_list(values) -> tuple[Dent, ...]:
    """[[dents]] rows -> Dent tuple, rejecting unknown / missing keys."""
    if not isinstance(values, list):
        raise ValueError("[[dents]] must be an array of tables")
    keys = list(CONFIG_MAP["dents"])
    out = []
    for k, row in enumerate(values, 1):
        unknown = set(row) - set(keys)
        if unknown:
            raise ValueError(f"unknown key(s) in dents[{k}]: {sorted(unknown)}")
        missing = [key for key in keys if key not in row]
        if missing:
            raise ValueError(f"dents[{k}] needs {', '.join(missing)}")
        out.append(Dent(*(float(row[key]) for key in keys)))
    return tuple(out)


def _courses_from_list(values) -> tuple[Course, ...]:
    """[[courses]] rows (bottom first) -> Course tuple, rejecting unknown keys."""
    if not isinstance(values, list):
        raise ValueError("[[courses]] must be an array of tables (bottom course first)")
    out = []
    for k, row in enumerate(values, 1):
        unknown = set(row) - set(CONFIG_MAP["courses"])
        if unknown:
            raise ValueError(f"unknown key(s) in courses[{k}]: {sorted(unknown)}")
        if "height" not in row or "thickness" not in row:
            raise ValueError(f"courses[{k}] needs height and thickness")
        out.append(Course(float(row["height"]), float(row["thickness"]),
                          None if row.get("divisions") is None else int(row["divisions"])))
    return tuple(out)


def load_config(path: Path) -> tuple[TankSpec, Path]:
    """Read a .toml config into a TankSpec plus its output .s2k path.

    Each model gets its own folder (SAP scatters .sdb/.msh/.Y_*/.LOG working
    files beside whatever it opens): <dir>/<name>/<name>.s2k, with relative
    dirs resolved against the config's own folder.
    """
    with path.open("rb") as fh:
        raw = tomllib.load(fh)
    spec, out_dir_raw, out_name = spec_from_dict(raw)
    out_name = out_name or path.stem
    out = (path.parent / out_dir_raw / out_name / f"{out_name}.s2k").resolve()
    return spec, out
