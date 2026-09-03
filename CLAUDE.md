# Pluto — agent instructions

Read `vault/handoffs/handoff.md` first (current state, hard constraints, file map, next task).
Repo root is the Obsidian vault; documentation lives under `vault/` only.

## Hard constraints

- All C# is **C# 5**, loaded via `Add-Type` under **Windows PowerShell 5.1**, warnings-as-errors.
  Compile-check by dot-sourcing `scripts/lib/Config.ps1` from a PS 5.1 `-NonInteractive` run
  (`C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`, never `pwsh`).
- Binary = results / geometry, C#-written, write-once. User-defined things go in the sidecar.
- Python only under `pythonTools/` (development side). Nothing in `scripts/` may need it.
- Files are CRLF; edit bytes-safely. Never edit `.obsidian/` while Obsidian is running.

## Vault structure

- Hierarchy: `vault/Home` → a **map** per folder (`Arms map`, `Format map`, `Viewer map`,
  `Audits map`, `Decisions map`, `Handoff log`) → notes. Maps carry one described line per
  note — **update the folder's map when a note is born or renamed.**
- **Breadcrumb** is the first line after the frontmatter of every note:
  `*↑ [[vault/Home|Home]] › [[vault/<folder>/<Map>|<Map>]]*`; maps carry `*↑ [[vault/Home|Home]]*`.
- Links are **path-qualified** (`[[vault/format/v4-schema|v4-schema]]`) and basenames are
  unique (no `index.md`). Frontmatter: `title`, `status` (current | draft | archived), `created`.
- Long notes fold `##` sections into collapsed callouts (`> [!info]- Title`).
- Handoffs: `vault/handoffs/handoff.md` is the living state note — refresh it at session end.
  Dated session records go to `vault/handoffs/YYYY-MM-DD.md` and a line in `Handoff log`.
- Graph colour groups: `vault/_meta/graph-colors.json` is the restore source for the
  PC-local `.obsidian/graph.json`.

## Working conventions

- Verify before reporting: PS 5.1 compile probe for C#, `node viewer/tests/*.js` for the viewer.
- Commit only when asked. Commit messages are verdicts, not activity.
