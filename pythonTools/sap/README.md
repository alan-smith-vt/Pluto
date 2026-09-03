# pythonTools/sap — SAP2000 tank generator (Python by design)

Development side only. Python 3.11+ (stdlib: `tomllib`, `dataclasses`). Kept in Python
because any future SAP2000 automation (OAPI over COM) is easiest from here; the *reader*
side is C# (`scripts/arms/SapToPluto.cs`) so the results path runs production-side.

    python build_tank.py configs/tank_hoop.toml        # -> models/tank_hoop/tank_hoop.s2k
    # import into SAP2000, run, export results .s2k, then (PowerShell 5.1):
    # [SapToPluto]::Export('models\tank_hoop\tank_hoop.s2k', 'models\tank_hoop\results.s2k', 'out\tank_hoop', 'sap/tank_hoop', $true)

| file | what |
|---|---|
| `build_tank.py` | tank wall mesh, base restraints, local axes, hydrostatic joint pattern -> `.s2k` |
| `configs/*.toml` | one config per model variant (units kip, ft, F) |
| `s2k_to_bin.py` | **superseded** by `SapToPluto.cs`; kept as the reference for the model-check fields (head, aspect ratio, normal direction) not yet carried over |

`models/` is generated and gitignored. Usage note: `vault/arms/sap-s2k-to-viewer.md`.
