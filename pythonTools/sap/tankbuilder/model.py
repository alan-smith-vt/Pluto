"""TankModel: joints, areas and hydrostatic values for one tank (no I/O)."""

from __future__ import annotations

import math

from .spec import TankSpec


class TankModel:
    """Joints, areas and hydrostatic pattern values for one tank.

    joints: id -> (x, y, z); thetas: id -> circumferential angle (rad);
    areas: id -> (j1, j2, j3, j4) counter-clockwise seen from outside, so the
    SAP local 3 axis points radially outward.
    """

    def __init__(self, spec: TankSpec):
        spec.validate()
        self.spec = spec
        self.joints: dict[int, tuple[float, float, float]] = {}
        self.thetas: dict[int, float] = {}
        self.areas: dict[int, tuple[int, int, int, int]] = {}
        self._build()

    # --- numbering ----------------------------------------------------------

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

    @property
    def top_joints(self) -> list[int]:
        return [self.joint_id(self.spec.n_z, i) for i in range(self.spec.n_theta)]

    def course_areas(self, ring: int) -> list[int]:
        """Area ids of one horizontal course (0 = bottom)."""
        n = self.spec.n_theta
        return list(range(1 + ring * n, 1 + (ring + 1) * n))

    def base_local_angle_deg(self, jid: int) -> float:
        """AngleA for a base joint: local X tangential, local Y radial OUTWARD.

        theta-90 (not theta+90) so a positive local-2 displacement is the base
        expanding, which is the sign we want to read in the results.
        """
        return math.degrees(self.thetas[jid]) - 90.0

    # --- groups ----------------------------------------------------------------

    def groups(self) -> dict[str, tuple[list[int], list[int]]]:
        """SAP group name -> (area ids, joint ids), in definition order.

        WALL = every shell; COURSE_kk = one horizontal course (01 = bottom);
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
            width = max(2, len(str(s.n_z)))
            for ring in range(s.n_z):
                out[f"COURSE_{ring + 1:0{width}d}"] = (self.course_areas(ring), [])
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
