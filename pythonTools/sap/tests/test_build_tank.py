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

from tankbuilder import Course, TankModel, TankSpec, load_config, parse_s2k  # noqa: E402
from tankbuilder.s2k import s2k_text  # noqa: E402
from tankbuilder.spec import spec_from_dict  # noqa: E402

CONFIGS = ROOT / "configs"
GOLDEN = HERE / "golden"


# --- golden -----------------------------------------------------------------

def test_example_matches_golden():
    spec, _ = load_config(CONFIGS / "example.toml")
    assert s2k_text(TankModel(spec)) == (GOLDEN / "example.s2k").read_text()


def test_cli_writes_the_same_file(tmp_path):
    out = tmp_path / "t.s2k"
    r = subprocess.run([sys.executable, str(ROOT / "build_tank.py"),
                        str(CONFIGS / "example.toml"), "-o", str(out)],
                       capture_output=True, text=True, check=True)
    assert "756 joints, 720 shells, 2 course(s) / 2 thickness(es), base released radially" in r.stdout
    assert out.read_text() == (GOLDEN / "example.s2k").read_text()


# --- config -----------------------------------------------------------------

def test_example_config_loads_and_builds():
    spec, out = load_config(CONFIGS / "example.toml")
    assert out.name == "example.s2k" and out.parent.name == "example"
    assert (spec.base_local_axes, spec.release_radial) == (True, True)
    assert spec.height == 40.0 and len(spec.courses) == 2 and spec.course_divisions() == [10, 10]
    m = TankModel(spec)
    assert len(m.joints) == 36 * 21 and len(m.areas) == 36 * 20
    assert m.sections == {"WALL_T1": 0.0208333, "WALL_T2": 0.015625}
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
    assert g["COURSE_02"] == (list(range(9, 17)), [])
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


# --- SAP groups ----------------------------------------------------------------

def test_group_membership():
    m = small()                                   # n_theta 8, n_z 4, one plate course
    g = m.groups()
    assert list(g) == ["WALL", "COURSE_01", "BASE_RING", "TOP_RING"]
    assert g["WALL"] == (list(range(1, 33)), [])
    assert g["COURSE_01"] == (list(range(1, 33)), [])
    assert g["BASE_RING"] == ([], list(range(1, 9))) and g["TOP_RING"] == ([], list(range(33, 41)))
    assert sum(len(a) for a, _ in g.values()) == 32 * 2       # every shell in WALL and one course


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
