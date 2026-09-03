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

## Sections

- `vault/handoffs/` — `handoff.md` is the living state note; dated files are frozen records.
- `vault/arms/` — one note per arm; the arm's C# lives in `scripts/arms/`.
- `vault/format/` — specs and the writer doc; the C# lives in `scripts/lib/writers`, `scripts/lib/sidecar`.
- `vault/viewer/` — viewer notes; the JS lives in `viewer/scripts/`.
- `vault/audits/` — audits; raw agent output under `audits/raw/` (JSON, not notes).
- `vault/decisions/` — decision records.

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
- **Graph colours** by folder (Home/maps gold, handoffs red, arms blue, format green, viewer
  orange, audits gray, decisions teal). `.obsidian/graph.json` is PC-local and gitignored;
  the restore source is `vault/_meta/graph-colors.json`. Never edit `.obsidian/` while
  Obsidian is running.
