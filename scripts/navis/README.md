# PlutoNavis — Navisworks add-in

Navisworks Manage / Simulate add-in (same .NET API; Freedom cannot load add-ins). Goal: extract duct
geometry (triangles → centrelines) from the federated model for the viewer and SAP model builds.
Current rung: a hello-world button that proves the add-in loads.

## Build and install (no Visual Studio, no SDK)

```
powershell -ExecutionPolicy Bypass -File scripts\navis\Build-NavisPlugin.ps1
```

- Finds the newest Navisworks Manage / Simulate under `C:\Program Files\Autodesk` (or `-NavisRoot <dir>`).
- Compiles with the Windows .NET Framework `csc.exe`, so **keep the sources C# 5**. Output:
  `scripts\navis\bin\PlutoNavis.dll`.
- **Copy by hand** (close Navisworks first; a loaded DLL is locked): in Explorer, copy the DLL into
  `<NavisRoot>\Plugins\PlutoNavis\` (the folder name must equal the DLL name; replace the old file). The
  script prints both paths. It has no write access to Program Files and **never elevates** (UAC on the
  work machine needs IT); Explorer's own permission prompt works.
- The per-user `%APPDATA%\Autodesk Navisworks <Product> <Year>\Plugins` folder did **not** load on
  Simulate 2025 (2026-10-05); the script removes an old copy there.

Then: open any NWD / NWF, ribbon **Tool add-ins 1** → **Pluto Hello**. Expected: a message box with the
file name and the number of appended models.

## Buttons

| Button | Class | Does |
|---|---|---|
| Pluto Hello | `HelloButton` | message box: file name, appended model count (proves the add-in loads) |
| Pluto Inventory | `InventoryButton` | read-only walk of the open model; every item with an Element-tab `IfcGUID` → `C:\Temp\hvac\inventory\<yyyyMMdd-HHmmss>\`: `items.csv` (id, source file, Revit category / family / type / system / size, bbox in document units), `property-names.csv` (every tab + property seen, count, sample value), `summary.txt` (models, units, counts by source file and category). Cancel from the progress bar writes partial results, flagged |
| Pluto Fab | `FabButton` | fabrication parts only (search Element/Category containing "Fabrication": MEP Fabrication Ductwork / Hangers) → `C:\Temp\hvac\fab\<yyyyMMdd-HHmmss>\`, same files; seconds. A fabrication part's Revit element (Element / Custom tabs, IfcGUID) sits above a composite object with only an Item tab, which is what a click selects; IfcGUIDs are shared within a fabrication assembly, so rows carry a unique `NavisId` |
| Pluto Ducts | `DuctsButton` | read-only; one search, then Ducts / Duct Fittings / Duct Accessories (insulation skipped) → `C:\Temp\hvac\ducts\<yyyyMMdd-HHmmss>\`: `ducts.csv` (properties parsed to numbers: sizes in, lengths ft; bbox; `OwnGeom` / `SkippedGeom`; for Ducts the triangle fit of its own geometry (stops at descendants with a different IfcGUID): endpoints, fitted length and section, checks `LenErr_ft` / `SizeErr_in` / `TriVsBbox_ft`, `Flag`), `duct_parts.csv` (one row per geometry item under each duct, used or skipped, with its own fit and any line segments), `duct_ends.csv` (per duct end: overlap + / gap − with the collinear neighbouring duct), `cl_segments.csv` + `cl_nodes.csv` (centreline graph from the Revit centrelines, which come through as line primitives: segments per element, nodes merged at 0.02 ft with degree and categories), `ducts_tri.bin` (duct triangles, world coordinates: int32 row, int32 nTri, nTri × 9 float32), `summary.txt` (counts, check statistics). Triangles via the COM API (`ComApiBridge`); the build references `Autodesk.Navisworks.ComApi.dll` and `Autodesk.Navisworks.Interop.ComApi.dll` from the install. Hidden state (the search includes hidden items): `ducts.csv` `Hidden` (`self` / `ancestor` / empty) and `HiddenGeom` (own geometry items hidden), `duct_parts.csv` `Hidden`; counts in the summary; the `-Mesh` overlay puts hidden / partly hidden elements in their own groups |
| Pluto Box | `BoxButton` | read-only; a dialog asks for a centre (paste the Pluto viewer readout: plant E, N, EL in **inches**; or feet) and a half-size in feet (option: only geometry under duct / fabrication elements; last values kept in `C:\Temp\hvac\box\last.txt`). **No property search**: walks the tree from the model roots and skips any subtree whose bbox (hidden included) misses the box, so it costs what is in the box, not the federation. Every geometry item inside → `C:\Temp\hvac\box\<yyyyMMdd-HHmmss>\`: `box_items.csv` (item NavisId / name / class / hidden; nearest ancestor-or-self with an IfcGUID: GUID, category, family, type, size, name, NavisId, levels up; tree path; triangles total / kept; bbox), `box_tri.bin` (only triangles touching the box), `summary.txt`. Stops at 5 M triangles (flagged). `Export-DuctsViewer.ps1 -Run <that folder>` makes the overlay: one group per element category, hidden items in red groups, each triangle labelled with where its mesh hangs |

## Viewer file from a Pluto Ducts run

```
powershell -ExecutionPolicy Bypass -File scripts\navis\Export-DuctsViewer.ps1 -Run C:\Temp\hvac\ducts\<yyyyMMdd-HHmmss>
```

Writes `<run>\ducts.bin` + `ducts.features.json` (`scripts\arms\DuctsToPluto.cs`): ducts along their Revit
centrelines (fitted ends when a duct has none) with box / pipe sections from size and wall thickness;
fitting centrelines with the fitting's size (symbol linework dropped: segments at nodes of degree ≥ 5);
fittings without centrelines and accessories as bbox blocks; node group "Loose ends". Inches with the plant
worldOffset, so in the viewer **Add overlay…** puts it on the SP3D plant file (rot 0: Navisworks X/Y/Z =
plant E/N/EL).

## If the button is missing

- Restart Navisworks once more (the tab has been seen to appear only on a later start).
- Folder name ≠ DLL name, or a copy of `Autodesk.Navisworks.Api.dll` beside the plugin (never ship it; the
  script references the installed one only).
- Load errors: must be x64 (the script builds x64).

## Rules

- Plugin id is `HelloButton.Pluto` (class name + developer id from the `[Plugin]` attribute); the
  Automation API runs plugins by this id.
- No project names, numbers or paths in this folder; the plugin only touches the open document.
