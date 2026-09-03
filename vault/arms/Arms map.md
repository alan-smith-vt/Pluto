# Arms map

*↑ [[vault/Home|Home]]*

Hub for `vault/arms/` — one usage note per import/export arm. The C# for each lives in
`scripts/arms/` and loads with the lib through `scripts/lib/Config.ps1` (one Add-Type batch,
PowerShell 5.1, C# 5). Every arm writes the same pair: `<outBase>.bin` (v4) +
`<outBase>.features.json` (sidecar groups). Drop both on the viewer together.

## Plant (production side, SQL exports)

- [[vault/arms/plant-combined-to-viewer|plant-combined-to-viewer]] — **start here for the combined workflow**: pipes + steel + bbox fallback → one `CombinedToPluto` file, end to end.
- [[vault/arms/pipe-csv-to-viewer|pipe-csv-to-viewer]] — SP3D piping: v4 CSV → `PipeCsvReader` (room filter, sizing, progress bar) → `PipeToPluto` → viewer. Pipe-size groups; `PartOid` labels.
- [[vault/arms/steel-csv-to-viewer|steel-csv-to-viewer]] — W-shape members: row-per-member CSV with dims, local-Y vector and SP3D cardinal point → `SteelToPluto` I-sections. Section-name groups.

## Solvers

- [[vault/arms/sap-s2k-to-viewer|sap-s2k-to-viewer]] — SAP2000: `.s2k` model + results → `SapToPluto` (reuses the lib's `Sap2kParser`/`Sap2kReader`, SAP axes kept) → v4 shells with per-corner forces, global displacements, and section / thickness / restraint / local-axes groups. The tank generator that feeds it is Python (`pythonTools/sap/`).
- STAAD — the post-processing lib itself (`scripts/lib/`): `.anl` readers, DSRs, section cuts, STAAD input generation. No arm note yet; see the audit in [[vault/audits/scripts-sanitized-audit|scripts-sanitized-audit]] for the re-hydration state.
