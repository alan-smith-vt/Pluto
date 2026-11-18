---
title: Pluto handoff, current state
status: current
created: 2026-11-18
---

*↑ [[vault/Pluto Home|Home]] › [[vault/handoffs/Handoffs map|Handoffs map]]*

# Pluto handoff: current state

**Where it stands (2026-11-18).** Branch `pipestress` holds:
- **The pipe workflow.** An Excel workbook or a TOML becomes one pipe model, reviewed in the viewer with CAESAR's error
  checks, and written as a CAESAR II `.cii`. An imported job comes back byte for byte.
- **The frame side.** A lossless SAP2000 `.s2k` model, edited in the frame workbook (3D View, live checks) and written
  to SAP and to STAAD.Pro (`-Std`, exact or refused).
- **The CAESAR → SAP check model.** `Build-Frame -In <CAESAR job>` seeds the frame model from the pipe, for member
  forces and support reactions:
  - frames, bends, rigids, expansion joints, hangers and nozzles;
  - linear and nonlinear supports;
  - CAESAR's cases, each OPE continuing from SUS when a support is nonlinear.

  It is verified against CAESAR 15.01 and SAP 24.2 on two probes. The linear model agrees within 5% away from tees.
  The nonlinear one agrees except on friction-sized loads, as SAP's friction follows the load path.

**Read first:** [[vault/format/pipe-to-frame|pipe-to-frame]],
[[vault/decisions/2026-11-12-caesar-to-sap|2026-11-12-caesar-to-sap]],
[[vault/format/sap-s2k-tables|sap-s2k-tables]], [[vault/arms/frame-workbook|frame-workbook]].

**Gaps in the check model:**
- **B31J's tee flexibility.** Tees are rigid joints, with a notice. The formulas are needed: B31J Table 1-1, or
  docs.hexagonppm.com allowed in the cloud environment.
- **Not yet run by a probe:**
  - an OPE with its own pressure set;
  - reducer stepping;
  - a finite expansion joint's weight;
  - k with flanged ends;
  - rotational gaps;
  - the hanger count.
- **Element forces** have not been compared with CAESAR's yet. Only restraint loads and displacements have.

**Code issues found by the vault audit (2026-11-18), not fixed:**
- `Build-Frame -Xlsx` replaces an existing workbook without a check (no stamp, no `-Force`, no `.bak`): re-seeding a
  CAESAR job loses the edits made in its frame workbook.
- Frames and nonlinear cases added in the frame workbook to a seed lack the seed's SAP settings (`AutoMesh=No`, the
  raised step limits): `FrameModel`'s templates have neither.
- `viewer/index.html` gives the Auto-reload and Auto range checkboxes the same id (`feaAuto`): Auto range does
  nothing, and unticking Auto-reload switches the colour range to manual.
- The viewer's Find searches shell ids only; `FEAFeatures.worldOffset()` reads a top-level `units`, not
  `model.units` ([[vault/viewer/model-rail|model-rail]]).
- A personal Windows path is in the pushed history (fixed in the file on 2026-11-18): purging it is the user's call.

**Waiting on the production machine:** the PS 5.1 compile probe (`Config.ps1`) for the session's C#.

**Next:**
1. **Round 3, the sample job, in SAP only.** CAESAR's outputs are already in `scripts/caesar/testdata`: restraint loads,
   displacements and local element forces for all six cases. Seed it, run it in SAP and export.
   - The comparison adds element forces for the first time.
   - It exercises reducer stepping, expansion-joint weight, rotational gaps and two tees at full size.
   - Materials are Pluto's defaults (A53 B) unless the job's CAESAR workbook has a Materials sheet.
2. **B31J tee flexibility,** once its formulas are in hand.

**Later, not planned:** SAP back to CAESAR: results by CAESAR node and element, the support steel's stiffness into
CAESAR's restraints, re-seeding that keeps SAP-side additions, and writing a seeded model back as a `.cii`. See
"Possible later" in [[vault/decisions/2026-11-12-caesar-to-sap|2026-11-12-caesar-to-sap]].

Last session: [[vault/handoffs/handoff-2026-11-18|handoff-2026-11-18]].
