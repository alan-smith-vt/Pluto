# Pluto — agent instructions

Read `vault/handoffs/handoff.md` first (current state, hard constraints, file map, next task).
Repo root is the Obsidian vault; documentation lives under `vault/` only.

## Hard constraints

- All C# is **C# 5**, loaded via `Add-Type` under **Windows PowerShell 5.1**, warnings-as-errors.
  Compile-check by dot-sourcing `scripts/lib/Config.ps1` from a PS 5.1 `-NonInteractive` run
  (`C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`, never `pwsh`).
- Binary = results / geometry, C#-written, write-once. User-defined things go in the sidecar.
- Python only under `pythonTools/` (development side). Nothing in `scripts/` may need it.
  SAP2000 is driven over its OAPI with `comtypes` (`pythonTools/sap/tankbuilder/sap_api.py`).
- Files are CRLF; edit bytes-safely. Never edit `.obsidian/` while Obsidian is running.

## Vault structure

- Hierarchy: `vault/Pluto` → a **map** per folder (`Arms map`, `Format map`, `Viewer map`,
  `Audits map`, `Decisions map`, `Handoff log`) → notes. Maps carry one described line per
  note — **update the folder's map when a note is born or renamed.**
- **Breadcrumb** is the first line after the frontmatter of every note:
  `*↑ [[vault/Pluto|Home]] › [[vault/<folder>/<Map>|<Map>]]*`; maps carry `*↑ [[vault/Pluto|Home]]*`.
- Links are **path-qualified** (`[[vault/format/v4-schema|v4-schema]]`) and basenames are
  unique (no `index.md`). Frontmatter: `title`, `status` (current | draft | archived), `created`.
- Long notes fold `##` sections into collapsed callouts (`> [!info]- Title`).
- Handoffs: every session writes its own dated record `vault/handoffs/YYYY-MM-DD.md` (suffix
  b, c… for a second session the same day; updating today's is fine) plus a line in
  `Handoff log`. `vault/handoffs/handoff.md` is a short **current-state** note, not a log:
  refresh it at session end, keep history out of it.
- Graph colour groups: `vault/_meta/graph-colors.json` is the restore source for the
  PC-local `.obsidian/graph.json`.

## Working conventions

- Verify before reporting: PS 5.1 compile probe for C#, `node viewer/tests/*.js` for the viewer.
- Commit only when asked. Commit messages are verdicts, not activity.
