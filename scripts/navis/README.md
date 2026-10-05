# PlutoNavis — Navisworks add-in

Navisworks Manage / Simulate add-in (same .NET API; Freedom cannot load add-ins). Goal: extract duct
geometry (triangles → centrelines) from the federated model for the viewer and SAP model builds.
Current rung: a hello-world button that proves the add-in loads.

## Build and install (no Visual Studio, no SDK)

Close Navisworks, then:

```
powershell -ExecutionPolicy Bypass -File scripts\navis\Build-NavisPlugin.ps1
```

- Finds the newest Navisworks Manage / Simulate under `C:\Program Files\Autodesk` (or `-NavisRoot <dir>`).
- Compiles with the Windows .NET Framework `csc.exe`, so **keep the sources C# 5**.
- Copies `PlutoNavis.dll` to `<NavisRoot>\Plugins\PlutoNavis\` (the folder name must equal the DLL name).
  Only the copy runs as admin: one UAC prompt per install. It stops if Navisworks is running (the loaded
  DLL is locked) and checks the installed file matches the build. `-NoInstall` stops after the build
  (`scripts\navis\bin\`).
- The per-user `%APPDATA%\Autodesk Navisworks <Product> <Year>\Plugins` folder did **not** load on
  Simulate 2025 (2026-10-05); the script removes an old copy there.

Then: open any NWD / NWF, ribbon **Tool add-ins 1** → **Pluto Hello**. Expected: a message box with the
file name and the number of appended models.

## If the button is missing

- Restart Navisworks once more (the tab has been seen to appear only on a later start).
- Folder name ≠ DLL name, or a copy of `Autodesk.Navisworks.Api.dll` beside the plugin (never ship it; the
  script references the installed one only).
- Load errors: must be x64 (the script builds x64).

## Rules

- Plugin id is `HelloButton.Pluto` (class name + developer id from the `[Plugin]` attribute); the
  Automation API runs plugins by this id.
- No project names, numbers or paths in this folder; the plugin only touches the open document.
