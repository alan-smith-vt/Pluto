"""TankModel: joints, areas and hydrostatic values for one tank (no I/O)."""

from __future__ import annotations

import math

from .spec import TankSpec


class IdAllocator:
    """Hands out contiguous id blocks per SAP object type (joints, areas,
    frames, links), each numbered from 1. Every part of the model (wall,
    baseplate, dome, ring wall ...) claims its block here, so ids never
    collide and a part's range is known: block(kind, name) -> range."""

    def __init__(self):
        self._next: dict[str, int] = {}
        self.blocks: dict[tuple[str, str], range] = {}

    def claim(self, kind: str, name: str, count: int) -> range:
        start = self._next.get(kind, 1)
        block = range(start, start + count)
        self._next[kind] = start + count
        self.blocks[(kind, name)] = block
        return block

    def block(self, kind: str, name: str) -> range:
        return self.blocks[(kind, name)]


class TankModel:
    """Joints, areas and hydrostatic pattern values for one tank.

    joints: id -> (x, y, z); thetas: id -> circumferential angle (rad);
    areas: id -> (j1, j2, j3, j4) counter-clockwise seen from outside, so the
    SAP local 3 axis points radially outward; area_section: id -> section name;
    sections: section name -> thickness (one per distinct course thickness).
    """

    def __init__(self, spec: TankSpec):
        spec.validate()
        self.spec = spec
        self.ids = IdAllocator()
        self.joints: dict[int, tuple[float, float, float]] = {}
        self.thetas: dict[int, float] = {}
        self.areas: dict[int, tuple[int, int, int, int]] = {}
        self.area_section: dict[int, str] = {}
        self.sections: dict[str, float] = {}
        self._build_levels()
        self._build()

    # --- numbering ----------------------------------------------------------

    def joint_id(self, ring: int, i: int) -> int:
        """Wall joint: ring 0 at the base, i wraps circumferentially."""
        return self._wall_joints.start + ring * self.spec.n_theta + (i % self.spec.n_theta)

    @property
    def n_rows(self) -> int:
        """Mesh rows (rows of shells) up the wall = sum of course divisions."""
        return len(self.z_levels) - 1

    def _build_levels(self) -> None:
        """z of every mesh ring, and the plate course each mesh row belongs to.
        Rows are laid out course by course so every course boundary is a ring."""
        s = self.spec
        self.z_levels: list[float] = [0.0]
        self.row_course: list[int] = []
        z0 = 0.0
        for c_idx, (course, n) in enumerate(zip(s.plate_courses, s.course_divisions())):
            dz = course.height / n
            for k in range(1, n + 1):
                self.z_levels.append(z0 + k * dz)
                self.row_course.append(c_idx)
            z0 += course.height
        self.z_levels[-1] = s.height   # exact top, no accumulated rounding
        # one section per distinct thickness, in bottom-up order of first use
        self.course_section: list[str] = []
        for course in s.plate_courses:
            name = next((n for n, t in self.sections.items() if t == course.thickness), None)
            if name is None:
                name = "WALL_T%d" % (len(self.sections) + 1)
                self.sections[name] = course.thickness
            self.course_section.append(name)

    def _build(self) -> None:
        s = self.spec
        dtheta = 2.0 * math.pi / s.n_theta
        self._wall_joints = self.ids.claim("joint", "wall", (self.n_rows + 1) * s.n_theta)
        self._wall_areas = self.ids.claim("area", "wall", self.n_rows * s.n_theta)

        for ring, z in enumerate(self.z_levels):
            for i in range(s.n_theta):
                theta = i * dtheta
                jid = self.joint_id(ring, i)
                self.joints[jid] = (
                    s.radius * math.cos(theta),
                    s.radius * math.sin(theta),
                    z,
                )
                self.thetas[jid] = theta

        aid = self._wall_areas.start - 1
        for ring in range(self.n_rows):
            section = self.course_section[self.row_course[ring]]
            for i in range(s.n_theta):
                aid += 1
                self.areas[aid] = (
                    self.joint_id(ring, i),
                    self.joint_id(ring, i + 1),
                    self.joint_id(ring + 1, i + 1),
                    self.joint_id(ring + 1, i),
                )
                self.area_section[aid] = section

    @property
    def base_joints(self) -> list[int]:
        return [self.joint_id(0, i) for i in range(self.spec.n_theta)]

    @property
    def top_joints(self) -> list[int]:
        return [self.joint_id(self.n_rows, i) for i in range(self.spec.n_theta)]

    def row_areas(self, ring: int) -> list[int]:
        """Area ids of one mesh row of shells (0 = bottom)."""
        n = self.spec.n_theta
        start = self._wall_areas.start
        return list(range(start + ring * n, start + (ring + 1) * n))

    def course_areas(self, course: int) -> list[int]:
        """Area ids of one plate course (0 = bottom): every mesh row in it."""
        return [a for ring in range(self.n_rows) if self.row_course[ring] == course
                for a in self.row_areas(ring)]

    def base_local_angle_deg(self, jid: int) -> float:
        """AngleA for a base joint: local X tangential, local Y radial OUTWARD.

        theta-90 (not theta+90) so a positive local-2 displacement is the base
        expanding, which is the sign we want to read in the results.
        """
        return math.degrees(self.thetas[jid]) - 90.0

    # --- groups ----------------------------------------------------------------

    def groups(self) -> dict[str, tuple[list[int], list[int]]]:
        """SAP group name -> (area ids, joint ids), in definition order.

        WALL = every shell; COURSE_kk = one plate course (01 = bottom);
        BASE_RING / TOP_RING = the boundary joint rings. Names are what SAP
        writes back in GROUPS 2 - ASSIGNMENTS, so SapToPluto turns them into
        sidecar groups unchanged.
        """
        s = self.spec
        out: dict[str, tuple[list[int], list[int]]] = {}
        if not s.groups:
            return out
        out["WALL"] = (sorted(self.areas), [])
        if s.course_groups:
            n_courses = len(s.plate_courses)
            width = max(2, len(str(n_courses)))
            for c in range(n_courses):
                out[f"COURSE_{c + 1:0{width}d}"] = (self.course_areas(c), [])
        out["BASE_RING"] = ([], self.base_joints)
        out["TOP_RING"] = ([], self.top_joints)
        return out

    # --- hydrostatics --------------------------------------------------------

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
        arc = 2.0 * math.pi * s.radius / s.n_theta
        forces: dict[int, float] = {}
        zs = self.z_levels
        for ring in range(self.n_rows + 1):
            # half of each adjoining row's height (end rings have only one)
            below = zs[ring] - zs[ring - 1] if ring > 0 else 0.0
            above = zs[ring + 1] - zs[ring] if ring < self.n_rows else 0.0
            trib_h = (below + above) / 2.0
            for i in range(s.n_theta):
                jid = self.joint_id(ring, i)
                forces[jid] = self.hydro_value(jid) * arc * trib_h
        return forces
