#!/usr/bin/env python3
"""Convert SAP2000 .s2k text into the viewer's FEAV v3 .bin format.

The viewer in sample/ was written for STAAD and reads a per-corner field
binary (see sample/README.md).  This script is the SAP-side writer for that
same format, so the STAAD viewer opens SAP models unchanged.

Inputs
------
model.s2k        geometry, always required -- the file sap/build_tank.py wrote
results.s2k      OPTIONAL, exported from SAP after running the analysis:
                 File > Export > SAP2000 .s2k Text File, ticking
                   "Element Forces - Area Shells"   (set output at joints, not
                                                     just element centres)
                   "Joint Displacements"
                 Results may live in the same file as the geometry; pass it
                 twice, or just pass the one file.

Per-corner, not averaged: SAP writes one result row per element per joint, and
that is exactly what the viewer's bilinear shader wants.  Nodal averaging is
deliberately not done here -- the viewer smooths on request instead.

With no results file, the model still opens: a single "Hydrostatic head" field
is written from the joint pattern, which is enough to eyeball the mesh and the
pressure distribution before the first analysis run.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import struct
from pathlib import Path

MAGIC = 0x46454156  # 'FEAV'
VERSION = 3
HEADER_FIELDS = 15
HEADER_BYTES = HEADER_FIELDS * 4  # 60
MAX_CORNERS = 4

# SAP shell component -> viewer component, in the STAAD viewer's own order
# (shears, axials, moments) so the dropdown reads the same for both solvers.
FORCE_COMPONENTS = [
    ("V13", "Shear X", "kip/ft"),
    ("V23", "Shear Y", "kip/ft"),
    ("F12", "Shear IP", "kip/ft"),
    ("F11", "Axial X (hoop)", "kip/ft"),
    ("F22", "Axial Y (meridional)", "kip/ft"),
    ("M11", "Moment X", "kip-ft/ft"),
    ("M22", "Moment Y", "kip-ft/ft"),
    ("M12", "Moment IP", "kip-ft/ft"),
]
# SAP reports joint displacements in each joint's LOCAL axes.  That frame is
# not written out: it is local only where local axes happen to be assigned
# (here, the base ring alone), so a field built from it mixes two frames and
# is misleading anywhere else.  These keys are just what we read from the
# table before rotating to global.
DISP_COMPONENTS = [
    ("U1", "Translation X", "ft"),
    ("U2", "Translation Y", "ft"),
    ("U3", "Translation Z", "ft"),
    ("R1", "Rotation X", "rad"),
    ("R2", "Rotation Y", "rad"),
    ("R3", "Rotation Z", "rad"),
]
GLOBAL_DISP_COMPONENTS = [
    ("U1", "Translation GX", "ft"),
    ("U2", "Translation GY", "ft"),
    ("U3", "Translation GZ", "ft"),
    ("R1", "Rotation GX", "rad"),
    ("R2", "Rotation GY", "rad"),
    ("R3", "Rotation GZ", "rad"),
]

# Cylindrical components, resolved from the global vectors and each joint's
# own position about the model's vertical axis.  Meaningful at EVERY joint,
# unlike SAP's local frame, so this is what a settlement pattern should be
# read from.  Vertical is already global Z, so there is no cylindrical
# counterpart for it.
#
# Rotations resolve the same way: "Rotation R" is rotation ABOUT the radial
# axis and "Rotation T" is rotation about the tangential axis -- the latter
# being the meridional bending rotation, the one that goes non-zero when a
# base is fixed rather than released.
CYL_DISP_COMPONENTS = [
    (0, "Translation R", "ft"),    # radial, + outward
    (0, "Translation T", "ft"),    # tangential, + counter-clockwise from +Z
    (3, "Rotation R", "rad"),      # about the radial axis
    (3, "Rotation T", "rad"),      # about the tangential axis
]

_PAIR = re.compile(r'([A-Za-z0-9_#]+)=("([^"]*)"|\S*)')

# Tables read from the model file only, never merged from a second input.
GEOMETRY_TABLES = {
    "JOINT COORDINATES",
    "CONNECTIVITY - AREA",
    "JOINT PATTERN ASSIGNMENTS",
}


def parse_s2k(text: str) -> dict[str, list[dict[str, str]]]:
    """Parse .s2k tables into {TABLE NAME: [row dicts]}, keys upper-cased.

    Quoted values may contain spaces, and a trailing underscore continues a
    row onto the next line -- both are why this scans pairs instead of
    splitting on whitespace.
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
            current.append({k.upper(): (q if q else v)
                            for k, v, q in _PAIR.findall(line)})
    return tables


def _num(v, default=math.nan) -> float:
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


class Model:
    """Nodes, elements and per-corner fields, ready to serialise."""

    def __init__(self, tables):
        self.tables = tables
        self.node_ids: list[str] = []
        self.node_xyz: list[tuple[float, float, float]] = []
        self.node_index: dict[str, int] = {}
        for r in tables.get("JOINT COORDINATES", []):
            self.node_index[r["JOINT"]] = len(self.node_ids)
            self.node_ids.append(r["JOINT"])
            self.node_xyz.append((_num(r.get("XORR")), _num(r.get("Y")),
                                  _num(r.get("Z"))))

        self.elem_ids: list[str] = []
        self.elem_nodes: list[list[int]] = []
        self.elem_index: dict[str, int] = {}
        for r in tables.get("CONNECTIVITY - AREA", []):
            n = int(_num(r.get("NUMJOINTS"), 4))
            idx = [self.node_index[r[f"JOINT{i}"]] for i in range(1, n + 1)
                   if r.get(f"JOINT{i}") in self.node_index]
            if len(idx) < 3:
                continue
            self.elem_index[r["AREA"]] = len(self.elem_ids)
            self.elem_ids.append(r["AREA"])
            self.elem_nodes.append(idx)

        if not self.node_ids or not self.elem_ids:
            raise ValueError("no JOINT COORDINATES / CONNECTIVITY - AREA found")

    def joint_local_frames(self) -> dict[str, tuple[tuple[float, ...], ...]]:
        """Joint label -> 3x3 rotation taking LOCAL vectors to GLOBAL.

        SAP reports joint displacements in the joint's LOCAL axes, so every
        joint carrying a local-axis assignment (for us: the base ring, rotated
        so local Y is radial) comes out in a different frame from the rest of
        the model.  Feeding that straight to a deformed shape makes the base
        ring displace in one global direction instead of radially outward.

        Local axes are built from global by rotating A about Z(3), then B
        about the new Y(2), then C about the new X(1) -- so the matrix whose
        COLUMNS are the local axes in global coordinates is Rz(A)Ry(B)Rx(C),
        and global = M . local.
        """
        frames = {}
        for r in self.tables.get("JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL", []):
            a = math.radians(_num(r.get("ANGLEA"), 0.0) or 0.0)
            bb = math.radians(_num(r.get("ANGLEB"), 0.0) or 0.0)
            c = math.radians(_num(r.get("ANGLEC"), 0.0) or 0.0)
            ca, sa = math.cos(a), math.sin(a)
            cb, sb = math.cos(bb), math.sin(bb)
            cc, sc = math.cos(c), math.sin(c)
            # Rz(a) . Ry(b) . Rx(c)
            m = (
                (ca * cb, ca * sb * sc - sa * cc, ca * sb * cc + sa * sc),
                (sa * cb, sa * sb * sc + ca * cc, sa * sb * cc - ca * sc),
                (-sb,     cb * sc,                cb * cc),
            )
            frames[r["JOINT"]] = m
        return frames

    def corner_slot(self, elem_i: int, joint_label: str) -> int | None:
        """Which corner of this element a result row belongs to."""
        node_i = self.node_index.get(joint_label)
        if node_i is None:
            return None
        nodes = self.elem_nodes[elem_i]
        return nodes.index(node_i) if node_i in nodes else None


def build_model_fields(model: Model):
    """Fields describing the MODEL itself, for reviewing it before a run.

    Returns (component metadata, per-corner value arrays indexed
    [elem * MAX_CORNERS + corner]).  These do not vary with load case, so the
    same values are written into every load-case plane -- the cost is a few
    hundred KB and it means the model-check fields stay selectable while you
    are looking at results, not just on an unanalysed model.

    They carry kind "model", which the viewer treats as a generic field and
    groups on its own in the component dropdown.
    """
    n_slots = len(model.elem_ids) * MAX_CORNERS

    # --- per-joint -------------------------------------------------------
    head = {r["JOINT"]: _num(r.get("VALUE"), 0.0)
            for r in model.tables.get("JOINT PATTERN ASSIGNMENTS", [])}

    restrained = {}
    for r in model.tables.get("JOINT RESTRAINT ASSIGNMENTS", []):
        restrained[r["JOINT"]] = sum(
            1 for k in ("U1", "U2", "U3", "R1", "R2", "R3")
            if str(r.get(k, "")).lower().startswith("y"))

    axis_angle = {r["JOINT"]: _num(r.get("ANGLEA"), 0.0)
                  for r in model.tables.get(
                      "JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL", [])}

    # --- per-element -----------------------------------------------------
    section_of = {r["AREA"]: r.get("SECTION")
                  for r in model.tables.get("AREA SECTION ASSIGNMENTS", [])}
    thick_of = {r["SECTION"]: _num(r.get("THICKNESS"))
                for r in model.tables.get("AREA SECTION PROPERTIES", [])}

    cx = sum(p[0] for p in model.node_xyz) / len(model.node_xyz)
    cy = sum(p[1] for p in model.node_xyz) / len(model.node_xyz)

    fields = {name: [math.nan] * n_slots for name in (
        "head", "restraint", "angle", "has_axes", "thickness", "aspect",
        "normal")}

    for e, nodes in enumerate(model.elem_nodes):
        pts = [model.node_xyz[n] for n in nodes]

        thickness = thick_of.get(section_of.get(model.elem_ids[e]), math.nan)

        # Edge-length spread: a cheap mesh-quality read that flags stretched
        # elements, which is where shell results go soft.
        edges = [math.dist(pts[i], pts[(i + 1) % len(pts)])
                 for i in range(len(pts))]
        aspect = max(edges) / min(edges) if min(edges) > 1e-12 else math.nan

        # Does the element normal point outward?  Local 3 sets which face a
        # surface pressure pushes on, so a flipped element silently loads the
        # wall the wrong way.  +1 outward, -1 inward.
        u = [pts[1][i] - pts[0][i] for i in range(3)]
        v = [pts[-1][i] - pts[0][i] for i in range(3)]
        nrm = (u[1] * v[2] - u[2] * v[1],
               u[2] * v[0] - u[0] * v[2],
               u[0] * v[1] - u[1] * v[0])
        ex = sum(p[0] for p in pts) / len(pts) - cx
        ey = sum(p[1] for p in pts) / len(pts) - cy
        radial_dot = nrm[0] * ex + nrm[1] * ey
        normal_out = math.nan if abs(radial_dot) < 1e-12 else \
            (1.0 if radial_dot > 0 else -1.0)

        for k, node_i in enumerate(nodes[:MAX_CORNERS]):
            slot = e * MAX_CORNERS + k
            label = model.node_ids[node_i]
            fields["head"][slot] = head.get(label, 0.0)
            fields["restraint"][slot] = float(restrained.get(label, 0))
            # Zero-filled, NOT NaN: the viewer's bilinear quads treat any
            # no-data corner as a no-data ELEMENT, so NaN here would grey out
            # the whole base ring instead of highlighting it.  "Local axes
            # assigned" carries the which-joints question unambiguously,
            # since angle 0 is itself a legitimate value.
            fields["angle"][slot] = axis_angle.get(label, 0.0)
            fields["has_axes"][slot] = 1.0 if label in axis_angle else 0.0
            fields["thickness"][slot] = thickness
            fields["aspect"][slot] = aspect
            fields["normal"][slot] = normal_out

    metas = [
        {"name": "Hydrostatic head", "kind": "model", "unit": "kip/ft^2"},
        {"name": "Restrained DOF", "kind": "model", "unit": "count"},
        {"name": "Local axis angle A", "kind": "model", "unit": "deg"},
        {"name": "Local axes assigned", "kind": "model", "unit": "1/0"},
        {"name": "Wall thickness", "kind": "model", "unit": "ft"},
        {"name": "Mesh aspect ratio", "kind": "model", "unit": "-"},
        {"name": "Normal outward", "kind": "model", "unit": "+1/-1"},
    ]
    arrays = [fields[k] for k in
              ("head", "restraint", "angle", "has_axes", "thickness", "aspect",
               "normal")]
    return metas, arrays


def build_fields(model: Model):
    """Return (components, load_cases, data) for the corner-field block.

    data[lc][elem][corner][component], as a flat list of floats per LC.
    NaN marks no-data: the unused 4th slot of a triangle, and any corner a
    result row never covered.
    """
    force_rows = model.tables.get("ELEMENT FORCES - AREA SHELLS", [])
    disp_rows = model.tables.get("JOINT DISPLACEMENTS", [])

    model_metas, model_arrays = build_model_fields(model)

    if not force_rows:
        # Geometry-only: the model-check fields ARE the model, which is the
        # whole point of converting before a run.
        lcs = [{"name": "Model (no results)", "type": "primary"}]
        n_model = len(model_metas)
        plane = [math.nan] * (len(model.elem_ids) * MAX_CORNERS * n_model)
        for slot in range(len(model.elem_ids) * MAX_CORNERS):
            for c, arr in enumerate(model_arrays):
                plane[slot * n_model + c] = arr[slot]
        return model_metas, lcs, [plane]

    present = [c for c in FORCE_COMPONENTS if c[0] in force_rows[0]]
    has_disp = bool(disp_rows)
    frames = model.joint_local_frames()
    comps = [{"name": label, "kind": "stress", "unit": unit}
             for _, label, unit in present]
    if has_disp:
        comps += [{"name": label, "kind": "displacement", "unit": unit}
                  for _, label, unit in GLOBAL_DISP_COMPONENTS]
        comps += [{"name": label, "kind": "displacement", "unit": unit}
                  for _, label, unit in CYL_DISP_COMPONENTS]
    off_model = len(comps)
    comps += model_metas
    n_comp = len(comps)

    # Load cases in first-seen order, so the dropdown matches the SAP run.
    cases: list[str] = []
    for r in force_rows:
        c = r.get("OUTPUTCASE") or r.get("CASE") or "UNNAMED"
        if c not in cases:
            cases.append(c)

    stride_e = MAX_CORNERS * n_comp
    planes = [[math.nan] * (len(model.elem_ids) * stride_e) for _ in cases]
    case_i = {c: i for i, c in enumerate(cases)}

    # Model-check fields are load-case independent; repeat them in every plane
    # so they stay selectable while results are showing.
    for p in planes:
        for slot in range(len(model.elem_ids) * MAX_CORNERS):
            base = (slot // MAX_CORNERS) * stride_e + (slot % MAX_CORNERS) * n_comp
            for c, arr in enumerate(model_arrays):
                p[base + off_model + c] = arr[slot]

    for r in force_rows:
        e = model.elem_index.get(r.get("AREA") or r.get("AREAELEM"))
        if e is None:
            continue
        k = model.corner_slot(e, r.get("JOINT", ""))
        if k is None:
            continue
        p = planes[case_i[r.get("OUTPUTCASE") or r.get("CASE") or "UNNAMED"]]
        base = e * stride_e + k * n_comp
        for c, (sap_key, _, _) in enumerate(present):
            p[base + c] = _num(r.get(sap_key), math.nan)

    if has_disp:
        # Joint displacements are per node; fan them out to every corner that
        # references that node.
        by_case_joint = {}
        for r in disp_rows:
            c = r.get("OUTPUTCASE") or r.get("CASE") or "UNNAMED"
            by_case_joint.setdefault(c, {})[r.get("JOINT")] = r
        off_global = len(present)
        off_cyl = off_global + len(GLOBAL_DISP_COMPONENTS)

        # The tank axis: the centroid of the joints in plan. Taken from the
        # model rather than assumed to be (0,0), so a model built off-origin
        # still resolves radial correctly.
        cx = sum(p[0] for p in model.node_xyz) / len(model.node_xyz)
        cy = sum(p[1] for p in model.node_xyz) / len(model.node_xyz)

        def to_global(r, joint_label):
            """(U1,U2,U3,R1,R2,R3) rotated out of the joint's local frame."""
            vals = [_num(r.get(k), math.nan) for k, _, _ in DISP_COMPONENTS]
            m = frames.get(joint_label)
            if m is None:
                return vals
            out = list(vals)
            for base in (0, 3):   # translations, then rotations
                v = vals[base:base + 3]
                if any(math.isnan(x) for x in v):
                    continue
                for i in range(3):
                    out[base + i] = sum(m[i][j] * v[j] for j in range(3))
            return out
        for c_name, i in case_i.items():
            rows = by_case_joint.get(c_name)
            if not rows:
                continue
            p = planes[i]
            for e, nodes in enumerate(model.elem_nodes):
                for k, node_i in enumerate(nodes[:MAX_CORNERS]):
                    label = model.node_ids[node_i]
                    r = rows.get(label)
                    if not r:
                        continue
                    base = e * stride_e + k * n_comp
                    gvals = to_global(r, label)
                    for c, v in enumerate(gvals):
                        p[base + off_global + c] = v

                    # Cylindrical, from the global vectors and this joint's
                    # angle about the axis. A joint ON the axis has no
                    # direction to resolve against -- leave it no-data
                    # rather than inventing a zero.
                    x, y, _z = model.node_xyz[node_i]
                    dx, dy = x - cx, y - cy
                    rad = math.hypot(dx, dy)
                    ct, st = (dx / rad, dy / rad) if rad > 1e-9 else (math.nan,) * 2
                    for c, (src, label_, _u) in enumerate(CYL_DISP_COMPONENTS):
                        vx, vy = gvals[src], gvals[src + 1]
                        if math.isnan(vx) or math.isnan(vy) or math.isnan(ct):
                            p[base + off_cyl + c] = math.nan
                        elif label_.endswith(" R"):
                            p[base + off_cyl + c] = vx * ct + vy * st
                        else:
                            p[base + off_cyl + c] = -vx * st + vy * ct

    lcs = [{"name": c, "type": "primary"} for c in cases]
    return comps, lcs, planes


def write_bin(model: Model, path: Path) -> dict:
    comps, lcs, planes = build_fields(model)
    n_nodes, n_elems, n_lc = len(model.node_ids), len(model.elem_ids), len(planes)
    n_comp = len(comps)

    meta = {"loadCases": lcs, "components": comps}
    # Match the GLOBAL translation names exactly: the deformed shape has to be
    # driven by the global set, and the local set's names also begin with
    # "Translation", so a prefix test silently finds six and gives up.
    global_names = [label for _, label, _ in GLOBAL_DISP_COMPONENTS[:3]]
    disp_idx = [i for i, c in enumerate(comps) if c["name"] in global_names]
    if len(disp_idx) == 3:
        meta["displacementVector"] = disp_idx
    meta_bytes = json.dumps(meta).encode("utf-8")
    meta_bytes += b" " * (-len(meta_bytes) % 4)  # keep later blocks 4-aligned

    meta_off = HEADER_BYTES
    nodes_off = meta_off + len(meta_bytes)
    elems_off = nodes_off + n_nodes * 3 * 4
    node_id_off = elems_off + n_elems * 5 * 4
    elem_id_off = node_id_off + n_nodes * 4
    field_off = elem_id_off + n_elems * 4

    # Real SAP labels are strings; the ID tables are uint32.  Fall back to a
    # 1-based ordinal when a label is not numeric, so "find by ID" still works
    # on our generated models and never writes garbage for renamed ones.
    def as_u32(label: str, ordinal: int) -> int:
        try:
            v = int(label)
            return v if 0 <= v < 2**32 else ordinal
        except ValueError:
            return ordinal

    with path.open("wb") as fh:
        fh.write(struct.pack(
            "<15I", MAGIC, VERSION, HEADER_BYTES, n_nodes, n_elems, n_lc,
            n_comp, MAX_CORNERS, meta_off, len(meta_bytes), nodes_off,
            elems_off, node_id_off, elem_id_off, field_off))
        fh.write(meta_bytes)
        for x, y, z in model.node_xyz:
            fh.write(struct.pack("<3f", x, y, z))
        for nodes in model.elem_nodes:
            n = min(len(nodes), MAX_CORNERS)
            rec = [n] + list(nodes[:n]) + [0] * (MAX_CORNERS - n)
            fh.write(struct.pack("<5I", *rec))
        for i, label in enumerate(model.node_ids):
            fh.write(struct.pack("<I", as_u32(label, i + 1)))
        for i, label in enumerate(model.elem_ids):
            fh.write(struct.pack("<I", as_u32(label, i + 1)))
        for plane in planes:
            fh.write(struct.pack(f"<{len(plane)}f", *plane))

    return {"nodes": n_nodes, "elements": n_elems, "load_cases": n_lc,
            "local_axis_joints": len(model.joint_local_frames()),
            "components": [c["name"] for c in comps],
            "case_names": [c["name"] for c in lcs]}


def resolve_inputs(inputs: list[Path]) -> tuple[list[Path], Path]:
    """Expand a model folder into (files to read, default output .bin).

    Given models/tank_hoop/, the model is tank_hoop.s2k -- named for the
    folder, the way build_tank.py writes it -- and every OTHER .s2k/.$2k
    beside it is treated as an exported results file, whatever it is called.
    Explicit file arguments are passed through untouched.
    """
    if len(inputs) != 1 or not inputs[0].is_dir():
        return inputs, inputs[0].with_suffix(".bin")

    folder = inputs[0]
    name = folder.resolve().name
    model = folder / f"{name}.s2k"
    if not model.exists():
        raise FileNotFoundError(
            f"{model} not found -- a model folder must hold <folder>.s2k")

    extras = sorted(f for f in folder.iterdir()
                    if f.suffix.lower() in (".s2k", ".$2k") and f != model)
    return [model] + extras, folder / f"{name}.bin"


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("inputs", nargs="+", type=Path,
                   help="a model folder (models/<name>/), or a model .s2k plus "
                        "any exported results .s2k")
    p.add_argument("-o", "--out", type=Path,
                   help="output .bin (default: alongside the first input)")
    a = p.parse_args(argv)

    try:
        sources, default_out = resolve_inputs(a.inputs)
    except FileNotFoundError as exc:
        p.error(str(exc))

    tables: dict[str, list[dict[str, str]]] = {}
    for i, src in enumerate(sources):
        if not src.exists():
            p.error(f"{src}: no such file")
        for name, rows in parse_s2k(src.read_text(errors="replace")).items():
            # Geometry comes from the model file ALONE.  SAP leaves its own
            # <name>.$2k dump beside the model and result exports often carry
            # the model tables too; merging either would append a second copy
            # of every joint and element -- a doubled node count that still
            # "works" and quietly halves nothing you would notice on screen.
            if i > 0 and name in GEOMETRY_TABLES:
                continue
            tables.setdefault(name, []).extend(rows)

    try:
        model = Model(tables)
    except ValueError as exc:
        p.error(f"{sources[0]}: {exc}")

    if len(sources) > 1:
        print("reading " + ", ".join(s.name for s in sources))
    out = a.out or default_out
    info = write_bin(model, out)
    print(f"{out}: {info['nodes']} nodes, {info['elements']} elements, "
          f"{info['load_cases']} load case(s) "
          f"[{', '.join(info['case_names'])}], "
          f"{len(info['components'])} components")
    if info["local_axis_joints"] and "Translation GX" in info["components"]:
        print(f"  rotated {info['local_axis_joints']} joint(s) with local axes "
              f"back to global for the displacement vector")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
