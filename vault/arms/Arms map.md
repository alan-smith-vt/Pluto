# Arms map

*↑ [[vault/Pluto|Home]]*

Hub for `vault/arms/` — one usage note per import/export arm. The C# for each lives in
`scripts/arms/` and loads with the lib through `scripts/lib/Config.ps1` (one Add-Type batch,
PowerShell 5.1, C# 5). Every arm writes the same pair: `<outBase>.bin` (v4) +
`<outBase>.features.json` (sidecar groups). Drop both on the viewer together.

## Plant (production side, SQL exports)

- [[vault/arms/plant-combined-to-viewer|plant-combined-to-viewer]] — **start here for the combined workflow**: pipes + steel + bbox fallback → one `CombinedToPluto` file, end to end.
- [[vault/arms/pipe-csv-to-viewer|pipe-csv-to-viewer]] — SP3D piping: v4 CSV → `PipeCsvReader` (room filter, sizing, progress bar) → `PipeToPluto` → viewer. Pipe-size groups; `PartOid` labels.
- [[vault/arms/steel-csv-to-viewer|steel-csv-to-viewer]] — W-shape members: row-per-member CSV with dims, local-Y vector and SP3D cardinal point → `SteelToPluto` I-sections. Section-name groups.

## Solvers

- [[vault/arms/sap-tank-builder|sap-tank-builder]] — the Python tank generator (`pythonTools/sap/tankbuilder`): what each feature is in the model (courses, baseplate, roof + gutter ring, gap support layer, ring wall, dents), the plans for ports and surveys, how to check the ring-wall section, and which SAP tables are still unverified.
- [[vault/arms/sap-tank-model-diagrams|sap-tank-model-diagrams]] — the model in three generated pictures: loads and case chain, shell local axes on wall / baseplate / roof, beam node connectivity at the eave and the rim.
- [[vault/arms/settlement-profiles|settlement-profiles]] — ground displacement on the ground joints (`[settlement]`, trench first): config, the audit CSV + generated SVG (plan heatmap + elevation) and how `run_sap.py` refreshes them.
- [[vault/arms/tank-hand-calcs|tank-hand-calcs]] — closed-form expectations for TANK-A under dead and hydrostatic load (wall weight-above, dome membrane, eave tension ring, pR hoop by course, long-cylinder base edge solution, plate tension, ring wall bearing) checked row by row against the model; which face is top and the face-stress formula.
- [[vault/arms/sap-results-coverage|sap-results-coverage]] — results side of the SAP arm: every shell / frame / joint / link quantity SAP2000 holds against what `sap_api` pulls and what `SapToPluto` shows (per-corner forces, F/t membrane stresses, global displacements, frame end forces), what is dropped (face stresses, principals, link forces, reactions, interior stations), and the cheapest additions.
- [[vault/arms/beam-offset-study|beam-offset-study]] — the tank's three-joint gap chain built four ways (coincident at centroid / top with Transform=No / top with Transform=Yes / true elevations) as a beam and as a ring on SAP 26: equivalent under vertical load and settlement (A = B = C, X within 0.4 %), so C — no coincident joints — is the layout to move the tank to; the `Transform` column finding. `pythonTools/sap/offset_study.py [--ring] --run`.
- [[vault/arms/ring-wall-load-path|ring-wall-load-path]] — the ring wall in pictures (SVG assets): tangential-only restraints in plan, and the rim → contact gap → wall → soil gap → ground section with the SAP 26 check numbers.
- [[vault/arms/sap-s2k-to-viewer|sap-s2k-to-viewer]] — SAP2000: `.s2k` model + results → `SapToPluto` (reuses the lib's `Sap2kParser`/`Sap2kReader`, SAP axes kept) → v4 shells with per-corner forces, global displacements, and section / thickness / restraint / local-axes groups. The tank generator that feeds it is Python (`pythonTools/sap/`).
- STAAD — the post-processing lib itself (`scripts/lib/`): `.anl` readers, DSRs, section cuts, STAAD input generation. No arm note yet; see the audit in [[vault/audits/scripts-sanitized-audit|scripts-sanitized-audit]] for the re-hydration state.
