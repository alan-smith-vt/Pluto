# scripts/voyager — transfer crate

Verified code copied from the Voyager repo (`D:\Notes\repos\Voyager\code\`), which never leaves the D: machine. Pluto is how it reaches the production box. Edit in Voyager, re-copy here; do not fork.

- `Import-SqlExplorer.ps1`, `SqlExplorer.cs`, `Atlas.cs` — the SQL harness (`Invoke-Sql`, `Invoke-Atlas`). Needs `config.json` on the box; never here.
- `Export-CalcRuns.ps1`, `Join-CalcRooms.ps1`, `PdfText.cs` — calc PDFs → room map. Usage: `vault/arms/calc-room-map.md`.

All PowerShell 5.1 / C# 5, no packages. CRLF.
