"""TankModel: joints, areas, frames and hydrostatic values for one tank (no I/O)."""

from __future__ import annotations

import math

from .section import z_pair_outline, z_pair_section
from .spec import TankSpec


class IdAllocator:
    """Hands out contiguous id blocks per SAP object type (joints, areas,
    frames, links), each numbered from 1. Every part of the model (wall,
    baseplate, roof, ring wall ...) claims its block here, so ids never
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
    """Joints, areas, frames and hydrostatic pattern values for one tank.

    joints: id -> (x, y, z); thetas: id -> circumferential angle (rad);
    areas: id -> joint tuple (4 = quad, 3 = tri), ordered so the SAP local 3
    axis points radially outward on the wall and UP on the caps;
    area_section: id -> section name; sections: section name -> thickness;
    area_face: id -> "Bottom" | "Top", the wet face for the HYDRO pressure
    (absent = no pressure); frames: id -> (joint I, joint J);
    frame_section: id -> section name; frame_sections: name -> property dict;
    frame_outlines: name -> (y, z) polygon for sections SAP has no shape for
    (written beside the .s2k for the viewer).
    Foundation "gap": ground_joints (fixed, coincident with the baseplate
    joints), ground_of: tank joint -> ground joint, links: id -> (I = ground,
    J = tank), link_prop: id -> property name, link_props: name -> {"k": ...}.
    Ring wall: ringwall_joints (the rim's ground joints, now the frame joints
    of the RINGWALL frames), ringwall_spring: U3 spring per joint (springs mode).
    Dents: dent_joints: dent index -> {wall joint: offset fraction} for the
    joints the dent moved (DENT_nn groups come from these).
    """

    def __init__(self, spec: TankSpec):
        spec.validate()
        self.spec = spec
        self.ids = IdAllocator()
        self.joints: dict[int, tuple[float, float, float]] = {}
        self.thetas: dict[int, float] = {}
        self.areas: dict[int, tuple[int, ...]] = {}
        self.area_section: dict[int, str] = {}
        self.area_face: dict[int, str] = {}
        self.sections: dict[str, float] = {}
        self.frames: dict[int, tuple[int, int]] = {}
        self.frame_section: dict[int, str] = {}
        self.frame_sections: dict[str, dict] = {}
        self.frame_outlines: dict[str, list[tuple[float, float]]] = {}
        self.cap_joints: dict[str, list[int]] = {}   # interior joints of each cap (rim excluded)
        self.cap_ring: dict[str, dict[int, int]] = {}  # cap -> joint -> ring index (0 = centre)
        self.ground_joints: list[int] = []
        self.ground_of: dict[int, int] = {}
        self.links: dict[int, tuple[int, int]] = {}
        self.link_prop: dict[int, str] = {}
        self.link_props: dict[str, dict] = {}
        self.ringwall_joints: list[int] = []
        self.ringwall_spring: float = 0.0
        self.dent_joints: dict[int, dict[int, float]] = {}
        self._build_levels()
        self._build_wall()
        self._apply_dents()
        if spec.baseplate:
            self._build_baseplate()
        if spec.roof:
            self._build_roof()
        if spec.foundation == "gap":
            self._build_foundation()
        if spec.ringwall:
            self._build_ringwall()

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

    # --- wall -----------------------------------------------------------------

    def _build_wall(self) -> None:
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
                self.area_face[aid] = "Bottom"     # water on the local -3 (inside) face

    # --- dents -----------------------------------------------------------------

    def dent_fraction(self, dent_index: int, jid: int) -> float:
        """0..1 share of the dent depth at a wall joint: cos^2 bell over an
        elliptical footprint (width along the arc, height up the wall)."""
        d = self.spec.dents[dent_index]
        x, y, z = self.joints[jid]
        theta = self.thetas[jid]
        dtheta = (theta - math.radians(d.angle_deg) + math.pi) % (2.0 * math.pi) - math.pi
        arc = self.spec.radius * dtheta
        rho = math.hypot(arc / (d.width / 2.0), (z - d.elevation) / (d.height / 2.0))
        if rho >= 1.0:
            return 0.0
        return math.cos(math.pi * rho / 2.0) ** 2

    def _apply_dents(self) -> None:
        """Move wall joints radially inward by depth x fraction; the mesh and
        the ids are untouched. Applied before the caps, so the caps' rims (the
        base / top rings) follow if a dent reaches them."""
        for n, d in enumerate(self.spec.dents):
            moved: dict[int, float] = {}
            for jid in self._wall_joints:
                f = self.dent_fraction(n, jid)
                if f <= 0.0:
                    continue
                x, y, z = self.joints[jid]
                r = math.hypot(x, y)
                r2 = r - d.depth * f
                self.joints[jid] = (x * r2 / r, y * r2 / r, z)
                moved[jid] = f
            self.dent_joints[n] = moved

    def dent_areas(self, dent_index: int) -> list[int]:
        """Wall shells with at least one corner the dent moved."""
        moved = self.dent_joints.get(dent_index, {})
        return [a for a in self._wall_areas if any(j in moved for j in self.areas[a])]

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

    # --- polar caps (baseplate, roof) ------------------------------------------

    def _polar_cap(self, name: str, rim: list[int], n_r: int, z_of_r, section: str,
                   face: str | None) -> None:
        """Mesh a disc inside an existing rim ring of n_theta joints.

        n_r rings of elements: quads between concentric rings k = n_r (the rim)
        .. 1, and a fan of triangles from ring 1 to a centre joint. Ring k sits
        at r = R*k/n_r; z_of_r(r) gives its height. Element corners run inner ->
        outer -> outer+1 -> inner+1, so local 3 = r x theta = UP (the wet face
        of the baseplate is then "Top"). Interior joints and areas claim their
        own id blocks; the rim joints are shared with the wall.
        """
        s = self.spec
        n = s.n_theta
        dtheta = 2.0 * math.pi / n
        joints = self.ids.claim("joint", name, (n_r - 1) * n + 1)
        areas = self.ids.claim("area", name, n_r * n)
        centre = joints.start

        def jid(k: int, i: int) -> int:               # ring k (1..n_r), spoke i
            if k == n_r:
                return rim[i % n]
            return joints.start + 1 + (k - 1) * n + (i % n)

        ring_of = {centre: 0}
        for i in range(n):
            ring_of[rim[i]] = n_r
        self.joints[centre] = (0.0, 0.0, z_of_r(0.0))
        self.thetas[centre] = 0.0
        for k in range(1, n_r):
            r = s.radius * k / n_r
            z = z_of_r(r)
            for i in range(n):
                theta = i * dtheta
                j = jid(k, i)
                self.joints[j] = (r * math.cos(theta), r * math.sin(theta), z)
                self.thetas[j] = theta
                ring_of[j] = k
        self.cap_joints[name] = list(joints)
        self.cap_ring[name] = ring_of

        aid = areas.start - 1
        for k in range(1, n_r):                        # quads: ring k -> k+1
            for i in range(n):
                aid += 1
                self.areas[aid] = (jid(k, i), jid(k + 1, i), jid(k + 1, i + 1), jid(k, i + 1))
        for i in range(n):                             # centre fan
            aid += 1
            self.areas[aid] = (centre, jid(1, i), jid(1, i + 1))
        for a in areas:
            self.area_section[a] = section
            if face:
                self.area_face[a] = face

    def _build_baseplate(self) -> None:
        s = self.spec
        self.sections["BASEPLATE"] = s.baseplate_thickness
        self._polar_cap("baseplate", self.base_joints, s.baseplate_n_r,
                        lambda r: 0.0, "BASEPLATE", "Top")

    def roof_z(self, r: float) -> float:
        """Height of the spherical roof at plan radius r: crown radius Rc,
        centre of the sphere below the eave so that z(R) = wall height."""
        s = self.spec
        rc = s.roof_crown_radius
        return s.height + math.sqrt(rc * rc - r * r) - math.sqrt(rc * rc - s.radius * s.radius)

    @property
    def roof_rise(self) -> float:
        return self.roof_z(0.0) - self.spec.height

    def _build_roof(self) -> None:
        s = self.spec
        self.sections["ROOF"] = s.roof_thickness
        self._polar_cap("roof", self.top_joints, s.roof_n_r, self.roof_z, "ROOF", None)
        if s.roof_ring:
            # compression ring at the eave: (2) equal-leg angles back to back
            # forming a Z (not a T) -- SAP has no such shape, so a General
            # section carries the computed properties and the outline goes to
            # the viewer via the .outlines.txt sidecar.
            outline = z_pair_outline(s.roof_ring_leg, s.roof_ring_thickness)
            self.frame_outlines["ROOF_RING"] = outline
            self.frame_sections["ROOF_RING"] = z_pair_section(s.roof_ring_leg, s.roof_ring_thickness)
            top = self.top_joints
            frames = self.ids.claim("frame", "roof_ring", s.n_theta)
            for k, fid in enumerate(frames):
                self.frames[fid] = (top[k], top[(k + 1) % s.n_theta])
                self.frame_section[fid] = "ROOF_RING"

    @property
    def baseplate_areas(self) -> list[int]:
        return list(self.ids.block("area", "baseplate")) if self.spec.baseplate else []

    @property
    def roof_areas(self) -> list[int]:
        return list(self.ids.block("area", "roof")) if self.spec.roof else []

    @property
    def baseplate_interior_joints(self) -> list[int]:
        """Baseplate joints inside the rim (the rim is the wall's base ring)."""
        return self.cap_joints.get("baseplate", [])

    @property
    def baseplate_joints(self) -> list[int]:
        """Every baseplate joint: interior (centre first) then the rim."""
        return self.baseplate_interior_joints + (self.base_joints if self.spec.baseplate else [])

    def baseplate_tributary_area(self, jid: int) -> float:
        """Plan area carried by one baseplate joint, ft^2: the centre disc, an
        annulus slice for an interior ring, half an annulus slice at the rim.
        Sums to pi R^2 over the plate."""
        s = self.spec
        n_r, n = s.baseplate_n_r, s.n_theta
        dr = s.radius / n_r
        k = self.cap_ring["baseplate"][jid]
        if k == 0:
            return math.pi * (dr / 2.0) ** 2
        r_in = (k - 0.5) * dr
        r_out = min((k + 0.5) * dr, s.radius)
        return math.pi * (r_out * r_out - r_in * r_in) / n

    # --- foundation: ground joints + compression-only gap links ------------------

    def _build_foundation(self) -> None:
        """Gap support: one fixed ground joint coincident with every baseplate
        joint, one zero-length Gap link (I = ground, J = tank; local 1 = +Z for
        a zero-length link, so the tank pressing down closes the gap). One link
        property per baseplate ring, k = subgrade modulus x tributary area."""
        s = self.spec
        tank = self.baseplate_joints
        ground = self.ids.claim("joint", "ground", len(tank))
        links = self.ids.claim("link", "gap", len(tank))
        width = max(2, len(str(s.baseplate_n_r)))
        for tj, gj, lid in zip(tank, ground, links):
            self.joints[gj] = self.joints[tj]
            self.thetas[gj] = self.thetas[tj]
            self.ground_joints.append(gj)
            self.ground_of[tj] = gj
            k = self.cap_ring["baseplate"][tj]
            name = f"GAP_R{k:0{width}d}"
            if name not in self.link_props:
                self.link_props[name] = {
                    "k": s.subgrade_modulus * self.baseplate_tributary_area(tj),
                    "ring": k, "tributary_area": self.baseplate_tributary_area(tj),
                }
            self.links[lid] = (gj, tj)
            self.link_prop[lid] = name

    @property
    def gap(self) -> bool:
        return self.spec.foundation == "gap"

    # --- ring wall ---------------------------------------------------------------

    def _build_ringwall(self) -> None:
        """Closed polygon of concrete frames on the rim's ground joints (which
        stop being fixed: springs or fixed supports on the same joints; the gap
        links above them now act shell <-> ring wall). Frame axis at the top of
        the wall; local 2 is up by SAP default, so t3 = depth."""
        s = self.spec
        self.ringwall_joints = [self.ground_of[j] for j in self.base_joints]
        self.frame_sections["RINGWALL"] = {
            "Material": "CONC", "Shape": "Rectangular",
            "t3": s.ringwall_depth, "t2": s.ringwall_width,
        }
        frames = self.ids.claim("frame", "ringwall", s.n_theta)
        rw = self.ringwall_joints
        for k, fid in enumerate(frames):
            self.frames[fid] = (rw[k], rw[(k + 1) % s.n_theta])
            self.frame_section[fid] = "RINGWALL"
        arc = 2.0 * math.pi * s.radius / s.n_theta
        self.ringwall_spring = s.subgrade_modulus * s.ringwall_width * arc

    @property
    def concrete(self) -> dict | None:
        """Ring-wall concrete material (kip, ft): E = 57000 sqrt(f'c psi)."""
        s = self.spec
        if not s.ringwall:
            return None
        e_ksi = 57.0 * math.sqrt(s.ringwall_fc)
        return {"name": "CONC", "E": e_ksi * 144.0, "poisson": 0.2,
                "unit_weight": s.ringwall_unit_weight, "alpha": 5.5e-06, "fc": s.ringwall_fc}

    @property
    def plate_ground_joints(self) -> list[int]:
        """Ground joints that stay fixed ground: all of them, minus the ring wall's."""
        rw = set(self.ringwall_joints)
        return [g for g in self.ground_joints if g not in rw]

    # --- groups ----------------------------------------------------------------

    def groups(self) -> dict[str, tuple[list[int], list[int], list[int]]]:
        """SAP group name -> (area ids, joint ids, frame ids), in definition order.

        WALL = every wall shell; COURSE_kk = one plate course (01 = bottom);
        BASE_RING / TOP_RING = the boundary joint rings; BASEPLATE / ROOF =
        the cap shells; ROOF_RING = the eave frames. Names are what SAP
        writes back in GROUPS 2 - ASSIGNMENTS, so SapToPluto turns them into
        sidecar groups unchanged.
        """
        s = self.spec
        out: dict[str, tuple[list[int], list[int], list[int]]] = {}
        if not s.groups:
            return out
        out["WALL"] = (list(self._wall_areas), [], [])
        if s.course_groups:
            n_courses = len(s.plate_courses)
            width = max(2, len(str(n_courses)))
            for c in range(n_courses):
                out[f"COURSE_{c + 1:0{width}d}"] = (self.course_areas(c), [], [])
        out["BASE_RING"] = ([], self.base_joints, [])
        out["TOP_RING"] = ([], self.top_joints, [])
        if s.baseplate:
            out["BASEPLATE"] = (self.baseplate_areas, [], [])
        if s.roof:
            out["ROOF"] = (self.roof_areas, [], [])
            if s.roof_ring:
                out["ROOF_RING"] = ([], [], list(self.ids.block("frame", "roof_ring")))
        if self.gap:
            out["GROUND"] = ([], self.plate_ground_joints, [])
        if s.ringwall:
            out["RINGWALL"] = ([], [], list(self.ids.block("frame", "ringwall")))
            out["RINGWALL_TOP"] = ([], self.ringwall_joints, [])
        for n in range(len(s.dents)):
            out[f"DENT_{n + 1:02d}"] = (self.dent_areas(n), [], [])
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
        On the baseplate this is the full head everywhere.
        """
        js = self.areas[aid]
        zs = [self.joints[j][2] for j in js]
        return self._pressure_at(sum(zs) / len(zs))

    def joint_radial_forces(self) -> dict[int, float]:
        """Outward radial force per WALL joint, kip, from tributary area x pressure.

        Fallback to the surface-pressure tables: statically equivalent at the
        joints, so global response matches, but the shells no longer carry a
        true distributed load. Covers the wall only -- the baseplate is not
        loaded in this mode.
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
