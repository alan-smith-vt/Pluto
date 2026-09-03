"""TankSpec: the parameters of one tank model, and the TOML config loader."""

from __future__ import annotations

import tomllib
from dataclasses import dataclass
from pathlib import Path

# --- constants (kip, ft, F) -------------------------------------------------

GAMMA_WATER = 0.0624  # kip/ft^3
G_ACCEL = 32.174  # ft/s^2
STEEL = dict(
    name="A36",
    E=4176000.0,  # kip/ft^2  (29000 ksi)
    poisson=0.3,
    unit_weight=0.490,  # kip/ft^3
    alpha=6.5e-06,
    fy=5184.0,  # kip/ft^2 (36 ksi)
)


@dataclass
class TankSpec:
    # [geometry]
    radius: float = 30.0  # ft, to the shell mid-surface
    height: float = 40.0  # ft, wall height
    thickness: float = 0.0208333  # ft (0.25 in)
    # [mesh]
    n_theta: int = 36  # radial (circumferential) divisions
    n_z: int = 20  # vertical divisions
    # [fluid]
    fill_fraction: float = 1.0  # 1.0 = filled to the top of the wall
    fluid_weight: float = GAMMA_WATER
    load_mode: str = "uniform"  # "uniform" | "joints"
    # [supports]
    base_local_axes: bool = False
    release_radial: bool = False
    # [material]
    mat_name: str = STEEL["name"]
    mat_e: float = STEEL["E"]
    mat_poisson: float = STEEL["poisson"]
    mat_unit_weight: float = STEEL["unit_weight"]
    mat_alpha: float = STEEL["alpha"]

    @property
    def fill_height(self) -> float:
        return self.height * self.fill_fraction

    def validate(self) -> None:
        if self.n_theta < 3:
            raise ValueError("mesh.n_theta must be at least 3")
        if self.n_z < 1:
            raise ValueError("mesh.n_z must be at least 1")
        if self.radius <= 0 or self.height <= 0 or self.thickness <= 0:
            raise ValueError("geometry.radius/height/thickness must be positive")
        if not 0.0 <= self.fill_fraction <= 1.0:
            raise ValueError("fluid.fill_fraction must be between 0 and 1")
        if self.load_mode not in ("uniform", "joints"):
            raise ValueError(f"fluid.load_mode {self.load_mode!r} not recognised")
        if self.release_radial and not self.base_local_axes:
            raise ValueError(
                "supports.release_radial needs supports.base_local_axes = true, "
                "otherwise the released direction is global Y, not radial"
            )


# --- config ------------------------------------------------------------------

# TOML section -> key -> TankSpec field.  Anything not listed is rejected, so a
# typo in a config is an error instead of a silently ignored setting.
CONFIG_MAP = {
    "geometry": {"radius": "radius", "height": "height", "thickness": "thickness"},
    "mesh": {"n_theta": "n_theta", "n_z": "n_z"},
    "fluid": {
        "fill_fraction": "fill_fraction",
        "unit_weight": "fluid_weight",
        "load_mode": "load_mode",
    },
    "supports": {
        "base_local_axes": "base_local_axes",
        "release_radial": "release_radial",
    },
    "material": {
        "name": "mat_name",
        "e": "mat_e",
        "poisson": "mat_poisson",
        "unit_weight": "mat_unit_weight",
        "alpha": "mat_alpha",
    },
}


def spec_from_dict(raw: dict) -> tuple[TankSpec, str, str]:
    """Build a validated TankSpec from parsed TOML. Returns (spec, out_dir, out_name)."""
    out_dir_raw = "../models"
    out_name = None
    fields: dict[str, object] = {}
    for section, values in raw.items():
        if section == "output":
            unknown = set(values) - {"dir", "name"}
            if unknown:
                raise ValueError(f"unknown key(s) in [output]: {sorted(unknown)}")
            out_dir_raw = values.get("dir", out_dir_raw)
            out_name = values.get("name", out_name)
            continue
        if section not in CONFIG_MAP:
            raise ValueError(f"unknown config section [{section}]")
        if not isinstance(values, dict):
            raise ValueError(f"[{section}] must be a table of key = value entries")
        for key, value in values.items():
            if key not in CONFIG_MAP[section]:
                raise ValueError(f"unknown key {key!r} in [{section}]")
            fields[CONFIG_MAP[section][key]] = value
    spec = TankSpec(**fields)
    spec.validate()
    return spec, out_dir_raw, out_name


def load_config(path: Path) -> tuple[TankSpec, Path]:
    """Read a .toml config into a TankSpec plus its output .s2k path.

    Each model gets its own folder (SAP scatters .sdb/.msh/.Y_*/.LOG working
    files beside whatever it opens): <dir>/<name>/<name>.s2k, with relative
    dirs resolved against the config's own folder.
    """
    with path.open("rb") as fh:
        raw = tomllib.load(fh)
    spec, out_dir_raw, out_name = spec_from_dict(raw)
    out_name = out_name or path.stem
    out = (path.parent / out_dir_raw / out_name / f"{out_name}.s2k").resolve()
    return spec, out
