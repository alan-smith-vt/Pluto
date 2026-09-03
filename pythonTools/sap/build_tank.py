#!/usr/bin/env python3
"""Build a cylindrical steel tank shell model as a SAP2000 .s2k text file.

    python build_tank.py configs/tank_hoop.toml [-o out.s2k]

Thin CLI over the tankbuilder package (spec.py: TankSpec + TOML config;
model.py: TankModel joints/areas/hydrostatics; s2k.py: the .s2k tables).
Units are kip, ft, F throughout; conventions in tankbuilder/__init__.py.

Scope today: cylindrical wall meshed by circumferential and vertical
divisions, thin shells, one steel material, one wall thickness, base ring
pinned or (base_local_axes + release_radial) freed radially so the wall
goes into hoop, hydrostatic pressure from the fill height as a surface
pressure on the inside face. Next: ring-wall base, anchor chairs, settlement.
"""

from __future__ import annotations

import argparse
import sys
import tomllib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from tankbuilder import TankModel, load_config, write_s2k  # noqa: E402


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("config", type=Path, help="path to a .toml tank config")
    p.add_argument("-o", "--out", type=Path,
                   help="output .s2k (overrides [output] in the config)")
    a = p.parse_args(argv)

    try:
        spec, cfg_out = load_config(a.config)
    except (ValueError, tomllib.TOMLDecodeError) as exc:
        p.error(f"{a.config}: {exc}")

    out = a.out or cfg_out
    model = TankModel(spec)
    out.parent.mkdir(parents=True, exist_ok=True)
    write_s2k(model, out)

    base = "released radially" if spec.release_radial else "pinned"
    print(
        f"{out}: {len(model.joints)} joints, {len(model.areas)} shells, "
        f"base {base}, base pressure "
        f"{spec.fluid_weight * spec.fill_height:.4f} kip/ft^2"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
