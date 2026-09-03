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

from tankbuilder import TankModel, TankSpec, load_config, parse_s2k  # noqa: E402
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
    assert "756 joints, 720 shells, base released radially" in r.stdout
    assert out.read_text() == (GOLDEN / "example.s2k").read_text()


# --- config -----------------------------------------------------------------

def test_example_config_loads_and_builds():
    spec, out = load_config(CONFIGS / "example.toml")
    assert out.name == "example.s2k" and out.parent.name == "example"
    assert (spec.base_local_axes, spec.release_radial) == (True, True)
    m = TankModel(spec)
    assert len(m.joints) == 36 * 21 and len(m.areas) == 36 * 20
    assert "END TABLE DATA" in s2k_text(m)


@pytest.mark.parametrize("raw, msg", [
    ({"geometry": {"radius": 1, "hieght": 2}}, "unknown key 'hieght'"),
    ({"lids": {}}, "unknown config section"),
    ({"supports": {"release_radial": True}}, "needs supports.base_local_axes"),
    ({"mesh": {"n_theta": 2}}, "n_theta must be at least 3"),
    ({"fluid": {"load_mode": "magic"}}, "load_mode"),
    ({"output": {"file": "x"}}, "unknown key(s) in [output]"),
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


def test_numbering_convention():
    m = small()
    assert len(m.joints) == 8 * 5 and len(m.areas) == 8 * 4
    assert m.joint_id(0, 0) == 1 and m.joint_id(0, 8) == 1          # wraps
    assert m.joint_id(1, 0) == 9 and m.joint_id(4, 7) == 40
    assert m.base_joints == list(range(1, 9)) and m.top_joints == list(range(33, 41))
    assert m.course_areas(0) == list(range(1, 9)) and m.course_areas(3) == list(range(25, 33))
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
