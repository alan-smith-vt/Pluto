"""TankModel: joints, areas, frames and hydrostatic values for one tank (no I/O)."""

from __future__ import annotations

import math

from .section import eave_ring_outline, eave_ring_section
from .spec import TankSpec


def graded_sizes(length: float, coarse: float, edge: float, growth: float,
                 band: float, at_start: bool, at_end: bool) -> list[float]:
    """Element sizes along a segment of `length`, in order from its start.

    At a refined end the sizes run edge, edge*growth, edge*growth^2 ... (capped
    at `coarse`) until the band is covered; the interior is filled with rows
    as close to `coarse` as fit. The sizes sum to `length` exactly.
    """
    def ramp() -> list[float]:
        out, total, s = [], 0.0, edge
        while total < band - 1e-9 and s < coarse - 1e-9:
            out.append(s)
            total += s
            s = min(s * growth, coarse)
        return out
    head = ramp() if at_start else []
    tail = ramp() if at_end else []
    interior = length - sum(head) - sum(tail)
    if interior <= 0.0:
        raise ValueError(f"edge refinement ({band:g} ft bands) does not fit in a {length:g} ft segment")
    n = max(1, round(interior / coarse))
    return head + [interior / n] * n + tail[::-1]


def ellipe(k: float, n: int = 2000) -> float:
    """Complete elliptic integral of the second kind E(k) = int_0^{pi/2} sqrt(1 - k^2 sin^2 t) dt,
    Simpson's rule (E(0) = pi/2, E(1) = 1). No SciPy on the development side."""
    k2 = min(max(k, 0.0), 1.0) ** 2
    h = (math.pi / 2.0) / n
    total = 0.0
    for i in range(n + 1):
        t = i * h
        f = math.sqrt(max(0.0, 1.0 - k2 * math.sin(t) ** 2))
        total += f * (1 if i in (0, n) else (4 if i % 2 else 2))
    return total * h / 3.0


def boussinesq_dish(rho: float) -> float:
    """Settlement of a flexible, uniformly loaded circle on an elastic half-space at r/a = rho,
    relative to the centre: w(r) / w(0) = (2 / pi) E(r / a) inside the circle (1 at the centre,
    2/pi = 0.637 at the edge; Bowles 5e Eq. 5-16 shape factor I_s = 1 / 0.64)."""
    return 2.0 / math.pi * ellipe(min(rho, 1.0))


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
    frame_cardinal: frame -> SAP insertion point (8 = top centre: the ring wall
    hangs below its joints; drawing only, Transform=No)
    (written beside the .s2k for the viewer).
    Foundation "gap": ground_joints (fixed, coincident with the baseplate
    joints), ground_of: tank joint -> ground joint, links: id -> (I = ground,
    J = tank), link_prop: id -> property name, link_props: name -> {"k": ...}.
    Ring wall: ringwall_joints = top-of-wall joints (the rim's former ground
    joints; the rim gap links now act shell -> wall with a stiff contact k),
    ringwall_ground = fixed ground joints under the wall, ringwall_soil_k = the
    soil gap link stiffness between them (support "gap"; settlements later go
    on ringwall_ground).
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
        self.frame_cardinal: dict[int, int] = {}      # frame -> SAP cardinal point (default 10 = centroid)
        self.frame_transform: dict[int, bool] = {}    # frame -> SAP Transform flag (default False: drawing only)
        self.cap_joints: dict[str, list[int]] = {}   # interior joints of each cap (rim excluded)
        self.cap_ring: dict[str, dict[int, int]] = {}  # cap -> joint -> ring index (0 = centre)
        self.cap_radii_of: dict[str, list[float]] = {}  # cap -> ring radii, centre .. rim
        self.ground_joints: list[int] = []
        self.ground_of: dict[int, int] = {}
        self.links: dict[int, tuple[int, int]] = {}
        self.link_prop: dict[int, str] = {}
        self.link_props: dict[str, dict] = {}
        self.ringwall_joints: list[int] = []
        self.ringwall_ground: list[int] = []
        self.ringwall_soil_k: float = 0.0
        self.ringwall_contact_k: float = 0.0
        self.dent_joints: dict[int, dict[int, float]] = {}
        self.bearing_joints: list[int] = []           # plate joints over the ring wall width, on GAP_BEARING links
        self.overhang_joints: list[int] = []          # plate edge ring beyond the shell ([baseplate] overhang)
        self.bearing_arm_joints: list[int] = []       # their support joints on the ring wall centroid line
        self.soil_face_joints: list[int] = []         # [ringwall] soil_links = "faces": joints at the faces on the centroid line
        self._arm_from: dict[tuple, int] = {}         # (spoke theta, outer side?) -> last arm joint on that side
        self.settlements: dict[int, float] = {}       # ground joint -> dz (ft, negative = down)
        self.area_local_angle: dict[int, float] = {}  # area -> local-axis rotation (deg) about local 3
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
        if spec.settlement != "none":
            self._build_settlement()
        self._assign_area_axes()

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
        last = len(s.plate_courses) - 1
        for c_idx, (course, n) in enumerate(zip(s.plate_courses, s.course_divisions())):
            dz = course.height / n
            at_base = c_idx == 0 and self.refines("wall_base")
            at_top = c_idx == last and self.refines("wall_top")
            if at_base or at_top:
                sizes = graded_sizes(course.height, dz, s.edge_size, s.edge_growth,
                                     s.edge_length, at_base, at_top)
                z = z0
                for h in sizes:
                    z += h
                    self.z_levels.append(z)
                    self.row_course.append(c_idx)
            else:
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

    def refines(self, edge: str) -> bool:
        """Is this shell edge graded ([mesh] edge_length > 0 and named in refine)?"""
        s = self.spec
        return s.edge_length > 0.0 and edge in s.refine_edges

    def cap_radii(self, n_r: int, edge: str) -> list[float]:
        """Ring radii of a polar cap, centre (0) to rim (R): R k / n_r, or graded
        at the rim from edge_size out to edge_length when that edge is refined."""
        s = self.spec
        if not self.refines(edge):
            return [s.radius * k / n_r for k in range(n_r + 1)]
        if edge == "plate_rim" and self.refines("concrete_edge"):
            # two segments: 0 -> concrete inner face, graded at its outer end; then the
            # plate over the concrete to the shell at edge_size (2026-09-11)
            r_c = s.radius - s.ringwall_width / 2.0
            sizes = graded_sizes(r_c, s.radius / n_r, s.edge_size, s.edge_growth, s.edge_length, False, True)
            n_over = max(2, round((s.radius - r_c) / s.edge_size))
            sizes += [(s.radius - r_c) / n_over] * n_over
        else:
            sizes = graded_sizes(s.radius, s.radius / n_r, s.edge_size, s.edge_growth,
                                 s.edge_length, False, True)
        radii, r = [0.0], 0.0
        for h in sizes:
            r += h
            radii.append(r)
        radii[-1] = s.radius
        return radii

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

    def _polar_cap(self, name: str, rim: list[int], radii: list[float], z_of_r, section: str,
                   face: str | None) -> None:
        """Mesh a disc inside an existing rim ring of n_theta joints.

        radii[k] is the radius of ring k, 0 (centre) .. n_r (the rim, = R):
        quads between concentric rings k = n_r .. 1, and a fan of triangles
        from ring 1 to a centre joint; z_of_r(r) gives each ring's height.
        Element corners run inner -> outer -> outer+1 -> inner+1, so local 3 =
        r x theta = UP (the wet face of the baseplate is then "Top"). Interior
        joints and areas claim their own id blocks; the rim joints are shared
        with the wall.
        """
        s = self.spec
        n = s.n_theta
        n_r = len(radii) - 1
        self.cap_radii_of[name] = list(radii)
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
            r = radii[k]
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
        self._polar_cap("baseplate", self.base_joints, self.cap_radii(s.baseplate_n_r, "plate_rim"),
                        lambda r: 0.0, "BASEPLATE", "Top")
        if s.baseplate_overhang > 0.0:
            self._build_overhang()

    def _build_overhang(self) -> None:
        """One ring of quads from the rim out to R + overhang (the plate edge beyond
        the shell): a new joint ring at z = 0, corners inner -> outer -> outer+1 ->
        inner+1 like the cap so local 3 is up, section BASEPLATE, no fluid face.
        The joints join the baseplate joint list (ring index n_r + 1, radius
        appended to cap_radii_of), so the foundation gives them a link and, with
        ringwall.plate_bearing, they sit on the concrete like the rings inside."""
        s = self.spec
        n = s.n_theta
        dtheta = 2.0 * math.pi / n
        r_out = s.radius + s.baseplate_overhang
        radii = self.cap_radii_of["baseplate"]
        k_out = len(radii)                      # rim is k_out - 1
        radii.append(r_out)
        joints = self.ids.claim("joint", "baseplate_overhang", n)
        areas = self.ids.claim("area", "baseplate_overhang", n)
        rim = self.base_joints
        ring_of = self.cap_ring["baseplate"]
        for i, j in enumerate(joints):
            theta = i * dtheta
            self.joints[j] = (r_out * math.cos(theta), r_out * math.sin(theta), 0.0)
            self.thetas[j] = theta
            ring_of[j] = k_out
        outer = list(joints)
        for i, a in enumerate(areas):
            self.areas[a] = (rim[i], outer[i], outer[(i + 1) % n], rim[(i + 1) % n])
            self.area_section[a] = "BASEPLATE"
        self.cap_joints["baseplate"].extend(outer)
        self.overhang_joints = outer

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
        self._polar_cap("roof", self.top_joints, self.cap_radii(s.roof_n_r, "roof_rim"), self.roof_z, "ROOF", None)
        if s.roof_ring:
            # compression ring at the eave: an angle on the wall top plus a flat
            # bar on its horizontal leg (an L) -- SAP has no such shape, so a General
            # section carries the computed properties and the outline goes to
            # the viewer via the .outlines.txt sidecar.
            outline = eave_ring_outline(s.roof_ring_leg, s.roof_ring_thickness)
            self.frame_outlines["ROOF_RING"] = outline
            self.frame_sections["ROOF_RING"] = eave_ring_section(s.roof_ring_leg, s.roof_ring_thickness)
            top = self.top_joints
            frames = self.ids.claim("frame", "roof_ring", s.n_theta)
            for k, fid in enumerate(frames):
                self.frames[fid] = (top[k], top[(k + 1) % s.n_theta])
                self.frame_section[fid] = "ROOF_RING"

    @property
    def baseplate_areas(self) -> list[int]:
        if not self.spec.baseplate:
            return []
        out = list(self.ids.block("area", "baseplate"))
        if self.overhang_joints:
            out += list(self.ids.block("area", "baseplate_overhang"))
        return out

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
        """Plan area carried by one baseplate joint, ft^2: an annulus slice between
        the ring bisectors for an interior ring, half a slice at the rim, and at the
        centre the consistent share of the triangle fan (a third of each triangle,
        n/3 x r1^2 sin(2 pi/n) / 2), since that is the load SAP puts on the joint;
        ring 1 gives up the difference so the areas still sum to pi R^2 (2026-09-11:
        the bisector disc, pi (r1/2)^2, left the centre spring 1.33 x too soft)."""
        s = self.spec
        n = s.n_theta
        radii = self.cap_radii_of["baseplate"]
        k = self.cap_ring["baseplate"][jid]
        fan = n * 0.5 * radii[1] ** 2 * math.sin(2.0 * math.pi / n) / 3.0
        if k == 0:
            return fan
        r_in = (radii[k - 1] + radii[k]) / 2.0
        r_out = (radii[k] + radii[k + 1]) / 2.0 if k + 1 < len(radii) else radii[-1]
        area = math.pi * (r_out * r_out - r_in * r_in) / n
        if k == 1:
            area -= (fan - math.pi * (radii[1] / 2.0) ** 2) / n
        return area

    # --- foundation: ground joints + compression-only gap links ------------------

    def pad_zone_factor(self, ring: int) -> float:
        """Multiplier on the pad subgrade modulus for plate ring k ([foundation] zone):
        1 everywhere for "none"; zone_factor inside the rim band for "step"; the inverse
        of the Boussinesq dish, pi / (2 E(r/R)), for "boussinesq" (1.0 centre, 1.571 rim)."""
        s = self.spec
        if s.pad_zone == "none":
            return 1.0
        r = self.cap_radii_of["baseplate"][ring]
        if s.pad_zone == "step":
            return s.pad_zone_factor if r >= s.radius - s.pad_zone_width - 1e-6 else 1.0
        return 1.0 / boussinesq_dish(r / s.radius)

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
                    "k": s.subgrade_modulus * self.pad_zone_factor(k) * self.baseplate_tributary_area(tj),
                    "ring": k, "tributary_area": self.baseplate_tributary_area(tj),
                    "zone_factor": self.pad_zone_factor(k),
                }
            self.links[lid] = (gj, tj)
            self.link_prop[lid] = name

    @property
    def gap(self) -> bool:
        return self.spec.foundation == "gap"

    # --- shell local axes: 1 = meridional, 2 = circumferential everywhere -------

    def _assign_area_axes(self) -> None:
        """Rotate every shell's local 1-2 axes so that local 1 runs along a
        meridian (up the wall, radially out on the baseplate, up the slope on
        the roof) and local 2 runs circumferentially. SAP's default gives a
        non-horizontal element 2 = projection of +Z and 1 = horizontal, so +90
        deg puts 1 on the meridian; the flat baseplate gets 1 = +X by default,
        so its angle is the element's azimuth (local 3 is up there, see
        _polar_cap). Result labels F11 / S11 = meridional, F22 / S22 =
        circumferential on every shell (2026-09-04)."""
        plate = set(self.baseplate_areas) if self.spec.baseplate else set()
        for a, js in self.areas.items():
            if a in plate:
                cx = sum(self.joints[j][0] for j in js) / len(js)
                cy = sum(self.joints[j][1] for j in js) / len(js)
                self.area_local_angle[a] = math.degrees(math.atan2(cy, cx))
            else:
                self.area_local_angle[a] = 90.0

    def meridional_direction(self, aid: int) -> tuple[float, float, float]:
        """Unit vector local 1 should have after the rotation: radial outward on
        the baseplate; the in-surface projection of +Z (up the meridian) on the
        wall and roof, taken at the element centroid of the spherical cap."""
        s = self.spec
        js = self.areas[aid]
        cx = sum(self.joints[j][0] for j in js) / len(js)
        cy = sum(self.joints[j][1] for j in js) / len(js)
        r = math.hypot(cx, cy)
        if s.baseplate and aid in set(self.baseplate_areas):
            return (cx / r, cy / r, 0.0) if r > 0 else (1.0, 0.0, 0.0)
        if s.roof and aid in set(self.roof_areas):
            # the flat facet's own normal (what SAP uses), not the sphere's
            p0, p1, p2 = (self.joints[j] for j in js[:3])
            ax, ay, az = (p1[i] - p0[i] for i in range(3))
            bx, by, bz = (p2[i] - p0[i] for i in range(3))
            nx, ny, nz = ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx
            nm = math.sqrt(nx * nx + ny * ny + nz * nz)
            nx, ny, nz = nx / nm, ny / nm, nz / nm
            # projection of +Z onto the facet plane = up the slope
            tx, ty, tz = -nz * nx, -nz * ny, 1.0 - nz * nz
            m = math.sqrt(tx * tx + ty * ty + tz * tz)
            return (tx / m, ty / m, tz / m) if m > 1e-9 else (1.0, 0.0, 0.0)
        return (0.0, 0.0, 1.0)

    # --- settlement: ground displacement on the ground joints -------------------

    @property
    def all_ground_joints(self) -> list[int]:
        """Every fixed ground joint: under the plate and under the ring wall."""
        return self.plate_ground_joints + self.ringwall_ground

    def settlement_dz(self, x: float, y: float) -> float:
        """Ground settlement w(x, y) in ft (negative = down) for the configured
        profile. d = -x sin(theta) + y cos(theta) - c is the signed distance
        from the line at direction theta through the point at offset c.
        trench: parabolic across a straight trench of width W on that line,
        w = -depth (1 - (2 d / W)^2) for |d| <= W/2, else 0.
        slope: half-plane linear settlement hinged on that line, zero for
        d <= 0 and w = -depth * d / R beyond it, so the full depth is reached
        at the tank edge (d = R; the ring wall just outside gets slightly more)."""
        s = self.spec
        if s.settlement == "none":
            return 0.0
        th = math.radians(s.settlement_direction_deg)
        d = -x * math.sin(th) + y * math.cos(th) - s.settlement_offset
        if s.settlement == "trench":
            u = 2.0 * d / s.settlement_width
            return -s.settlement_depth * (1.0 - u * u) if abs(u) <= 1.0 else 0.0
        if s.settlement == "slope":
            return -s.settlement_depth * d / s.radius if d > 0.0 else 0.0
        return 0.0

    def _build_settlement(self) -> None:
        for g in self.all_ground_joints:
            x, y, _ = self.joints[g]
            self.settlements[g] = self.settlement_dz(x, y)

    # --- ring wall ---------------------------------------------------------------

    def _build_ringwall(self) -> None:
        """Rim load path: tank rim joint -> GAP_CONTACT link (compression only,
        k = the concrete column under the joint, E A / depth) -> wall joint
        (the rim's former ground joint) -> RINGWALL frames -> GAP_SOIL link
        (compression only, k = subgrade x width x arc) -> fixed ground joint
        (support "gap"; "fixed" pins the wall joint in U3 instead). The wall
        joint keeps a tangential restraint only (local axes as the rim), so
        the ring can expand and settle with the tank. Local 2 is up by SAP
        default, so t3 = depth.

        joints = "elevations" (default since 2026-09-08): the wall joint sits
        at the section centroid, z = -depth/2, and the ground joint at the
        base, z = -depth; both links have length depth/2 (I below J, so local
        1 = +Z as for a zero-length link) and the frame needs no insertion
        point. Nothing is coincident, so the chain can be picked apart in the
        SAP GUI. joints = "top": all three at the wall top, insertion point 8
        (top centre) drawing the section below the joints, Transform per
        [ringwall] transform. Same answers for vertical load and settlement
        (vault/arms/beam-offset-study)."""
        s = self.spec
        self.ringwall_joints = [self.ground_of[j] for j in self.base_joints]
        elev = s.ringwall_joints == "elevations"
        if elev:
            for wj in self.ringwall_joints:
                x, y, z = self.joints[wj]
                self.joints[wj] = (x, y, z - s.ringwall_depth / 2.0)
        arc = 2.0 * math.pi * s.radius / s.n_theta
        # the rim's gap links: soil stiffness -> concrete contact stiffness
        self.ringwall_contact_k = self.concrete["E"] * s.ringwall_width * arc / s.ringwall_depth
        base = set(self.base_joints)
        rim_links = [lid for lid, (_, j) in self.links.items() if j in base]
        old_props = {self.link_prop[lid] for lid in rim_links}
        for lid in rim_links:
            self.link_prop[lid] = "GAP_CONTACT"
        for name in old_props:
            if name not in self.link_prop.values():
                del self.link_props[name]
        self.link_props["GAP_CONTACT"] = {"k": self.ringwall_contact_k, "ring": None,
                                          "tributary_area": s.ringwall_width * arc}
        ks_rw = s.ringwall_subgrade if s.ringwall_subgrade > 0.0 else s.subgrade_modulus
        self.ringwall_soil_k = ks_rw * s.ringwall_width * arc
        if s.ringwall_support == "gap":
            ground = self.ids.claim("joint", "ringwall_ground", s.n_theta)
            links = self.ids.claim("link", "ringwall_gap", s.n_theta)
            for wj, gj, lid in zip(self.ringwall_joints, ground, links):
                x, y, z = self.joints[wj]
                self.joints[gj] = (x, y, z - s.ringwall_depth / 2.0) if elev else (x, y, z)
                self.thetas[gj] = self.thetas[wj]
                self.ringwall_ground.append(gj)
                self.links[lid] = (gj, wj)
                self.link_prop[lid] = "GAP_SOIL"
            self.link_props["GAP_SOIL"] = {"k": self.ringwall_soil_k, "ring": None,
                                           "tributary_area": s.ringwall_width * arc}
        self.frame_sections["RINGWALL"] = {
            "Material": "CONC", "Shape": "Rectangular",
            "t3": s.ringwall_depth, "t2": s.ringwall_width,
        }
        frames = self.ids.claim("frame", "ringwall", s.n_theta)
        rw = self.ringwall_joints
        for k, fid in enumerate(frames):
            self.frames[fid] = (rw[k], rw[(k + 1) % s.n_theta])
            self.frame_section[fid] = "RINGWALL"
            if not elev:
                self.frame_cardinal[fid] = 8        # top centre: section hangs below the joints
                self.frame_transform[fid] = s.ringwall_transform
        if s.ringwall_plate_bearing:
            self._build_plate_bearing()
        if s.ringwall_soil_links == "faces":
            self._build_soil_faces()

    def _build_soil_faces(self) -> None:
        """Replace the one GAP_SOIL link under each axis joint with two at the ring wall's
        inner and outer faces, k/2 each, so the ring has the rotational bearing stiffness of
        its width (k C^2 / 4 per spoke). The face joints sit on the centroid line, z = -A/2,
        and continue the RINGWALL_ARM chain on their side (an existing arm joint at a face
        is reused); each gets a fixed ground joint at z = -A below it. Needs joints =
        "elevations" and plate_bearing (the chain), checked in the spec."""
        s = self.spec
        n = s.n_theta
        r_in, r_out = s.radius - s.ringwall_width / 2.0, s.radius + s.ringwall_width / 2.0
        # drop the centroid soil links and their ground joints
        old = [l for l, (i, j) in self.links.items() if j in set(self.ringwall_joints) and self.link_prop[l] == "GAP_SOIL"]
        for l in old:
            gi, _ = self.links.pop(l)
            del self.link_prop[l]
            self.ringwall_ground.remove(gi)
            del self.joints[gi]
            self.thetas.pop(gi, None)
        self.link_props["GAP_SOIL"]["k"] = self.ringwall_soil_k / 2.0
        self.link_props["GAP_SOIL"]["tributary_area"] /= 2.0
        r_arm = {j: math.hypot(*self.joints[j][:2]) for j in self.bearing_arm_joints}
        at_face = {(round(self.thetas[j], 9), round(r_arm[j], 6)): j for j in self.bearing_arm_joints}
        by_theta = {round(self.thetas[j], 9): j for j in self.ringwall_joints}
        new_joints = self.ids.claim("joint", "soil_face", 2 * n)
        ground = self.ids.claim("joint", "soil_face_ground", 2 * n)
        links = self.ids.claim("link", "soil_face", 2 * n)
        frames = self.ids.claim("frame", "soil_face_arm", 2 * n)
        nj, ng, nl, nf = iter(new_joints), iter(ground), iter(links), iter(frames)
        for wj in self.ringwall_joints:
            th = round(self.thetas[wj], 9)
            theta = self.thetas[wj]
            for r_face, outer in ((r_in, False), (r_out, True)):
                fj = at_face.get((th, round(r_face, 6)))
                if fj is None:
                    fj = next(nj)
                    self.joints[fj] = (r_face * math.cos(theta), r_face * math.sin(theta), -s.ringwall_depth / 2.0)
                    self.thetas[fj] = theta
                    fid = next(nf)
                    self.frames[fid] = (self._arm_from.get((th, outer), by_theta[th]), fj)
                    self.frame_section[fid] = "RINGWALL"
                    self._arm_from[(th, outer)] = fj
                    self.bearing_arm_joints.append(fj)
                self.soil_face_joints.append(fj)
                gj = next(ng)
                self.joints[gj] = (r_face * math.cos(theta), r_face * math.sin(theta), -s.ringwall_depth)
                self.thetas[gj] = theta
                self.ringwall_ground.append(gj)
                lid = next(nl)
                self.links[lid] = (gj, fj)
                self.link_prop[lid] = "GAP_SOIL"
        # unused ids (a face joint reused an arm joint) are simply never written
        used_frames = [f for f in frames if f in self.frames]
        self.ids.blocks[("frame", "soil_face_arm")] = range(frames.start, frames.start + len(used_frames))

    def _build_plate_bearing(self) -> None:
        """Plate joints that lie over the ring wall width (r >= R - C/2, rim excluded)
        sit on concrete, not on the pad. Each loses its GAP_Rnn pad link and its
        ground joint, and gets instead: an arm joint on the ring wall centroid line
        under it (z = -A/2), a rigid-ish RINGWALL_ARM frame (the ring wall section
        itself) from the spoke's ring wall axis joint radially in to the arm joint,
        and a GAP_BEARING contact link (I = arm joint, J = plate joint, length A/2,
        local 1 = +Z) with k = E_c x tributary area / depth. The arms on one
        spoke are chained end to end (axis joint -> outermost arm joint -> next
        inward), never overlapping. Needs joints = "elevations" (the arm sits at
        the centroid elevation)."""
        s = self.spec
        if s.ringwall_joints != "elevations":
            raise ValueError("ringwall.plate_bearing needs ringwall.joints = 'elevations'")
        r_in = s.radius - s.ringwall_width / 2.0 - 1e-6
        radii = self.cap_radii_of["baseplate"]
        ring_of = self.cap_ring["baseplate"]
        rim = set(self.base_joints)
        bearing = [j for j in self.baseplate_interior_joints
                   if j not in rim and radii[ring_of[j]] >= r_in]
        # per spoke, nearest the axis first on each side (inside: outermost ring first;
        # the overhang ring outside), so each arm starts where the previous one on its
        # side ended and never crosses the axis joint
        bearing.sort(key=lambda j: (round(self.thetas[j], 9), abs(radii[ring_of[j]] - s.radius)))
        if not bearing:
            return
        by_theta = {round(self.thetas[j], 9): j for j in self.ringwall_joints}
        arm_from: dict[tuple, int] = {}                 # (spoke, side) -> joint the next arm starts from
        arm_joints = self.ids.claim("joint", "bearing_arm", len(bearing))
        links = self.ids.claim("link", "bearing", len(bearing))
        frames = self.ids.claim("frame", "ringwall_arm", len(bearing))
        old_props = set()
        for pj, aj, lid, fid in zip(bearing, arm_joints, links, frames):
            # drop the pad link and its ground joint
            gj = self.ground_of.pop(pj)
            old = [l for l, (i, j) in self.links.items() if j == pj]
            for l in old:
                old_props.add(self.link_prop.pop(l))
                del self.links[l]
            self.ground_joints.remove(gj)
            del self.joints[gj]
            self.thetas.pop(gj, None)
            # arm joint under the plate joint, on the centroid line
            x, y, _ = self.joints[pj]
            self.joints[aj] = (x, y, -s.ringwall_depth / 2.0)
            self.thetas[aj] = self.thetas[pj]
            th = round(self.thetas[pj], 9)
            side = (th, radii[ring_of[pj]] > s.radius)
            self.frames[fid] = (arm_from.get(side, by_theta[th]), aj)
            arm_from[side] = aj
            self._arm_from = arm_from
            self.frame_section[fid] = "RINGWALL"
            self.links[lid] = (aj, pj)
            self.link_prop[lid] = "GAP_BEARING"
        for name in old_props:
            if name not in self.link_prop.values():
                del self.link_props[name]
        cushion = s.ringwall_cushion_modulus > 0.0
        if cushion:
            # sand cushion: k per unit area = E / t, one property per plate ring (its own
            # tributary area), and the shell-line contact link sits on the same sand
            per_area = s.ringwall_cushion_modulus / s.ringwall_cushion_thickness
            width = max(2, len(str(len(radii))))
            for pj, lid in zip(bearing, links):
                k = ring_of[pj]
                name = f"GAP_BEAR_R{k:0{width}d}"
                if name not in self.link_props:
                    trib = self.baseplate_tributary_area(pj)
                    self.link_props[name] = {"k": per_area * trib, "ring": k, "tributary_area": trib}
                self.link_prop[lid] = name
            rim_trib = self.baseplate_tributary_area(self.base_joints[0])
            self.ringwall_contact_k = per_area * rim_trib
            self.link_props["GAP_CONTACT"] = {"k": self.ringwall_contact_k, "ring": None,
                                              "tributary_area": rim_trib}
        else:
            trib = sum(self.baseplate_tributary_area(j) for j in bearing) / len(bearing)
            self.link_props["GAP_BEARING"] = {
                "k": self.concrete["E"] * trib / s.ringwall_depth, "ring": None, "tributary_area": trib}
        self.bearing_joints = bearing
        self.bearing_arm_joints = list(arm_joints)

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
            out["RINGWALL_AXIS" if s.ringwall_joints == "elevations" else "RINGWALL_TOP"] = ([], self.ringwall_joints, [])
            if self.ringwall_ground:
                out["RINGWALL_GROUND"] = ([], self.ringwall_ground, [])
            if self.overhang_joints:
                out["PLATE_OVERHANG"] = (list(self.ids.block("area", "baseplate_overhang")), self.overhang_joints, [])
            if self.bearing_joints:
                out["PLATE_BEARING"] = ([], self.bearing_joints, [])
                arm_frames = list(self.ids.block("frame", "ringwall_arm"))
                if ("frame", "soil_face_arm") in self.ids.blocks:
                    arm_frames += list(self.ids.block("frame", "soil_face_arm"))
                out["RINGWALL_ARM"] = ([], self.bearing_arm_joints, arm_frames)
            if self.soil_face_joints:
                out["RINGWALL_SOIL_FACES"] = ([], self.soil_face_joints, [])
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
