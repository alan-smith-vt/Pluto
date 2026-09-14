"""One colour per model run for the base support study figures, so a run keeps its colour
across every figure it appears in (figures 1 to 7 of the Notes study note).

Four anchor colours, visually distinct:

    ORIGINAL  flat pad, one point spring under the ring wall (the first modelling)
    GRADED    Boussinesq-graded pad, ring wall soil at 1.0 x the pad rim (the baseline)
    RIGID     graded pad, ring wall soil at 16 x the pad rim (4250 kcf, near rigid)
    PAD42     the 42.5 kcf pad under the rigid ring wall (the soft end of the pad sweep)

Runs between the anchors sit on a gradient: the ring wall sweep (1.27, 3.2, 6.4 x rim) from
GRADED to RIGID by log ratio, the pad sweep (85 kcf) from RIGID to PAD42 by log pad modulus.
BOWLES is the one-off doubled-edge-band calibration model.
"""
from __future__ import annotations

import math
import re

ORIGINAL = "#2f6b9a"   # blue
GRADED = "#3f8f7a"     # teal
RIGID = "#d08a1f"      # amber
PAD42 = "#7b2d8e"      # purple
BOWLES = "#b4472b"     # rust, the -cal-step2 calibration model only

RIM = math.pi / 2      # graded pad rim factor; ring wall ratios are quoted against the rim


def _lerp(a: str, b: str, t: float) -> str:
    t = max(0.0, min(1.0, t))
    ca = [int(a[i:i + 2], 16) for i in (1, 3, 5)]
    cb = [int(b[i:i + 2], 16) for i in (1, 3, 5)]
    return "#" + "".join(f"{round(x + (y - x) * t):02x}" for x, y in zip(ca, cb))


def ringwall_colour(ratio_rim: float) -> str:
    """GRADED at 1 x the pad rim to RIGID at 16 x, log-interpolated."""
    return _lerp(GRADED, RIGID, math.log(max(ratio_rim, 1.0)) / math.log(16.0))


def pad_colour(ks_pad: float) -> str:
    """RIGID at the 170 kcf pad to PAD42 at 42.5, log-interpolated (rigid ring wall family)."""
    return _lerp(RIGID, PAD42, math.log(170.0 / ks_pad) / math.log(4.0))


def run_colour(name: str, ratio_pad: float | None = None, ks_pad: float | None = None) -> str:
    """Colour for a model by its name (TANK-A-<suffix>); ratio_pad = ring wall ks / pad ks,
    ks_pad = pad modulus, both used when the name alone does not fix the point on a gradient."""
    n = name.lower()
    if "cal-step2" in n:
        return BOWLES
    if "ks42" in n:
        return PAD42
    if "ks85" in n:
        return pad_colour(ks_pad or 85.0)
    if "rw4250" in n:
        return RIGID
    m = re.search(r"rw(\d+)", n)
    if m and "bq" in n:
        return ringwall_colour(int(m.group(1)) / 170.0 / RIM)
    if m:                                   # flat-pad model with a stiffer ring wall (rw340)
        return ringwall_colour(int(m.group(1)) / 170.0 / RIM)
    if "cal-bouss" in n or n.endswith("-bq") or "bq" in n:
        return GRADED
    if ratio_pad and ratio_pad > 1.0 + 1e-6:
        return ringwall_colour(ratio_pad / RIM)
    return ORIGINAL


def profile_colour(pr: dict) -> str:
    return run_colour(pr["name"], pr.get("ratio"), pr.get("ks"))
