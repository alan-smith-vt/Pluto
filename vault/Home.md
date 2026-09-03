# Pluto

Obsidian vault for all Pluto documentation (vault root = repo root; wiki-links are
path-qualified: `[[vault/format/x|x]]`). Pluto is a FEMAP-style hub: solver import/export
**arms** converge on one common model (the v4 binary + features sidecar) and one generic
**viewer**. Production code is C# 5 under PowerShell 5.1 (`scripts/`); the viewer is
plain JS (`viewer/`).

**Start here:** [[vault/handoffs/handoff|handoff]] — current state, hard constraints, file map, what is next.

## Maps

- **[[vault/handoffs/Handoff log|Handoff log]]** — the current handoff plus one archived note per handoff date, newest first. What landed, what was decided, what broke.
- **[[vault/arms/Arms map|Arms map]]** — one usage note per import/export arm (pipe CSV, steel CSV, combined plant, SAP2000): what to export, how to run it, what lands where.
- **[[vault/format/Format map|Format map]]** — the common model: v4 binary schema, the frozen v3 schema, the features sidecar, and the C# writer.
- **[[vault/viewer/Viewer map|Viewer map]]** — viewer feature overview and the old-viewer merge plan (predicates done, section-cut persistence next).
- **[[vault/audits/Audits map|Audits map]]** — read-only audits: the sanitized-scripts damage table + re-hydration checklist, and the SapViewer fold-in audit.
- **[[vault/decisions/Decisions map|Decisions map]]** — design decisions, one dated note each (currently recorded inline in the specs and handoffs; move them here as they are made).

## Sections (vault)

- `vault/handoffs/` — `handoff.md` is the living state note; dated files are frozen records.
- `vault/arms/` — one note per arm; the arm's C# lives in `scripts/arms/`.
- `vault/format/` — specs and the writer doc; the C# lives in `scripts/lib/writers`, `scripts/lib/sidecar`.
- `vault/viewer/` — viewer notes; the JS lives in `viewer/scripts/`.
- `vault/audits/` — audits; raw agent output under `audits/raw/` (JSON, not notes).
- `vault/decisions/` — decision records.

## Code folders (no READMEs — this is their description)

- `scripts/` — the STAAD post-processing toolchain, transcribed from the production environment
  (moved from `scripts_sanitized/` 2026-09-03). Entry `Section-Cut.ps1`; `lib/Config.ps1`
  loads every `.cs` under `lib/` and `arms/` in one `Add-Type` batch (path-anchored, dot-source
  it from anywhere) under Windows PowerShell 5.1, C# 5, warnings-as-errors. Three files are
  throwing stubs with a `// STUB` header pending re-hydration (`lib/postProcessing/DsrCalculators.cs`,
  `lib/postProcessing/ResultsPostProcessor.cs`, `lib/preProcessing/staadInputBuilder.cs`);
  emptied paths elsewhere are marked `// sanitized`. Damage table + checklist:
  [[vault/audits/scripts-sanitized-audit|scripts-sanitized-audit]].
- `scripts/lib/writers/RawViewerWriter.cs`, `scripts/lib/sidecar/FeaturesSidecar.cs` — the
  production v4 + sidecar writers ([[vault/format/raw-viewer-writer|raw-viewer-writer]]).
- `scripts/arms/` — the Pluto arms ([[vault/arms/Arms map|Arms map]]).
- `viewer/` — plain JS/HTML/CSS, Three.js r128 vendored under `viewer/lib/`
  ([[vault/viewer/Viewer overview|Viewer overview]]).
- `pythonTools/sap/` — development-side Python: `build_tank.py` (CLI) + the `tankbuilder`
  package (`spec.py` / `model.py` / `s2k.py`) + `configs/*.toml` generate the SAP2000 tank
  `.s2k` (`python build_tank.py configs/example.toml` → `models/<name>/`); `python -m pytest`
  runs 18 tests incl. a byte-exact golden. New configs/goldens are gitignored (project data).
  `s2k_to_bin.py` is superseded reference. `models/` is generated and gitignored. Python by
  design (SAP OAPI automation belongs there); see [[vault/arms/sap-s2k-to-viewer|sap-s2k-to-viewer]].
- `archive/` — superseded code, nothing here is loaded or built. `ViewerSource/` is the
  pre-Pluto browser viewer (predicates already ported; its `sectionCut.js` is the CRUD/JSON
  reference for viewer-merge Phase B). `RawViewerWriter_v3.cs` is the v3 writer, retired
  2026-09-03 after its name catalogs moved into the v4 writer.
- `.claude/skills/` — project-scoped Claude Code skills, `<name>/SKILL.md` each. Empty so
  far; candidates: run the viewer, generate a sample `.bin`, validate an arm's output.

## Conventions

- **Hierarchy:** Home → map → note. Every note's first line after the frontmatter is a
  breadcrumb `*↑ [[vault/Home|Home]] › [[vault/<folder>/<Map>|<Map>]]*`; maps carry `*↑ [[vault/Home|Home]]*`.
- **Maps carry descriptions, not bare links.** Add a line to the folder's map when a note is
  born; that line is the note's one-sentence abstract.
- **Frontmatter** on every note: `title`, `status` (`current` | `draft` | `archived`), `created`.
- **Handoffs:** update `handoff.md` at session end; when its dated sections grow past a screen,
  move them to `handoffs/YYYY-MM-DD.md` and list them in the log.
- **Long notes** fold sections into collapsed callouts (`> [!info]- Title`); the callout titles
  are the table of contents.
- **Graph:** colour groups by folder (Home/maps gold, arms blue, format green, viewer orange,
  audits gray, decisions teal); handoffs, archived notes and code READMEs are filtered out
  with the search `-path:vault/handoffs -path:archive -file:README -path:pythonTools -path:scripts -path:.claude`.
  `.obsidian/graph.json` is PC-local and gitignored; the restore source (groups + filter) is
  `vault/_meta/graph-colors.json`. Never edit `.obsidian/` while Obsidian is running.
- **No READMEs in code folders.** GitHub gets the root `README.md` (short landing page,
  filtered off the graph); everything else is described here and in the maps.
