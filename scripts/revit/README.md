# PlutoRevit — Revit add-in

Read-only export of HVAC connectivity from the Revit model (Revit 2025 on the secure machine). Revit stores
connectors with position, size and what they connect to, so the duct graph comes straight from the model
instead of the Navisworks mesh probe. No view needed: run it from the opening view (the 3D view crashes).

## Build and install (no Visual Studio, no SDK, no Roslyn)

```
powershell -ExecutionPolicy Bypass -File scripts\revit\Build-RevitPlugin.ps1
```

- Compiles a .NET Framework DLL with the Windows `csc.exe` (**keep the sources C# 5**) against the installed
  `RevitAPI.dll` / `RevitAPIUI.dll`, referencing the Framework `System.Runtime` facade for the .NET 8 API.
  Unproven on Revit 2025 until the first run (2026-10-09).
- Installs per user (no elevation): `PlutoRevit.dll` + `PlutoRevit.addin` into
  `%APPDATA%\Autodesk\Revit\Addins\<year>\`. Close Revit before rebuilding.

Then: open the model, **Add-Ins → External Tools → Pluto Connectors**.

## Commands

| Command | Class | Does |
|---|---|---|
| Pluto Connectors | `ExportConnectors` | fabrication ductwork, ducts, fittings, accessories, flex, terminals, mechanical equipment → `C:\Temp\hvac\revit\<yyyyMMdd-HHmmss>\`: `elements.csv` (ElementId, UniqueId, IfcGUID, category, family / fabrication product, type / alias, size, system, level, fabrication flag, connector count), `connectors.csv` (owner, connector id, type, domain, shape, origin ft, direction, width / height / diameter in, connected-to `ElementId:ConnectorId` list, logical links skipped), `summary.txt` (counts by category and family / type, open End connectors, errors) |
| Pluto Mesh | `ExportMesh` | the triangulated solids (`Face.Triangulate`, Fine detail; Revit draws its views from the same solids) of every model element (any model category; the overlay run / no-run split sets non-run items aside) whose bbox touches a box in `C:\Temp\hvac\revit\mesh-boxes.txt` (written by `Export-DuctsViewer.ps1 -Revit <run> -Room <codes>`: one box per room in Revit internal ft + the fit to the Navisworks frame) → `C:\Temp\hvac\revit-mesh\<yyyyMMdd-HHmmss>\`: `mesh_items.csv` (ElementId, UniqueId, IfcGUID, category, family, type, RunName (insulation / lining: the covered duct's), system, fabrication flag, host, box, triangles, triangles; summary counts elements with no geometry), `mesh_tri.bin` (only triangles touching a box), `mesh-boxes.txt`, `summary.txt` (NEW line). Stops at 10 M triangles. `Export-DuctsViewer.ps1 -Run <that folder>` makes the overlay in the Navisworks frame |
