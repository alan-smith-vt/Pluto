"""Thin-walled section outlines and their properties (kip, ft).

Used for frame sections SAP2000 has no built-in shape for (the eave ring: two
equal-leg angles forming a Z). The outline is a closed polygon in the
section-local (y, z) frame -- y horizontal, z vertical, origin at the
centroid -- which is what the viewer's POLY section draws, and the
properties feed a SAP "General" frame section.
"""

from __future__ import annotations

import math


def polygon_properties(pts: list[tuple[float, float]]) -> dict:
    """Area, centroid and second moments of a simple closed polygon (shoelace).

    Returns A, cy, cz, Iyy (about the horizontal axis through the centroid,
    i.e. sum z^2 dA), Izz (about the vertical axis, sum y^2 dA), Iyz.
    """
    a = cy = cz = iyy = izz = iyz = 0.0
    n = len(pts)
    for i in range(n):
        y0, z0 = pts[i]
        y1, z1 = pts[(i + 1) % n]
        cross = y0 * z1 - y1 * z0
        a += cross
        cy += (y0 + y1) * cross
        cz += (z0 + z1) * cross
        iyy += (z0 * z0 + z0 * z1 + z1 * z1) * cross
        izz += (y0 * y0 + y0 * y1 + y1 * y1) * cross
        iyz += (y0 * z1 + 2 * y0 * z0 + 2 * y1 * z1 + y1 * z0) * cross
    a *= 0.5
    if a < 0:                                   # clockwise outline
        a, cy, cz, iyy, izz, iyz = -a, -cy, -cz, -iyy, -izz, -iyz
    cy /= 6 * a
    cz /= 6 * a
    iyy = iyy / 12 - a * cz * cz
    izz = izz / 12 - a * cy * cy
    iyz = iyz / 24 - a * cy * cz
    return dict(A=a, cy=cy, cz=cz, Iyy=iyy, Izz=izz, Iyz=iyz)


def z_pair_section(leg: float, t: float) -> dict:
    """General-section fields for the Z pair: web 2t thick (open-section
    J = sum b t^3 / 3 with the doubled web), AS2 = web area, AS3 = flanges.
    Properties are taken in SAP's own axes (local 3 = -viewer y, see
    z_pair_outline), which only flips the sign of I23."""
    j = leg * (2 * t) ** 3 / 3.0 + 2 * (leg - t) * t ** 3 / 3.0
    sap_axes = [(-y, z) for y, z in z_pair_outline(leg, t)]
    return general_section(sap_axes, j, 2 * leg * t, 2 * (leg - t) * t)


def z_pair_outline(leg: float, t: float) -> list[tuple[float, float]]:
    """(2) equal-leg angles back to back forming a Z: web 2t thick, height = leg,
    centred on the centroid (0, 0), in the VIEWER's section frame (y, z):
    z = up (depth), y = up x member axis. For the eave ring running
    counter-clockwise, +y points INTO the tank -- so the top flange (+y) sits
    under the roof plate and the bottom flange (-y) sticks out as the gutter.
    SAP's local 3 is -y."""
    L, h = leg, leg / 2.0
    return [(t, -h), (-L, -h), (-L, t - h), (-t, t - h), (-t, h), (L, h), (L, h - t), (t, h - t)]


def general_section(pts: list[tuple[float, float]], j: float, as2: float, as3: float) -> dict:
    """SAP 'General' frame section fields from an outline, in the SAP frame
    local axes: local 2 = z (up), local 3 = y. I33 bends about the horizontal
    axis (uses z), I22 about the vertical one (uses y). The torsion constant
    and the shear areas (AS2 along 2 = web, AS3 along 3 = flanges) depend on
    the wall layout, so the caller supplies them. Plastic moduli are taken
    equal to the elastic ones (design-only fields; the analysis uses A, J, I)."""
    p = polygon_properties(pts)
    ys = [y for y, _ in pts]
    zs = [z for _, z in pts]
    depth = max(zs) - min(zs)
    width = max(ys) - min(ys)
    i33, i22 = p["Iyy"], p["Izz"]
    c2 = max(abs(max(zs)), abs(min(zs)))
    c3 = max(abs(max(ys)), abs(min(ys)))
    s33, s22 = i33 / c2, i22 / c3
    return {
        "Shape": "General", "t3": depth, "t2": width,
        "Area": p["A"], "TorsConst": j, "I33": i33, "I22": i22, "I23": p["Iyz"],
        "AS2": as2, "AS3": as3,
        "S33": s33, "S22": s22, "Z33": s33, "Z22": s22,
        "R33": math.sqrt(i33 / p["A"]), "R22": math.sqrt(i22 / p["A"]),
    }
