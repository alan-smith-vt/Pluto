# archive/

Superseded code kept for reference only — nothing here is loaded or built.

- `ViewerSource/` — the pre-Pluto browser viewer (init → sectionCut → predicate → app,
  concatenated by `build.ps1`). Predicates were ported to `viewer/scripts/predicates.js`
  (2026-09-01); `sectionCut.js` is the CRUD/grouping/JSON reference for viewer-merge
  Phase B. Known bugs are catalogued in `vault/audits/scripts-sanitized-audit.md`.
- `RawViewerWriter_v3.cs` — the v3 binary writer, retired 2026-09-03 after its name
  catalogs / `BuildComponents` were ported into the v4 writer in `scripts/lib/writers/`.
