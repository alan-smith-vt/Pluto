"""TankSpec: the parameters of one tank model, and the TOML config loader."""

from __future__ import annotations

import tomllib
from dataclasses import dataclass
from pathlib import Path

# --- constants (kip, ft, F) -------------------------------------------------

GAMMA_WATER = 0.0624  # kip/ft^3
EDGES = ("wall_base", "wall_top", "roof_rim", "plate_rim", "concrete_edge")   # [mesh] refine choices
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
    # [mesh] edge refinement (2026-09-10): rows / rings graded from edge_size at a
    # shell edge, growing by edge_growth per element, out to edge_length, then the
    # coarse size. Meridional only: the response at these edges is axisymmetric, so
    # the circumferential size stays n_theta (long thin quads, no transition triangles).
    # edge_length = 0 turns it off. refine names the edges that get it.
    edge_size: float = 0.25        # ft, first element at a refined edge
    edge_length: float = 0.0       # ft, extent of the graded band (0 = uniform mesh)
    edge_growth: float = 1.5       # size ratio element to element, away from the edge
    refine_edges: tuple[str, ...] = ("wall_base", "wall_top", "roof_rim", "plate_rim")
    # "concrete_edge" (2026-09-11): the baseplate rings are graded inward from the ring
    # wall's inner face (r = R - C/2) as well, and the plate over the concrete is meshed at
    # edge_size; needs a ring wall. Resolves the plate bending over the concrete corner.
    # [fluid]
    fill_fraction: float = 1.0  # 1.0 = filled to the top of the wall
    fluid_weight: float = GAMMA_WATER
    load_mode: str = "uniform"  # "uniform" | "joints"
    # [baseplate]  flat plate inside the base ring, polar mesh, water on its Top face.
    # Interior joints are held vertically (U3) until the gap-link support layer lands.
    baseplate: bool = False
    baseplate_thickness: float = 0.03125  # ft (3/8 in)
    baseplate_n_r: int = 8                # radial rings of elements (rim .. centre fan)
    baseplate_overhang: float = 0.0       # ft the plate projects beyond the shell mid-surface (2026-09-10):
                                          # one ring of quads outside the rim, no fluid on it; its joints
                                          # sit over the ring wall and take the plate_bearing chain
    # [roof]  spherical cap on the top ring, crown radius >= tank radius, dead load only
    roof: bool = False
    roof_crown_radius: float = 48.0       # ft (0.8 D for the default tank)
    roof_thickness: float = 0.03125       # ft (3/8 in)
    roof_n_r: int = 8
    roof_ring: bool = True                # eave compression ring: angle + flat bar (L, 2t horizontal plate)
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
    # [foundation] pad spring zoning (2026-09-11; Bowles, Foundation Analysis and Design 5e,
    # 10-5 and 10-12): a Winkler bed under a uniform load settles flat, a half-space does
    # not, so the pad springs are stiffened toward the rim to stand in for the coupling.
    #   "none":       every plate ring at subgrade_modulus
    #   "step":       rings at r >= R - zone_width get zone_factor x subgrade_modulus
    #                 (Bowles' two-zone / "double the edge springs", the tank-base case)
    #   "boussinesq": ring factor = w(0) / w(r) of a flexible uniformly loaded circle on an
    #                 elastic half-space, pi / (2 E(r/R)), 1 at the centre to pi/2 = 1.571
    #                 at the rim, so a uniform pressure settles the plate in the Boussinesq
    #                 dish (edge = 2/pi = 0.64 x centre) -- Bowles' zoned-ks option
    # Pad springs only: the ring wall soil link keeps [ringwall] subgrade_modulus.
    pad_zone: str = "none"
    pad_zone_width: float = 0.0           # ft, radial band from the shell inward ("step")
    pad_zone_factor: float = 2.0          # multiplier on subgrade_modulus in the band ("step")
    # [ringwall]  concrete ring under the shell (needs foundation "gap"). Load path:
    # rim joint -> contact gap link -> wall top joint (frame axis) -> frames -> soil gap
    # link -> fixed ground joint. "fixed" pins the wall top in U3 instead of the soil link.
    ringwall: bool = False
    ringwall_width: float = 1.25          # ft, radial (C)
    ringwall_depth: float = 3.75          # ft, vertical (A)
    ringwall_fc: float = 3000.0           # psi; E = 57000 sqrt(f'c)
    ringwall_unit_weight: float = 0.150   # kip/ft^3
    ringwall_support: str = "gap"         # "gap": soil gap link k = subgrade x width x arc | "fixed"
    ringwall_subgrade: float = 0.0        # kip/ft^3 under the ring wall (2026-09-10); 0 = foundation.subgrade_modulus
    ringwall_soil_links: str = "centroid" # 2026-09-11: "centroid" = one GAP_SOIL link under the axis joint (no
                                          #   rotational bearing stiffness, the ring rolls on a point); "faces" = two
                                          #   links at the inner and outer faces, k/2 each, on joints tied to the
                                          #   axis by RINGWALL_ARM frames: same vertical stiffness, rotational
                                          #   stiffness k C^2/4 per spoke (= ks C^3/12 per unit length). Needs
                                          #   joints = "elevations" and plate_bearing.
    ringwall_joints: str = "elevations"   # "elevations": rim at the wall top, wall joint at the centroid (-A/2),
                                          #   ground joint at the base (-A), links with length, cardinal 10 (2026-09-08)
                                          # "top": all three coincident at the wall top, cardinal 8 (pre-2026-09-08)
    ringwall_plate_bearing: bool = False  # 2026-09-10: plate joints over the ring wall width (r >= R - C/2) bear on the
                                          # concrete through GAP_BEARING links + rigid RINGWALL_ARM frames instead of pad springs
    ringwall_cushion_modulus: float = 0.0   # 2026-09-10: sand cushion between plate and ring wall, E (ksf); 0 = hard
                                            # concrete contact. With it, every plate joint over the ring wall (rim
                                            # included) bears through k = E / thickness x its tributary area
    ringwall_cushion_thickness: float = 0.0 # ft
    ringwall_transform: bool = False      # SAP "Transform" for the top-centre insertion: False = drawing
                                          # only (analysis on the wall-top joint line); True = rigid arms
                                          # joint -> centroid (a horizontal push at the top rolls the ring)
    # [[dents]]  wall imperfections applied to the joint coordinates
    dents: tuple[Dent, ...] = ()
    # [settlement]  ground displacement on the ground joints (plate GROUND + RINGWALL_GROUND)
    # from a surface w(x, y); needs foundation "gap". Pattern SETTLE, nonlinear case
    # NL_SETTLE continuing from NL_HYDRO (the gaps open where the ground drops away).
    settlement: str = "none"              # "none" | "trench" (parabolic) | "slope" (half-plane, linear)
    settlement_depth: float = 0.0         # ft, positive = down: trench centreline / slope at the tank edge (d = R)
    settlement_width: float = 0.0         # ft, trench width (w = 0 at +/- width/2); unused by slope
    settlement_direction_deg: float = 0.0 # trench axis / slope hinge line direction in plan, from +X
    settlement_offset: float = 0.0        # ft, axis / hinge offset from the tank centre, +90 deg side
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
        if isinstance(self.refine_edges, list):
            self.refine_edges = tuple(self.refine_edges)
        bad = set(self.refine_edges) - set(EDGES)
        if bad:
            raise ValueError(f"mesh.refine: unknown edge(s) {sorted(bad)} (choose from {', '.join(EDGES)})")
        if "concrete_edge" in self.refine_edges and self.edge_length > 0 and not (self.ringwall and self.baseplate):
            raise ValueError("mesh.refine 'concrete_edge' needs a baseplate and a ring wall")
        if self.edge_length < 0 or self.edge_size <= 0 or self.edge_growth < 1.0:
            raise ValueError("mesh.edge_length must be >= 0, edge_size > 0 and edge_growth >= 1")
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
            if self.baseplate_overhang < 0:
                raise ValueError("baseplate.overhang must be >= 0")
            if self.baseplate_overhang > 0 and self.ringwall and self.baseplate_overhang > self.ringwall_width / 2.0:
                raise ValueError("baseplate.overhang runs past the ring wall's outer face")
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
        if self.pad_zone not in ("none", "step", "boussinesq"):
            raise ValueError(f"foundation.zone {self.pad_zone!r} not recognised (none | step | boussinesq)")
        if self.pad_zone != "none" and self.foundation != "gap":
            raise ValueError("foundation.zone needs foundation.mode = 'gap' (it scales the pad springs)")
        if self.pad_zone == "step" and (self.pad_zone_width <= 0 or self.pad_zone_factor <= 0):
            raise ValueError("foundation.zone = 'step' needs a positive zone_width and zone_factor")
        if self.ringwall:
            if self.foundation != "gap":
                raise ValueError("ringwall.enabled needs foundation.mode = 'gap' (its joints are the rim's ground joints)")
            if self.ringwall_width <= 0 or self.ringwall_depth <= 0 or self.ringwall_fc <= 0:
                raise ValueError("ringwall.width, depth and fc must be positive")
            if self.ringwall_support == "springs":       # pre-2026-09-04 name
                self.ringwall_support = "gap"
            if self.ringwall_support not in ("gap", "fixed"):
                raise ValueError(f"ringwall.support {self.ringwall_support!r} not recognised (gap | fixed)")
            if self.ringwall_joints not in ("elevations", "top"):
                raise ValueError(f"ringwall.joints {self.ringwall_joints!r} not recognised (elevations | top)")
            if self.ringwall_subgrade < 0:
                raise ValueError("ringwall.subgrade_modulus must be >= 0 (0 = foundation.subgrade_modulus)")
            if self.ringwall_soil_links not in ("centroid", "faces"):
                raise ValueError(f"ringwall.soil_links {self.ringwall_soil_links!r} not recognised (centroid | faces)")
            if self.ringwall_soil_links == "faces" and not (self.ringwall_joints == "elevations" and self.ringwall_plate_bearing
                                                            and self.ringwall_support == "gap"):
                raise ValueError("ringwall.soil_links = 'faces' needs joints = 'elevations', plate_bearing = true and support = 'gap'")
            if self.ringwall_cushion_modulus < 0 or self.ringwall_cushion_thickness < 0:
                raise ValueError("ringwall.cushion_modulus and cushion_thickness must be >= 0")
            if self.ringwall_cushion_modulus > 0:
                if self.ringwall_cushion_thickness <= 0:
                    raise ValueError("ringwall.cushion_modulus needs a positive cushion_thickness")
                if not self.ringwall_plate_bearing:
                    raise ValueError("ringwall.cushion_modulus needs ringwall.plate_bearing = true")
        if self.settlement not in ("none", "trench", "slope"):
            raise ValueError(f"settlement.profile {self.settlement!r} not recognised (none | trench | slope)")
        if self.settlement != "none":
            if self.foundation != "gap":
                raise ValueError("settlement needs foundation.mode = 'gap' (it moves the ground joints)")
            if self.settlement_depth <= 0:
                raise ValueError("settlement.depth must be positive")
            if self.settlement == "trench" and self.settlement_width <= 0:
                raise ValueError("settlement.width must be positive for a trench")
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
    "mesh": {"n_theta": "n_theta", "n_z": "n_z", "edge_size": "edge_size",
             "edge_length": "edge_length", "edge_growth": "edge_growth", "refine": "refine_edges"},
    "fluid": {
        "fill_fraction": "fill_fraction",
        "unit_weight": "fluid_weight",
        "load_mode": "load_mode",
    },
    "baseplate": {
        "enabled": "baseplate",
        "thickness": "baseplate_thickness",
        "n_r": "baseplate_n_r",
        "overhang": "baseplate_overhang",
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
    "foundation": {"mode": "foundation", "subgrade_modulus": "subgrade_modulus",
                   "zone": "pad_zone", "zone_width": "pad_zone_width", "zone_factor": "pad_zone_factor"},
    "ringwall": {
        "enabled": "ringwall",
        "width": "ringwall_width",
        "depth": "ringwall_depth",
        "fc": "ringwall_fc",
        "unit_weight": "ringwall_unit_weight",
        "support": "ringwall_support",
        "subgrade_modulus": "ringwall_subgrade",
        "soil_links": "ringwall_soil_links",
        "joints": "ringwall_joints",
        "transform": "ringwall_transform",
        "plate_bearing": "ringwall_plate_bearing",
        "cushion_modulus": "ringwall_cushion_modulus",
        "cushion_thickness": "ringwall_cushion_thickness",
    },
    "dents": {"angle_deg": "angle_deg", "elevation": "elevation", "depth": "depth",
              "width": "width", "height": "height"},
    "settlement": {
        "profile": "settlement",
        "depth": "settlement_depth",
        "width": "settlement_width",
        "direction_deg": "settlement_direction_deg",
        "offset": "settlement_offset",
    },
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
