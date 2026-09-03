#!/usr/bin/env python3
"""Config -> .s2k -> SAP2000 (run) -> results.s2k -> SapToPluto -> viewer, in one go.

    python run_sap.py configs/example.toml              # everything
    python run_sap.py configs/example.toml --no-build   # reuse the existing .s2k
    python run_sap.py configs/example.toml --no-run     # results already in SAP
    python run_sap.py configs/example.toml --no-viewer  # stop after the .bin

Steps (each skippable):
  build    tankbuilder writes <models>/<name>/<name>.s2k from the config
  run      attach to the running SAP2000 (or start one), OpenFile the .s2k,
           Save the .sdb, RunAnalysis
  results  Results.JointDispl + Results.AreaForceShell (full precision) ->
           <models>/<name>/results.s2k
  export   scripts/arms/SapToPluto.cs via Windows PowerShell 5.1 (Config.ps1)
           -> <name>.bin + <name>.features.json beside the model
  viewer   a local static server on the repo root + the default browser at
           viewer/index.html?bin=...&features=... (the viewer fetches both)
"""

from __future__ import annotations

import argparse
import http.server
import subprocess
import sys
import threading
import time
import webbrowser
from functools import partial
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
REPO = HERE.parent.parent

from tankbuilder import TankModel, load_config, write_s2k  # noqa: E402

PS51 = r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"


def step_build(cfg: Path) -> tuple[Path, str]:
    spec, out = load_config(cfg)
    out.parent.mkdir(parents=True, exist_ok=True)
    model = TankModel(spec)
    write_s2k(model, out)
    print(f"[build]   {out}  ({len(model.joints)} joints, {len(model.areas)} shells, "
          f"{len(model.groups())} groups)")
    return out, spec_model_id(cfg)


def spec_model_id(cfg: Path) -> str:
    return f"sap/{cfg.stem}"


def step_run(s2k: Path, run: bool):
    from tankbuilder.sap_api import SapSession
    sap = SapSession.attach_or_start()
    print(f"[sap]     SAP2000 {sap.version} ({'started' if sap.started else 'attached'})")
    if run:
        sap.open(s2k)
        j, a, _ = sap.counts()
        print(f"[open]    {j} joints, {a} areas, groups: {', '.join(sap.groups())}  ({sap.open_seconds:.0f}s)")
        secs = sap.run()
        print(f"[run]     cases {', '.join(sap.load_cases())}  ({secs:.1f}s)")
    return sap


def step_results(sap, s2k: Path) -> Path:
    res = s2k.parent / "results.s2k"
    res.write_text(sap.results_s2k())
    print(f"[results] {res}  ({res.stat().st_size // 1024} kB)")
    return res


def step_export(s2k: Path, results: Path, model_id: str, cylindrical: bool) -> tuple[Path, Path]:
    out_base = s2k.with_suffix("")
    script = s2k.parent / "_export.ps1"
    cyl = "$true" if cylindrical else "$false"
    script.write_text(
        "$ErrorActionPreference = 'Stop'\n"
        f". '{REPO / 'scripts' / 'lib' / 'Config.ps1'}'\n"
        f"$r = [SapToPluto]::Export('{s2k}', '{results}', '{out_base}', '{model_id}', {cyl})\n"
        "Write-Output $r.Summary()\n")
    r = subprocess.run([PS51, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-File", str(script)], capture_output=True, text=True)
    for line in r.stdout.splitlines():
        if line.strip() and not line.startswith(("Nodes Extracted", "Elements Extracted")):
            print("[export]  " + line)
    if r.returncode != 0:
        print(r.stderr, file=sys.stderr)
        raise SystemExit(f"SapToPluto failed ({r.returncode})")
    return out_base.with_suffix(".bin"), out_base.with_suffix(".features.json")


def step_viewer(bin_path: Path, features: Path, port: int) -> None:
    handler = partial(http.server.SimpleHTTPRequestHandler, directory=str(REPO))
    handler.log_message = lambda *a, **k: None          # quiet
    httpd = http.server.ThreadingHTTPServer(("127.0.0.1", port), handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    rel = lambda p: "/" + p.resolve().relative_to(REPO).as_posix()
    url = (f"http://127.0.0.1:{port}/viewer/index.html"
           f"?bin={rel(bin_path)}&features={rel(features)}")
    print(f"[viewer]  {url}")
    webbrowser.open(url)
    print("          serving; Ctrl+C to stop")
    try:
        while True:                     # a timed wait keeps Ctrl+C working on Windows
            time.sleep(0.5)
    except KeyboardInterrupt:
        print("")
        print("          stopped")
    finally:
        httpd.shutdown()


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("config", type=Path)
    p.add_argument("--no-build", action="store_true", help="reuse the existing .s2k")
    p.add_argument("--no-run", action="store_true", help="SAP already holds the analysed model")
    p.add_argument("--no-export", action="store_true", help="stop after results.s2k")
    p.add_argument("--no-viewer", action="store_true", help="stop after the .bin")
    p.add_argument("--no-cylindrical", action="store_true", help="skip Translation R / T")
    p.add_argument("--port", type=int, default=8765)
    a = p.parse_args(argv)

    if a.no_build:
        _, s2k = load_config(a.config)
        model_id = spec_model_id(a.config)
        print(f"[build]   skipped, using {s2k}")
    else:
        s2k, model_id = step_build(a.config)

    sap = step_run(s2k, run=not a.no_run)
    results = step_results(sap, s2k)
    if a.no_export:
        return 0
    bin_path, features = step_export(s2k, results, model_id, cylindrical=not a.no_cylindrical)
    if a.no_viewer:
        return 0
    step_viewer(bin_path, features, a.port)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
