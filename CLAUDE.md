# Pluto — agent instructions

Read the current handoff first: `vault/handoffs/handoff-current.md`, for the current state and the next task. The
records before 2026-11-18 are in the Notes vault's `Tools/Pluto Handoffs/`. This repo normally sits at
`D:\Repos\Pluto`, beside the Notes vault at `D:\Notes`.
Repo root is the Obsidian vault; tool documentation lives under `vault/`.

## Hard constraints

- **No project-identifying information in Pluto, ever** (files, commit messages, branch names):
  no project numbers, clients, sites, model / asset / group / section names from real models,
  project or network paths, people, employer, or wording about the security posture of the
  machines (say "production machine", not "secure"). Use generic placeholders (`TANK-A`,
  `DUCT_ALL`, `<project dir>`). Project studies and anything tied to a real job go in the Notes
  vault (production-machine only); the handoffs in `vault/handoffs/` name no project. History was
  purged of such data on 2026-09-17.
- All C# is **C# 5**, loaded via `Add-Type` under **Windows PowerShell 5.1**, warnings-as-errors.
  Compile-check by dot-sourcing `scripts/lib/Config.ps1` from a PS 5.1 `-NonInteractive` run
  (`C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`, never `pwsh`).
- Binary = results / geometry, C#-written, write-once. User-defined things go in the sidecar.
- Python only under `pythonTools/` (development side). Nothing in `scripts/` may need it.
- **SAP2000 has one controller: the C# in `scripts/sap/` (`SapSession.cs` and the classes on it).**
  Python never calls the OAPI. It runs `scripts/sap/Run-Sap.ps1` via `pythonTools/sap/tankbuilder/sap_cli.py`.
  Before writing any SAP code, check the capability table in `vault/arms/sap-controller.md`;
  extend the C# (a method plus a runner switch) rather than writing a second driver. The old
  Python `sap_api.py` is retired in `archive/` (2026-10-02).
- Files are CRLF; edit bytes-safely. Never edit `.obsidian/` while Obsidian is running.

## Vault structure

- Hierarchy: `vault/Pluto Home` (a stub whose up-link opens the full tool note `Tools/Pluto.md` in the Notes vault) → a **map** per folder (`Arms map`, `Format map`, `Viewer map`,
  `Audits map`, `Decisions map`, `Handoffs map`) → notes. Maps carry one described line per
  note — **update the folder's map when a note is born or renamed.**
- **Breadcrumb** is the first line after the frontmatter of every note:
  `*↑ [[vault/Pluto Home|Home]] › [[vault/<folder>/<Map>|<Map>]]*`; maps carry `*↑ [[vault/Pluto Home|Home]]*`.
- Links are **path-qualified** (`[[vault/format/v4-schema|v4-schema]]`) and basenames are
  unique (no `index.md`). Frontmatter: `title`, `status` (current | draft | archived), `created`.
- Long notes fold `##` sections into collapsed callouts (`> [!info]- Title`).
- Handoffs live in `vault/handoffs/` (from 2026-11-18):
  - every session writes its own dated record, `handoff-YYYY-MM-DD.md` (suffix b, c… for a second
    session the same day; updating today's is fine);
  - it adds a line for the record in `Handoffs map`, which is the log, newest first;
  - `handoff-current.md` is a short **current-state** note, not a log: refresh it at session end
    and keep history out of it.

  A handoff names no project, like every file here. The basenames differ from the Notes vault's
  own handoff notes, because this repo also sits inside Notes. Records up to 2026-11-05 stay in
  Notes (`Tools/Pluto Handoffs/`).
- Graph colour groups + filter: `.obsidian/graph.json` is tracked (the only
  tracked `.obsidian` file); `vault/_meta/graph-colors.json` is its restore source.

## Working conventions

- Verify before reporting: PS 5.1 compile probe for C#, `node viewer/tests/*.js` for the viewer.
- Commit only when asked. Commit messages are verdicts, not activity.
