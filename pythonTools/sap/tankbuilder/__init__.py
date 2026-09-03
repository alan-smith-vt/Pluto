"""tankbuilder -- SAP2000 tank model generator (kip, ft, F).

Pipeline: TOML config -> TankSpec (spec.py) -> TankModel (model.py) ->
.s2k text (s2k.py) -> SAP2000 -> results .s2k -> scripts/arms/SapToPluto.cs.

    from tankbuilder import load_config, TankModel, write_s2k
    spec, out = load_config(Path("configs/tank_hoop.toml"))
    write_s2k(TankModel(spec), out)

Geometry / numbering conventions (SapToPluto and the tests rely on these):
  * origin at the centre of the tank base, +Z up
  * wall joint id = 1 + ring_index * n_theta + theta_index, ring 0 at the base;
    mesh rings sit on every plate-course boundary ([[courses]] in the config);
    other parts claim id blocks after the wall via TankModel.ids (IdAllocator)
  * area joints are ordered (theta_i, z_k), (theta_i+1, z_k), (theta_i+1, z_k+1),
    (theta_i, z_k+1) so the shell local 3 axis points radially OUTWARD.
    Water is therefore on the local -3 side = shell face 5 = "Bottom", and a
    positive pressure on that face pushes outward.
"""

from .spec import GAMMA_WATER, G_ACCEL, STEEL, Course, TankSpec, load_config
from .model import TankModel
from .s2k import S2KWriter, parse_s2k, write_s2k

__all__ = [
    "GAMMA_WATER", "G_ACCEL", "STEEL", "Course", "TankSpec", "load_config",
    "TankModel", "S2KWriter", "parse_s2k", "write_s2k",
]
