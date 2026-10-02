"""SAP2000 from Python: calls the C# controller (scripts/sap/Run-Sap.ps1). Python never talks to
the OAPI itself; every capability lives in scripts/sap/SapSession.cs and friends
(vault/arms/sap-controller). Add a switch there, not an OAPI call here.

    r = run_sap_ps(open=s2k, run=True, results=s2k.parent / "results.s2k", area_axes=tsv)
    r["version"], r["counts"], r["cases"], ...      # the runner's RESULT json

The runner's output goes to a log file, never a pipe, and stdin is closed: a SAP2000 started by
the child inherits its handles, so reading a pipe to EOF would wait for SAP to exit (the pipe
hang, 2026-09). The log is echoed line by line once the runner returns.
"""

from __future__ import annotations

import json
import subprocess
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
RUNNER = REPO / "scripts" / "sap" / "Run-Sap.ps1"
PS51 = r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"

_SWITCHES = {"no_save_sdb", "new_instance", "keep_open", "run", "run_if_needed", "no_results",
             "final_only", "no_viewer_file", "cylindrical"}
_VALUES = {"open", "area_axes", "run_on_copy", "tables", "tables_out", "results", "export", "model_id",
           "self_test", "sap_dir"}


def _flag(name: str) -> str:
    return "-" + "".join(p.capitalize() for p in name.split("_"))


def run_sap_ps(echo: bool = True, log: Path | None = None, **kw) -> dict:
    """Run Run-Sap.ps1 with keyword switches (open=..., run=True, results=..., export=...,
    final_only=True, ...; names as in the runner, snake_case). Returns its RESULT json.
    Raises RuntimeError with the log tail when the runner fails."""
    args = [PS51, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", str(RUNNER)]
    for k, v in kw.items():
        if k in _SWITCHES:
            if v:
                args.append(_flag(k))
        elif k in _VALUES:
            if v is not None:
                args += [_flag(k), str(v)]
        else:
            raise TypeError(f"Run-Sap.ps1 has no switch for {k!r}")
    if log is None:
        log = Path(tempfile.gettempdir()) / "pluto-run-sap.log"
    with open(log, "w", encoding="utf-8", errors="replace") as fh:
        rc = subprocess.run(args, stdin=subprocess.DEVNULL, stdout=fh, stderr=subprocess.STDOUT).returncode
    lines = log.read_text(encoding="utf-8", errors="replace").splitlines()
    result = {}
    for line in lines:
        if line.startswith("RESULT "):
            result = json.loads(line[7:])
        elif echo and line.strip():
            print(line)
    if rc != 0:
        raise RuntimeError(f"Run-Sap.ps1 failed ({rc}); log {log}:\n" + "\n".join(lines[-30:]))
    return result
