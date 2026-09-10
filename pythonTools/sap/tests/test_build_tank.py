"""tankbuilder tests.  Run from pythonTools/sap:  python -m pytest -q

The golden test is the contract: tests/golden/example.s2k is what the writer
produced for configs/example.toml on 2026-09-03, with the writer that was
byte-identical to the file SAP2000 v25 imported and ran (the SapViewer
tank_hoop model). Any change to the writer must keep it byte-identical or
regenerate it on purpose:  python build_tank.py configs/example.toml -o tests/golden/example.s2k
Other configs and goldens are gitignored (project data).
"""

from __future__ import annotations

import math
import subprocess
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT))

from tankbuilder import Course, Dent, TankModel, TankSpec, load_config, parse_s2k  # noqa: E402
from tankbuilder.s2k import outlines_text, s2k_text  # noqa: E402
from tankbuilder.section import polygon_properties, eave_ring_outline, eave_ring_section  # noqa: E402
from tankbuilder.spec import spec_from_dict  # noqa: E402

CONFIGS = ROOT / "configs"
GOLDEN = HERE / "golden"


# --- golden -----------------------------------------------------------------

def test_example_matches_golden():
    spec, _ = load_config(CONFIGS / "example.toml")
    m = TankModel(spec)
    assert s2k_text(m) == (GOLDEN / "example.s2k").read_text()
    assert outlines_text(m) == (GOLDEN / "example.outlines.txt").read_text()


def test_cli_writes_the_same_file(tmp_path):
    out = tmp_path / "t.s2k"
    r = subprocess.run([sys.executable, str(ROOT / "build_tank.py"),
                        str(CONFIGS / "example.toml"), "-o", str(out)],
                       capture_output=True, text=True, check=True)
    assert ("1371 joints, 1152 shells, 2 course(s), baseplate (216 shells), "
            "roof (216 shells, rise 10.53 ft, 36 ring frames), base released radially, "
            "217 gap links on ground (ks 100 kip/ft^3), ring wall 1 x 3 ft (gap: 36 contact + 36 soil links), 1 dent(s)") in r.stdout
    assert out.read_text() == (GOLDEN / "example.s2k").read_text()


# --- config -----------------------------------------------------------------

def test_example_config_loads_and_builds():
    spec, out = load_config(CONFIGS / "example.toml")
    assert out.name == "example.s2k" and out.parent.name == "example"
    assert (spec.base_local_axes, spec.release_radial) == (True, True)
    assert spec.height == 40.0 and len(spec.courses) == 2 and spec.course_divisions() == [10, 10]
    m = TankModel(spec)
    assert len(m.ids.block("joint", "wall")) == 36 * 21 and len(m.ids.block("area", "wall")) == 36 * 20
    assert len(m.joints) == 36 * 21 + 2 * (5 * 36 + 1) + (5 * 36 + 1 + 36) + 36   # + ground joints + ring wall ground
    assert len(m.areas) == 36 * 20 + 2 * 6 * 36
    assert m.sections == {"WALL_T1": 0.0208333, "WALL_T2": 0.015625, "BASEPLATE": 0.0208333, "ROOF": 0.0208333}
    assert "END TABLE DATA" in s2k_text(m)


@pytest.mark.parametrize("raw, msg", [
    ({"geometry": {"radius": 1, "hieght": 2}}, "unknown key 'hieght'"),
    ({"lids": {}}, "unknown config section"),
    ({"supports": {"release_radial": True}}, "needs supports.base_local_axes"),
    ({"mesh": {"n_theta": 2}}, "n_theta must be at least 3"),
    ({"fluid": {"load_mode": "magic"}}, "load_mode"),
    ({"output": {"file": "x"}}, "unknown key(s) in [output]"),
    ({"courses": [{"height": 1, "thick": 2}]}, "unknown key(s) in courses[1]"),
    ({"courses": [{"height": 1}]}, "courses[1] needs height and thickness"),
    ({"courses": []}, "given but empty"),
    ({"courses": {"height": 1, "thickness": 1}}, "must be an array of tables"),
    ({"geometry": {"thickness": 1}, "courses": [{"height": 1, "thickness": 1}]}, "not both"),
    ({"geometry": {"height": 5}, "courses": [{"height": 1, "thickness": 1}]}, "courses sum to 1"),
    ({"courses": [{"height": 1, "thickness": 1, "divisions": 0}]}, "divisions must be at least 1"),
])
def test_config_rejects_typos_and_bad_values(raw, msg):
    with pytest.raises(ValueError, match=msg.replace("(", r"\(").replace(")", r"\)").replace("[", r"\[").replace("]", r"\]")):
        spec_from_dict(raw)


def test_output_name_defaults_to_config_stem(tmp_path):
    cfg = tmp_path / "mytank.toml"
    cfg.write_text("[geometry]\nradius = 10\n")
    spec, out = load_config(cfg)
    assert spec.radius == 10 and out == (tmp_path / "../models/mytank/mytank.s2k").resolve()


# --- model conventions --------------------------------------------------------

def small():
    return TankModel(TankSpec(radius=10.0, height=8.0, n_theta=8, n_z=4))


def test_ids_are_contiguous_blocks_per_kind():
    m = small()
    assert m.ids.block("joint", "wall") == range(1, 41)
    assert m.ids.block("area", "wall") == range(1, 33)
    assert m.ids.claim("joint", "base", 5) == range(41, 46)     # next part continues
    assert m.ids.claim("frame", "ringwall", 8) == range(1, 9)   # frames number from 1


def test_numbering_convention():
    m = small()
    assert len(m.joints) == 8 * 5 and len(m.areas) == 8 * 4
    assert m.joint_id(0, 0) == 1 and m.joint_id(0, 8) == 1          # wraps
    assert m.joint_id(1, 0) == 9 and m.joint_id(4, 7) == 40
    assert m.base_joints == list(range(1, 9)) and m.top_joints == list(range(33, 41))
    assert m.row_areas(0) == list(range(1, 9)) and m.row_areas(3) == list(range(25, 33))
    assert m.areas[1] == (1, 2, 10, 9)


def test_area_normals_point_outward():
    """Joint order (i,k) (i+1,k) (i+1,k+1) (i,k+1): local 3 = radial outward."""
    m = small()
    for aid, (j1, j2, j3, j4) in m.areas.items():
        p1, p2, p4 = (m.joints[j] for j in (j1, j2, j4))
        u = [p2[k] - p1[k] for k in range(3)]
        v = [p4[k] - p1[k] for k in range(3)]
        n = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
        cx = sum(m.joints[j][0] for j in (j1, j2, j3, j4)) / 4
        cy = sum(m.joints[j][1] for j in (j1, j2, j3, j4)) / 4
        assert n[0] * cx + n[1] * cy > 0, aid


# --- courses -------------------------------------------------------------------

def courses_model(**kw):
    spec = TankSpec(radius=10.0, n_theta=4, n_z=6, height=12.0,
                    courses=(Course(3.0, 0.04), Course(4.0, 0.03), Course(5.0, 0.04)), **kw)
    return TankModel(spec)


def test_courses_put_a_ring_on_every_boundary():
    m = courses_model()                            # n_z 6 over 12 ft -> 2 + 2 + 2 rows... by height
    assert m.spec.course_divisions() == [2, 2, 2]  # round(6*3/12)=2, round(6*4/12)=2, round(6*5/12)=2
    assert m.z_levels == pytest.approx([0, 1.5, 3, 5, 7, 9.5, 12])
    assert m.n_rows == 6 and len(m.joints) == 4 * 7 and m.top_joints == list(range(25, 29))
    assert m.row_course == [0, 0, 1, 1, 2, 2]


def test_courses_share_sections_by_thickness_and_group_by_course():
    m = courses_model()
    assert m.sections == {"WALL_T1": 0.04, "WALL_T2": 0.03}     # 1st and 3rd course share
    assert m.course_section == ["WALL_T1", "WALL_T2", "WALL_T1"]
    assert [m.area_section[a] for a in (1, 9, 17)] == ["WALL_T1", "WALL_T2", "WALL_T1"]
    g = m.groups()
    assert list(g) == ["WALL", "COURSE_01", "COURSE_02", "COURSE_03", "BASE_RING", "TOP_RING"]
    assert g["COURSE_02"] == (list(range(9, 17)), [], [])
    t = parse_s2k(s2k_text(m))
    assert [r["SECTION"] for r in t["AREA SECTION PROPERTIES"]] == ["WALL_T1", "WALL_T2"]
    assert {r["AREA"]: r["SECTION"] for r in t["AREA SECTION ASSIGNMENTS"]}["9"] == "WALL_T2"


def test_course_divisions_explicit_and_at_least_one():
    spec = TankSpec(radius=10.0, n_theta=4, n_z=4, height=12.0,
                    courses=(Course(0.1, 0.04), Course(11.9, 0.03, divisions=7)))
    assert spec.course_divisions() == [1, 7]       # 4*0.1/12 rounds to 0 -> 1
    m = TankModel(spec)
    assert m.n_rows == 8 and m.z_levels[1] == pytest.approx(0.1) and m.z_levels[-1] == 12.0


def test_uneven_rows_keep_joint_forces_statically_equivalent():
    m = courses_model(load_mode="joints")
    total = sum(m.joint_radial_forces().values())
    assert total == pytest.approx(0.0624 * 12 ** 2 / 2 * 2 * math.pi * 10, rel=1e-9)


def test_single_thickness_config_is_one_course():
    spec, _ = load_config(CONFIGS / "example.toml")
    single = TankSpec(radius=10.0, height=8.0, thickness=0.02)
    assert single.plate_courses == (Course(8.0, 0.02),)
    assert spec.plate_courses == spec.courses


def test_hydrostatics():
    m = small()                                   # gamma 0.0624, H 8
    assert m.hydro_value(1) == pytest.approx(0.0624 * 8)
    assert m.hydro_value(33) == 0.0               # top ring
    assert m.area_pressure(1) == pytest.approx(0.0624 * 7)   # centroid z = 1
    assert m.area_pressure(25) == pytest.approx(0.0624 * 1)  # top course
    # joint-force fallback integrates to gamma*H^2/2 per unit circumference
    total = sum(m.joint_radial_forces().values())
    assert total == pytest.approx(0.0624 * 8 ** 2 / 2 * 2 * math.pi * 10, rel=1e-9)


def test_partial_fill_zeroes_dry_joints():
    m = TankModel(TankSpec(radius=10.0, height=8.0, n_theta=8, n_z=4, fill_fraction=0.5))
    assert m.hydro_value(m.joint_id(2, 0)) == 0.0            # at the surface
    assert m.hydro_value(m.joint_id(1, 0)) == pytest.approx(0.0624 * 2)


def test_base_local_axes_put_local_y_radially_outward():
    m = TankModel(TankSpec(radius=10.0, height=8.0, n_theta=8, n_z=4, base_local_axes=True))
    for j in m.base_joints:
        a = math.radians(m.base_local_angle_deg(j))
        # local Y = Rz(A) * (0,1,0) = (-sin A, cos A); must align with the joint's radial
        x, y, _ = m.joints[j]
        assert (-math.sin(a) * x + math.cos(a) * y) / 10.0 == pytest.approx(1.0)


# --- written tables round-trip -------------------------------------------------

def test_written_tables_parse_back():
    spec = TankSpec(radius=10.0, height=8.0, n_theta=8, n_z=4,
                    base_local_axes=True, release_radial=True)
    t = parse_s2k(s2k_text(TankModel(spec)))
    assert len(t["JOINT COORDINATES"]) == 40 and len(t["CONNECTIVITY - AREA"]) == 32
    assert t["PROGRAM CONTROL"][0]["CURRUNITS"] == "Kip, ft, F"
    r = {row["JOINT"]: row for row in t["JOINT RESTRAINT ASSIGNMENTS"]}
    assert len(r) == 8 and r["1"]["U2"] == "No" and r["1"]["U1"] == "Yes"
    assert len(t["JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL"]) == 8
    assert float(t["JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL"][0]["ANGLEA"]) == -90.0
    assert len(t["AREA LOADS - SURFACE PRESSURE"]) == 32
    assert {row["CASE"] for row in t["LOAD CASE DEFINITIONS"]} == {"DEAD", "HYDRO"}


def test_joint_load_mode_writes_forces_not_pressures():
    spec = TankSpec(radius=10.0, height=8.0, n_theta=8, n_z=4, load_mode="joints")
    t = parse_s2k(s2k_text(TankModel(spec)))
    assert "AREA LOADS - SURFACE PRESSURE" not in t
    rows = t["JOINT LOADS - FORCE"]
    assert len(rows) == 32                         # top ring (zero force) skipped
    r0 = rows[0]
    assert float(r0["F2"]) == pytest.approx(0.0, abs=1e-12) and float(r0["F1"]) > 0


# --- baseplate / roof (polar caps) ----------------------------------------------

def capped(**kw):
    return TankModel(TankSpec(radius=10.0, height=8.0, n_theta=8, n_z=4,
                              baseplate=True, baseplate_n_r=3, baseplate_thickness=0.02,
                              roof=True, roof_crown_radius=16.0, roof_n_r=3, roof_thickness=0.02,
                              **kw))


def normal(m, aid):
    js = m.areas[aid]
    p1, p2, p3 = (m.joints[j] for j in js[:3])
    u = [p2[k] - p1[k] for k in range(3)]
    v = [p3[k] - p1[k] for k in range(3)]
    return (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])


def test_caps_claim_blocks_after_the_wall_and_share_the_rims():
    m = capped()                                     # wall: 40 joints, 32 areas
    assert m.ids.block("joint", "baseplate") == range(41, 41 + 2 * 8 + 1)   # 2 interior rings + centre
    assert m.ids.block("area", "baseplate") == range(33, 33 + 3 * 8)        # 2 quad rings + fan
    assert m.ids.block("joint", "roof") == range(58, 75) and m.ids.block("area", "roof") == range(57, 81)
    assert m.ids.block("frame", "roof_ring") == range(1, 9)
    # outer quads use the wall's own base / top joints, no duplicates
    outer = m.areas[33 + 8]                          # ring 2 -> rim
    assert outer[1] in m.base_joints and outer[2] in m.base_joints
    assert m.areas[57 + 8][1] in m.top_joints
    assert len(m.joints) == 40 + 17 + 17 and len(set(m.joints)) == len(m.joints)


def test_cap_topology_and_normals():
    m = capped()
    bp = m.baseplate_areas
    assert [len(m.areas[a]) for a in bp] == [4] * 16 + [3] * 8
    assert all(m.joints[j][2] == 0.0 for a in bp for j in m.areas[a])
    for a in bp:
        assert normal(m, a)[2] > 0, a                # local 3 up -> water on "Top"
    for a in m.roof_areas:
        assert normal(m, a)[2] > 0, a
    centre = m.ids.block("joint", "baseplate").start
    assert m.joints[centre] == (0.0, 0.0, 0.0)
    assert all(m.areas[a][0] == centre for a in bp[16:])


def test_roof_is_a_spherical_cap_on_the_top_ring():
    m = capped()                                     # R 10, H 8, crown 16
    rise = 16.0 - math.sqrt(16.0 ** 2 - 10.0 ** 2)
    assert m.roof_rise == pytest.approx(rise)
    assert m.roof_z(10.0) == pytest.approx(8.0)
    crown = m.ids.block("joint", "roof").start
    assert m.joints[crown] == pytest.approx((0.0, 0.0, 8.0 + rise))
    for j in m.cap_joints["roof"]:
        x, y, z = m.joints[j]
        assert math.hypot(x, y) ** 2 + (z - (8.0 + rise - 16.0)) ** 2 == pytest.approx(16.0 ** 2)


def test_roof_ring_frames_close_the_eave():
    m = capped()
    fr = [m.frames[f] for f in sorted(m.frames)]
    assert [i for i, _ in fr] == m.top_joints and fr[-1][1] == m.top_joints[0]
    assert set(m.frame_section.values()) == {"ROOF_RING"}
    sec = m.frame_sections["ROOF_RING"]
    assert sec["Shape"] == "General" and sec["t3"] == pytest.approx(0.28125) and sec["t2"] == pytest.approx(0.25)
    assert sec["Area"] == pytest.approx(3 * 0.25 * 0.03125 - 0.03125 ** 2)
    assert not capped(roof_ring=False).frames
    assert "ROOF_RING" in m.frame_outlines


def test_eave_ring_section_properties():
    leg, t = 0.25, 0.03125
    pts = eave_ring_outline(leg, t)
    p = polygon_properties(pts)
    assert len(pts) == 6
    assert p["A"] == pytest.approx(3 * leg * t - t * t)
    # anchored at the wall: the shell line (y = 0) is the inner face, outward is -y,
    # the down leg hangs below the wall top (z < 0); no upstanding lip
    ys = [y for y, _ in pts]; zs = [z for _, z in pts]
    assert max(ys) == 0.0 and min(ys) == pytest.approx(-leg)
    assert min(zs) == pytest.approx(t - leg) and max(zs) == pytest.approx(2 * t)
    assert p["Iyz"] != pytest.approx(0.0)                     # an L is asymmetric
    # hand check: 2t plate on top of the wall + the (leg - t) down leg
    plate = leg * (2 * t) ** 3 / 12 + 2 * leg * t * (t - p["cz"]) ** 2
    down = t * (leg - t) ** 3 / 12 + t * (leg - t) * ((t - leg) / 2 - p["cz"]) ** 2
    assert p["Iyy"] == pytest.approx(plate + down, rel=1e-9)
    g = eave_ring_section(leg, t)
    assert g["t3"] == pytest.approx(leg + t) and g["t2"] == pytest.approx(leg)
    assert g["I33"] == pytest.approx(p["Iyy"]) and g["I22"] == pytest.approx(p["Izz"])
    assert g["S33"] == pytest.approx(p["Iyy"] / max(abs(max(zs) - p["cz"]), abs(min(zs) - p["cz"])))
    assert g["TorsConst"] == pytest.approx(leg * (2 * t) ** 3 / 3 + (leg - t) * t ** 3 / 3)
    assert g["AS2"] == pytest.approx((leg - t) * t) and g["AS3"] == pytest.approx(2 * leg * t) and g["R33"] > 0
    # square: no product of inertia, equal moments
    sq = polygon_properties([(-1, -1), (1, -1), (1, 1), (-1, 1)])
    assert sq["A"] == 4 and sq["Iyy"] == pytest.approx(4 / 3) and sq["Iyz"] == pytest.approx(0)


def test_ringwall_frames_insert_at_top_centre():
    from tankbuilder.s2k import insertion_tables
    m = walled(ringwall_joints="top")
    rw = list(m.ids.block("frame", "ringwall"))
    assert all(m.frame_cardinal[f] == 8 for f in rw) and set(m.frame_cardinal) == set(rw)
    (name, rows), = insertion_tables(m)
    assert name == "FRAME INSERTION POINT ASSIGNMENTS" and len(rows) == len(rw)
    assert rows[0].startswith(f'   Frame={rw[0]}   CardinalPt="8 (top center)"   Mirror2=No')
    assert "Transform=No" in rows[0] and "CoordSys=Local" in rows[0]
    assert not insertion_tables(capped())            # no ring wall -> no table
    assert not insertion_tables(walled())            # "elevations": wall joint at the centroid, cardinal 10


def test_ringwall_joints_at_true_elevations():
    """Default layout since 2026-09-08: rim at the wall top, wall joint at -depth/2,
    ground joint at -depth, links with length (I below J), group RINGWALL_AXIS."""
    m = walled()
    for rim, w, g in zip(m.base_joints, m.ringwall_joints, m.ringwall_ground):
        x, y, z = m.joints[rim]
        assert m.joints[w] == (x, y, z - 1.5) and m.joints[g] == (x, y, z - 3.0)
    assert not m.frame_cardinal and not m.frame_transform
    g = m.groups()
    assert g["RINGWALL_AXIS"] == ([], m.ringwall_joints, []) and "RINGWALL_TOP" not in g
    top = walled(ringwall_joints="top")
    assert all(top.joints[w] == top.joints[r] == top.joints[gj]
               for r, w, gj in zip(top.base_joints, top.ringwall_joints, top.ringwall_ground))
    assert top.groups()["RINGWALL_TOP"] == ([], top.ringwall_joints, [])


def test_outlines_sidecar_written_beside_the_s2k(tmp_path):
    from tankbuilder import write_s2k
    m = capped()
    out = tmp_path / "t.s2k"
    write_s2k(m, out)
    side = tmp_path / "t.outlines.txt"
    assert side.exists()
    line = side.read_text().splitlines()[0]
    assert line.startswith("ROOF_RING: ") and len(line.split(": ")[1].split()) == 6
    assert outlines_text(m) == side.read_text()
    write_s2k(small(), out)                        # no frames -> stale sidecar removed
    assert not side.exists()


def test_baseplate_pressure_face_and_restraints():
    m = capped()
    t = parse_s2k(s2k_text(m))
    press = {r["AREA"]: r for r in t["AREA LOADS - SURFACE PRESSURE"]}
    assert press["1"]["FACE"] == "Bottom"
    for a in m.baseplate_areas:
        assert press[str(a)]["FACE"] == "Top" and float(press[str(a)]["PRESSURE"]) == pytest.approx(0.0624 * 8)
    assert not any(str(a) in press for a in m.roof_areas)
    rest = {r["JOINT"]: r for r in t["JOINT RESTRAINT ASSIGNMENTS"]}
    for j in m.baseplate_interior_joints:
        assert (rest[str(j)]["U1"], rest[str(j)]["U3"]) == ("No", "Yes")
    assert not any(str(j) in rest for j in m.cap_joints["roof"])


def test_cap_tables_and_groups_written():
    m = capped()
    t = parse_s2k(s2k_text(m))
    conn = {r["AREA"]: r for r in t["CONNECTIVITY - AREA"]}
    assert conn["33"]["NUMJOINTS"] == "4" and conn["56"]["NUMJOINTS"] == "3" and "JOINT4" not in conn["56"]
    assert [r["SECTION"] for r in t["AREA SECTION PROPERTIES"]] == ["WALL_T1", "BASEPLATE", "ROOF"]
    assert len(t["CONNECTIVITY - FRAME"]) == 8 and t["CONNECTIVITY - FRAME"][0]["JOINTI"] == "33"
    fs = t["FRAME SECTION PROPERTIES 01 - GENERAL"][0]
    assert fs["SECTIONNAME"] == "ROOF_RING" and fs["SHAPE"] == "General" and float(fs["I33"]) > 0
    assert t["FRAME SECTION ASSIGNMENTS"][0]["ANALSECT"] == "ROOF_RING"
    g = m.groups()
    assert list(g)[-3:] == ["BASEPLATE", "ROOF", "ROOF_RING"]
    assert g["ROOF_RING"] == ([], [], list(range(1, 9)))
    asg = [r for r in t["GROUPS 2 - ASSIGNMENTS"] if r["GROUPNAME"] == "ROOF_RING"]
    assert len(asg) == 8 and asg[0]["OBJECTTYPE"] == "Frame"
    plain = parse_s2k(s2k_text(small()))
    assert "CONNECTIVITY - FRAME" not in plain and "FRAME SECTION PROPERTIES 01 - GENERAL" not in plain


@pytest.mark.parametrize("raw, msg", [
    ({"roof": {"enabled": True, "crown_radius": 5}, "geometry": {"radius": 10}}, "crown_radius"),
    ({"baseplate": {"enabled": True, "n_r": 0}}, "n_r at least 1"),
    ({"roof": {"crownradius": 5}}, "unknown key 'crownradius'"),
])
def test_cap_config_validation(raw, msg):
    with pytest.raises(ValueError, match=msg):
        spec_from_dict(raw)


# --- foundation: ground joints + gap links ----------------------------------------

def gapped():
    return capped(foundation="gap", subgrade_modulus=100.0)


def test_gap_layer_has_a_fixed_ground_joint_under_every_baseplate_joint():
    m = gapped()                                     # baseplate: 17 interior + 8 rim = 25 joints
    assert len(m.ground_joints) == 25 and len(m.links) == 25
    assert m.ids.block("joint", "ground") == range(75, 100) and m.ids.block("link", "gap") == range(1, 26)
    for tj, gj in m.ground_of.items():
        assert m.joints[gj] == m.joints[tj]          # coincident
    assert set(m.ground_of) == set(m.baseplate_joints)
    for lid, (i, j) in m.links.items():
        assert i in m.ground_joints and j == [t for t, g in m.ground_of.items() if g == i][0]


def test_tributary_areas_sum_to_the_plate():
    m = gapped()                                     # R 10, n_r 3, n_theta 8
    total = sum(m.baseplate_tributary_area(j) for j in m.baseplate_joints)
    assert total == pytest.approx(math.pi * 100.0)
    dr = 10.0 / 3
    assert m.baseplate_tributary_area(m.ids.block("joint", "baseplate").start) == pytest.approx(math.pi * (dr / 2) ** 2)
    assert m.baseplate_tributary_area(m.base_joints[0]) == pytest.approx(math.pi * (100 - (10 - dr / 2) ** 2) / 8)
    props = m.link_props
    assert list(props) == ["GAP_R00", "GAP_R01", "GAP_R02", "GAP_R03"]
    assert props["GAP_R03"]["k"] == pytest.approx(100.0 * m.baseplate_tributary_area(m.base_joints[0]))
    assert not capped().links and not capped().ground_joints


def test_gap_tables_restraints_and_cases():
    m = gapped()
    t = parse_s2k(s2k_text(m))
    assert [r["LINK"] for r in t["LINK PROPERTY DEFINITIONS 01 - GENERAL"]] == ["GAP_R00", "GAP_R01", "GAP_R02", "GAP_R03"]
    gp = t["LINK PROPERTY DEFINITIONS 05 - GAP"][0]
    assert (gp["DOF"], gp["NONLINEAR"], gp["OPEN"]) == ("U1", "Yes", "0") and float(gp["TRANSK"]) > 0
    conn = t["CONNECTIVITY - LINK"]
    assert len(conn) == 25 and int(conn[0]["JOINTI"]) in m.ground_joints
    assert t["LINK PROPERTY ASSIGNMENTS"][0]["LINKPROP"] == "GAP_R00"
    rest = {r["JOINT"]: r for r in t["JOINT RESTRAINT ASSIGNMENTS"]}
    assert rest[str(m.base_joints[0])]["U3"] == "No" and rest[str(m.base_joints[0])]["U1"] == "Yes"
    assert not any(str(j) in rest for j in m.baseplate_interior_joints)
    g = rest[str(m.ground_joints[0])]
    assert all(g[d] == "Yes" for d in ("U1", "U2", "U3", "R1", "R2", "R3"))
    cases = {r["CASE"]: r for r in t["LOAD CASE DEFINITIONS"]}
    assert cases["NL_DEAD"]["TYPE"] == "NonStatic" and cases["NL_HYDRO"]["INITIALCOND"] == "NL_DEAD"
    assert len(t["CASE - STATIC 2 - NONLINEAR LOAD APPLICATION"]) == 2
    assert m.groups()["GROUND"] == ([], m.ground_joints, [])
    fixed = parse_s2k(s2k_text(capped()))
    assert "CONNECTIVITY - LINK" not in fixed and "NL_DEAD" not in {r["CASE"] for r in fixed["LOAD CASE DEFINITIONS"]}


# --- ring wall ------------------------------------------------------------------

def walled(**kw):
    return capped(foundation="gap", subgrade_modulus=100.0, ringwall=True,
                  ringwall_width=1.0, ringwall_depth=3.0, **kw)


def test_ringwall_frames_close_on_the_rims_ground_joints():
    m = walled()
    rw = m.ringwall_joints
    assert rw == [m.ground_of[j] for j in m.base_joints] and len(rw) == 8
    assert m.ids.block("frame", "ringwall") == range(9, 17)         # after the 8 roof-ring frames
    fr = [m.frames[f] for f in m.ids.block("frame", "ringwall")]
    assert [i for i, _ in fr] == rw and fr[-1][1] == rw[0]
    assert m.frame_sections["RINGWALL"] == {"Material": "CONC", "Shape": "Rectangular", "t3": 3.0, "t2": 1.0}
    arc = 2 * math.pi * 10 / 8
    assert m.ringwall_soil_k == pytest.approx(100.0 * 1.0 * arc)
    assert m.ringwall_contact_k == pytest.approx(m.concrete["E"] * 1.0 * arc / 3.0)
    assert set(m.plate_ground_joints).isdisjoint(rw) and len(m.plate_ground_joints) == 17
    # load path: rim joint -> GAP_CONTACT -> wall top -> GAP_SOIL -> fixed ground joint
    base = set(m.base_joints)
    rim = {m.links[l][1]: l for l in m.links if m.links[l][1] in base}
    assert len(rim) == 8 and all(m.link_prop[l] == "GAP_CONTACT" for l in rim.values())
    assert [m.links[rim[j]][0] for j in m.base_joints] == rw
    assert len(m.ringwall_ground) == 8 and all(m.joints[g][:2] == m.joints[w][:2] for g, w in zip(m.ringwall_ground, rw))
    soil = {m.links[l][1]: l for l in m.ids.block("link", "ringwall_gap")}
    assert [m.links[soil[w]][0] for w in rw] == m.ringwall_ground
    assert all(m.link_prop[l] == "GAP_SOIL" for l in soil.values())
    assert {"GAP_CONTACT", "GAP_SOIL"} <= set(m.link_props)
    assert not any(m.link_prop[l] == "GAP_R03" for l in m.links)      # rim ring prop replaced
    assert m.link_props["GAP_SOIL"]["k"] == pytest.approx(m.ringwall_soil_k)
    c = m.concrete
    assert c["E"] == pytest.approx(57 * math.sqrt(3000) * 144) and c["name"] == "CONC"
    assert capped().concrete is None


def test_ringwall_tables():
    m = walled()
    t = parse_s2k(s2k_text(m))
    mats = {r["MATERIAL"]: r for r in t["MATERIAL PROPERTIES 01 - GENERAL"]}
    assert mats["CONC"]["TYPE"] == "Concrete" and "A36" in mats
    secs = {r["SECTIONNAME"]: r for r in t["FRAME SECTION PROPERTIES 01 - GENERAL"]}
    assert secs["RINGWALL"]["MATERIAL"] == "CONC" and secs["RINGWALL"]["SHAPE"] == "Rectangular"
    assert secs["ROOF_RING"]["MATERIAL"] == "A36"
    rest = {r["JOINT"]: r for r in t["JOINT RESTRAINT ASSIGNMENTS"]}
    rw0 = str(m.ringwall_joints[0])
    assert (rest[rw0]["U1"], rest[rw0]["U2"], rest[rw0]["U3"], rest[rw0]["R3"]) == ("Yes", "No", "No", "No")
    g0 = str(m.plate_ground_joints[0])
    assert rest[g0]["U3"] == "Yes" and rest[g0]["R3"] == "Yes"
    gr0 = str(m.ringwall_ground[0])
    assert rest[gr0]["U1"] == "Yes" and rest[gr0]["U3"] == "Yes" and rest[gr0]["R3"] == "Yes"
    assert "JOINT SPRING ASSIGNMENTS 1 - UNCOUPLED" not in t
    axes = {r["JOINT"]: r for r in t["JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL"]}
    assert float(axes[rw0]["ANGLEA"]) == pytest.approx(m.base_local_angle_deg(m.base_joints[0]))
    gap = {r["LINK"]: r for r in t["LINK PROPERTY DEFINITIONS 05 - GAP"]}
    assert float(gap["GAP_SOIL"]["TRANSK"]) == pytest.approx(m.ringwall_soil_k)
    assert float(gap["GAP_CONTACT"]["TRANSK"]) == pytest.approx(m.ringwall_contact_k)
    g = m.groups()
    assert g["RINGWALL"] == ([], [], list(range(9, 17))) and g["RINGWALL_AXIS"] == ([], m.ringwall_joints, [])
    assert g["RINGWALL_GROUND"] == ([], m.ringwall_ground, []) and g["GROUND"] == ([], m.plate_ground_joints, [])
    fixed_m = walled(ringwall_support="fixed")
    fixed = parse_s2k(s2k_text(fixed_m))
    assert not fixed_m.ringwall_ground and "GAP_SOIL" not in fixed_m.link_props
    assert {r["JOINT"]: r for r in fixed["JOINT RESTRAINT ASSIGNMENTS"]}[rw0]["U3"] == "Yes"
    assert walled(ringwall_support="springs").spec.ringwall_support == "gap"     # old name still accepted


# --- shell local axes -------------------------------------------------------------

def test_shell_axes_meridional_everywhere():
    m = capped()
    assert set(m.area_local_angle) == set(m.areas)
    wall = set(m.ids.block("area", "wall"))
    assert all(m.area_local_angle[a] == 90.0 for a in wall)
    assert all(m.area_local_angle[a] == 90.0 for a in m.roof_areas)
    # baseplate: angle = azimuth of the element centroid, so local 1 is radial
    for a in m.baseplate_areas:
        js = m.areas[a]
        cx = sum(m.joints[j][0] for j in js) / len(js); cy = sum(m.joints[j][1] for j in js) / len(js)
        assert m.area_local_angle[a] == pytest.approx(math.degrees(math.atan2(cy, cx)))
        d = m.meridional_direction(a)
        assert d[2] == 0.0 and math.hypot(d[0], d[1]) == pytest.approx(1.0)
        assert d[0] * cx + d[1] * cy > 0            # outward
    for a in wall:
        assert m.meridional_direction(a) == (0.0, 0.0, 1.0)
    for a in m.roof_areas:
        d = m.meridional_direction(a)
        assert d[2] > 0 and math.sqrt(sum(c * c for c in d)) == pytest.approx(1.0)   # up the slope
    t = parse_s2k(s2k_text(m))
    rows = t["AREA LOCAL AXES ASSIGNMENTS 1 - TYPICAL"]
    assert len(rows) == len(m.areas) and rows[0]["ADVANCEAXES"] == "No"
    assert {int(r["AREA"]): float(r["ANGLE"]) for r in rows} == pytest.approx(m.area_local_angle)


# --- settlement ------------------------------------------------------------------

def trenched(**kw):
    return walled(settlement="trench", settlement_depth=0.5, settlement_width=10.0, **kw)


def test_trench_settlement_profile_and_tables():
    m = trenched()
    # parabola across the trench: full depth on the axis, zero at the edges, zero outside
    assert m.settlement_dz(0.0, 0.0) == pytest.approx(-0.5)
    assert m.settlement_dz(7.0, 0.0) == pytest.approx(-0.5)           # anywhere along the axis
    assert m.settlement_dz(0.0, 2.5) == pytest.approx(-0.5 * 0.75)
    assert m.settlement_dz(0.0, 5.0) == pytest.approx(0.0) and m.settlement_dz(0.0, 6.0) == 0.0
    # rotated 90 deg the trench runs along Y, offset shifts the axis along +X... (the +90 side)
    r = trenched(settlement_direction_deg=90.0, settlement_offset=3.0)
    assert r.settlement_dz(-3.0, 8.0) == pytest.approx(-0.5) and r.settlement_dz(2.0, 0.0) == pytest.approx(0.0)
    # every ground joint (plate + ring wall) has an entry; only those inside the trench move
    assert set(m.settlements) == set(m.plate_ground_joints) | set(m.ringwall_ground)
    moved = {j for j, v in m.settlements.items() if v != 0.0}
    assert moved and all(abs(m.joints[j][1]) < 5.0 for j in moved)
    assert all(abs(m.joints[j][1]) >= 5.0 for j in m.settlements if j not in moved)
    t = parse_s2k(s2k_text(m))
    pats = {r["LOADPAT"] for r in t["LOAD PATTERN DEFINITIONS"]}
    assert "SETTLE" in pats
    gd = t["JOINT LOADS - GROUND DISPLACEMENT"]
    assert len(gd) == len(moved) and all(r["LOADPAT"] == "SETTLE" and r["U1"] == "0" for r in gd)
    assert {int(r["JOINT"]) for r in gd} == moved
    cases = {r["CASE"]: r for r in t["LOAD CASE DEFINITIONS"]}
    assert cases["NL_SETTLE"]["INITIALCOND"] == "NL_HYDRO" and cases["NL_SETTLE"]["TYPE"] == "NonStatic"
    nl = {r["CASE"] for r in t["CASE - STATIC 2 - NONLINEAR LOAD APPLICATION"]}
    assert nl == {"NL_DEAD", "NL_HYDRO", "NL_SETTLE"}
    # none: no pattern, no table, no case
    t0 = parse_s2k(s2k_text(walled()))
    assert "JOINT LOADS - GROUND DISPLACEMENT" not in t0 and "NL_SETTLE" not in {r["CASE"] for r in t0["LOAD CASE DEFINITIONS"]}
    with pytest.raises(ValueError):
        capped(settlement="trench", settlement_depth=0.5, settlement_width=10.0)   # needs gap


def test_slope_settlement_profile():
    m = walled(settlement="slope", settlement_depth=0.5)          # R = 10
    assert m.settlement_dz(0.0, -3.0) == 0.0 and m.settlement_dz(5.0, 0.0) == 0.0     # the flat half, hinge line included
    assert m.settlement_dz(0.0, 5.0) == pytest.approx(-0.25)
    assert m.settlement_dz(3.0, 10.0) == pytest.approx(-0.5)                          # full depth at the tank edge
    assert m.settlement_dz(0.0, 11.0) == pytest.approx(-0.55)                         # ring wall just outside: a little more
    r = walled(settlement="slope", settlement_depth=0.5, settlement_direction_deg=90.0)   # hinge along Y: rises toward -X
    assert r.settlement_dz(-10.0, 2.0) == pytest.approx(-0.5) and r.settlement_dz(4.0, 0.0) == 0.0
    moved = {j for j, v in m.settlements.items() if v != 0.0}
    assert moved and all(m.joints[j][1] > 0 for j in moved) and all(m.joints[j][1] <= 0 for j in m.settlements if j not in moved)
    t = parse_s2k(s2k_text(m))
    assert len(t["JOINT LOADS - GROUND DISPLACEMENT"]) == len(moved)
    with pytest.raises(ValueError):
        walled(settlement="slope", settlement_depth=0.0)
    walled(settlement="slope", settlement_depth=0.5, settlement_width=0.0)            # width not needed for a slope


def test_settlement_audit_writes_csv_and_svg(tmp_path):
    import csv
    sys.path.insert(0, str(ROOT))
    from settlement_audit import write_audit
    m = trenched()
    csv_path, svg_path = write_audit(m, tmp_path, "t")
    rows = list(csv.DictReader(open(csv_path, newline="")))
    assert [c for c in rows[0]] == ["node", "x", "y", "z", "dx", "dy", "dz"]
    assert len(rows) == len(m.settlements)
    by = {int(r["node"]): r for r in rows}
    g0 = m.all_ground_joints[0]
    assert float(by[g0]["dz"]) == pytest.approx(m.settlements[g0]) and by[g0]["dx"] == "0" and by[g0]["dy"] == "0"
    assert float(by[g0]["x"]) == pytest.approx(m.joints[g0][0])
    svg = svg_path.read_text()
    assert svg.startswith("<svg") and "PLAN" in svg and "ELEVATION" in svg and svg.count("<circle") >= len(m.settlements)
    import xml.dom.minidom
    xml.dom.minidom.parseString(svg)


# --- dents ---------------------------------------------------------------------

def dented(**kw):
    dent = Dent(angle_deg=0.0, elevation=4.0, depth=0.2, width=6.0, height=4.0)
    return TankModel(TankSpec(radius=10.0, height=8.0, n_theta=8, n_z=4, dents=(dent,), **kw))


def test_dent_moves_the_centre_joint_by_its_depth_and_nothing_outside():
    m = dented()                                        # spoke 0 at theta 0, ring 2 at z = 4
    centre = m.joint_id(2, 0)
    x, y, z = m.joints[centre]
    assert (x, y, z) == pytest.approx((9.8, 0.0, 4.0))  # full depth at rho = 0
    assert m.dent_fraction(0, centre) == 1.0
    # spoke 1 is 7.85 ft of arc away: outside a 6 ft wide footprint
    assert m.dent_fraction(0, m.joint_id(2, 1)) == 0.0
    x1, y1, _ = m.joints[m.joint_id(2, 1)]
    assert math.hypot(x1, y1) == pytest.approx(10.0)
    # 2 ft above/below: rho = 1 -> zero, 1 ft above: cos^2(pi/4) = 0.5
    assert m.dent_fraction(0, m.joint_id(3, 0)) == pytest.approx(0.0, abs=1e-12)
    m2 = TankModel(TankSpec(radius=10.0, height=8.0, n_theta=8, n_z=8,
                            dents=(Dent(0.0, 4.0, 0.2, 6.0, 4.0),)))
    assert m2.dent_fraction(0, m2.joint_id(5, 0)) == pytest.approx(0.5)
    assert set(m.dent_joints[0]) == {centre}
    assert m.dent_areas(0) == [9, 16, 17, 24]           # the four shells sharing that joint
    assert m.groups()["DENT_01"] == ([9, 16, 17, 24], [], [])


def test_dent_wraps_around_theta_zero_and_keeps_ground_joints_coincident():
    dent = Dent(angle_deg=350.0, elevation=0.0, depth=0.1, width=8.0, height=4.0)
    m = TankModel(TankSpec(radius=10.0, height=8.0, n_theta=8, n_z=4, dents=(dent,),
                           baseplate=True, baseplate_n_r=2, foundation="gap"))
    j0 = m.joint_id(0, 0)                               # theta 0 is 1.75 ft of arc from 350 deg
    assert 0 < m.dent_fraction(0, j0) < 1
    assert m.joints[m.ground_of[j0]] == m.joints[j0]    # ground joint copied after the dent


@pytest.mark.parametrize("raw, msg", [
    ({"ringwall": {"enabled": True}, "baseplate": {"enabled": True}}, "needs foundation.mode = 'gap'"),
    ({"ringwall": {"enabled": True, "support": "magic"}, "baseplate": {"enabled": True}, "foundation": {"mode": "gap"}}, "not recognised"),
    ({"dents": [{"angle_deg": 0, "elevation": 1, "depth": 0.1, "width": 1}]}, r"dents\[1\] needs height"),
    ({"dents": [{"angle_deg": 0, "elevation": 99, "depth": 0.1, "width": 1, "height": 1}]}, "off the wall"),
    ({"dents": [{"angle_deg": 0, "elevation": 1, "depth": 0, "width": 1, "height": 1}]}, "non-zero"),
])
def test_ringwall_and_dent_config_validation(raw, msg):
    with pytest.raises(ValueError, match=msg):
        spec_from_dict(raw)


@pytest.mark.parametrize("raw, msg", [
    ({"foundation": {"mode": "gap"}}, "needs baseplate.enabled"),
    ({"foundation": {"mode": "springs"}}, "not recognised"),
    ({"foundation": {"mode": "gap", "subgrade_modulus": 0}, "baseplate": {"enabled": True}}, "must be positive"),
])
def test_foundation_config_validation(raw, msg):
    with pytest.raises(ValueError, match=msg):
        spec_from_dict(raw)


# --- SAP groups ----------------------------------------------------------------

def test_group_membership():
    m = small()                                   # n_theta 8, n_z 4, one plate course
    g = m.groups()
    assert list(g) == ["WALL", "COURSE_01", "BASE_RING", "TOP_RING"]
    assert g["WALL"] == (list(range(1, 33)), [], [])
    assert g["COURSE_01"] == (list(range(1, 33)), [], [])
    assert g["BASE_RING"] == ([], list(range(1, 9)), []) and g["TOP_RING"] == ([], list(range(33, 41)), [])
    assert sum(len(a) for a, _, _ in g.values()) == 32 * 2    # every shell in WALL and one course


def test_group_tables_written_and_switchable():
    t = parse_s2k(s2k_text(small()))
    names = [r["GROUPNAME"] for r in t["GROUPS 1 - DEFINITIONS"]]
    assert names == ["WALL", "COURSE_01", "BASE_RING", "TOP_RING"]   # one course = one group
    asg = t["GROUPS 2 - ASSIGNMENTS"]
    assert len(asg) == 32 + 32 + 8 + 8                              # WALL + COURSE_01 + rings
    assert {r["OBJECTTYPE"] for r in asg} == {"Area", "Joint"}
    assert [r["OBJECTLABEL"] for r in asg if r["GROUPNAME"] == "TOP_RING"] == [str(j) for j in range(33, 41)]
    no_courses = TankModel(TankSpec(radius=10.0, height=8.0, n_theta=8, n_z=4, course_groups=False))
    assert list(no_courses.groups()) == ["WALL", "BASE_RING", "TOP_RING"]
    off = TankModel(TankSpec(radius=10.0, height=8.0, n_theta=8, n_z=4, groups=False))
    assert "GROUPS 1 - DEFINITIONS" not in parse_s2k(s2k_text(off))


def test_course_names_pad_to_course_count():
    m = TankModel(TankSpec(radius=10.0, height=120.0, n_theta=4, n_z=120,
                           courses=tuple(Course(1.0, 0.02) for _ in range(120))))
    names = list(m.groups())
    assert names[1] == "COURSE_001" and names[120] == "COURSE_120"
# --- edge refinement -------------------------------------------------------------

def test_graded_sizes_sum_and_grow():
    from tankbuilder.model import graded_sizes
    s = graded_sizes(9.0, 1.0, 0.25, 1.5, 4.0, True, False)
    assert abs(sum(s) - 9.0) < 1e-12
    assert s[:4] == [0.25, 0.375, 0.5625, 0.84375] and max(s) <= 1.0 + 1e-12
    both = graded_sizes(9.0, 1.0, 0.25, 1.5, 4.0, True, True)
    assert both[:4] == both[::-1][:4] and abs(sum(both) - 9.0) < 1e-12
    with pytest.raises(ValueError, match="does not fit"):
        graded_sizes(1.0, 1.0, 0.25, 1.5, 4.0, True, True)


def test_refined_edges_grade_wall_and_caps_and_keep_tributary_total():
    spec = TankSpec(radius=30.0, height=40.0, courses=(Course(20.0, 0.02), Course(20.0, 0.015)),
                    n_theta=36, n_z=20, baseplate=True, baseplate_n_r=5, roof=True,
                    roof_crown_radius=48.0, roof_n_r=5, foundation="gap",
                    edge_length=4.0, edge_size=0.25, edge_growth=1.5)
    m = TankModel(spec)
    dz = [b - a for a, b in zip(m.z_levels, m.z_levels[1:])]
    assert dz[:2] == [0.25, 0.375] and dz[-2:] == [0.375, 0.25] and m.z_levels[-1] == 40.0
    assert all(c == 0 for c in m.row_course[:len(dz) // 2]) and m.row_course.count(0) + m.row_course.count(1) == len(dz)
    for cap in ("baseplate", "roof"):
        r = m.cap_radii_of[cap]
        assert r[0] == 0.0 and r[-1] == 30.0 and abs((r[-1] - r[-2]) - 0.25) < 1e-12
    total = sum(m.baseplate_tributary_area(j) for j in m.baseplate_joints)
    assert abs(total - math.pi * 30.0 ** 2) < 1e-9
    # off by default: the uniform mesh is unchanged
    plain = TankModel(TankSpec(radius=30.0, height=40.0, baseplate=True, roof=True, roof_crown_radius=48.0))
    assert len(set(round(b - a, 9) for a, b in zip(plain.z_levels, plain.z_levels[1:]))) == 1


@pytest.mark.parametrize("raw, msg", [
    ({"mesh": {"refine": ["wall_base", "lid"]}}, "unknown edge"),
    ({"mesh": {"edge_growth": 0.5}}, "edge_growth >= 1"),
])
def test_refine_config_validation(raw, msg):
    with pytest.raises(ValueError, match=msg):
        spec_from_dict(raw)
# --- plate bearing on the ring wall ----------------------------------------------

def test_plate_bearing_moves_rim_band_joints_onto_the_ring_wall():
    spec = TankSpec(radius=30.0, height=40.0, baseplate=True, baseplate_n_r=5, roof=True,
                    roof_crown_radius=48.0, foundation="gap", ringwall=True, ringwall_width=1.0,
                    ringwall_depth=3.0, edge_length=3.0, edge_size=0.25, edge_growth=1.5,
                    ringwall_plate_bearing=True)
    m = TankModel(spec)
    radii = m.cap_radii_of["baseplate"]
    inner = {j for j in m.baseplate_interior_joints if radii[m.cap_ring["baseplate"][j]] >= 29.5 - 1e-6}
    assert set(m.bearing_joints) == inner and len(inner) == 36   # one graded ring (r = 29.75) inside C/2
    for pj, aj in zip(m.bearing_joints, m.bearing_arm_joints):
        assert pj not in m.ground_of and m.joints[aj][2] == -1.5
        assert m.joints[aj][:2] == m.joints[pj][:2]
    links = [(i, j) for l, (i, j) in m.links.items() if m.link_prop[l] == "GAP_BEARING"]
    assert len(links) == len(inner) and all(j in inner for _, j in links)
    arms = [m.frames[f] for f in m.ids.block("frame", "ringwall_arm")]
    assert all(i in set(m.ringwall_joints) for i, _ in arms) and len(arms) == len(inner)
    # two rings inside C/2 chain end to end on each spoke: axis -> outer arm -> inner arm
    spec2 = TankSpec(radius=30.0, height=40.0, baseplate=True, baseplate_n_r=5, roof=True,
                     roof_crown_radius=48.0, foundation="gap", ringwall=True, ringwall_width=1.5,
                     ringwall_depth=3.0, edge_length=3.0, edge_size=0.25, edge_growth=1.5,
                     ringwall_plate_bearing=True)
    m2 = TankModel(spec2)
    arms2 = [m2.frames[f] for f in m2.ids.block("frame", "ringwall_arm")]
    assert len(arms2) == 2 * 36
    axis = set(m2.ringwall_joints)
    starts_at_axis = [ij for ij in arms2 if ij[0] in axis]
    chained = [ij for ij in arms2 if ij[0] in set(m2.bearing_arm_joints)]
    assert len(starts_at_axis) == 36 and len(chained) == 36
    assert all(math.hypot(*m2.joints[i][:2]) > math.hypot(*m2.joints[j][:2]) for i, j in arms2)
    assert "GAP_BEARING" in m.link_props and m.link_props["GAP_BEARING"]["k"] > 0
    assert len(m.ground_joints) == len(m.baseplate_joints) - len(inner)
    g = m.groups()
    assert set(g["PLATE_BEARING"][1]) == inner and len(g["RINGWALL_ARM"][2]) == len(inner)
    assert "END TABLE DATA" in s2k_text(m)


def test_plate_bearing_needs_elevations():
    with pytest.raises(ValueError, match="elevations"):
        TankModel(TankSpec(radius=30.0, height=40.0, baseplate=True, roof=True, roof_crown_radius=48.0,
                           foundation="gap", ringwall=True, ringwall_joints="top", edge_length=3.0,
                           ringwall_plate_bearing=True))
