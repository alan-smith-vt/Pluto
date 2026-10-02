#!/usr/bin/env python3
"""Config -> .s2k -> SAP2000 (run) -> results.s2k -> SapToPluto -> viewer, in one go.

    python run_sap.py configs/example.toml              # everything
    python run_sap.py configs/example.toml --no-build   # reuse the existing .s2k
    python run_sap.py configs/example.toml --no-run     # results already in SAP
    python run_sap.py configs/example.toml --no-viewer  # stop after the .bin
    python run_sap.py configs/example.toml --figures    # also redraw the vault's model diagrams

Steps (each skippable):
  build    tankbuilder writes <models>/<name>/<name>.s2k from the config
  run      scripts/sap/Run-Sap.ps1 (the C# SAP controller; Python never calls the OAPI):
           attach to the running SAP2000 (or start one, left open), OpenFile the .s2k,
           Save the .sdb, read back the shell local axes, RunAnalysis
  results  same call: joint, shell, frame and link results (full precision) ->
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


def step_audit(cfg: Path, s2k: Path) -> None:
    """Settlement audit beside the model (CSV + SVG), when a profile is set. Nothing is
    written into the vault: `settlement_audit.py <config> --vault-svg` does that on demand
    (2026-09-10; every run used to overwrite vault/arms/assets/settlement-<name>.svg)."""
    from settlement_audit import write_audit
    spec, _ = load_config(cfg)
    if spec.settlement == "none":
        return
    csv_path, svg_path = write_audit(TankModel(spec), s2k.parent, s2k.stem)
    print(f"[audit]   {csv_path.name}, {svg_path.name}  (beside the model; --vault-svg on settlement_audit.py to publish)")


def step_figures(cfg: Path) -> None:
    """The vault's model diagrams (loads, shell local axes, beam nodes) drawn from this
    config. Only on --figures (2026-09-10): every run used to overwrite them, so a
    benchmark variant without a plate or ring wall replaced the documentation pictures.
    Standalone: `doc_figures.py <config> [-o dir]`."""
    import doc_figures
    spec, _ = load_config(cfg)
    m = TankModel(spec)
    doc_figures.ASSETS.mkdir(parents=True, exist_ok=True)
    names = []
    for name, fn in (("tank-loads", doc_figures.fig_loads), ("tank-local-axes", doc_figures.fig_axes),
                     ("tank-beam-nodes", doc_figures.fig_beam_nodes)):
        (doc_figures.ASSETS / f"{name}.svg").write_bytes(fn(m).replace("\n", "\r\n").encode("utf-8"))
        names.append(name)
    print(f"[figures] {', '.join(names)} -> vault/arms/assets")


def step_axes_check(axes_tsv: Path, cfg: Path) -> None:
    """After import: does SAP's local 1 match the meridional direction the builder
    intended, on every shell? Reports the count and the worst angle; a failure here
    means the F11 / S11 = meridional labels are lying on those elements. axes_tsv is
    Run-Sap.ps1 -AreaAxes output (Area, L1x, L1y, L1z in global)."""
    import math
    spec, _ = load_config(cfg)
    model = TankModel(spec)
    if not model.area_local_angle:
        return
    worst, bad, n = 0.0, [], 0
    try:
        local1 = {}
        for line in axes_tsv.read_text().splitlines()[1:]:
            a, x, y, z = line.split("	")
            local1[int(a)] = (float(x), float(y), float(z))
        for a in sorted(model.areas):
            v = local1[a]
            e = model.meridional_direction(a)
            dot = abs(v[0] * e[0] + v[1] * e[1] + v[2] * e[2])
            ang = math.degrees(math.acos(max(-1.0, min(1.0, dot))))
            n += 1
            worst = max(worst, ang)
            if ang > 1.0:
                bad.append((a, ang))
    except Exception as exc:   # missing / unreadable axes file: report, do not stop the run
        print(f"[axes]    could not read area local axes back ({exc}); labels unverified")
        return
    if bad:
        print(f"[axes]    WARNING local 1 is off the meridian on {len(bad)}/{n} shells (worst {worst:.2f} deg), "
              f"e.g. area {bad[0][0]}")
    else:
        print(f"[axes]    local 1 = meridional on all {n} shells (worst {worst:.3f} deg)")


def step_sap(s2k: Path, run: bool, cfg: Path) -> Path:
    """One Run-Sap.ps1 call: open + axes + run (unless --no-run: SAP already holds the
    analysed model), then results.s2k. A SAP it starts is left open, as before."""
    from tankbuilder.sap_cli import run_sap_ps
    res = s2k.parent / "results.s2k"
    axes = s2k.parent / "area-local1.tsv"
    run_sap_ps(open=s2k if run else None, area_axes=axes if run else None, run=run,
               results=res, keep_open=True, log=s2k.parent / "run-sap.log")
    if run:
        step_axes_check(axes, cfg)
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


class _NoCacheHandler(http.server.SimpleHTTPRequestHandler):
    """Static files with Cache-Control: no-store. SimpleHTTPRequestHandler sends
    Last-Modified, so browsers heuristically cache viewer.css / *.js across
    sessions and a stale stylesheet hides new overlays (readout, 2026-09-03)."""

    def end_headers(self):
        self.send_header("Cache-Control", "no-store, must-revalidate")
        self.send_header("Expires", "0")
        super().end_headers()

    def log_message(self, *a, **k):                      # quiet
        pass


def step_viewer(bin_path: Path, features: Path, port: int) -> None:
    handler = partial(_NoCacheHandler, directory=str(REPO))
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
    p.add_argument("--figures", action="store_true",
                   help="also redraw the vault's model diagrams (vault/arms/assets/tank-*.svg) from this config")
    p.add_argument("--port", type=int, default=8765)
    a = p.parse_args(argv)

    if a.no_build:
        _, s2k = load_config(a.config)
        model_id = spec_model_id(a.config)
        print(f"[build]   skipped, using {s2k}")
    else:
        s2k, model_id = step_build(a.config)
    step_audit(a.config, s2k)
    if a.figures:
        step_figures(a.config)

    results = step_sap(s2k, run=not a.no_run, cfg=a.config)
    if a.no_export:
        return 0
    bin_path, features = step_export(s2k, results, model_id, cylindrical=not a.no_cylindrical)
    if a.no_viewer:
        return 0
    step_viewer(bin_path, features, a.port)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
