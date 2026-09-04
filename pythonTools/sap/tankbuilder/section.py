"""Thin-walled section outlines and their properties (kip, ft).

Used for frame sections SAP2000 has no built-in shape for (the eave ring: an
angle plus a flat bar forming an L with a doubled horizontal plate). The outline is a closed polygon in the
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


def eave_ring_section(leg: float, t: float) -> dict:
    """General-section fields for the eave ring: horizontal plate 2t thick
    (open-section J = sum b t^3 / 3), AS2 = the down leg (shear along local 2
    = vertical), AS3 = the horizontal plate. Properties are taken in SAP's
    own axes (local 3 = -viewer y, see eave_ring_outline), which only flips
    the sign of I23."""
    j = leg * (2 * t) ** 3 / 3.0 + (leg - t) * t ** 3 / 3.0
    sap_axes = [(-y, z) for y, z in eave_ring_outline(leg, t)]
    return general_section(sap_axes, j, (leg - t) * t, 2 * leg * t)


def eave_ring_outline(leg: float, t: float) -> list[tuple[float, float]]:
    """Eave compression ring: an equal-leg angle on top of the wall, its
    horizontal leg running OUTWARD and its other leg hanging down flush on
    the outside of the shell, plus a flat bar of the same leg width on top of
    the horizontal leg (the second angle with its upstanding leg removed),
    so the horizontal plate is 2t thick. An L, not a Z (2026-09-04).

    Viewer section frame (y, z): z = up, y = up x member axis = INTO the tank
    for the counter-clockwise ring, so outward is -y. The outline is anchored
    at the wall, not the centroid: origin = shell line at the top of the wall,
    so the viewer draws it flush without BPRP offsets (the SAP General section
    is centroidal regardless; the small eccentricity to the joint is ignored).
    SAP's local 3 is -y. Points run counter-clockwise."""
    L = leg
    return [(0.0, t - L), (0.0, 2 * t), (-L, 2 * t), (-L, 0.0), (-t, 0.0), (-t, t - L)]


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
    c2 = max(abs(max(zs) - p["cz"]), abs(min(zs) - p["cz"]))   # extreme fibres from the centroid
    c3 = max(abs(max(ys) - p["cy"]), abs(min(ys) - p["cy"]))
    s33, s22 = i33 / c2, i22 / c3
    return {
        "Shape": "General", "t3": depth, "t2": width,
        "Area": p["A"], "TorsConst": j, "I33": i33, "I22": i22, "I23": p["Iyz"],
        "AS2": as2, "AS3": as3,
        "S33": s33, "S22": s22, "Z33": s33, "Z22": s22,
        "R33": math.sqrt(i33 / p["A"]), "R22": math.sqrt(i22 / p["A"]),
    }
