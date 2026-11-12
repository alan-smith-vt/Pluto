# scripts/voyager — transfer crate

Verified code copied from the Voyager repo (`D:\Repos\Voyager\code\`), which never leaves the D: machine. Pluto is how it reaches the production box. Edit in Voyager, re-copy here; do not fork.

- `Import-SqlExplorer.ps1`, `SqlExplorer.cs`, `Atlas.cs` — the SQL harness (`Invoke-Sql`, `Invoke-Atlas`). Needs `config.json` on the box; never here.
- `Export-DuctPorts.ps1` — routed SP3D ducts as (part, port xyz, hub, run) rows; `-Census` first. Draft, unverified.
- `Export-ClassCensus.ps1` — find the classes behind a kind of object by table name (`-Like Wall,Slab`), bbox shape split per classid, sample oids for `Invoke-Atlas`; `-Series` lists a classid block. Read-only. Run on a synthetic database only.
- `Export-Walls.ps1` — walls (classid 240024 with a bbox) as axis-aligned boxes: `walls.csv` (extents in m, shape wall / slab / skew wall / skew slab / small, thickness, axis), thickness histogram, levels; `-Attrs` = string attribute census of the class. Read-only. Run on a synthetic database only.
- `Export-CalcRuns.ps1`, `Join-CalcRooms.ps1`, `PdfText.cs` — calc PDFs → room map. Usage: `vault/arms/calc-room-map.md`.

All PowerShell 5.1 / C# 5, no packages. CRLF.
