# scripts/ — STAAD post-processing toolchain (production, transcribed)

Moved here from `scripts_sanitized/` on 2026-09-03 once the lib compiled again.
Entry point is `Section-Cut.ps1`; `lib/Config.ps1` loads every `.cs` under `lib/` and `exporters/` in a
single `Add-Type` batch (path-anchored, so dot-source it from anywhere) under **Windows PowerShell 5.1** (C# 5 only, warnings-as-errors).

Three files are throwing **stubs** (`// STUB` header) pending re-hydration from the
production environment: `lib/postProcessing/DsrCalculators.cs`,
`lib/postProcessing/ResultsPostProcessor.cs`, `lib/preProcessing/staadInputBuilder.cs`.
Emptied paths/names elsewhere are marked `// sanitized`. `lib/writers/RawViewerWriter.cs` is the v4 binary writer, `lib/sidecar/FeaturesSidecar.cs`
the sidecar writer, `exporters/` the pipe / steel / combined CSV arms (usage notes in
`vault/arms/`). The full damage table and
re-hydration checklist: `vault/audits/scripts-sanitized-audit.md`.

The old browser viewer these scripts targeted lives in `archive/ViewerSource/`
(reference only; its predicate module is already ported to `viewer/scripts/predicates.js`).
