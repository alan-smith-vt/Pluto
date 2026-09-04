#!/usr/bin/env python3
"""Build a cylindrical steel tank shell model as a SAP2000 .s2k text file.

    python build_tank.py configs/tank_hoop.toml [-o out.s2k]

Thin CLI over the tankbuilder package (spec.py: TankSpec + TOML config;
model.py: TankModel joints/areas/hydrostatics; s2k.py: the .s2k tables).
Units are kip, ft, F throughout; conventions in tankbuilder/__init__.py.

Scope today: cylindrical wall meshed by circumferential and vertical
divisions, thin shells, one steel material, plate courses of differing
thickness ([[courses]], one shell section per thickness, a mesh ring on every
course boundary), base ring pinned or (base_local_axes + release_radial)
freed radially so the wall goes into hoop, hydrostatic pressure from the
fill height as a surface pressure on the inside face; optional baseplate (polar mesh, full head on
its top face, interior held vertically for now) and spherical roof with an
eave ring of double angles. Next: gap-link support layer, ring wall,
settlement profiles.
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
    if model.gap:
        n_soil = len(model.ringwall_ground)
        base += f", {len(model.links) - n_soil} gap links on ground (ks {spec.subgrade_modulus:g} kip/ft^3)"
    if spec.ringwall:
        base += (f", ring wall {spec.ringwall_width:g} x {spec.ringwall_depth:g} ft "
                 f"({spec.ringwall_support}: {len(model.ringwall_joints)} contact + {len(model.ringwall_ground)} soil links)")
    if spec.dents:
        base += f", {len(spec.dents)} dent(s)"
    if spec.settlement != "none":
        moved = sum(1 for v in model.settlements.values() if v != 0.0)
        size = (f"{spec.settlement_depth:g} ft deep x {spec.settlement_width:g} ft wide" if spec.settlement == "trench"
                else f"{spec.settlement_depth:g} ft at the edge")
        base += f", settlement {spec.settlement} {size} at {spec.settlement_direction_deg:g} deg ({moved} ground joints moved)"
    parts = [f"{len(spec.plate_courses)} course(s)"]
    if spec.baseplate:
        parts.append(f"baseplate ({len(model.baseplate_areas)} shells)")
    if spec.roof:
        parts.append(f"roof ({len(model.roof_areas)} shells, rise {model.roof_rise:.2f} ft"
                     + (f", {len(model.ids.block('frame', 'roof_ring'))} ring frames)" if spec.roof_ring else ")"))
    print(
        f"{out}: {len(model.joints)} joints, {len(model.areas)} shells, "
        + ", ".join(parts) + f", base {base}, base pressure "
        f"{spec.fluid_weight * spec.fill_height:.4f} kip/ft^2"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
