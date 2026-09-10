---
title: Beam offset study — the three-joint chain built four ways, beam and ring
status: current
created: 2026-09-08
---

*↑ [[vault/Pluto|Home]] › [[vault/arms/Arms map|Arms map]]*

The ring wall's support chain — **tank joint → contact gap → wall joint → soil gap →
ground joint**, three coincident joints and two compression-only links per spoke — built
four ways with the same section, links and loads, once as a two-span beam and once as a
full ring, to settle whether the vertical offset between the wall-top joint and the
section centroid changes the vertical load path or the settlement response. It does not:
the four schemes are equivalent, so the layout can be chosen for ease of auditing, and
that is **C**, where nothing is coincident. Audit checks for the real wall:
[[vault/audits/ring-wall-gap-audit|ring-wall-gap-audit]]; the wall itself:
[[vault/arms/ring-wall-load-path|ring-wall-load-path]].

![[vault/arms/assets/beam-offset-alternatives.svg|1100]]

## The four schemes

| key | group | joints | frame insertion | links |
|---|---|---|---|---|
| A | `A_COINCIDENT` | all three at the section **centroid** | cardinal 10 | zero length |
| B | `B_TANK` | all three at the **top** of the section | cardinal 8, **Transform = No** — section drawn A/2 lower, analysis on the joint line; what the builder meant | zero length |
| X | `X_XFORM` | as B | cardinal 8, **Transform = Yes** — rigid arms joint → centroid, stiffness at the centroid; what the tank actually had until 2026-09-08 | zero length |
| C | `C_TRUE` | tank joint at the top (z = 0), wall joint at the centroid (z = −A/2), ground joint at the base (z = −A) | cardinal 10 | length A/2 each, I below J, local 1 = +Z |

Link properties are the same in all four: `GAP_CONTACT` U1 gap (E·C·L/A), `GAP_SOIL` U1
gap (ks·C·L). Both also carry small linear shear and rocking terms so the chains stand
without joint restraints; they carry nothing under vertical load. Ground joints fixed;
tank joints restrained in rotation only.

## Model and cases

`pythonTools/sap/offset_study.py` → `models/offset-study/offset-beam.s2k` (4 × 5 chains,
2 × 20 ft spans) and `--ring` → `offset-ring.s2k` (4 × 72 chains, R = 33.625 ft, the
TANK-A ring wall section 1.25 × 3.75 ft, ks = 170 kcf). Staged nonlinear, vertical only:

| case | load |
|---|---|
| `NL_DEAD` | self weight |
| `NL_VERT` | 3 kip/ft down on the tank joints (the shell bearing on the wall centreline) |
| `NL_SETTLE` | ground displacement: half-plane slope 0.5 ft at the edge (ring, as the tank config), middle joint −0.1 ft (beam) |

```
python offset_study.py --run            # beam
python offset_study.py --ring --run     # ring
```

`--run` imports over the OAPI, **sets and echoes the insertion point per scheme**
(`FrameObj.SetInsertionPoint` / `GetInsertionPoint`, because of the column story below),
echoes `PropLink.GetGap`, runs, and prints + writes CSV: frame force envelopes, minimum
wall U3, wall rolling rotation, tank sway, minimum soil link P and the number of open soil
gaps.

## Results (SAP2000 26.3.0, 2026-09-08)

Ring:

| case | quantity | A | B | C | X |
|---|---|---|---|---|---|
| `NL_VERT` | M3 max (kip-ft) | 0.750 | 0.750 | 0.750 | 0.714 |
| `NL_VERT` | wall U3 (ft) | −0.017425 | −0.017425 | −0.017425 | −0.017425 |
| `NL_VERT` | soil link P (kip) | −10.866 | −10.866 | −10.866 | −10.866 |
| `NL_SETTLE` | M3 max (kip-ft) | 555.07 | 555.07 | 555.07 | 552.9 |
| `NL_SETTLE` | T max (kip-ft) | 148.5 | 148.5 | 148.5 | 147.8 |
| `NL_SETTLE` | wall U3 min (ft) | −0.52134 | −0.52134 | −0.52134 | −0.52135 |
| `NL_SETTLE` | soil P min (kip) | −39.396 | −39.396 | −39.396 | −39.467 |

Beam: V2, M3, U3 and link P identical for A, B, C in every case (`NL_SETTLE` M3 273.97,
the middle gap open); X within 1.3 %. Full tables: `offset-beam.csv`, `offset-ring.csv`
beside the models.

## Why they are equivalent

- **A vertical force on the section's vertical centreline makes no couple**, wherever
  along the depth it enters. The tank weight arriving at the top of the wall and the soil
  reaction leaving at the base are collinear; the wall depth is not a lever arm.
- **A Gap link's k is a number, not EA/L.** Giving the links length (C) moves the joints
  in the picture and changes nothing in the stiffness.
- **Settlement drives the ring by shape.** The imposed vertical profile produces the same
  bending and twist whether the analysis axis is at the top or the centroid, and the
  ring's twist moves the wall radially, which the vertical links do not feel.
- **X is the odd one** only because a rigid arm couples axial force with end rotation
  (hence the small P in the frames and the 0.4 % on M3). It is not wrong, just not
  drawing-only.

The equivalence holds for vertical loading and vertical settlement, which is all the
tank sees today. It would stop holding only if a horizontal force entered at the top of
the wall, which nothing in the current model produces (the rim is radially free on the
wall).

## What the tank had, and the fix

The builder wrote `StiffTransform=No`; SAP's column is `Transform`, so the row was
ignored and SAP kept its default, **Yes**. The TANK-A results to date are therefore
scheme X, within 0.4 % of the others for everything reported. Fixed 2026-09-08: the
builder writes `Transform`, defaults to No, and `[ringwall] transform = true` selects X.

## Decision: C

All four being equal in the numbers, **C** is the layout to move the tank to: tank rim
joint at the wall top, wall frame joint at the centroid, ground joint at the base, links
of real length. Every joint is then its own point in the SAP GUI — no three-deep
coincident stack to pick through, no insertion-point offset to explain, the section
drawn where its stiffness is. That is a builder change (the rim's former ground joints
split into a centroid joint and a base joint per spoke; `run_sap.py` rerun afterwards);
until then the tank stays as built.
