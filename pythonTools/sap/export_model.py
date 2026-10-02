#!/usr/bin/env python3
r"""Whatever SAP2000 holds -> viewer .bin + .features.json, no config, no manual export.

    python export_model.py                          # the model open in SAP -> ~/Downloads/<name>.bin
    python export_model.py -o D:\x\viewer\Model     # output base (no extension)
    python export_model.py "D:\x\Model.sdb"         # open that file in the running SAP first (replaces what it holds)
    python export_model.py "D:\x\Model.sdb" --new-instance   # ... in a second SAP; the first is untouched
    python export_model.py --run                    # (re)run the analysis before pulling results
    python export_model.py --cylindrical            # tanks / silos: add Translation R / T
    python export_model.py --view                   # serve and open the viewer afterwards

A thin wrapper: the work is scripts/sap/Run-Sap.ps1 -Export (C#, SapExport.cs), which the
production machine runs directly. What it does:
  attach   the running SAP, or a fresh one with --new-instance (closed at the end unless
           --keep-open); with a path, File.OpenFile it
  run      only with --run or when a case has no results. SAP saves the model when it runs,
           so the model is first saved AS <outBase>.sdb (a working copy) and every case is
           flagged to run; the file SAP opened is never written
  model    the ANALYSIS MESH (PointElm / AreaElm / LineElm renumbered 1..N) with sections, local
           axes, restraints and groups -> <outBase>.model.s2k, ids -> <outBase>.labels.csv
  results  joints, shells, frames for every case, full precision; a case with several saved
           steps becomes one viewer case per step, <case>.<n> (--final-only: last step only)
           -> <outBase>.results.s2k (skipped with --no-results)
  export   SapToPluto -> <outBase>.bin + .features.json

Not carried: tendons, cables, links, solids (no viewer domain); frame end releases and
explicit insertion offsets; reactions and link forces (see vault/arms/sap-results-coverage).
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from tankbuilder.sap_cli import run_sap_ps  # noqa: E402


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("sdb", nargs="?", type=Path, help="model to open first (default: the one SAP holds)")
    p.add_argument("-o", "--out", type=Path, help="output base path, no extension (default: ~/Downloads/<model name>)")
    p.add_argument("--new-instance", action="store_true", help="start a second SAP2000 instead of attaching")
    p.add_argument("--keep-open", action="store_true", help="with --new-instance: leave that SAP running afterwards")
    p.add_argument("--run", action="store_true", help="run the analysis first (default: only if results are missing)")
    p.add_argument("--no-results", action="store_true", help="geometry only")
    p.add_argument("--final-only", action="store_true",
                   help="staged / multi-step cases: only the last step, under the bare case name (default: every step as <case>.<n>)")
    p.add_argument("--cylindrical", action="store_true", help="add Translation R / T about the plan centroid")
    p.add_argument("--view", action="store_true", help="serve the repo and open the viewer on the result")
    p.add_argument("--port", type=int, default=8765)
    a = p.parse_args(argv)

    out = a.out.resolve() if a.out else Path.home() / "Downloads"   # a folder: the runner appends the model name
    r = run_sap_ps(open=a.sdb.resolve() if a.sdb else None, no_save_sdb=True,
                   new_instance=a.new_instance, keep_open=a.keep_open,
                   run_on_copy=None if a.no_results else "auto", run_if_needed=not a.run and not a.no_results,
                   export=out, no_results=a.no_results, final_only=a.final_only, cylindrical=a.cylindrical)
    bin_path, features = Path(r["bin"]), Path(r["features"])
    print(f"[done]    {bin_path}  +  {features.name}")
    if a.view:
        from run_sap import step_viewer
        step_viewer(bin_path, features, a.port)
    return 0


if __name__ == "__main__":
    sys.exit(main())
